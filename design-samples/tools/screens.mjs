// Screenshots every screen of the local dev app (never wizard) at iPhone sizes.
//   node design-samples/tools/screens.mjs <outDir> [light,dark]
// Needs playwright-core (NODE_PATH or a local install) and Google Chrome. Demo data: seed-demo.sql.
import { chromium } from "playwright-core";
import { signIn } from "./app.mjs";
import fs from "node:fs";
import path from "node:path";

const BASE = process.env.GYM_URL || "http://localhost:5130";
const outDir = process.argv[2] || "design-samples/screenshots/before";
const schemes = (process.argv[3] || "dark").split(",");
const sizes = [
    { name: "390", width: 390, height: 844 },
    { name: "375", width: 375, height: 667 },
];
const ids = JSON.parse(process.env.GYM_IDS || "{}");
const day = (offset) => {
    const d = new Date(Date.now() + 3 * 3600e3 + offset * 86400e3); // Europe/Sofia, close enough for a URL
    return d.toISOString().slice(0, 10);
};

const screens = [
    ["today", "/today"],
    ["train", "/train"],
    ["session-active", `/train/sessions/${ids.active}`],
    ["session-finished", `/train/sessions/${ids.finished}`],
    ["summary", `/train/sessions/${ids.finished}/summary`],
    ["history", "/train/history"],
    ["templates", "/train/templates"],
    ["template-edit", `/train/templates/${ids.template}`],
    ["exercise-picker", `/train/sessions/${ids.active}/add-exercise`],
    ["progress", "/progress"],
    ["food", "/food"],
    ["food-yesterday", `/food?date=${day(-1)}`],
    ["food-add-text", `/food/add?date=${day(0)}&meal=Dinner&tab=text`],
    ["food-add-barcode", `/food/add?date=${day(0)}&meal=Snack&tab=barcode`],
    ["food-add-search", `/food/add?date=${day(0)}&meal=Snack&tab=search&q=${encodeURIComponent("кисело мляко")}`],
    ["food-add-recent", `/food/add?date=${day(0)}&meal=Snack&tab=recent`],
    ["food-add-manual", `/food/add?date=${day(0)}&meal=Snack&tab=manual`],
    ["food-barcode-found", `/food/barcode?date=${day(0)}&meal=Snack&code=3801234567898`],
    ["food-entry", `/food/entries/${ids.entry}`],
    ["settings", "/settings"],
    ["settings-model", "/settings/model"],
    ["not-found", "/train/sessions/999999"],
];

async function shot(page, file) {
    await page.waitForLoadState("networkidle").catch(() => {});
    await page.waitForTimeout(400);
    await page.screenshot({ path: file, fullPage: true });
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
for (const scheme of schemes) {
    for (const size of sizes) {
        const dir = path.join(outDir, `${size.name}-${scheme}`);
        fs.mkdirSync(dir, { recursive: true });
        const ctx = await browser.newContext({
            viewport: { width: size.width, height: size.height },
            deviceScaleFactor: 3, isMobile: true, hasTouch: true, colorScheme: scheme,
            locale: "en-GB", timezoneId: "Europe/Sofia",
        });
        const page = await ctx.newPage();

        await page.goto(`${BASE}/account/login`);
        await shot(page, path.join(dir, "login.png"));
        await page.goto(`${BASE}/offline.html`);
        await shot(page, path.join(dir, "offline.png"));

        await signIn(page, BASE, "demo@example.test");
        for (const [name, url] of screens) {
            await page.goto(BASE + url);
            await shot(page, path.join(dir, `${name}.png`));
        }

        // Rest timer running: log the next blank set on the active session.
        await page.goto(`${BASE}/train/sessions/${ids.active}`);
        const blank = page.locator("form[data-log-set]").first();
        if (await blank.count()) {
            await blank.locator('button[type="submit"]').click();
            await page.waitForTimeout(1200);
            await page.screenshot({ path: path.join(dir, "session-rest-timer.png"), fullPage: false });
        }

        // Text parsing confirmation list (built-in parser locally, no network).
        await page.goto(`${BASE}/food/add?date=${day(0)}&meal=Dinner&tab=text`);
        await page.fill('textarea[name="text"]', "200 г пилешко филе, 150 g cooked rice, a banana, шопска салата");
        await Promise.all([page.waitForLoadState("load"), page.locator('form[action="/food/parse"] button[type="submit"]').click()]);
        await shot(page, path.join(dir, "food-confirm.png"));

        // Empty states with a fresh account.
        await ctx.clearCookies();
        await signIn(page, BASE, "empty@example.test");
        for (const [name, url] of [["empty-today", "/today"], ["empty-train", "/train"], ["empty-progress", "/progress"], ["empty-food", "/food"], ["empty-history", "/train/history"]]) {
            await page.goto(BASE + url);
            await shot(page, path.join(dir, `${name}.png`));
        }
        await ctx.close();
        console.log("done", dir);
    }
}
await browser.close();
