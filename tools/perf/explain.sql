-- Local synthetic database only. Run after seeding:
-- docker exec -i repa-perf-db psql -X -U gym -d gym -v email=perf1@example.test < tools/perf/explain.sql > /tmp/repa-explain.txt
-- Override email with the synthetic account used by the harness. No production data is needed.
-- These are representative SQL shapes, not a claim to be byte-for-byte EF-generated SQL.
\set ON_ERROR_STOP on
\pset pager off
SELECT "Id" AS uid FROM "AspNetUsers" WHERE "Email" = :'email' \gset
SELECT 'dataset' AS section, (SELECT count(*) FROM "FoodItems") AS foods,
       (SELECT count(*) FROM "Sessions" WHERE "UserId" = :'uid') AS sessions,
       (SELECT count(*) FROM "Bodyweights" WHERE "UserId" = :'uid') AS weights,
       (SELECT count(*) FROM "NutritionTargetDays" WHERE "UserId" = :'uid') AS target_days;

SELECT 'food-search-original' AS section;
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
SELECT f."Id", f."Name", f."NameBg", f."Brand", f."KcalPer100g", f."ProteinPer100g", f."CarbsPer100g", f."FatPer100g",
       f."FiberPer100g", f."SaturatedFatPer100g", f."DefaultServingGrams", f."OwnerUserId" IS NOT NULL AS "Own",
       (f."Source" = 'Manual' AND f."OwnerUserId" IS NULL AND f."AddedByUserId" IS NOT NULL AND f."AddedByUserId" <> :'uid') AS "ByOther",
       GREATEST(word_similarity('banana', f."Name"), word_similarity('banana', COALESCE(f."NameBg", '')))::float8 AS "Score"
FROM "FoodItems" f
WHERE (f."OwnerUserId" IS NULL OR f."OwnerUserId" = :'uid')
  AND GREATEST(word_similarity('banana', f."Name"), word_similarity('banana', COALESCE(f."NameBg", ''))) >= 0.3
ORDER BY "Own" DESC, "Score" DESC, length(f."Name") LIMIT 50;

SELECT 'session-recent-ids' AS section;
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
SELECT se."SessionId"
FROM "SessionExercises" se JOIN "Sessions" s ON s."Id" = se."SessionId"
WHERE se."DeletedAt" IS NULL AND s."UserId" = :'uid' AND se."ExerciseId" = 1
  AND s."StartedAt" < now()
ORDER BY s."StartedAt" DESC LIMIT 6;

SELECT 'weight-all-history' AS section;
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
SELECT "Id", "UserId", "Date", "WeightKg"
FROM "Bodyweights" WHERE "UserId" = :'uid' ORDER BY "Date";

SELECT 'progress-target-snapshots-all-history' AS section;
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
SELECT "UserId", "Date", "Values"
FROM "NutritionTargetDays" WHERE "UserId" = :'uid' AND "Date" <= CURRENT_DATE ORDER BY "Date";

SELECT 'food-last-amount' AS section;
EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
SELECT "FoodItemId", "Grams", "CreatedAt"
FROM "FoodLog"
WHERE "UserId" = :'uid' AND "DeletedAt" IS NULL AND "FoodItemId" IS NOT NULL
  AND "FoodItemId" IN (SELECT "Id" FROM "FoodItems" ORDER BY "Id" LIMIT 20)
  AND "CreatedAt" >= now() - interval '90 days' AND "Grams" > 0;
