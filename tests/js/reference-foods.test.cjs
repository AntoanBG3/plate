const assert = require("node:assert/strict");
const fs = require("node:fs");
const { test } = require("node:test");
const path = require("node:path");
const { pathToFileURL } = require("node:url");

const repo = path.resolve(__dirname, "../..");

// A reference food linked by its English name to a USDA food should have about its energy; the 2026-11-21 audit found
// 38 that don't (reports/reference-foods-audit.md). Those are listed as known; a new one fails, and a fixed one has to
// leave the list so it can't come back unnoticed.
test("no new reference food disagrees with its USDA twin", async () => {
    const { conflicts } = await import(pathToFileURL(path.join(repo, "tools/foods/audit-reference.mjs")).href);
    const known = new Set(fs.readFileSync(path.join(repo, "tools/foods/reference-conflicts.known.txt"), "utf8")
        .split("\n").map((l) => l.trim()).filter((l) => l && !l.startsWith("#")));
    const found = conflicts().map((c) => c.name_bg);

    assert.deepEqual(found.filter((n) => !known.has(n)), [], "new conflicts; see node tools/foods/audit-reference.mjs");
    assert.deepEqual([...known].filter((n) => !found.includes(n)), [], "fixed; remove these from reference-conflicts.known.txt");
});
