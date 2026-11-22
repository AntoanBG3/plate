// tools/perf/deploy-probe.mjs against a local server that goes down and comes back with another version (P17).
const assert = require("node:assert/strict");
const { test } = require("node:test");
const http = require("node:http");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const probePath = path.resolve(__dirname, "../../tools/perf/deploy-probe.mjs");
const probe = () => import(probePath);

/** A sign-in page whose behaviour the test switches: "A" or "B" (a version), "refuse", "503" or "hang". */
async function server() {
    const state = { mode: "A" };
    const srv = http.createServer((req, res) => {
        if (state.mode === "refuse") return req.socket.destroy();
        if (state.mode === "hang") return; // never answers; the probe's timeout ends it
        if (state.mode === "503") {
            res.writeHead(503);
            return res.end("starting");
        }
        res.writeHead(200, { "Content-Type": "text/html" });
        res.end(`<html data-version="${state.mode}"><body>Sign in</body></html>`);
    });
    await new Promise((r) => srv.listen(0, "127.0.0.1", r));
    return { state, url: `http://127.0.0.1:${srv.address().port}/account/login`, close: () => { srv.closeAllConnections(); srv.close(); } };
}

test("a deploy's outage and version change are measured", async () => {
    const { run } = await probe();
    const s = await server();
    const at = (ms, mode) => setTimeout(() => { s.state.mode = mode; }, ms);
    at(300, "refuse");
    at(600, "hang");
    at(900, "503");
    at(1200, "B");
    try {
        const result = await run({ url: s.url, seconds: 1.6, intervalMs: 50, timeoutMs: 120, out: null, ssh: null }, () => {});
        const sum = result.summary;
        assert.deepEqual(sum.versions, ["A", "B"]);
        assert.ok(sum.statuses["200"] > 0 && sum.statuses["0"] > 0 && sum.statuses["503"] > 0, JSON.stringify(sum.statuses));
        assert.ok(result.samples.some((x) => x.error === "timeout"), "a request that never answers is a timeout");
        assert.equal(sum.outages.length, 1, "one outage from the first refusal to the first good answer of B");
        assert.ok(sum.longestOutageSeconds >= 0.7 && sum.longestOutageSeconds <= 1.3, `outage ${sum.longestOutageSeconds} s`);
        assert.equal(sum.stillDown, false);
        assert.ok(sum.ttfbMs.median !== null);
    } finally {
        s.close();
    }
});

test("a server still down at the end says so", async () => {
    const { run } = await probe();
    const s = await server();
    s.state.mode = "503";
    try {
        const result = await run({ url: s.url, seconds: 0.3, intervalMs: 50, timeoutMs: 120, out: null, ssh: null }, () => {});
        assert.equal(result.summary.stillDown, true);
        assert.equal(result.summary.longestOutageSeconds, 0, "an outage without an end has no length yet");
        assert.deepEqual(result.summary.versions, []);
    } finally {
        s.close();
    }
});

test("requests are one at a time and never faster than the interval", async () => {
    const { run } = await probe();
    let open = 0;
    let most = 0;
    const srv = http.createServer((req, res) => {
        open++;
        most = Math.max(most, open);
        setTimeout(() => { open--; res.end('<html data-version="A">'); }, 30);
    });
    await new Promise((r) => srv.listen(0, "127.0.0.1", r));
    try {
        const result = await run({ url: `http://127.0.0.1:${srv.address().port}/`, seconds: 0.5, intervalMs: 100, timeoutMs: 500, out: null, ssh: null }, () => {});
        assert.equal(most, 1);
        assert.ok(result.samples.length <= 6, `${result.samples.length} samples in 0.5 s at 100 ms`);
    } finally {
        srv.close();
    }
});

test("bad arguments are refused, and a real server can't be polled faster than once a second", async () => {
    const { parseArgs } = await probe();
    assert.throws(() => parseArgs(["--interval-ms", "200"]), /only allowed for localhost/);
    assert.throws(() => parseArgs(["--url", "ftp://example.test"]), /http or https/);
    assert.throws(() => parseArgs(["--seconds", "-1"]), /positive/);
    assert.throws(() => parseArgs(["--loud", "yes"]), /unknown/);
    assert.throws(() => parseArgs(["--ssh", "wizard; rm -rf /"]), /host name/);
    assert.equal(parseArgs(["--url", "http://localhost:5130/account/login", "--interval-ms", "50"]).intervalMs, 50);
    assert.equal(parseArgs([]).url, "https://plate.peychev.uk/account/login");

    const cli = spawnSync(process.execPath, [probePath, "--interval-ms", "10"], { encoding: "utf8", timeout: 10000 });
    assert.equal(cli.status, 2);
    assert.match(cli.stderr, /only allowed for localhost/);
});
