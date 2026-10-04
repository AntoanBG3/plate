// Checks soft navigation end to end: tabs, push and Back, a POST form, session expiry and offline.
// Usage: node design-samples/tools/nav-check.mjs [baseUrl]   (needs the demo account from seed-demo.sql)
import { chromium } from "playwright-core";
import assert from "node:assert/strict";

const base = process.argv[2] || "http://localhost:5130";
const browser = await chromium.launch({ channel: "chrome" });
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, serviceWorkers: "allow" });
const errors = [];

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
