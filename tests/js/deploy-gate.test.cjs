const assert = require("node:assert/strict");
const { test } = require("node:test");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

function deploy(fail = "", dryRun = false, changeCheckout = false, failMatch = "") {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "gym-deploy-gate-"));
    const bin = path.join(root, "bin");
    const trace = path.join(root, "trace");
    fs.mkdirSync(bin);
    fs.mkdirSync(path.join(root, "tools"));
    for (const name of ["deploy.sh", "check.sh"]) {
        fs.copyFileSync(path.resolve(__dirname, "../../tools", name), path.join(root, "tools", name));
    }
    const script = `#!/bin/bash
name=$(basename "$0")
echo "$name $*" >> "$TRACE"
case "$name $1" in
  "git branch") echo main ;;
  "git rev-parse") echo tested-version ;;
  "git status") if [ -f "$TRACE.changed" ]; then echo ' M changed.cs'; fi ;;
  "curl "*) echo '<html data-version="tested-version">' ;;
esac
if [ "$name $1" = 'dotnet test' ] && [ "$CHANGE_CHECKOUT" = '1' ]; then touch "$TRACE.changed"; fi
if [ "$name $1" = "$FAIL_STAGE" ]; then exit 1; fi
if [ -n "$FAIL_MATCH" ] && case "$name $*" in *"$FAIL_MATCH"*) true ;; *) false ;; esac; then exit 1; fi
`;
    for (const name of ["git", "docker", "node", "dotnet", "ssh", "rsync", "curl"]) {
        fs.writeFileSync(path.join(bin, name), script, { mode: 0o755 });
    }
    try {
        const result = spawnSync("/bin/bash", [path.join(root, "tools/deploy.sh"), ...(dryRun ? ["--dry-run"] : [])], {
            env: { ...process.env, PATH: bin + path.delimiter + process.env.PATH, TRACE: trace, FAIL_STAGE: fail, FAIL_MATCH: failMatch, CHANGE_CHECKOUT: changeCheckout ? "1" : "0" },
            // Only a guard against a hang. macOS checks each freshly written stub on its first run, which took
            // up to 5 s for a whole script on a busy Mac and failed the gate on 2026-11-15.
            encoding: "utf8", timeout: 30000,
        });
        assert.ifError(result.error);
        return { status: result.status, calls: fs.readFileSync(trace, "utf8").trim().split("\n") };
    } finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
}

for (const stage of ["docker info", "node --test", "dotnet restore", "dotnet build", "dotnet test"]) {
    test(`a failed ${stage} blocks all remote deployment operations`, () => {
        const result = deploy(stage);
        assert.equal(result.status, 1);
        assert.equal(result.calls.some((c) => /^(ssh|rsync|curl) /.test(c)), false);
    });
}

test("successful checks finish before the first remote operation", () => {
    const result = deploy();
    assert.equal(result.status, 0);
    const tests = result.calls.findIndex((c) => c.startsWith("dotnet test "));
    const remote = result.calls.findIndex((c) => c.startsWith("ssh "));
    assert.ok(tests >= 0 && remote > tests);
});

test("old pre-deploy dumps are archived only after the new version is served", () => {
    const result = deploy();
    assert.equal(result.status, 0);
    const served = result.calls.findIndex((c) => c.startsWith("curl "));
    const prune = result.calls.findIndex((c) => c.startsWith("ssh ") && c.includes("prune-predeploy-dumps.sh --apply"));
    assert.ok(served >= 0 && prune > served);
});

test("a failed archive step doesn't fail a deploy that worked", () => {
    const result = deploy("", false, false, "prune-predeploy-dumps.sh --apply");
    assert.equal(result.status, 0);
    assert.ok(result.calls.some((c) => c.includes("prune-predeploy-dumps.sh --apply")), "the step ran and failed");
});

test("changes made while checks run block deployment of an untested checkout", () => {
    const result = deploy("", false, true);
    assert.equal(result.status, 1);
    assert.equal(result.calls.some((c) => /^(ssh|rsync|curl) /.test(c)), false);
});

test("a dry run only previews the copy and skips builds and remote mutations", () => {
    const result = deploy("", true);
    assert.equal(result.status, 0);
    assert.equal(result.calls.some((c) => /^(docker|dotnet|node|ssh) /.test(c)), false);
    assert.ok(result.calls.some((c) => c.startsWith("rsync ") && c.includes("--dry-run")));
});
