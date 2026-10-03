// Clicks through each sample and reports script errors and whether view transitions ran.
import { chromium } from "playwright-core";
import path from "node:path";
const dir = path.join(path.dirname(new URL(import.meta.url).pathname), "..");
const browser = await chromium.launch({ channel: "chrome", headless: true });
for (const s of ["a-fitness", "b-mono", "c-apple"]) {
    const page = await browser.newPage({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
    const errors = [];
    page.on("pageerror", (e) => errors.push(e.message));
    await page.addInitScript(() => {
        window.__vt = 0;
        const orig = document.startViewTransition?.bind(document);
        if (orig) document.startViewTransition = (cb) => { window.__vt++; return orig(cb); };
    });
    await page.goto(`file://${dir}/${s}.html?timer=off`);
    await page.waitForTimeout(500);
    await page.click("[data-go=session][data-dir=forward]");
    await page.waitForTimeout(600);
    const pillBefore = await page.$eval("[data-timer-pill]", (e) => e.classList.contains("is-visible"));
    await page.click("[data-set]:not(.is-done) [data-done]");
    await page.waitForTimeout(600);
    const pillAfter = await page.$eval("[data-timer-pill]", (e) => e.classList.contains("is-visible"));
    const rows = await page.$$eval("[data-exercise]:first-of-type [data-set]", (r) => r.length);
    await page.click("[data-add-set]");
    await page.waitForTimeout(500);
    const rows2 = await page.$$eval("[data-exercise]:first-of-type [data-set]", (r) => r.length);
    await page.click("[data-menu-trigger]");
    const menu = await page.$eval(".menu", (m) => m.classList.contains("is-open"));
    await page.click("[data-screen=session] .large-title");
    await page.click("[data-timer-pill]", { position: { x: 20, y: 20 } });
    await page.waitForTimeout(600);
    const sheetOpen = await page.evaluate(() => document.body.classList.contains("sheet-open"));
    // drag the sheet down to dismiss
    const box = await page.$eval(".sheet-panel", (p) => { const r = p.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + 30 }; });
    await page.mouse.move(box.x, box.y); await page.mouse.down();
    for (let i = 1; i <= 10; i++) { await page.mouse.move(box.x, box.y + i * 30); await page.waitForTimeout(16); }
    await page.mouse.up();
    await page.waitForTimeout(700);
    const sheetClosed = await page.evaluate(() => !document.body.classList.contains("sheet-open") && document.querySelector("[data-sheet]").hidden);
    await page.click("[data-tab=today]");
    await page.waitForTimeout(500);
    const vt = await page.evaluate(() => window.__vt);
    console.log(s, { pillBefore, pillAfter, rowsAdded: rows2 - rows, menu, sheetOpen, sheetClosed, viewTransitions: vt, errors });
    await page.close();
}
await browser.close();
