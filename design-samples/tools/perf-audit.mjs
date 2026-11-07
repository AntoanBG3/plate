// Run: node design-samples/tools/perf-audit.mjs http://localhost:5130 > tools/perf/results/browser.json
// Requires playwright-core in this directory's node_modules and installed Chrome. LOCAL synthetic data only.
// Optional PERF_EMAIL/PERF_PASSWORD, PERF_RUNS (>=5), PERF_RTTS=100,400, PERF_CPU=4, PERF_FIXTURE label.
// Optional untimed cache diagnostic: PERF_DIAGNOSTIC=1 PERF_RTTS=0 PERF_CPU=1 (writes proxy-only worker reply hook).
// Reseed the clean synthetic fixture before running; do not measure a day filled by prior POST load tests.
// HTTPS upstream requires scratch PERF_TLS_DIR (default /private/tmp/repa-perf-tls) with key.pem and cert.pem.
// A loopback proxy changes ONLY the worker's cache name to model a deploy; no app or source files are changed.
import { chromium } from "playwright-core";
import http from "node:http";
import https from "node:https";
import fs from "node:fs";
import { performance } from "node:perf_hooks";

const upstream = new URL(process.argv[2] || "http://localhost:5130");
if (!["localhost", "127.0.0.1", "[::1]"].includes(upstream.hostname)) throw new Error("Local synthetic server required");
const runs = Math.max(5, Number(process.env.PERF_RUNS || 5));
const rtts = (process.env.PERF_RTTS || "100,400").split(",").map(Number);
const cpu = Number(process.env.PERF_CPU || 4);
const email = process.env.PERF_EMAIL || "demo@example.test";
const password = process.env.PERF_PASSWORD || "demo-password-123";
const diagnostic = process.env.PERF_DIAGNOSTIC === "1";
let revision = "initial";
let requests = [];
let proxyLatencyMs = 0;
let nextNetworkSlotMs = 0;
const tlsDir = process.env.PERF_TLS_DIR || "/private/tmp/repa-perf-tls";
const serve = upstream.protocol === "https:" ? (handler) => https.createServer({ key: fs.readFileSync(`${tlsDir}/key.pem`), cert: fs.readFileSync(`${tlsDir}/cert.pem`) }, handler) : (handler) => http.createServer(handler);
const proxy = serve((req, res) => {
    const begin = performance.now();
    const target = new URL(req.url, upstream);
    const headers = { ...req.headers, host: upstream.host, "accept-encoding": "identity" };
    if (target.pathname === "/sw.js") {
        delete headers["if-none-match"];
        delete headers["if-modified-since"];
    }
    const transport = upstream.protocol === "https:" ? https : http;
    const forward = transport.request(target, { method: req.method, headers, rejectUnauthorized: false }, (answer) => {
        const chunks = [];
        answer.on("data", (chunk) => chunks.push(chunk));
        answer.on("end", () => {
            let body = Buffer.concat(chunks);
            const outgoing = { ...answer.headers };
            if (target.pathname === "/sw.js") {
                body = Buffer.from(body.toString().replace(/const CACHE = "([^"]+)"/, `const CACHE = "$1-audit-${revision}"`));
                if (diagnostic) body = Buffer.concat([body, Buffer.from(`\nself.addEventListener("message", (event) => { if (event.data === "__perf_worker_cache" && event.ports[0]) { caches.keys().then((keys) => event.ports[0].postMessage({ cache: CACHE, keys })); } });\n`)]);
                delete outgoing.etag;
                delete outgoing["last-modified"];
                outgoing["cache-control"] = "no-store";
            }
            if (outgoing.location?.startsWith(upstream.origin)) outgoing.location = outgoing.location.replace(upstream.origin, origin);
            outgoing["content-length"] = body.length;
            delete outgoing["transfer-encoding"];
            // Apply the profile to every target, including the worker's own fetches (page CDP throttling alone does not).
            const finishAt = Math.max(performance.now() + proxyLatencyMs, nextNetworkSlotMs) + body.length / 1500000 * 1000;
            nextNetworkSlotMs = finishAt;
            setTimeout(() => {
                res.writeHead(answer.statusCode, outgoing);
                res.end(body);
                requests.push({ path: target.pathname, bytes: body.length, status: answer.statusCode, ms: performance.now() - begin,
                    conditionalRequest: !!(headers["if-none-match"] || headers["if-modified-since"]),
                    requestCacheControl: headers["cache-control"] || null, requestPragma: headers.pragma || null,
                    responseCacheControl: outgoing["cache-control"] || null, responseEtag: outgoing.etag || null,
                    responseEncoding: outgoing["content-encoding"] || "identity" });
            }, Math.max(0, finishAt - performance.now()));
        });
    });
    forward.on("error", (err) => { res.writeHead(502); res.end(err.message); });
    req.pipe(forward);
});
await new Promise((resolve) => proxy.listen(0, "127.0.0.1", resolve));
const origin = `${upstream.protocol}//127.0.0.1:${proxy.address().port}`;
const browser = await chromium.launch({ channel: "chrome", args: ["--ignore-certificate-errors"] });
const result = { diagnostic, fixture: process.env.PERF_FIXTURE || "Not supplied; record synthetic dataset counts alongside these results", generatedUtc: new Date().toISOString(), upstream: upstream.href, browser: browser.version(), viewport: "390x844", cpuSlowdown: cpu, downloadBytesPerSecond: 1500000, networkNote: "Loopback proxy applies 100/400ms response delay and shared 1.5MB/s download schedule to page AND service-worker fetches. Complete bodies are delivered at scheduled completion (not streamed). Identity encoding; ordinary asset conditional validation preserved, only the synthetic worker source bypasses it. Proxy counts HTTP body bytes (304 has 0); cache sizes are decoded bytes, not production compressed transfer. Upload bandwidth is not throttled.", runs, samples: [] };
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const netSnapshot = () => requests.length;
const networkSince = (n) => {
    const rows = requests.slice(n);
    return { requests: rows.length, bytes: rows.reduce((sum, r) => sum + r.bytes, 0), staticRequests: rows.filter((r) => /^\/(css|js|lib)\//.test(r.path)).length,
        statuses: Object.fromEntries([...new Set(rows.map((r) => r.status))].map((status) => [status, rows.filter((r) => r.status === status).length])),
        conditionalRequests: rows.filter((r) => r.conditionalRequest).length,
        cacheHeaderSamples: rows.filter((r) => ["/sw.js", "/css/app.css", "/lib/barcode-detector/zxing_reader.wasm", "/lib/chartjs/chart.umd.min.js"].includes(r.path)).map(({path, status, conditionalRequest, requestCacheControl, requestPragma, responseCacheControl, responseEtag, responseEncoding}) => ({path, status, conditionalRequest, requestCacheControl, requestPragma, responseCacheControl, responseEtag, responseEncoding})),
        failures: rows.filter((r) => r.status >= 400), paths: rows.map((r) => r.path) };
};
const cacheSnapshot = (page) => page.evaluate(async () => {
    const names = await caches.keys();
    const records = [];
    for (const name of names) {
        const cache = await caches.open(name);
        const keys = await cache.keys();
        let bytes = 0;
        for (const key of keys) bytes += (await (await cache.match(key)).arrayBuffer()).byteLength;
        records.push({ name, entries: keys.length, decodedBytes: bytes, versionedEntries: keys.filter((k) => new URL(k.url).search).length });
    }
    return records;
});
async function settled(page) {
    await page.waitForFunction(() => !document.documentElement.dataset.nav && document.getAnimations().every((a) => a.playState !== "running" || !String(a.effect?.pseudoElement || "").includes("view-transition")), null, { polling: "raf" });
}
async function navigate(page, href, label) {
    await page.evaluate(() => { window.__auditLongTasks.length = 0; });
    const n = netSnapshot();
    const t0 = performance.now();
    await page.tap(`.tabbar a[href="${href}"]`);
    await page.waitForSelector(`.tabbar a[href="${href}"].is-active`);
    const feedbackMs = performance.now() - t0;
    await page.waitForFunction((tab) => document.documentElement.dataset.tab === tab, href.slice(1), { polling: "raf" });
    const contentMs = performance.now() - t0;
    await settled(page);
    return { journey: label, feedbackMs, contentMs, settledMs: performance.now() - t0, network: networkSince(n), longTasks: await page.evaluate(() => window.__auditLongTasks.splice(0)) };
}
try {
    // Bootstrap only cookies outside measurement; new contexts do not inherit HTTP caches or service workers.
    const authContext = await browser.newContext({ ignoreHTTPSErrors: true, serviceWorkers: "block" });
    const authPage = await authContext.newPage();
    await authPage.goto(`${origin}/account/login`);
    await authPage.fill('input[name="email"]', email);
    await authPage.fill('input[name="password"]', password);
    await Promise.all([authPage.waitForURL(/\/today/), authPage.click('button[type="submit"]')]);
    const authentication = await authContext.storageState();
    await authContext.close();
    for (const latencyMs of rtts) for (let run = 1; run <= runs; run++) {
        proxyLatencyMs = latencyMs;
        nextNetworkSlotMs = performance.now();
        revision = `${latencyMs}-${run}-old`;
        const context = await browser.newContext({ storageState: authentication, viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, hasTouch: true, isMobile: true, ignoreHTTPSErrors: true, serviceWorkers: "allow" });
        const page = await context.newPage();
        page.setDefaultTimeout(60000);
        await page.addInitScript(() => {
            window.__auditLongTasks = [];
            new PerformanceObserver((list) => window.__auditLongTasks.push(...list.getEntries().map((e) => ({ startTime: e.startTime, duration: e.duration })))).observe({ type: "longtask", buffered: true });
        });
        const cdp = await context.newCDPSession(page);
        await cdp.send("Emulation.setCPUThrottlingRate", { rate: cpu });
        const sample = { latencyMs, run };
        let n = netSnapshot();
        let t0 = performance.now();
        await page.goto(`${origin}/today`, { waitUntil: "load" });
        if (new URL(page.url()).pathname !== "/today") throw new Error(`Synthetic auth failed: ${page.url()}`);
        sample.coldAppLoadMs = performance.now() - t0;
        await page.waitForFunction(async () => (await navigator.serviceWorker.getRegistration())?.active?.state === "activated");
        await page.waitForFunction(() => navigator.serviceWorker.controller !== null);
        sample.installReadyMs = performance.now() - t0;
        sample.installNetwork = networkSince(n);
        sample.installLongTasks = await page.evaluate(() => window.__auditLongTasks.splice(0));
        sample.installCache = await cacheSnapshot(page);
        sample.applicationVersion = await page.evaluate(() => document.documentElement.dataset.version);
        await settled(page);
        sample.firstModuleNavigation = [];
        for (const [href, label] of [["/train", "today-train"], ["/food", "train-food"], ["/progress", "food-progress"], ["/today", "progress-today"]]) sample.firstModuleNavigation.push(await navigate(page, href, label));
        sample.warmNavigation = [];
        for (const [href, label] of [["/train", "today-train"], ["/food", "train-food"], ["/progress", "food-progress"], ["/today", "progress-today"]]) sample.warmNavigation.push(await navigate(page, href, label));
        sample.afterNavigationCache = await cacheSnapshot(page);
        revision = `${latencyMs}-${run}-new`;
        await page.evaluate(() => { window.__auditLongTasks.length = 0; });
        n = netSnapshot();
        t0 = performance.now();
        await page.evaluate(async () => {
            const registration = await navigator.serviceWorker.getRegistration();
            await Promise.race([
                Promise.all([
                    new Promise((resolve) => navigator.serviceWorker.addEventListener("controllerchange", resolve, { once: true })),
                    registration.update(),
                ]),
                new Promise((_, reject) => setTimeout(() => reject(new Error("Worker update did not activate within 60s")), 60000)),
            ]);
        });
        // controllerchange may fire while activate.waitUntil still deletes old caches; wait for completion.
        await page.waitForFunction(async () => {
            const registration = await navigator.serviceWorker.getRegistration();
            return registration?.active?.state === "activated" && !registration.installing && !registration.waiting;
        });
        sample.syntheticDeployWorkerUpdateMs = performance.now() - t0;
        sample.updateNetwork = networkSince(n);
        sample.updateLongTasks = await page.evaluate(() => window.__auditLongTasks.splice(0));
        sample.afterUpdateCache = await cacheSnapshot(page);
        if (diagnostic) {
            const controllerInfo = () => page.evaluate(() => new Promise((resolve, reject) => {
                const channel = new MessageChannel();
                const timeout = setTimeout(() => reject(new Error("Worker diagnostic reply timed out")), 5000);
                channel.port1.onmessage = (event) => { clearTimeout(timeout); resolve(event.data); };
                navigator.serviceWorker.controller.postMessage("__perf_worker_cache", [channel.port2]);
            }));
            sample.workerDiagnostic = { immediate: await controllerInfo() };
            await sleep(1000);
            sample.workerDiagnostic.afterOneSecond = await controllerInfo();
            sample.workerDiagnostic.delayedCaches = await cacheSnapshot(page);
            sample.workerDiagnostic.registration = await page.evaluate(async () => {
                const r = await navigator.serviceWorker.getRegistration();
                return { activeState: r.active?.state, installingState: r.installing?.state, waitingState: r.waiting?.state, controlledState: navigator.serviceWorker.controller?.state };
            });
        }
        sample.navigationRace = await page.evaluate(async () => {
            const originalFetch = window.fetch;
            window.fetch = async (...args) => {
                const response = await originalFetch(...args);
                if (new URL(String(args[0]), location.href).pathname === "/train") await new Promise((resolve) => setTimeout(resolve, 1000));
                return response;
            };
            try {
                const first = window.gymNav("/train");
                await new Promise((resolve) => setTimeout(resolve, 50));
                const second = window.gymNav("/food");
                await second;
                const afterLatestResponse = document.documentElement.dataset.tab;
                await first;
                return { firstRequested: "train", lastRequested: "food", injectedFirstResponseDelayMs: 1000, afterLatestResponse, finalTab: document.documentElement.dataset.tab, finalPath: location.pathname };
            } finally {
                window.fetch = originalFetch;
            }
        });
        await settled(page);
        // Fault-injection and cache-inspection work is intentionally excluded from timed-phase long-task totals.
        result.samples.push(sample);
        process.stderr.write(`browser audit ${latencyMs}ms run ${run}/${runs} complete\n`);
        await context.close();
        await sleep(100);
    }
} catch (error) {
    result.error = String(error.stack || error);
    process.stderr.write(`${error}\n`);
    process.exitCode = 1;
} finally {
    console.log(JSON.stringify(result, null, 2));
    await browser.close();
    await new Promise((resolve) => proxy.close(resolve));
}
