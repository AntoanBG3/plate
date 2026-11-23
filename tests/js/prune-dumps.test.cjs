// tools/prune-predeploy-dumps.sh against a temporary home folder (P13).
const assert = require("node:assert/strict");
const { test } = require("node:test");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const script = path.resolve(__dirname, "../../tools/prune-predeploy-dumps.sh");
const DAY = 86400;

/** A home with the given files, each { name, daysOld }; docker's pg_restore check passes unless told otherwise. */
function home(files, { badArchive = false } = {}) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "plate-prune-"));
    const dir = path.join(root, "home");
    const bin = path.join(root, "bin");
    fs.mkdirSync(dir);
    fs.mkdirSync(bin);
    const now = Date.now() / 1000;
    for (const f of files) {
        const p = path.join(dir, f.name);
        if (f.dir) fs.mkdirSync(p);
        else fs.writeFileSync(p, "dump " + f.name);
        fs.utimesSync(p, now - f.daysOld * DAY, now - f.daysOld * DAY);
    }
    fs.writeFileSync(path.join(bin, "docker"), `#!/bin/sh\necho "$@" >> "${root}/docker.log"\ncat > /dev/null\nexit ${badArchive ? 1 : 0}\n`, { mode: 0o755 });
    const run = (...args) => spawnSync("bash", [script, ...args], {
        env: { ...process.env, PATH: bin + path.delimiter + process.env.PATH, PRUNE_HOME: dir }, encoding: "utf8", timeout: 10000,
    });
    const names = (sub = "") => fs.existsSync(path.join(dir, sub)) ? fs.readdirSync(path.join(dir, sub)).sort() : [];
    return { run, names, dir, cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}

const dumps = (n, firstDaysOld, prefix = "plate-pre") =>
    Array.from({ length: n }, (_, i) => ({ name: `${prefix}-v${i}-2026.dump`, daysOld: firstDaysOld + i * 7 }));

test("a dry run lists what would move and changes nothing", () => {
    const h = home(dumps(8, 1));
    try {
        const before = h.names();
        const r = h.run();
        assert.equal(r.status, 0, r.stderr);
        assert.deepEqual(h.names(), before);
        assert.match(r.stdout, /would move: plate-pre-v5-2026\.dump/);
        assert.match(r.stdout, /Dry run/);
    } finally { h.cleanup(); }
});

test("the newest five and those from the last 30 days stay; older ones go to the archive", () => {
    // Ages 1, 8, 15, 22, 29, 36, 43, 50 days: the newest five, then 36+ days old move.
    const h = home([...dumps(8, 1), { name: "gymtracker-pre-phase9-20261005-1543.dump", daysOld: 49 }]);
    try {
        const r = h.run("--apply");
        assert.equal(r.status, 0, r.stderr);
        assert.deepEqual(h.names("predeploy-dumps-archive"),
            ["gymtracker-pre-phase9-20261005-1543.dump", "plate-pre-v5-2026.dump", "plate-pre-v6-2026.dump", "plate-pre-v7-2026.dump"]);
        assert.deepEqual(h.names().filter((n) => n.endsWith(".dump")),
            ["plate-pre-v0-2026.dump", "plate-pre-v1-2026.dump", "plate-pre-v2-2026.dump", "plate-pre-v3-2026.dump", "plate-pre-v4-2026.dump"]);
    } finally { h.cleanup(); }
});

test("dumps from the last 30 days stay even beyond the newest five", () => {
    const h = home(dumps(9, 1).map((d, i) => ({ ...d, daysOld: 1 + i * 3 }))); // all within 25 days
    try {
        const r = h.run("--apply");
        assert.equal(r.status, 0, r.stderr);
        assert.deepEqual(h.names("predeploy-dumps-archive"), []);
        assert.match(r.stdout, /Nothing to move/);
    } finally { h.cleanup(); }
});

test("only plate-pre and gymtracker-pre dumps directly in the home folder count", () => {
    const h = home([
        ...dumps(5, 1),
        { name: "plate-db.dump", daysOld: 90 },
        { name: "plate-pre-old.dump.tmp", daysOld: 90 },
        { name: "notes plate-pre-x.dump", daysOld: 90 },
        { name: "plate-pre-folder.dump", daysOld: 90, dir: true },
        { name: "plate-pre-with space-2026.dump", daysOld: 60 },
    ]);
    try {
        const r = h.run("--apply");
        assert.equal(r.status, 0, r.stderr);
        assert.deepEqual(h.names("predeploy-dumps-archive"), ["plate-pre-with space-2026.dump"]);
        for (const kept of ["plate-db.dump", "plate-pre-old.dump.tmp", "notes plate-pre-x.dump", "plate-pre-folder.dump"]) {
            assert.ok(h.names().includes(kept), kept + " is left alone");
        }
    } finally { h.cleanup(); }
});

test("nothing moves when the newest dump doesn't read", () => {
    const h = home(dumps(8, 1), { badArchive: true });
    try {
        const r = h.run("--apply");
        assert.equal(r.status, 1);
        assert.match(r.stderr, /doesn't read as a Postgres archive/);
        assert.deepEqual(h.names("predeploy-dumps-archive"), []);
    } finally { h.cleanup(); }
});

test("an archived dump with the same name is never overwritten", () => {
    const h = home(dumps(7, 1));
    try {
        fs.mkdirSync(path.join(h.dir, "predeploy-dumps-archive"));
        fs.writeFileSync(path.join(h.dir, "predeploy-dumps-archive", "plate-pre-v6-2026.dump"), "earlier copy");
        const r = h.run("--apply");
        assert.equal(r.status, 0, r.stderr);
        assert.match(r.stdout, /kept \(an archived dump has the same name\): plate-pre-v6-2026\.dump/);
        assert.equal(fs.readFileSync(path.join(h.dir, "predeploy-dumps-archive", "plate-pre-v6-2026.dump"), "utf8"), "earlier copy");
        assert.ok(h.names().includes("plate-pre-v6-2026.dump"));
    } finally { h.cleanup(); }
});

test("an unknown argument is refused", () => {
    const h = home([]);
    try {
        assert.equal(h.run("--force").status, 2);
    } finally { h.cleanup(); }
});
