const assert = require("node:assert/strict");
const { test } = require("node:test");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

function drill(options = {}) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "gym-restore-test-"));
    const trace = path.join(root, "calls.jsonl");
    const config = {
        live: "FoodLog=38 Profiles=5", restored: "FoodLog=30 Profiles=5",
        liveTables: "FoodLog\nProfiles", restoredTables: "FoodLog\nProfiles",
        liveSchema: "same column definitions", restoredSchema: "same column definitions",
        fail: "", nightly: true, ...options,
    };
    const mock = [
        "#!/usr/bin/env node",
        'const fs = require("node:fs");',
        'const c = JSON.parse(process.env.DRILL_CONFIG), a = process.argv.slice(2);',
        'fs.appendFileSync(process.env.DRILL_TRACE, JSON.stringify(a) + "\\n");',
        'if (a.includes("pg_restore")) { process.stdin.resume(); process.stdin.on("end", () => process.exit(c.fail === "restore" ? 7 : 0)); }',
        'else if (a.includes("readlink")) console.log("synthetic-nightly");',
        'else if (a.includes("cat") || a.some(x => x.includes("pg_dump"))) { if (c.fail === "archive") process.exit(8); console.log("synthetic archive"); }',
        'else if (a.includes("psql") || a.some(x => x.includes("psql"))) {',
        '  if (c.fail === "query") process.exit(9);',
        '  const live = a.includes("plate-db"), sql = a[a.length - 1];',
        '  console.log(sql.includes("information_schema.columns") ? (live ? c.liveSchema : c.restoredSchema)',
        '    : sql.includes("query_to_xml") ? (live ? c.live : c.restored)',
        '    : (live ? c.liveTables : c.restoredTables));',
        '}',
    ].join("\n");
    fs.writeFileSync(path.join(root, "docker"), mock, { mode: 0o755 });
    fs.writeFileSync(path.join(root, "sleep"), "#!/bin/sh\nexit 0\n", { mode: 0o755 });
    try {
        const result = spawnSync("bash", [path.resolve(__dirname, "../../tools/restore-drill.sh"), ...(config.nightly ? ["nightly"] : [])], {
            env: { ...process.env, PATH: root + path.delimiter + process.env.PATH,
                DRILL_CONFIG: JSON.stringify(config), DRILL_TRACE: trace },
            encoding: "utf8", timeout: 10000,
        });
        assert.ifError(result.error);
        const calls = fs.readFileSync(trace, "utf8").trim().split("\n").map(JSON.parse);
        return { ...result, calls };
    } finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
}

for (const [name, values] of [
    ["a missing table", { restored: "Profiles=5", restoredTables: "Profiles" }],
    ["an extra table", { restored: "Extra=1 FoodLog=30 Profiles=5", restoredTables: "Extra\nFoodLog\nProfiles" }],
    ["different column definitions", { restoredSchema: "a required column is missing" }],
    ["no tables", { live: "", restored: "", liveTables: "", restoredTables: "", liveSchema: "", restoredSchema: "" }],
]) {
    test("nightly restore rejects " + name, () => {
        const result = drill(values);
        assert.notEqual(result.status, 0);
        assert.doesNotMatch(result.stdout, /Restore OK/);
    });
}

test("nightly restore accepts older row counts only with matching tables and columns", () => {
    const result = drill();
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Restore OK/);
    assert.match(result.stdout, /Counts differ/);
});

test("an immediate dump requires exact row counts", () => {
    const mismatch = drill({ nightly: false });
    assert.notEqual(mismatch.status, 0);
    assert.doesNotMatch(mismatch.stdout, /Restore OK/);
    const equal = drill({ nightly: false, restored: "FoodLog=38 Profiles=5" });
    assert.equal(equal.status, 0, equal.stderr);
});

for (const fail of ["archive", "restore", "query"]) {
    test("a failed " + fail + " command rejects the drill", () => {
        const result = drill({ fail });
        assert.notEqual(result.status, 0);
        assert.doesNotMatch(result.stdout, /Restore OK/);
        assert.ok(result.calls.some(a => a[0] === "rm" && a.includes("plate-restore-drill")), "temporary container cleanup runs");
    });
}
