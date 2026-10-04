// Measures, per tab tap with emulated latency: feedback (tapped tab shows active), content (new page in the DOM)
// and settled (no animation running). --mpa blocks nav.js to measure full page loads on the same build.
// Usage: node design-samples/tools/nav-timing.mjs [baseUrl] [--rtt 150] [--mpa]
import { chromium } from "playwright-core";

const base = process.argv[2] && !process.argv[2].startsWith("--") ? process.argv[2] : "http://localhost:5130";
const rttArg = process.argv.indexOf("--rtt");
const rtt = rttArg > 0 ? Number(process.argv[rttArg + 1]) : 150;

const browser = await chromium.launch({ channel: "chrome" });
const mpa = process.argv.includes("--mpa");
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, hasTouch: true, isMobile: true, serviceWorkers: "block" });
if (mpa) {
    await context.route(/\/js\/nav\.js/, (route) => route.abort());
}
const page = await context.newPage();
await page.goto(`${base}/account/login`);
await page.fill('input[name="email"]', "demo@example.test");
await page.fill('input[name="password"]', "demo-password-123");
await Promise.all([page.waitForURL(/\/today/), page.click('button[type="submit"]')]);

const cdp = await context.newCDPSession(page);
await cdp.send("Network.emulateNetworkConditions", { offline: false, latency: rtt, downloadThroughput: 1.5e6, uploadThroughput: 750e3 });

async function measure(label, href, tab) {
    const t0 = Date.now();
    await page.tap(`.tabbar a[href="${href}"]`);
    await page.waitForSelector(`.tabbar a[href="/${tab}"].is-active`);
    const feedback = Date.now() - t0;
    await page.waitForFunction((t) => document.documentElement.dataset.tab === t, tab, { polling: "raf" });
    const content = Date.now() - t0;
    await page.waitForTimeout(50); // let a cross-document transition start before checking for animations
    await page.waitForFunction(() => !document.documentElement.dataset.nav
        && document.getAnimations().every((a) => a.playState !== "running" || !String(a.effect?.pseudoElement || "").includes("view-transition")), null, { polling: "raf" });
    const settled = Date.now() - t0;
    console.log(`${label.padEnd(20)} feedback ${String(feedback).padStart(4)}   content ${String(content).padStart(4)}   settled ${String(settled).padStart(4)} ms`);
    return [feedback, content, settled];
}

const runs = [];
for (let i = 0; i < 3; i++) {
    runs.push(await measure("today -> train", "/train", "train"));
    runs.push(await measure("train -> food", "/food", "food"));
    runs.push(await measure("food -> progress", "/progress", "progress"));
    runs.push(await measure("progress -> today", "/today", "today"));
}
const median = (i) => runs.map((r) => r[i]).sort((a, b) => a - b)[Math.floor(runs.length / 2)];
console.log(`${mpa ? "full page loads" : "soft navigation"} at ${rtt} ms RTT, medians: feedback ${median(0)}, content ${median(1)}, settled ${median(2)} ms`);
await browser.close();
