const assert = require("node:assert/strict");
const { test } = require("node:test");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

// Runs tools/check.sh with every command stubbed. The first `dotnet test` fails the tests named in `failed` and
// writes them to the result file it was asked for; a retry (a run with --filter) exits with `retryExit`.
function check(mode, failed = [], retryExit = 0, env = {}) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "plate-ci-retry-"));
    const bin = path.join(root, "bin");
    const trace = path.join(root, "trace");
    fs.mkdirSync(bin);
    fs.mkdirSync(path.join(root, "tools"));
    fs.copyFileSync(path.resolve(__dirname, "../../tools/check.sh"), path.join(root, "tools/check.sh"));
    const script = `#!/bin/bash
name=$(basename "$0")
echo "$name $*\${PLATE_E2E_TRACE_DIR:+ trace=$PLATE_E2E_TRACE_DIR}" >> "$TRACE"
[ "$name $1" = "dotnet test" ] || exit 0
dir=""; filter=""
while [ $# -gt 0 ]; do
  case "$1" in --results-directory) dir=$2; shift ;; --filter) filter=$2; shift ;; esac
  shift
done
if [ -n "$filter" ]; then exit "$RETRY_EXIT"; fi
[ -n "$FAILED" ] || exit 0
if [ -n "$dir" ]; then
  {
    echo '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
    IFS=,
    for t in $FAILED; do echo "<UnitTestResult testName=\\"$t(browser: &quot;webkit&quot;)\\" outcome=\\"Failed\\" />"; done
    echo '<UnitTestResult testName="Ns.Steady.Passes" outcome="Passed" /></Results></TestRun>'
  } > "$dir/first.trx"
fi
exit 1
`;
    for (const name of ["docker", "node", "dotnet", "pwsh"]) {
        fs.writeFileSync(path.join(bin, name), script, { mode: 0o755 });
    }
    try {
        const result = spawnSync("/bin/bash", [path.join(root, "tools/check.sh"), mode], {
            env: { ...process.env, PATH: bin + path.delimiter + process.env.PATH, TRACE: trace, FAILED: failed.join(","), RETRY_EXIT: String(retryExit), ...env },
            // Only a guard against a hang (see deploy-gate.test.cjs).
            encoding: "utf8", timeout: 30000,
        });
        assert.ifError(result.error);
        const runs = fs.readFileSync(trace, "utf8").trim().split("\n").filter((c) => c.startsWith("dotnet test "));
        return { status: result.status, runs };
    } finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
}

test("a browser run on CI with nothing failed runs once", () => {
    const result = check("--ci-browser");
    assert.equal(result.status, 0);
    assert.equal(result.runs.length, 1);
});

test("a failed browser test on CI is run once more and may pass", () => {
    const result = check("--ci-browser", ["Ns.Flaky.Loads", "Ns.Flaky.Taps"]);
    assert.equal(result.status, 0);
    assert.equal(result.runs.length, 2);
    assert.ok(result.runs[1].includes("--filter FullyQualifiedName=Ns.Flaky.Loads|FullyQualifiedName=Ns.Flaky.Taps"));
});

test("a browser test that fails again on CI fails the checks", () => {
    const result = check("--ci-browser", ["Ns.Broken.Always"], 1);
    assert.equal(result.status, 1);
    assert.equal(result.runs.length, 2);
});

test("more than five failed browser tests are not retried", () => {
    const result = check("--ci-browser", ["A.B.C1", "A.B.C2", "A.B.C3", "A.B.C4", "A.B.C5", "A.B.C6"]);
    assert.equal(result.status, 1);
    assert.equal(result.runs.length, 1);
});

test("nothing is retried outside the CI browser job", () => {
    for (const mode of ["--ci-core-web", "--browser-deps"]) {
        const result = check(mode, ["Ns.Flaky.Loads"]);
        assert.equal(result.status, 1, mode);
        assert.equal(result.runs.length, 1, mode);
        assert.equal(result.runs.some((c) => c.includes("--filter") || c.includes("--logger")), false, mode);
    }
});

test("only the retry records Playwright traces, next to its results", () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "plate-ci-results-"));
    try {
        const result = check("--ci-browser", ["Ns.Flaky.Loads"], 0, { CHECK_RESULTS_DIR: dir });
        assert.equal(result.status, 0);
        assert.equal(result.runs[0].includes("trace="), false);
        assert.ok(result.runs[0].includes(`--results-directory ${dir}`));
        assert.ok(result.runs[1].includes(`trace=${dir}/traces`));
        assert.ok(result.runs[1].includes("LogFileName=retry.trx"));
    } finally {
        fs.rmSync(dir, { recursive: true, force: true });
    }
});

test("with CHECK_RESULTS_DIR set, every test run writes a result file there", () => {
    const result = check("--ci-core-web", [], 0, { CHECK_RESULTS_DIR: "/ci/results" });
    assert.equal(result.status, 0);
    assert.deepEqual(result.runs.map((c) => /LogFileName=([^ ]+)/.exec(c)?.[1]), ["Plate.Core.Tests.trx", "Plate.Web.Tests.trx"]);
    assert.ok(result.runs.every((c) => c.includes("--results-directory /ci/results")));
});
