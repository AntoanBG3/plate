using System.Data.Common;
using FluentAssertions;
using Plate.Core.Domain;
using Plate.Web.Data;
using Plate.Web.Features.Food;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Plate.Web.Tests;

public class FoodSearchEquivalenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("oats")]
    [InlineData("пиле")]
    [InlineData("мляко")]
    [InlineData("бял ориз")]
    [InlineData("chickn")]
    [InlineData("cottage cheese")]
    [InlineData("banana, raw")]
    [InlineData("кисело мляко 2%")]
    [InlineData("pasta")]
    [InlineData("кашкавал")]
    [InlineData("rice cooked")]
    [InlineData("skyr")]
    public async Task Picker_and_matcher_results_equal_the_original_queries(string query)
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        // Include own-first ranking, a foreign private food, duplicates and partial multiword names.
        var foreign = await fixture.CreateUserAsync();
        s.Db.FoodItems.AddRange(
            new FoodItem { Name = query + " family recipe", OwnerUserId = s.UserId, Source = FoodSource.Manual },
            new FoodItem { Name = query + " secret recipe", OwnerUserId = foreign, Source = FoodSource.Manual },
            new FoodItem { Name = "Plain " + query + ", cooked", NameBg = "Просто " + query, Source = FoodSource.Usda });
        await s.Db.SaveChangesAsync();
        foreach (var minScore in new[] { 0, 0.3, 0.5, 0.8, 1.0 })
        {
            var expected = await OriginalAsync(s, query, 120, minScore);
            var actual = await s.Foods.SearchAsync(query, 120, minScore, default);
            actual.Should().Equal(expected, "IDs, order, scores and nutrition must be unchanged at threshold {0}", minScore);
        }
        (await s.Foods.SearchForPickerAsync(query, 20, default)).Should().Equal(await OriginalPickerAsync(s, query, 20));
    }

    [Fact]
    public async Task Exact_word_similarity_boundaries_are_included()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var item = new FoodItem { Name = "two words", OwnerUserId = s.UserId, Source = FoodSource.Manual };
        s.Db.FoodItems.Add(item);
        await s.Db.SaveChangesAsync();
        var boundary = await s.Db.Database.SqlQuery<double>($"SELECT word_similarity('word', 'two words')::float8 AS \"Value\"").SingleAsync();
        (await s.Foods.SearchAsync("word", 120, boundary, default)).Should().Contain(x => x.Id == item.Id);
        (await s.Foods.SearchAsync("word", 120, Math.BitIncrement(boundary), default)).Should().NotContain(x => x.Id == item.Id);
    }

    [Fact]
    public async Task A_search_preserves_the_callers_transaction_and_similarity_setting()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        await using var transaction = await s.Db.Database.BeginTransactionAsync();
        await s.Db.Database.ExecuteSqlRawAsync("SET LOCAL pg_trgm.word_similarity_threshold = 0.9");
        var item = new FoodItem { Name = "Transaction banana", OwnerUserId = s.UserId, Source = FoodSource.Manual };
        s.Db.FoodItems.Add(item);
        await s.Db.SaveChangesAsync();
        (await s.Foods.SearchAsync("banana", 120, 0.3, default)).Should().Equal(await OriginalAsync(s, "banana", 120, 0.3));
        var setting = await s.Db.Database.SqlQuery<string>($"SELECT current_setting('pg_trgm.word_similarity_threshold') AS \"Value\"").SingleAsync();
        setting.Should().Be("0.9");
        (await s.Db.FoodItems.AnyAsync(x => x.Id == item.Id)).Should().BeTrue();
        await transaction.CommitAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_actual_matcher_query_can_use_the_search_indexes(bool includeBrand)
    {
        var capture = new CaptureSearch();
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext(user, null, capture);
        // Finish index maintenance after the fixture's bulk seed before checking a normal planner choice.
        await db.Database.ExecuteSqlRawAsync("VACUUM (ANALYZE) \"FoodItems\"");
        var foods = new FoodItemService(db, new FixedCurrentUser(user), new FakeFoodDatabase());
        await foods.SearchAsync("sardines", 50, 0.3, default, includeBrand);
        capture.Sql.Should().NotBeNull();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setup = new NpgsqlCommand("SET LOCAL pg_trgm.word_similarity_threshold = 0.29999998", connection, transaction))
            await setup.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + capture.Sql, connection, transaction);
        command.Parameters.AddRange(capture.Parameters.ToArray());
        var plan = (string)(await command.ExecuteScalarAsync())!;
        plan.Should().Contain("Bitmap Index Scan").And.Contain("IX_FoodItems_Name");
        if (includeBrand) plan.Should().Contain("IX_FoodItems_Brand");
        plan.Should().NotContain("\"Node Type\": \"Seq Scan\"");
    }

    private sealed class CaptureSearch : DbCommandInterceptor
    {
        public string? Sql;
        public List<NpgsqlParameter> Parameters = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("word_similarity("))
            {
                Sql = command.CommandText;
                Parameters = command.Parameters.Cast<NpgsqlParameter>().Select(p => new NpgsqlParameter(p.ParameterName, p.Value ?? DBNull.Value)).ToList();
            }
            return ValueTask.FromResult(result);
        }
    }

    // Pin both pre-optimization SQL shapes as an independent equivalence oracle.
    private static Task<List<FoodMatch>> OriginalAsync(UserScope s, string q, int take, double minScore) =>
        s.Db.Database.SqlQuery<FoodMatch>($"""
            SELECT f."Id", f."Name", f."NameBg", f."Brand", f."KcalPer100g", f."ProteinPer100g", f."CarbsPer100g", f."FatPer100g",
                   f."FiberPer100g", f."SaturatedFatPer100g", f."DefaultServingGrams", f."OwnerUserId" IS NOT NULL AS "Own",
                   (f."Source" = 'Manual' AND f."OwnerUserId" IS NULL AND f."AddedByUserId" IS NOT NULL AND f."AddedByUserId" <> {s.UserId}) AS "ByOther",
                   GREATEST(word_similarity({q}, f."Name"), word_similarity({q}, COALESCE(f."NameBg", '')))::float8 AS "Score",
                   f."PieceGrams", f."PackGrams", f."ValuesEstimated", f."Unit" = 'Millilitres' AS "PerMillilitre"
            FROM "FoodItems" f
            WHERE (f."OwnerUserId" IS NULL OR f."OwnerUserId" = {s.UserId})
              AND GREATEST(word_similarity({q}, f."Name"), word_similarity({q}, COALESCE(f."NameBg", ''))) >= {minScore}
            ORDER BY "Own" DESC, "Score" DESC, length(f."Name")
            LIMIT {take}
            """).ToListAsync();

    private static async Task<List<FoodMatch>> OriginalPickerAsync(UserScope s, string q, int take)
    {
        var lower = q.ToLowerInvariant();
        var heads = FoodItemService.HeadForms(q);
        var firsts = q.Trim().Contains(' ') ? [] : heads;
        var found = await s.Db.Database.SqlQuery<FoodMatch>($"""
            WITH m AS (
                SELECT f."Id", f."Name", f."NameBg", f."Brand", f."KcalPer100g", f."ProteinPer100g", f."CarbsPer100g", f."FatPer100g",
                       f."FiberPer100g", f."SaturatedFatPer100g", f."DefaultServingGrams", f."OwnerUserId" IS NOT NULL AS "Own",
                       (f."Source" = 'Manual' AND f."OwnerUserId" IS NULL AND f."AddedByUserId" IS NOT NULL AND f."AddedByUserId" <> {s.UserId}) AS "ByOther",
                       f."PieceGrams", f."PackGrams", f."ValuesEstimated", f."Unit" = 'Millilitres' AS "PerMillilitre",
                       GREATEST(word_similarity({q}, f."Name"), word_similarity({q}, COALESCE(f."NameBg", ''))) AS sim,
                       starts_with(lower(f."Name"), {lower}) OR starts_with(lower(COALESCE(f."NameBg", '')), {lower}) AS pre,
                       starts_with(lower(COALESCE(f."NameBg", '')), {lower}) AND NOT starts_with(lower(f."Name"), {lower}) AS pre_bg,
                       lower(btrim(split_part(f."Name", ',', 1))) = ANY({heads}) OR lower(btrim(split_part(COALESCE(f."NameBg", ''), ',', 1))) = ANY({heads}) AS head,
                       lower(split_part(btrim(regexp_replace(f."Name", '[,(/]', ' ', 'g')), ' ', 1)) = ANY({firsts})
                           OR lower(split_part(btrim(regexp_replace(COALESCE(f."NameBg", ''), '[,(/]', ' ', 'g')), ' ', 1)) = ANY({firsts}) AS first
                FROM "FoodItems" f
                WHERE (f."OwnerUserId" IS NULL OR f."OwnerUserId" = {s.UserId})
                  AND GREATEST(word_similarity({q}, f."Name"), word_similarity({q}, COALESCE(f."NameBg", ''))) >= 0.3
            )
            SELECT "Id", "Name", "NameBg", "Brand", "KcalPer100g", "ProteinPer100g", "CarbsPer100g", "FatPer100g",
                   "FiberPer100g", "SaturatedFatPer100g", "DefaultServingGrams", "Own", "ByOther", (sim + CASE WHEN pre THEN 1 ELSE 0 END)::float8 AS "Score",
                   "PieceGrams", "PackGrams", "ValuesEstimated", "PerMillilitre"
            FROM m
            ORDER BY "Own" DESC, head DESC, first DESC, pre DESC, CASE WHEN pre_bg THEN length("NameBg") WHEN pre THEN length("Name") END, sim DESC, length("Name")
            LIMIT {take * 2}
            """).ToListAsync();
        var keep = found.GroupBy(f => (f.Own, Name: f.Name.ToLowerInvariant(), Brand: f.Brand?.ToLowerInvariant()))
            .Select(g => g.MinBy(f => f.Id)!.Id).ToHashSet();
        return found.Where(f => keep.Contains(f.Id)).Take(take).ToList();
    }
}
