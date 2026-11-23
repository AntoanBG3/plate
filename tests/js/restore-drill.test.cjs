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
        'const state = process.env.DRILL_STATE;',
        'const arg = (name) => { const i = a.indexOf(name); return i < 0 ? undefined : a[i + 1]; };',
        'const form = (field) => (a.find(x => x.startsWith(field + "=")) || "").slice(field.length + 1);',
        'if (process.env.DRILL_TOOL === "curl" || a.includes("curlimages/curl:8.14.1")) {',
        '  const url = a[a.length - 1];',
        '  if (a.includes("%{http_code}")) { process.stdout.write(c.appUp === false ? "502" : "200"); process.exit(0); }',
        '  if (url.includes("plate.peychev.uk")) { console.log("HTTP/2 200\\r\\nset-cookie: __Host-gym.af=live; path=/\\r\\n\\r\\n<input name=\\"__RequestVerificationToken\\" type=\\"hidden\\" value=\\"live-token\\" />"); process.exit(0); }',
        '  if (a.includes("--data-urlencode")) {',
        '    const live = form("__RequestVerificationToken") === "live-token";',
        '    if (live && c.newKey) fs.writeFileSync(fs.readFileSync(state, "utf8") + "/key-new.xml", "");',
        '    console.log(live && c.liveAccepted === false ? "HTTP/1.1 400 Bad Request\\r\\n" : "HTTP/1.1 302 Found\\r\\nLocation: /\\r\\nSet-Cookie: __Host-gym.auth=ok; path=/\\r\\n");',
        '    process.exit(0);',
        '  }',
        '  if (url.endsWith("/account/login")) { console.log("HTTP/1.1 200 OK\\r\\nSet-Cookie: __Host-gym.af=drill; path=/\\r\\n\\r\\n<html data-version=\\"abc1234\\"><input name=\\"__RequestVerificationToken\\" type=\\"hidden\\" value=\\"drill-token\\" />"); process.exit(0); }',
        '  console.log("HTTP/1.1 200 OK\\r\\n"); process.exit(0);',
        '}',
        'if (a[0] === "run" && a.includes("plate-app:latest")) { fs.writeFileSync(state, arg("-v").split(":")[0]); }',
        'if (a[0] === "run" && a.some(x => x.includes("chown -R 1000:1000 /out"))) { fs.writeFileSync(a.find(x => x.endsWith(":/out")).split(":")[0] + "/key-1.xml", ""); }',
        'if (a[0] === "logs") { console.log("fail: something"); process.exit(0); }',
        'if (a.includes("create-user")) { process.stdin.resume(); process.stdin.on("end", () => process.exit(0)); }',
        'else if (a.includes("pg_restore")) { process.stdin.resume(); process.stdin.on("end", () => process.exit(c.fail === "restore" ? 7 : 0)); }',
        'else if (a.includes("readlink")) console.log("synthetic-nightly");',
        'else if (a.includes("cat") || a.some(x => x.includes("pg_dump"))) { if (c.fail === "archive") process.exit(8); console.log("synthetic archive"); }',
        'else if (a.includes("psql") || a.some(x => x.includes("psql"))) {',
        '  if (c.fail === "query") process.exit(9);',
        '  const live = a.includes("plate-db"), sql = a[a.length - 1];',
        '  if (sql.includes("AspNetUsers")) { console.log("6"); process.exit(0); }',
        '  console.log(sql.includes("information_schema.columns") ? (live ? c.liveSchema : c.restoredSchema)',
        '    : sql.includes("query_to_xml") ? (live ? c.live : c.restored)',
        '    : (live ? c.liveTables : c.restoredTables));',
        '}',
    ].join("\n");
    fs.writeFileSync(path.join(root, "docker"), mock, { mode: 0o755 });
    fs.writeFileSync(path.join(root, "sleep"), "#!/bin/sh\nexit 0\n", { mode: 0o755 });
    // The host's curl (the live login page) runs the same mock.
    fs.writeFileSync(path.join(root, "curl"), "#!/bin/sh\nDRILL_TOOL=curl exec \"$(dirname \"$0\")/docker\" \"$@\"\n", { mode: 0o755 });
    try {
        const args = [...(config.nightly ? ["nightly"] : []), ...(config.app ? ["--app"] : [])];
        const result = spawnSync("bash", [path.resolve(__dirname, "../../tools/restore-drill.sh"), ...args], {
            env: { ...process.env, PATH: root + path.delimiter + process.env.PATH,
                DRILL_CONFIG: JSON.stringify(config), DRILL_TRACE: trace, DRILL_STATE: path.join(root, "state") },
            // Only a guard against a hang. Waiting for an app that never starts makes 60 mocked docker calls, which took
            // just over 10 s while the whole JS suite ran at once on 2026-11-23.
            encoding: "utf8", timeout: 30000,
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

test("the app stage signs in on the restored copy with the backed-up keys", () => {
    const result = drill({ app: true });
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Restore OK/);
    assert.match(result.stdout, /accepts antiforgery data made by the live app/);
    assert.match(result.stdout, /App OK/);
    const app = result.calls.find(a => a[0] === "run" && a.includes("plate-app:latest"));
    assert.ok(app.includes("plate-drill-net") && !app.some(x => x.includes("tunnel")), "the app runs on the drill network only");
    assert.ok(!app.some(x => /OpenRouter|Telegram/.test(x)), "no OpenRouter or Telegram settings reach the drill app");
    assert.ok(result.calls.some(a => a[0] === "network" && a[1] === "create" && a.includes("--internal")), "the drill network is internal");
});

for (const [name, values, message] of [
    ["the app doesn't start", { appUp: false }, /didn't start/],
    ["the backed-up keys can't read the live app's data", { liveAccepted: false }, /backed-up keys don't match/],
    ["the app writes a new key", { newKey: true }, /wrote a new key/],
]) {
    test("the app stage fails when " + name, () => {
        const result = drill({ app: true, ...values });
        assert.notEqual(result.status, 0);
        assert.match(result.stderr, message);
        assert.doesNotMatch(result.stdout, /App OK/);
        assert.ok(result.calls.some(a => a[0] === "rm" && a.includes("plate-drill-app") && a.includes("plate-restore-drill")), "both containers are removed");
        assert.ok(result.calls.some(a => a[0] === "network" && a[1] === "rm" && a.includes("plate-drill-net")), "the drill network is removed");
    });
}

test("the app stage needs the nightly mode", () => {
    const result = drill({ app: true, nightly: false, restored: "FoodLog=38 Profiles=5" });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /needs the nightly mode/);
});

test("the nightly dump is read where the absolute latest link resolves", () => {
    const result = drill();
    const read = result.calls.find(a => a.includes("cat"));
    assert.ok(read.includes("/mnt/storage:/mnt/storage:ro"), "storage is mounted at its own path");
    assert.ok(read.includes("/mnt/storage/backups/latest/plate-db.dump"));
});
