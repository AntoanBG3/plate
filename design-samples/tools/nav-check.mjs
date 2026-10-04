// Checks soft navigation end to end: tabs, push and Back, a POST form, session expiry and offline.
// Usage: node design-samples/tools/nav-check.mjs [baseUrl]   (needs the demo account from seed-demo.sql)
import { chromium } from "playwright-core";
import assert from "node:assert/strict";

const base = process.argv[2] || "http://localhost:5130";
const browser = await chromium.launch({ channel: "chrome" });
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, serviceWorkers: "allow" });
const errors = [];
// Track 250 ms intervals (the rest timer tick) so the check can see when they run.
await context.addInitScript(() => {
    const set = window.setInterval;
    const clear = window.clearInterval;
    window.__ticks = new Set();
    window.setInterval = (fn, ms, ...args) => {
        const id = set(fn, ms, ...args);
        if (ms === 250) {
            window.__ticks.add(id);
        }
        return id;
    };
    window.clearInterval = (id) => {
        window.__ticks.delete(id);
        clear(id);
    };
});

async function signedInPage() {
    const page = await context.newPage();
    page.on("pageerror", (e) => errors.push(e.message));
    page.on("console", (m) => m.type() === "error" && !m.text().includes("net::ERR_INTERNET_DISCONNECTED") && errors.push(m.text()));
    await page.goto(`${base}/account/login`);
    if (page.url().includes("/account/login")) {
        await page.fill('input[name="email"]', "demo@example.test");
        await page.fill('input[name="password"]', "demo-password-123");
        await Promise.all([page.waitForURL(/\/today/), page.click('button[type="submit"]')]);
    }
    await page.evaluate(() => { window.__doc = 1; });
    return page;
}

const page = await signedInPage();
const sameDoc = () => page.evaluate(() => window.__doc === 1);
const settled = (tab) => page.waitForFunction((t) => document.documentElement.dataset.tab === t && !document.documentElement.dataset.nav, tab);

for (const tab of ["train", "food", "progress", "settings", "today"]) {
    await page.tap(`.tabbar a[href="/${tab}"]`);
    await settled(tab);
    assert.equal(new URL(page.url()).pathname, `/${tab}`);
    assert.ok(await page.$(`.tabbar a[href="/${tab}"][aria-current="page"]`), `${tab} tab active`);
    assert.match(await page.title(), /Gym/);
    assert.ok(await sameDoc(), `${tab} was a soft navigation`);
}
assert.ok(await page.evaluate(() => [...document.querySelectorAll("canvas")].length === 0), "no leftover charts on Today");

await page.tap('.tabbar a[href="/progress"]');
await settled("progress");
assert.ok(await page.evaluate(() => [...document.querySelectorAll("canvas")].some((c) => window.Chart && Chart.getChart(c))), "charts drawn after soft navigation");

await page.tap('.tabbar a[href="/train"]');
await settled("train");
await page.tap('a[href="/train/history"]');
await page.waitForFunction(() => location.pathname === "/train/history" && !document.documentElement.dataset.nav);
await page.goBack();
await page.waitForFunction(() => location.pathname === "/train" && !document.documentElement.dataset.nav);
assert.ok(await sameDoc(), "push and Back stay in the document");

await page.tap('.tabbar a[href="/today"]');
await settled("today");
const weight = (80 + Math.random()).toFixed(1);
await page.fill('input[name="weightKg"]', weight);
await page.press('input[name="weightKg"]', "Enter");
await page.waitForFunction((w) => document.getElementById("main").innerText.includes(w) && !document.documentElement.dataset.nav, weight);
assert.ok(await sameDoc(), "weight POST was soft");
await page.goBack();
await page.waitForFunction(() => !document.documentElement.dataset.nav);
assert.ok(!page.url().includes("/today/weight"), "Back never lands on the POST url");

// Session page reached by soft navigation: logging a set starts the rest timer, leaving stops the tick.
const workout = await signedInPage();
assert.equal(await workout.evaluate(() => window.__ticks.size), 0, "no rest-timer tick on Today");
const resume = await workout.$('a[href^="/train/sessions/"]');
if (resume) {
    await resume.tap();
    await workout.waitForFunction(() => location.pathname.startsWith("/train/sessions/") && !document.documentElement.dataset.nav);
    assert.ok(await workout.evaluate(() => window.__doc === 1), "session page was a soft navigation");
    const clientId = await workout.evaluate(() => {
        const form = document.querySelector("form[data-log-set]:not(.pending)");
        if (!form) {
            return null;
        }
        form.querySelectorAll('input[name="weightKg"], input[name="reps"]').forEach((i) => { i.value = i.value || i.dataset.suggested || "5"; });
        form.querySelector('button[type="submit"]').click();
        return form.dataset.clientId;
    });
    if (clientId) {
        await workout.waitForSelector("#rest-timer:not([hidden])", { timeout: 5000 });
        await workout.waitForSelector(`form.set-row.done[data-client-id="${clientId}"]`, { timeout: 10000 });
        assert.equal(await workout.evaluate(() => window.__ticks.size), 1, "rest timer ticks while it runs");
        await workout.evaluate((id) => document.querySelector(`form.set-row.done[data-client-id="${id}"] .set-delete`).click(), clientId);
        await workout.waitForSelector(`form.set-row.done[data-client-id="${clientId}"]`, { state: "detached", timeout: 10000 });
        await workout.click("#rest-skip").catch(() => workout.evaluate(() => document.querySelector("[data-rest-skip]").click()));
    } else {
        console.log("no blank set on the active session; skipped the set check");
    }
    await workout.goBack();
    await workout.waitForFunction(() => location.pathname === "/today" && !document.documentElement.dataset.nav);
    assert.equal(await workout.evaluate(() => window.__ticks.size), 0, "tick stops after leaving the session");
} else {
    console.log("demo account has no active session; skipped the session check");
}
await workout.close();

// A slow form (the food text parser) that fails must not stay disabled.
const busy = await signedInPage();
await busy.route("**/food/parse", (route) => route.fulfill({ status: 500, contentType: "text/plain", body: "boom" }));
await busy.goto(`${base}/food/add?tab=text`);
await busy.fill('textarea[name="text"]', "a banana");
await busy.click('form[data-busy] button[type="submit"]');
await busy.waitForSelector("#toast:not([hidden])", { timeout: 5000 });
assert.ok(await busy.$eval('form[data-busy] button[type="submit"]', (b) => !b.disabled), "parse button usable again after a failed post");
await busy.close();
errors.splice(0, errors.length, ...errors.filter((e) => !e.includes("500")));

const offline = await signedInPage();
await context.setOffline(true);
await offline.tap('.tabbar a[href="/food"]');
await offline.waitForSelector("text=No connection", { timeout: 10000 });
await context.setOffline(false);
await offline.close();

const cookies = await context.cookies();
await context.clearCookies();
await context.addCookies(cookies.filter((c) => !/gym\.auth$/.test(c.name)));
await page.tap('.tabbar a[href="/food"]').catch(() => {});
await page.waitForURL(/\/account\/login/);
assert.ok(!(await page.evaluate(() => window.__doc === 1)), "expired session falls back to a full load");

assert.deepEqual(errors, []);
console.log("nav-check passed");
await browser.close();
