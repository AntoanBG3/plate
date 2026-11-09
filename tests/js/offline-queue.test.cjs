const assert = require("node:assert/strict");
const { test } = require("node:test");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const scripts = path.resolve(__dirname, "../../src/Plate.Web/wwwroot/js");
const source = (name) => fs.readFileSync(path.join(scripts, name), "utf8");
const settle = () => new Promise(setImmediate);

function browser(owner = "A", store = new Map()) {
    const events = {};
    const windowEvents = {};
    const modules = {};
    const requests = [];
    const intervals = [];
    const timeouts = [];
    const status = { hidden: true, textContent: "" };
    const context = {
        URLSearchParams, Date, console, AbortController,
        document: {
            documentElement: { dataset: { userId: owner } },
            addEventListener: (name, fn) => (events[name] ??= []).push(fn),
            body: { addEventListener() {} },
            getElementById: (id) => id === "queue-status" ? status : null,
            querySelector: () => null,
        },
        navigator: { onLine: true },
        localStorage: {
            getItem: (key) => store.get(key) ?? null,
            setItem: (key, value) => store.set(key, value),
            removeItem: (key) => store.delete(key),
        },
        setInterval(fn) { intervals.push(fn); },
        setTimeout(fn, ms) {
            const timer = { fn, ms, cleared: false };
            timeouts.push(timer);
            return timer;
        },
        clearTimeout(timer) { timer.cleared = true; },
        t: (s) => s,
        window: {
            addEventListener: (name, fn) => (windowEvents[name] ??= []).push(fn),
            gymModule: (name, fn) => { modules[name] = fn; },
            gymToken: () => "token", location: { pathname: "/train" },
        },
        fetch: async (url, options) => {
            requests.push({ url, ...options });
            return { ok: true, status: 200, text: async () => "" };
        },
    };
    vm.createContext(context);
    const run = (file) => vm.runInContext(source(file), context, { filename: file });
    run("queue.js");
    return { context, events, windowEvents, modules, requests, intervals, timeouts, status, store, run };
}

for (const phase of ["headers", "success body", "validation body"]) {
    test(`a set stalled at ${phase} times out, stays queued and replays the same ClientId`, async () => {
        const item = { clientId: "00000000-0000-4000-8000-000000000001", sessionExerciseId: 1,
            weightKg: 60, reps: 10, isWarmup: false, completedAt: new Date().toISOString() };
        const b = browser("A", new Map([["gym.pendingSets.v2.A", JSON.stringify([item])]]));
        const stored = new Set();
        b.context.fetch = async (url, options) => {
            b.requests.push({ url, ...options });
            const id = options.body.get("clientId");
            if (phase !== "validation body" || b.requests.length > 1) stored.add(id);
            if (b.requests.length > 1) return { ok: true, status: 200, text: async () => "" };
            const stalled = () => new Promise((_, reject) => {
                options.signal?.addEventListener("abort", () => reject(new Error("aborted")), { once: true });
            });
            return phase === "headers" ? stalled()
                : { ok: phase === "success body", status: phase === "success body" ? 200 : 400, text: stalled };
        };
        b.run("session.js");
        await settle();
        b.intervals[0]();
        b.windowEvents.online[0]();
        await settle();
        assert.equal(b.requests.length, 1, "only one send can be in flight");
        assert.equal(JSON.parse(b.store.get("gym.pendingSets.v2.A") || "[]")[0].clientId, item.clientId);
        const deadline = b.timeouts.find((timer) => timer.ms === 15000 && !timer.cleared);
        assert.ok(deadline, "the entire attempt must have a 15-second deadline");
        deadline.fn();
        await settle();
        assert.equal(b.requests[0].signal.aborted, true);
        assert.equal(deadline.cleared, true);
        assert.equal(JSON.parse(b.store.get("gym.pendingSets.v2.A") || "[]")[0].clientId, item.clientId);

        b.windowEvents.online[0]();
        await settle();
        assert.deepEqual(b.requests.map((r) => r.body.get("clientId")), [item.clientId, item.clientId]);
        assert.equal(stored.size, 1, "a lost acknowledgement must replay the original server identity");
        assert.equal(JSON.parse(b.store.get("gym.pendingSets.v2.A") || "[]").length, 0);
        assert.equal(b.timeouts.every((timer) => timer.cleared), true);
    });
}

test("a set that completes before its deadline clears the timer without aborting", async () => {
    const item = { clientId: "slow-set", sessionExerciseId: 1, weightKg: 60, reps: 10,
        isWarmup: false, completedAt: new Date().toISOString() };
    const b = browser("A", new Map([["gym.pendingSets.v2.A", JSON.stringify([item])]]));
    let complete;
    b.context.fetch = async (url, options) => {
        b.requests.push({ url, ...options });
        return { ok: true, status: 200, text: () => new Promise((resolve) => { complete = resolve; }) };
    };
    b.run("session.js");
    await settle();
    assert.equal(JSON.parse(b.store.get("gym.pendingSets.v2.A") || "[]").length, 1);
    complete("");
    await settle();
    assert.equal(JSON.parse(b.store.get("gym.pendingSets.v2.A") || "[]").length, 0);
    assert.equal(b.requests[0].signal?.aborted, false);
    assert.equal(b.timeouts.every((timer) => timer.cleared), true);
});

function feedbackForm(b) {
    const listeners = {};
    const navigations = [];
    const field = (value = "") => ({ value, disabled: false, hidden: false, style: {}, addEventListener() {} });
    const client = field("00000000-0000-4000-8000-000000000009");
    const message = field("Original report");
    const kind = field("Problem");
    const area = field("Speed");
    const file = field();
    const button = field();
    const text = { textContent: "Saved on this phone." };
    const notice = { hidden: true, querySelector: () => text };
    const nodes = {
        'input[type="file"]': file, '[data-shot-preview]': field(), '[data-shot-pick]': field(),
        '[data-shot-offline]': field(), '[data-queued]': notice,
        'input[name="clientId"]': client, 'textarea[name=message]': message, 'button[type=submit]': button,
    };
    const editable = [file, message, kind, area];
    const form = {
        action: "/feedback", reportValidity: () => true,
        querySelector: (s) => nodes[s],
        querySelectorAll: (s) => s.startsWith('input:not(') ? editable : [],
        addEventListener: (name, fn) => { listeners[name] = fn; },
    };
    let formReads = 0;
    b.context.FormData = class {
        constructor() {
            formReads++;
            this.data = new Map([["clientId", client.value], ["source", "Settings"]]);
            for (const [name, control] of [["message", message], ["kind", kind], ["area", area]]) {
                if (!control.disabled) this.data.set(name, control.value);
            }
        }
        delete(k) { this.data.delete(k); }
        set(k, v) { this.data.set(k, v); }
        entries() { return this.data.entries(); }
    };
    b.context.sessionStorage = { getItem: () => null };
    b.context.crypto = require("node:crypto").webcrypto;
    Object.assign(b.context.window, {
        innerWidth: 390, innerHeight: 844, matchMedia: () => ({ matches: false }),
        gymToast() {}, gymNav: (url) => navigations.push(url),
    });
    b.run("feedback.js");
    b.modules["feedback-form"](form);
    return {
        client, message, kind, area, file, button, notice, text, navigations,
        get formReads() { return formReads; },
        submit: () => listeners.submit({ preventDefault() {}, submitter: button }),
    };
}

function setForm() {
    const classes = new Set();
    const inputs = Object.fromEntries(Object.entries({
        clientId: "00000000-0000-4000-8000-000000000001", sessionExerciseId: "1",
        weightKg: "60", reps: "10", isWarmup: "true",
    }).map(([k, value]) => [k, { value }]));
    return {
        classes, inputs, dataset: {},
        classList: { add: (...s) => s.forEach((x) => classes.add(x)), remove: (...s) => s.forEach((x) => classes.delete(x)), contains: (s) => classes.has(s) },
        reportValidity: () => true,
        querySelector: (s) => inputs[s.match(/name="([^"]+)"/)[1]],
        querySelectorAll: (s) => s.includes("data-suggested") ? [] : Object.values(inputs),
    };
}

test("pending changes are private to their originating account, including after sign-out", () => {
    const shared = new Map();
    const a = browser("A", shared).context.window.gymQueue("gym.pendingFeedback");
    a.save([{ key: "private report", fields: [["message", "A's report"]] }]);
    const b = browser("B", shared).context.window.gymQueue("gym.pendingFeedback");
    assert.equal(b.load().length, 0);
    b.save([{ key: "B's report" }]);
    assert.equal(a.load()[0].key, "private report");
    assert.equal(browser("", shared).context.window.gymQueue("gym.pendingFeedback").load().length, 0);
});

test("an ownerless legacy queue is retained without being assigned to whoever signs in", () => {
    const store = new Map([["gym.pendingFeedback.v1", JSON.stringify([{ key: "unknown owner" }])]]);
    const q = browser("B", store).context.window.gymQueue("gym.pendingFeedback");
    assert.equal(q.hasLegacy(), true);
    assert.equal(q.load().length, 0);
    assert.equal(JSON.parse(store.get("gym.pendingFeedback.v1"))[0].key, "unknown owner");
});

test("storage failures retain changes and removals in memory, then recover when storage returns", () => {
    const b = browser();
    const storage = { ...b.context.localStorage };
    b.context.localStorage.setItem = () => { throw new Error("QuotaExceededError"); };
    b.context.localStorage.removeItem = () => { throw new Error("SecurityError"); };
    const q = b.context.window.gymQueue("gym.pendingSets");
    assert.equal(q.save([{ clientId: "set" }]), false);
    assert.equal(q.load()[0].clientId, "set");
    assert.equal(q.durable, false);
    q.save([]);
    assert.equal(q.load().length, 0);
    Object.assign(b.context.localStorage, storage);
    assert.equal(q.save([{ clientId: "next" }]), true);
    assert.equal(q.durable, true);
    assert.equal(JSON.parse(b.store.get("gym.pendingSets.v2.A"))[0].clientId, "next");
});

test("set logging still sends when storage is denied, and warns while the request is pending", async () => {
    const b = browser();
    b.context.localStorage.setItem = () => { throw new Error("QuotaExceededError"); };
    b.context.localStorage.removeItem = () => { throw new Error("SecurityError"); };
    let finish;
    b.context.fetch = (url, options) => {
        b.requests.push({ url, ...options });
        return new Promise((resolve) => { finish = resolve; });
    };
    b.run("session.js");
    await settle();
    const form = setForm();
    b.events.submit[0]({ target: { closest: () => form }, preventDefault() {} });
    assert.equal(b.requests.length, 1);
    assert.equal(b.requests[0].headers["X-Gym-User"], "A");
    assert.equal(b.status.hidden, false);
    assert.match(b.status.textContent, /can't save pending changes/);
    finish({ ok: true, status: 200, text: async () => "" });
    await settle();
    assert.equal(b.status.hidden, true);
});

test("sets stay queued when a stale tab's request is rejected for an account change", async () => {
    const b = browser();
    b.context.fetch = async () => ({ ok: false, status: 409 });
    b.run("session.js");
    await settle();
    b.events.submit[0]({ target: { closest: () => setForm() }, preventDefault() {} });
    await settle();
    assert.equal(JSON.parse(b.store.get("gym.pendingSets.v2.A")).length, 1);
});

for (const status of [401, 409]) {
    test(`an in-memory set survives HTTP ${status} without navigating away and can retry after sign-in`, async () => {
        const b = browser();
        b.context.localStorage.setItem = () => { throw new Error("QuotaExceededError"); };
        b.context.localStorage.removeItem = () => { throw new Error("SecurityError"); };
        b.context.fetch = async () => ({ ok: false, status });
        b.run("session.js");
        await settle();
        b.events.submit[0]({ target: { closest: () => setForm() }, preventDefault() {} });
        await settle();
        assert.equal(b.context.window.location.href, undefined);
        assert.equal(b.status.hidden, false);
        b.context.fetch = async () => ({ ok: true, status: 200, text: async () => "" });
        b.intervals[0]();
        await settle();
        assert.equal(b.status.hidden, true);
    });
}

test("feedback sends only this account's queue and includes its originating identity", async () => {
    const store = new Map([
        ["gym.pendingFeedback.v2.A", JSON.stringify([{ key: "A", url: "/feedback", fields: [["message", "private A"]] }])],
        ["gym.pendingFeedback.v2.B", JSON.stringify([{ key: "B", url: "/feedback", fields: [["message", "private B"]] }])],
    ]);
    const b = browser("B", store);
    b.run("feedback.js");
    await b.events.DOMContentLoaded[0]();
    assert.equal(b.requests.length, 1);
    assert.equal(b.requests[0].headers["X-Gym-User"], "B");
    assert.equal(b.requests[0].body.get("message"), "private B");
    assert.equal(JSON.parse(store.get("gym.pendingFeedback.v2.A"))[0].key, "A");
});

test("an account change pauses an already loaded queue", () => {
    const b = browser("A");
    const q = b.context.window.gymQueue("gym.pendingSets");
    assert.equal(q.active(), true);
    b.context.document.documentElement.dataset.userId = "B";
    assert.equal(q.active(), false);
    assert.equal(q.headers()["X-Gym-User"], "A");
});

test("a report is immutable while its first request is in flight", async () => {
    const b = browser();
    let respond;
    b.context.fetch = () => new Promise((resolve) => { respond = resolve; });
    const form = feedbackForm(b);
    const submitted = form.submit();
    await settle();
    assert.equal(form.message.disabled, true);
    assert.equal(form.kind.disabled, true);
    assert.equal(form.area.disabled, true);
    assert.equal(form.file.disabled, true);
    b.windowEvents.online.forEach((fn) => fn());
    assert.equal(form.file.disabled, true, "reconnecting must not re-enable attachment edits on the pending report");
    respond({ ok: true, status: 200 });
    await submitted;
    assert.equal(form.navigations.length, 1);
});

test("denied storage keeps a report visibly locked and retries the original id and fields", async () => {
    const b = browser();
    b.context.localStorage.setItem = () => { throw Error("QuotaExceededError"); };
    b.context.localStorage.removeItem = () => { throw Error("SecurityError"); };
    b.context.fetch = async () => { throw Error("offline"); };
    const form = feedbackForm(b);
    const id = form.client.value;
    await form.submit();
    assert.equal(form.message.value, "Original report");
    assert.equal(form.message.disabled, true);
    assert.equal(form.kind.disabled, true);
    assert.equal(form.area.disabled, true);
    assert.equal(form.button.disabled, false, "Send remains available to retry");
    assert.equal(form.notice.hidden, false);
    assert.match(form.text.textContent, /contents are locked/);
    let sent;
    b.context.fetch = async (_, options) => { sent = options.body; return { ok: true, status: 200 }; };
    await form.submit();
    assert.equal(sent.get("clientId"), id);
    assert.equal(sent.get("message"), "Original report");
    assert.equal(form.formReads, 1, "retry must use the queued payload, not re-read disabled controls");
    assert.equal(form.navigations.length, 1);
});

test("durable offline feedback unlocks a fresh draft without changing the queued report", async () => {
    const b = browser();
    b.context.navigator.onLine = false;
    b.context.fetch = async () => { throw Error("offline"); };
    const form = feedbackForm(b);
    const id = form.client.value;
    await form.submit();
    assert.equal(form.message.disabled, false);
    assert.equal(form.kind.disabled, false);
    assert.equal(form.area.disabled, false);
    assert.equal(form.message.value, "");
    assert.notEqual(form.client.value, id);
    assert.equal(form.text.textContent, "Saved on this phone.");
    const pending = JSON.parse(b.store.get("gym.pendingFeedback.v2.A"));
    assert.equal(new URLSearchParams(pending[0].fields).get("message"), "Original report");
});

// ---- the ownerless v1 queues left by the previous version ----

const legacySet = (clientId, daysAgo = 0, extra = {}) => ({
    clientId, sessionExerciseId: 1, weightKg: "60", reps: "10", isWarmup: false,
    completedAt: new Date(Date.now() - daysAgo * 864e5).toISOString(), ...extra,
});

test("legacy sets are adopted by the signed-in account and sent with its identity", async () => {
    const store = new Map([["gym.pendingSets.v1", JSON.stringify([legacySet("old-1")])]]);
    const b = browser("A", store);
    b.run("session.js");
    await settle();
    assert.equal(b.requests.length, 1);
    assert.equal(b.requests[0].headers["X-Gym-User"], "A");
    assert.equal(b.requests[0].body.get("clientId"), "old-1");
    assert.equal(store.has("gym.pendingSets.v1"), false);
    assert.equal(store.has("gym.pendingSets.v2.A"), false);
});

test("a legacy set another account logged goes back for that account and is not retried here", async () => {
    const store = new Map([["gym.pendingSets.v1", JSON.stringify([legacySet("theirs")])]]);
    const b = browser("B", store);
    let toasts = 0;
    b.context.window.gymToast = () => { toasts++; };
    b.context.fetch = async (url, options) => { b.requests.push({ url, ...options }); return { ok: false, status: 404, text: async () => "" }; };
    b.run("session.js");
    await settle();
    assert.equal(toasts, 0);
    assert.deepEqual(JSON.parse(store.get("gym.pendingSets.v1")).map((x) => [x.clientId, x.triedBy, x.legacy]), [["theirs", ["B"], undefined]]);
    assert.equal(store.has("gym.pendingSets.v2.B"), false);
    assert.match(b.status.textContent, /account that logged them/);

    const again = browser("B", store);
    again.run("session.js");
    await settle();
    assert.equal(again.requests.length, 0);

    const owner = browser("A", store);
    owner.run("session.js");
    await settle();
    assert.equal(owner.requests[0].body.get("clientId"), "theirs");
    assert.equal(store.has("gym.pendingSets.v1"), false);
});

test("a legacy set another account logged is kept when storage is too full to hand it back", async () => {
    const store = new Map([["gym.pendingSets.v1", JSON.stringify([legacySet("theirs"), legacySet("mine")])]]);
    const b = browser("B", store);
    b.context.fetch = async (url, options) => {
        b.requests.push({ url, ...options });
        return options.body.get("clientId") === "theirs"
            ? { ok: false, status: 404, text: async () => "" }
            : { ok: true, status: 200, text: async () => "" };
    };
    b.run("session.js");
    const setItem = b.context.localStorage.setItem;
    b.context.localStorage.setItem = (key, value) => { if (key.endsWith(".v1")) throw new Error("QuotaExceededError"); setItem(key, value); };
    await settle();
    assert.deepEqual(b.requests.map((r) => r.body.get("clientId")), ["theirs", "mine"]);
    const left = [...JSON.parse(store.get("gym.pendingSets.v1") ?? "[]"), ...JSON.parse(store.get("gym.pendingSets.v2.B") ?? "[]")];
    assert.deepEqual(left.map((x) => x.clientId), ["theirs"]);
});

test("legacy sets older than 30 days are dropped instead of adopted", async () => {
    const store = new Map([["gym.pendingSets.v1", JSON.stringify([legacySet("stale", 31)])]]);
    const b = browser("A", store);
    b.run("session.js");
    await settle();
    assert.equal(b.requests.length, 0);
    assert.equal(store.has("gym.pendingSets.v1"), false);
});

test("legacy sets stay in the v1 queue when the account's queue can't be saved", async () => {
    const store = new Map([["gym.pendingSets.v1", JSON.stringify([legacySet("kept")])]]);
    const b = browser("A", store);
    b.context.localStorage.setItem = (key, value) => { if (key.includes(".v2.")) throw new Error("QuotaExceededError"); store.set(key, value); };
    b.context.fetch = async () => { throw new Error("offline"); };
    b.run("session.js");
    await settle();
    assert.equal(JSON.parse(store.get("gym.pendingSets.v1"))[0].clientId, "kept");
});

test("the previous version's rest timer and drafts are cleared once someone is signed in", () => {
    const store = new Map([["gym.restTimer.v1", "{}"], ["gym.setDrafts.v1", "{}"]]);
    browser("", store).run("session.js");
    assert.equal(store.size, 2);
    browser("A", store).run("session.js");
    assert.equal(store.size, 0);
});

function legacySection() {
    const forms = {};
    const form = (name) => ({ listeners: {}, addEventListener(e, fn) { this.listeners[e] = fn; }, removeEventListener() {}, submit() { this.listeners.submit({ preventDefault() {} }); }, name });
    forms.send = form("send");
    forms.discard = form("discard");
    const list = { rows: [], replaceChildren(...rows) { this.rows = rows; } };
    return {
        forms, list, hidden: true,
        querySelector(s) { return s === "[data-legacy-list]" ? list : s === "[data-legacy-send]" ? forms.send : forms.discard; },
    };
}

function withDom(b) {
    b.context.document.createElement = () => ({ appendChild() {}, set textContent(v) { this.text = v; } });
    return b;
}

test("legacy feedback waits for the person signed in to send it or discard it", async () => {
    const report = { key: "r1", kind: "report", url: "/feedback", fields: [["message", "old"]], preview: "old" };
    const store = new Map([["gym.pendingFeedback.v1", JSON.stringify([report])]]);
    const b = withDom(browser("A", store));
    b.run("feedback.js");
    await b.events.DOMContentLoaded[0]();
    assert.equal(b.requests.length, 0);

    const section = legacySection();
    b.modules["feedback-legacy"](section);
    assert.equal(section.hidden, false);
    assert.equal(section.list.rows.length, 1);
    section.forms.send.submit();
    await settle();
    assert.equal(b.requests.length, 1);
    assert.equal(b.requests[0].headers["X-Gym-User"], "A");
    assert.equal(store.has("gym.pendingFeedback.v1"), false);
    assert.equal(section.hidden, true);

    const other = new Map([["gym.pendingFeedback.v1", JSON.stringify([report])]]);
    const c = withDom(browser("B", other));
    c.run("feedback.js");
    const discard = legacySection();
    c.modules["feedback-legacy"](discard);
    discard.forms.discard.submit();
    assert.equal(other.size, 0);
    assert.equal(discard.hidden, true);
    assert.equal(c.requests.length, 0);
});

function denyStorage(b) {
    b.context.localStorage.setItem = () => { throw new Error("QuotaExceededError"); };
    b.context.localStorage.removeItem = () => { throw new Error("SecurityError"); };
}

test("an in-memory set queue says once, not on every retry, that the account changed", async () => {
    const b = browser("A");
    denyStorage(b);
    let toasts = 0;
    b.context.window.gymToast = () => { toasts++; };
    b.context.fetch = async () => ({ ok: false, status: 409 });
    b.run("session.js");
    b.events.submit[0]({ target: { closest: () => setForm() }, preventDefault() {} });
    await settle();
    for (let i = 0; i < 3; i++) {
        b.intervals[0]();
        await settle();
    }
    assert.equal(toasts, 1);
});

test("an in-memory feedback queue says once, not on every retry, that the account changed", async () => {
    const b = browser("A");
    denyStorage(b);
    b.context.fetch = async () => ({ ok: false, status: 409 });
    const form = feedbackForm(b);
    const toasts = [];
    b.context.window.gymToast = (m) => toasts.push(m);
    await form.submit();
    for (let i = 0; i < 3; i++) {
        b.intervals[0]();
        await settle();
    }
    assert.equal(toasts.filter((m) => /Sign back into this account/.test(m)).length, 1);
});
