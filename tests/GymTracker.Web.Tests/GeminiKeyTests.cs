using System.Net;
using System.Text;
using FluentAssertions;
using GymTracker.Web.Data;
using GymTracker.Web.Features;
using GymTracker.Web.Integrations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class GeminiKeyTests(PostgresFixture fixture)
{
    private const string SettingsKey = "AQ.settings-key-0123456789abcdWXYZ";
    private const string EnvironmentKey = "AIza-environment-key-0123456789ENVK";

    private static readonly IDataProtectionProvider Protection = new EphemeralDataProtectionProvider();

    private static GeminiKeyStore Store(UserScope s, string environmentKey = "", IDataProtectionProvider? protection = null) =>
        new(s.Db, new FixedCurrentUser(s.UserId), protection ?? Protection,
            Options.Create(new GeminiOptions { ApiKey = environmentKey }), TimeProvider.System, NullLogger<GeminiKeyStore>.Instance);

    /// <summary>Answers every request with the given status and body, and remembers the key it was sent.</summary>
    private sealed class StubGemini(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? SentKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SentKey = request.Headers.GetValues("x-goog-api-key").Single();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private const string BananaResponse = """
        {"candidates":[{"content":{"parts":[{"text":"{\"items\":[{\"nameEn\":\"banana\",\"nameOriginal\":\"1 banana\",\"grams\":120,\"state\":\"na\",\"kcalPer100g\":89,\"proteinPer100g\":1.1,\"carbsPer100g\":23,\"fatPer100g\":0.3}]}"}]}}]}
        """;

    private static GeminiFoodParser Parser(StubGemini stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("https://gemini.test/") }, Options.Create(new GeminiOptions()), NullLogger<GeminiFoodParser>.Instance);

    [Fact]
    public async Task A_key_saved_in_settings_wins_over_the_environment_and_is_stored_encrypted()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var store = Store(s, EnvironmentKey);
        try
        {
            (await store.StatusAsync(default)).Should().Be(new GeminiKeyStatus(KeySource.Environment, "ENVK"));

            await store.SetAsync($"  {SettingsKey} ", default);

            (await store.GetAsync(default)).Should().Be(SettingsKey);
            (await store.StatusAsync(default)).Should().Be(new GeminiKeyStatus(KeySource.Settings, "WXYZ"));
            var stored = await s.Db.AppSettings.AsNoTracking().SingleAsync(x => x.Name == GeminiKeyStore.SettingName);
            stored.Value.Should().NotContain("settings-key");
            stored.UpdatedByUserId.Should().Be(s.UserId);

            await store.ClearAsync(default);
            (await store.GetAsync(default)).Should().Be(EnvironmentKey);
        }
        finally
        {
            await store.ClearAsync(default);
        }
    }

    [Fact]
    public async Task A_key_that_can_no_longer_be_decrypted_is_ignored()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        try
        {
            await Store(s).SetAsync(SettingsKey, default);

            // Different Data Protection keys, as after losing the keys folder.
            var afterKeyLoss = Store(s, protection: new EphemeralDataProtectionProvider());
            (await afterKeyLoss.StatusAsync(default)).Should().Be(new GeminiKeyStatus(KeySource.None, null));
        }
        finally
        {
            await Store(s).ClearAsync(default);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has a space in the middle of it ok")]
    public async Task Rejects_text_that_is_not_a_key(string key)
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());

        await Store(s).Invoking(x => x.SetAsync(key, default)).Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Test_sends_the_given_key_and_reports_success()
    {
        var stub = new StubGemini(HttpStatusCode.OK, BananaResponse);

        (await Parser(stub).TestAsync(SettingsKey, default)).Should().BeNull();
        stub.SentKey.Should().Be(SettingsKey);
    }

    [Fact]
    public async Task A_rejected_key_says_to_check_settings()
    {
        var stub = new StubGemini(HttpStatusCode.Unauthorized, """{"error":{"code":401}}""");

        (await Parser(stub).TestAsync(SettingsKey, default))!.ToString().Should().Contain("rejected the API key (401)").And.Contain("Settings");
    }

    [Fact]
    public async Task Without_any_key_the_built_in_parser_is_used()
    {
        await using var s = new UserScope(fixture, await fixture.CreateUserAsync());
        var stub = new StubGemini(HttpStatusCode.OK, BananaResponse);
        var services = new ServiceCollection().AddSingleton(Parser(stub)).BuildServiceProvider();

        var withoutKey = await new SwitchingFoodParser(Store(s), services).ParseAsync("200g rice", default);
        withoutKey.Model.Should().Be(FakeFoodParser.ModelName);
        stub.SentKey.Should().BeNull();

        var withEnvironmentKey = await new SwitchingFoodParser(Store(s, EnvironmentKey), services).ParseAsync("1 banana", default);
        withEnvironmentKey.Items.Should().ContainSingle(i => i.NameEn == "banana");
        stub.SentKey.Should().Be(EnvironmentKey);
    }
}
