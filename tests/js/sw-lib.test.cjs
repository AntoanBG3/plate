// The service worker keeps the pinned libraries in a cache of their own across deploys (P12).
// Run: node --test tests/js/sw-lib.test.cjs. After changing a file under wwwroot/lib, bump LIB in sw.js and
// record the new hashes: UPDATE_SW_LIB=1 node --test tests/js/sw-lib.test.cjs
const assert = require("node:assert/strict");
const { test } = require("node:test");
const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "../../src/Plate.Web/wwwroot");
const source = fs.readFileSync(path.join(root, "sw.js"), "utf8");
const hashesFile = path.join(__dirname, "sw-lib.hashes.json");
const ORIGIN = "https://plate.test";

const constant = (name) => source.match(new RegExp(`const ${name} = "([^"]+)"`))[1];

function libHashes() {
    const files = {};
    const walk = (dir) => {
        for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
            const full = path.join(dir, entry.name);
            if (entry.isDirectory()) walk(full);
            else files["/" + path.relative(root, full).split(path.sep).join("/")] = crypto.createHash("sha256").update(fs.readFileSync(full)).digest("hex");
        }
    };
    walk(path.join(root, "lib"));
    return Object.fromEntries(Object.entries(files).sort(([a], [b]) => a.localeCompare(b)));
}

test("a changed library file comes with a new LIB cache name", () => {
    const current = { lib: constant("LIB"), files: libHashes() };
    if (process.env.UPDATE_SW_LIB) fs.writeFileSync(hashesFile, JSON.stringify(current, null, 2) + "\n");
    const recorded = JSON.parse(fs.readFileSync(hashesFile, "utf8"));
    if (recorded.lib === current.lib) {
        assert.deepEqual(current.files, recorded.files,
            `A file under wwwroot/lib changed but LIB in sw.js is still ${current.lib}: installed apps would keep the old copy. ` +
            "Bump LIB, then run UPDATE_SW_LIB=1 node --test tests/js/sw-lib.test.cjs");
    } else {
        assert.fail(`LIB is now ${current.lib}; record its files with UPDATE_SW_LIB=1 node --test tests/js/sw-lib.test.cjs`);
    }
});

test("every precached file exists", () => {
    const precache = worker().precache;
    for (const file of precache) {
        assert.ok(fs.existsSync(path.join(root, file)), `${file} is precached but not in wwwroot`);
    }
});

// ---- a fake browser: Cache Storage in memory, fetch counted, one worker script per call ----

function storage() {
    const caches = new Map();
    const key = (r) => (typeof r === "string" ? new URL(r, ORIGIN).href : r.url);
    const open = (name) => {
        if (!caches.has(name)) caches.set(name, new Map());
        const entries = caches.get(name);
        return {
            async match(request, options = {}) {
                const url = key(request);
                if (entries.has(url)) return entries.get(url).clone();
                if (options.ignoreSearch) {
                    const bare = url.split("?")[0];
                    for (const [k, v] of entries) if (k.split("?")[0] === bare) return v.clone();
                }
                return undefined;
            },
            async put(request, response) { entries.set(key(request), response); },
            async addAll(urls) {
                const fetched = [];
                for (const url of urls) {
                    const response = await env.fetch(new URL(url, ORIGIN).href);
                    if (!response.ok) throw new TypeError("addAll failed");
                    fetched.push([new URL(url, ORIGIN).href, response]);
                }
                for (const [url, response] of fetched) entries.set(url, response);
            },
            async keys() { return [...entries.keys()].map((url) => ({ url })); },
            async delete(request) { return entries.delete(key(request)); },
        };
    };
    const env = {
        fetched: [],
        offline: false,
        fetch: async (input) => {
            const url = typeof input === "string" ? input : input.url;
            if (env.offline) throw new TypeError("offline");
            env.fetched.push(new URL(url).pathname + new URL(url).search);
            return new Response("body of " + url, { status: 200 });
        },
        caches: {
            open: async (name) => open(name),
            keys: async () => [...caches.keys()],
            delete: async (name) => caches.delete(name),
            match: async (request) => {
                for (const name of caches.keys()) {
                    const hit = await open(name).match(request);
                    if (hit) return hit;
                }
                return undefined;
            },
        },
        names: () => [...caches.keys()].sort(),
        entries: (name) => [...(caches.get(name) ?? new Map()).keys()].map((u) => u.replace(ORIGIN, "")).sort(),
    };
    return env;
}

function worker(env = storage(), cacheName = null) {
    const handlers = {};
    const code = cacheName ? source.replace(/const CACHE = "[^"]+"/, `const CACHE = "${cacheName}"`) : source;
    const self = {
        location: { origin: ORIGIN },
        addEventListener: (name, fn) => { handlers[name] = fn; },
        skipWaiting: async () => {},
        clients: { claim: async () => {} },
    };
    const context = { self, caches: env.caches, fetch: env.fetch, URL, Response, Request, Promise, console };
    vm.runInNewContext(code + "\n;this.__precache = PRECACHE;", context);
    const run = async (name, extra = {}) => {
        const waits = [];
        let responded;
        handlers[name]({ ...extra, waitUntil: (p) => waits.push(p), respondWith: (p) => { responded = p; } });
        const response = responded ? await responded : undefined;
        await Promise.all(waits);
        return response;
    };
    return {
        env,
        precache: context.__precache,
        install: () => run("install"),
        activate: () => run("activate"),
        get: (url) => run("fetch", { request: new Request(new URL(url, ORIGIN).href) }),
    };
}

const libCount = (w) => w.precache.filter((p) => p.startsWith("/lib/")).length;

test("a new worker downloads only the app files, keeping the libraries from before", async () => {
    const first = worker();
    await first.install();
    await first.activate();
    assert.equal(first.env.fetched.length, first.precache.length, "the first install fetches everything");

    const env = first.env;
    env.fetched.length = 0;
    const next = worker(env, "gym-static-next");
    await next.install();
    await next.activate();

    assert.equal(env.fetched.length, next.precache.length - libCount(next), "nothing under /lib is fetched again");
    assert.ok(env.fetched.every((p) => !p.startsWith("/lib/")));
    assert.deepEqual(env.names(), [constant("LIB"), "gym-static-next"].sort(), "the old app cache is gone, the library cache stays");
});

test("an app still on the old single cache gets its libraries once, and the old cache goes", async () => {
    const env = storage();
    const old = await env.caches.open("gym-static-v32");
    await old.put(`${ORIGIN}/lib/htmx/htmx.min.js`, new Response("old htmx"));

    const w = worker(env);
    await w.install();
    await w.activate();

    assert.equal(env.fetched.filter((p) => p.startsWith("/lib/")).length, libCount(w));
    assert.deepEqual(env.names(), [constant("CACHE"), constant("LIB")].sort());
});

test("a versioned library keeps its precached copy, and a versioned app file replaces older ones", async () => {
    const w = worker();
    await w.install();
    await w.activate();

    await w.get("/lib/htmx/htmx.min.js?v=abc");
    await w.get("/css/app.css?v=one");
    await w.get("/css/app.css?v=two");

    const lib = w.env.entries(constant("LIB")).filter((u) => u.startsWith("/lib/htmx/"));
    assert.deepEqual(lib, ["/lib/htmx/htmx.min.js", "/lib/htmx/htmx.min.js?v=abc"]);
    const css = w.env.entries(constant("CACHE")).filter((u) => u.startsWith("/css/app.css"));
    assert.deepEqual(css, ["/css/app.css?v=two"]);
});

test("offline, a library version never fetched falls back to the precached copy", async () => {
    const w = worker();
    await w.install();
    await w.activate();
    w.env.offline = true;

    const response = await w.get("/lib/barcode-detector/zxing_reader.wasm?v=new");

    assert.equal(await response.text(), `body of ${ORIGIN}/lib/barcode-detector/zxing_reader.wasm`);
});

test("a failed library download fails the install, so the old worker stays", async () => {
    const env = storage();
    const fetch = env.fetch;
    env.fetch = async (url) => (url.includes("zxing_reader.wasm") ? new Response("", { status: 503 }) : fetch(url));

    await assert.rejects(worker(env).install());
});
