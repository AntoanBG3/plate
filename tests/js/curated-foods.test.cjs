const assert = require("node:assert/strict");
const { test } = require("node:test");
const path = require("node:path");
const { pathToFileURL } = require("node:url");

const repo = path.resolve(__dirname, "../..");

// tools/foods/merge.mjs rewrites the seed from its sources and appends the hand-curated files; a curated row missing
// from the seed means the seed was edited or regenerated without them.
test("the food seed has every hand-curated row unchanged", async () => {
    const { readCsv } = await import(pathToFileURL(path.join(repo, "tools/foods/lib.mjs")).href);
    const seed = new Set(readCsv(path.join(repo, "src/Plate.Web/Data/Seed/bg-foods.csv")).map((r) => JSON.stringify(r)));
    const curated = readCsv(path.join(repo, "tools/foods/lidl-bakery.csv"));

    assert.ok(curated.length >= 43);
    assert.deepEqual(curated.filter((r) => !seed.has(JSON.stringify(r))).map((r) => r.name_bg), []);
});
