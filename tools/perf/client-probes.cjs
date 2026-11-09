// Run: node tools/perf/client-probes.cjs > tools/perf/results/client-probes.json
// Deterministic fault injection against unchanged shipped JS; no browser/network/real camera and no app writes.
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const root = path.resolve(__dirname, "../../src/Plate.Web/wwwroot");
const source = (name) => fs.readFileSync(path.join(root, "js", name), "utf8");
const settle = () => new Promise(setImmediate);

async function stalledSetQueue() {
    const original = [{ clientId: "00000000-0000-4000-8000-000000000001", sessionId: 1, sessionExerciseId: 1, weightKg: 60, reps: 10, isWarmup: false, completedAt: "2026-11-05T10:00:00Z" }];
    const store = new Map([["gym.pendingSets.v2.A", JSON.stringify(original)]]);
    const intervals = [], events = {}, requests = [];
    const context = {
        URLSearchParams, Date,
        document: { documentElement: { dataset: { userId: "A" } }, body: { addEventListener() {} }, addEventListener() {}, getElementById: () => null, querySelector: () => null },
        localStorage: { getItem: (k) => store.get(k) ?? null, setItem: (k, v) => store.set(k, v), removeItem: (k) => store.delete(k) },
        navigator: { onLine: true }, t: (s) => s,
        setInterval: (fn) => intervals.push(fn), setTimeout() {},
        window: { addEventListener: (name, fn) => { (events[name] ??= []).push(fn); }, gymModule() {}, gymToken: () => "synthetic-token" },
        fetch: (url, options) => { requests.push({ url, hasAbortSignal: !!options.signal }); return new Promise(() => {}); },
    };
    vm.createContext(context);
    vm.runInContext(source("queue.js"), context);
    vm.runInContext(source("session.js"), context);
    await settle();
    // Four 15-second retry callbacks and a reconnect occur while the browser's fetch remains pending.
    for (let i = 0; i < 4; i++) { intervals[0](); await settle(); }
    events.online.forEach((fn) => fn());
    await settle();
    return { scenario: "fetch never resolves; four retry callbacks plus online event", simulatedRetryWindowMs: 60000, requests, retainedPendingSets: JSON.parse(store.get("gym.pendingSets.v2.A")).length, interpretation: "No app deadline: later flushes cannot retry or send subsequent sets until browser fetch settles. Data remains durable." };
}
async function lateCameraPermission() {
    const events = {}, sheetEvents = {};
    let factory, resolvePermission, stoppedTracks = 0, startedIntervals = 0, videoPlayed = 0;
    const video = { play: async () => { videoPlayed++; } };
    const rootElement = {
        querySelector: (selector) => selector === "video" ? { replaceWith() {} } : selector === "[data-photo]" ? { addEventListener() {} } : {},
        addEventListener: (name, fn) => { sheetEvents[name] = fn; },
    };
    const context = {
        document: { addEventListener: (name, fn) => { (events[name] ??= []).push(fn); }, removeEventListener() {}, createElement: () => video },
        navigator: { onLine: true, mediaDevices: { getUserMedia: () => new Promise((resolve) => { resolvePermission = resolve; }) } },
        setInterval: () => { startedIntervals++; return startedIntervals; }, clearInterval() {}, t: (s) => s,
        window: { gymModule: (_name, fn) => { factory = fn; }, BarcodeDetectionAPI: { prepareZXingModule() {}, BarcodeDetector: class {} }, gymSheet: { show() {} }, addEventListener() {}, removeEventListener() {} },
    };
    vm.createContext(context);
    vm.runInContext(source("scanner.js"), context);
    const cleanup = factory(rootElement);
    const button = { matches: () => false };
    events.click.forEach((fn) => fn({ target: { closest: (selector) => selector.includes("data-start") ? button : null } }));
    // User closes the sheet and leaves the page before camera permission/device startup resolves.
    sheetEvents["sheet:close"]();
    cleanup();
    resolvePermission({ getTracks: () => [{ stop: () => { stoppedTracks++; } }] });
    await settle(); await settle();
    return { scenario: "getUserMedia resolves after sheet close and module cleanup", stoppedTracks, videoPlayed, startedIntervals, interpretation: "Late stream is started after teardown and no remaining cleanup owns it; capture lifetime after this point depends on browser/document lifecycle." };
}
async function outOfOrderNavigation() {
    const pending = {};
    const location = { href: "http://localhost/today", origin: "http://localhost", pathname: "/today", search: "" };
    const element = (values = {}) => ({
        values, dataset: {}, classList: { add() {}, remove() {} },
        get attributes() { return Object.entries(this.values).map(([name, value]) => ({ name, value })); },
        hasAttribute(name) { return name in this.values; },
        removeAttribute(name) { delete this.values[name]; },
        setAttribute(name, value) { this.values[name] = value; if (name.startsWith("data-")) this.dataset[name.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase())] = value; },
        replaceWith() {}, focus() {},
    });
    function htmlRoot(tab) {
        const el = element();
        for (const [name, value] of Object.entries({ "data-shell": "en-in", "data-user-id": "A", "data-version": "1", "data-tab": tab, "data-depth": "0" })) el.setAttribute(name, value);
        return el;
    }
    const root = htmlRoot("today"), main = element(), topbar = element(), overlay = element();
    const shell = { replaceWith() {}, querySelector: (s) => s === ".tabbar" ? null : s === "#main" ? main : topbar };
    const document = {
        documentElement: root, body: element(), scripts: [],
        querySelector: (s) => s === ".app-scale" ? shell : null,
        querySelectorAll: () => [], getElementById: (id) => id === "main" ? main : overlay,
        addEventListener() {}, dispatchEvent() {}, head: { appendChild() {} },
    };
    class DOMParser {
        parseFromString(tab) { return { documentElement: htmlRoot(tab), body: element(), title: tab, querySelector: () => shell, querySelectorAll: () => [], getElementById: () => overlay }; }
    }
    const history = { state: {}, replaceState() {}, pushState(_s, _t, url) { location.href = url; location.pathname = new URL(url).pathname; } };
    const fetch = (url) => new Promise((resolve) => { pending[new URL(url).pathname] = resolve; });
    const context = { URL, location, history, document, DOMParser, fetch, performance: { now: () => 0 }, setTimeout() {}, clearTimeout() {}, CustomEvent: class {}, htmx: { process() {} },
        window: { fetch, DOMParser, htmx: true, matchMedia: () => ({ matches: true }), addEventListener() {}, scrollTo() {} },
    };
    vm.createContext(context);
    vm.runInContext(source("nav.js"), context);
    const first = context.window.gymNav("/train");
    const second = context.window.gymNav("/food");
    const response = (tab) => ({ url: "http://localhost/" + tab, status: 200, headers: { get: () => "text/html" }, text: async () => tab });
    pending["/food"](response("food"));
    await second;
    const afterLatestResponse = root.dataset.tab;
    pending["/train"](response("train"));
    await first;
    return { requestedOrder: ["train", "food"], responseOrder: ["food", "train"], afterLatestResponse, finalTab: root.dataset.tab, finalPath: location.pathname, interpretation: "Earlier slow GET overwrites later chosen page; there is no request generation guard in go(). DOM is stubbed; browser corroboration remains useful." };
}
function precacheInventory() {
    const sw = fs.readFileSync(path.join(root, "sw.js"), "utf8");
    const block = sw.match(/const PRECACHE = \[([\s\S]*?)\];/)[1];
    const paths = ["/offline.html", ...Array.from(block.matchAll(/"([^"\n]+)"/g), (m) => m[1])];
    const assets = paths.map((p) => ({ path: p, bytes: fs.statSync(path.join(root, p)).size }));
    const totalBytes = assets.reduce((sum, a) => sum + a.bytes, 0);
    return { assets: assets.length, totalBytes, wasmBytes: assets.find((a) => a.path.endsWith(".wasm")).bytes, largest: assets.sort((a, b) => b.bytes - a.bytes).slice(0, 6), interpretation: "Decoded filesystem bytes, not compressed network transfer; current worker requests the entire list on each changed cache version; HTTP 304 validation can reuse existing body bytes." };
}
(async () => console.log(JSON.stringify({ generatedUtc: new Date().toISOString(), stalledQueue: await stalledSetQueue(), cameraRace: await lateCameraPermission(), navigationRace: await outOfOrderNavigation(), precache: precacheInventory() }, null, 2)))().catch((err) => { console.error(err); process.exitCode = 1; });
