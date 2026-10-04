// Counts static-file requests the service worker sends to the network while navigating between tabs.
// After the cache is warm the answer should be 0. Usage: node design-samples/tools/sw-check.mjs [baseUrl]
import { chromium } from "playwright-core";

const base = process.argv[2] || "http://localhost:5130";
const browser = await chromium.launch({ channel: "chrome" });
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, serviceWorkers: "allow" });
const page = await context.newPage();
await page.goto(`${base}/account/login`);
await page.fill('input[name="email"]', "demo@example.test");
await page.fill('input[name="password"]', "demo-password-123");
await Promise.all([page.waitForURL(/\/today/), page.click('button[type="submit"]')]);
await page.evaluate(() => navigator.serviceWorker.ready);
const tabs = ["/train", "/food", "/progress", "/settings", "/today"];
for (const t of tabs) { // warm-up: every versioned asset fetched once
    await page.goto(base + t);
}

let fromWorker = 0;
context.on("request", (r) => {
    if (r.serviceWorker() && /\/(css|js|lib)\//.test(new URL(r.url()).pathname)) {
        fromWorker++;
    }
});
for (const t of tabs) {
    await page.goto(base + t);
}
await page.waitForTimeout(1000);
console.log(`static network requests from the service worker over ${tabs.length} navigations: ${fromWorker}`);
await browser.close();
process.exit(fromWorker === 0 ? 0 : 1);
