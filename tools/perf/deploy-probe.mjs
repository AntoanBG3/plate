#!/usr/bin/env node
// Watches the sign-in page through a deploy and reports how long Plate was unavailable (P17). Observation only:
// one anonymous GET at a time, at most one a second against anything but this machine, no cookies, no sign-in,
// and it never restarts or changes anything. Run it in a second terminal, then deploy as usual:
//
//   node tools/perf/deploy-probe.mjs --out /private/tmp/deploy-probe.json --ssh wizard
//   tools/deploy.sh
//
// Stop it with Ctrl-C after the deploy (or give --seconds). It prints a summary and writes every sample to --out.
// --ssh <host> also records plate-app's and plate-db's start times before and after, read with `docker inspect`.
// Exit codes: 0 when it observed (outages are data, not errors), 2 for bad arguments, 1 when it couldn't run.
import { writeFileSync } from "node:fs";
import { performance } from "node:perf_hooks";
import { execFileSync } from "node:child_process";

const usage = "usage: node tools/perf/deploy-probe.mjs [--url <sign-in page>] [--seconds <n>] [--interval-ms <n>] [--timeout-ms <n>] [--out <file>] [--ssh <host>]";

export function parseArgs(argv) {
    const options = { url: "https://plate.peychev.uk/account/login", seconds: 600, intervalMs: 1000, timeoutMs: 10000, out: null, ssh: null };
    const names = { "--url": "url", "--seconds": "seconds", "--interval-ms": "intervalMs", "--timeout-ms": "timeoutMs", "--out": "out", "--ssh": "ssh" };
    for (let i = 0; i < argv.length; i += 2) {
        const name = names[argv[i]];
        if (!name || argv[i + 1] === undefined) throw new Error(`unknown or incomplete option ${argv[i]}\n${usage}`);
        options[name] = ["seconds", "intervalMs", "timeoutMs"].includes(name) ? Number(argv[i + 1]) : argv[i + 1];
    }
    let url;
    try {
        url = new URL(options.url);
    } catch {
        throw new Error(`not a URL: ${options.url}`);
    }
    if (!["http:", "https:"].includes(url.protocol)) throw new Error("the URL must be http or https");
    for (const n of ["seconds", "intervalMs", "timeoutMs"]) {
        if (!Number.isFinite(options[n]) || options[n] <= 0) throw new Error(`--${n.replace(/[A-Z]/g, (c) => "-" + c.toLowerCase())} must be a positive number`);
    }
    // Faster than one a second only against this machine (the tests); never load a real server.
    const local = ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname);
    if (!local && options.intervalMs < 1000) throw new Error("--interval-ms below 1000 is only allowed for localhost");
    if (options.ssh !== null && !/^[\w.-]+$/.test(options.ssh)) throw new Error("--ssh takes a host name from ~/.ssh/config");
    return options;
}

/** One sample: when, status (0 for no answer), time to headers, the served version, or why it failed. */
export async function sample(url, timeoutMs) {
    const at = new Date().toISOString();
    const begin = performance.now();
    try {
        const response = await fetch(url, { redirect: "manual", signal: AbortSignal.timeout(timeoutMs), headers: { "Cache-Control": "no-cache" } });
        const ttfbMs = Math.round(performance.now() - begin);
        const html = await response.text();
        const version = /data-version="([^"]*)"/.exec(html)?.[1] ?? null;
        return { at, status: response.status, ttfbMs, totalMs: Math.round(performance.now() - begin), version };
    } catch (e) {
        const timedOut = e.name === "TimeoutError" || e.name === "AbortError";
        return { at, status: 0, ttfbMs: null, totalMs: Math.round(performance.now() - begin), version: null, error: timedOut ? "timeout" : (e.cause?.code ?? e.message) };
    }
}

/**
 * The longest stretch without a 200 (from the first failed sample to the next good one), every version seen in order,
 * and the time-to-first-byte spread of the good samples.
 */
export function summarize(samples) {
    const good = (s) => s.status === 200;
    let longest = null;
    let start = null;
    const outages = [];
    for (const s of samples) {
        if (!good(s) && start === null) start = s;
        if (good(s) && start !== null) {
            const outage = { from: start.at, to: s.at, seconds: (Date.parse(s.at) - Date.parse(start.at)) / 1000 };
            outages.push(outage);
            if (!longest || outage.seconds > longest.seconds) longest = outage;
            start = null;
        }
    }
    if (start !== null) outages.push({ from: start.at, to: null, seconds: null });
    const versions = [];
    for (const s of samples) {
        if (s.version && versions.at(-1) !== s.version) versions.push(s.version);
    }
    const ttfb = samples.filter(good).map((s) => s.ttfbMs).sort((a, b) => a - b);
    const rank = (p) => (ttfb.length ? ttfb[Math.max(0, Math.ceil(p * ttfb.length) - 1)] : null);
    return {
        samples: samples.length,
        failed: samples.filter((s) => !good(s)).length,
        statuses: Object.fromEntries(Object.entries(samples.reduce((m, s) => ({ ...m, [s.status]: (m[s.status] ?? 0) + 1 }), {}))),
        outages,
        longestOutageSeconds: longest?.seconds ?? 0,
        stillDown: start !== null,
        versions,
        ttfbMs: { median: rank(0.5), p95: rank(0.95), max: ttfb.at(-1) ?? null },
    };
}

function containers(host) {
    try {
        const out = execFileSync("ssh", [host, "docker", "inspect", "-f", "'{{.Name}} {{.State.StartedAt}} {{.State.Status}}'", "plate-app", "plate-db"], { encoding: "utf8", timeout: 20000 });
        return out.trim().split("\n");
    } catch (e) {
        return [`could not read: ${e.message.split("\n")[0]}`];
    }
}

export async function run(options, log = console.log, stop = { requested: false }) {
    const samples = [];
    const before = options.ssh ? containers(options.ssh) : null;
    const end = performance.now() + options.seconds * 1000;
    while (!stop.requested && performance.now() < end) {
        const started = performance.now();
        const s = await sample(options.url, options.timeoutMs);
        samples.push(s);
        log(`${s.at} ${s.status || "-"} ${s.ttfbMs ?? "-"} ms ${s.version ?? ""}${s.error ? " " + s.error : ""}`);
        // Sequential: the next request starts an interval after this one started, or at once if this one took longer.
        const wait = options.intervalMs - (performance.now() - started);
        if (wait > 0) await new Promise((r) => setTimeout(r, wait));
    }
    const result = { url: options.url, intervalMs: options.intervalMs, containersBefore: before, containersAfter: options.ssh ? containers(options.ssh) : null, summary: summarize(samples), samples };
    if (options.out) writeFileSync(options.out, JSON.stringify(result, null, 2) + "\n");
    return result;
}

if (import.meta.url === `file://${process.argv[1]}`) {
    let options;
    try {
        options = parseArgs(process.argv.slice(2));
    } catch (e) {
        console.error(e.message);
        process.exit(2);
    }
    const stop = { requested: false };
    process.on("SIGINT", () => { stop.requested = true; });
    try {
        const result = await run(options, console.log, stop);
        const s = result.summary;
        console.log(`\n${s.samples} samples, ${s.failed} without a 200; longest outage ${s.longestOutageSeconds} s${s.stillDown ? ", and still down at the end" : ""}`);
        console.log(`versions: ${s.versions.join(" -> ") || "none seen"}; time to first byte median ${s.ttfbMs.median} ms, p95 ${s.ttfbMs.p95} ms`);
        if (result.containersBefore) console.log(`containers before: ${result.containersBefore.join("; ")}\ncontainers after:  ${result.containersAfter.join("; ")}`);
        if (options.out) console.log(`samples in ${options.out}`);
    } catch (e) {
        console.error(`the probe failed: ${e.message}`);
        process.exit(1);
    }
}
