// Measures tap-to-feedback and tap-to-settled for tab navigations, with emulated latency.
// Usage: node design-samples/tools/nav-timing.mjs [baseUrl] [--rtt 150]
import { chromium } from "playwright-core";

const base = process.argv[2] && !process.argv[2].startsWith("--") ? process.argv[2] : "http://localhost:5130";
const rttArg = process.argv.indexOf("--rtt");
const rtt = rttArg > 0 ? Number(process.argv[rttArg + 1]) : 150;

const browser = await chromium.launch({ channel: "chrome" });
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, hasTouch: true, isMobile: true });
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
    await page.waitForFunction((t) => document.documentElement.dataset.tab === t && !document.documentElement.dataset.nav, tab);
    const settled = Date.now() - t0;
    console.log(`${label.padEnd(20)} feedback ${String(feedback).padStart(4)} ms   settled ${String(settled).padStart(4)} ms`);
    return settled;
}

const runs = [];
for (let i = 0; i < 3; i++) {
    runs.push(await measure("today -> train", "/train", "train"));
    runs.push(await measure("train -> food", "/food", "food"));
    runs.push(await measure("food -> progress", "/progress", "progress"));
    runs.push(await measure("progress -> today", "/today", "today"));
}
runs.sort((a, b) => a - b);
console.log(`median settled ${runs[Math.floor(runs.length / 2)]} ms at ${rtt} ms RTT`);
await browser.close();
