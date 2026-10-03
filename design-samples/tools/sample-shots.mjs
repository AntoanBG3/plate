// Screenshots the design samples at 390×844 @3x: Today, the session with the rest timer running,
// the timer sheet open, and a scrolled session (compact top bar). Also records a short motion video.
//   node design-samples/tools/sample-shots.mjs [a-fitness,b-mono,...] [--video]
import { chromium } from "playwright-core";
import fs from "node:fs";
import path from "node:path";

const dir = path.join(path.dirname(new URL(import.meta.url).pathname), "..");
const out = path.join(dir, "screenshots");
fs.mkdirSync(out, { recursive: true });
const only = (process.argv[2] && !process.argv[2].startsWith("--")) ? process.argv[2].split(",") : null;
const video = process.argv.includes("--video");
const samples = fs.readdirSync(dir).filter((f) => /^[a-z]-.*\.html$/.test(f)).map((f) => f.replace(".html", ""))
    .filter((s) => !only || only.includes(s));

const browser = await chromium.launch({ channel: "chrome", headless: true });
for (const s of samples) {
    for (const theme of ["light", "dark"]) {
        const ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true, colorScheme: theme });
        const page = await ctx.newPage();
        const url = (q) => `file://${dir}/${s}.html?theme=${theme}&${q}`;
        await page.goto(url("screen=today"));
        await page.waitForTimeout(1600);
        await page.screenshot({ path: `${out}/${s}-${theme}-today.png` });
        await page.goto(url("screen=session"));
        await page.waitForTimeout(900);
        await page.screenshot({ path: `${out}/${s}-${theme}-session.png` });
        await page.evaluate(() => scrollTo(0, 420));
        await page.waitForTimeout(500);
        await page.screenshot({ path: `${out}/${s}-${theme}-session-scrolled.png` });
        await page.goto(url("screen=session&sheet=open"));
        await page.waitForTimeout(900);
        await page.screenshot({ path: `${out}/${s}-${theme}-timer-sheet.png` });
        await ctx.close();
    }
    if (video) {
        const vdir = path.join(out, "motion");
        const ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 1, isMobile: true, hasTouch: true, colorScheme: "dark", recordVideo: { dir: vdir, size: { width: 390, height: 844 } } });
        const page = await ctx.newPage();
        await page.goto(`file://${dir}/${s}.html?screen=today&timer=off`);
        await page.waitForTimeout(1800);
        await page.click("[data-go=session][data-dir=forward]");
        await page.waitForTimeout(900);
        await page.click("[data-set]:not(.is-done) [data-done]");
        await page.waitForTimeout(1400);
        await page.click("[data-add-set]");
        await page.waitForTimeout(900);
        await page.click("[data-timer-pill]", { position: { x: 30, y: 30 } });
        await page.waitForTimeout(1500);
        await page.click("[data-sheet-close]");
        await page.waitForTimeout(900);
        await page.click("[data-tab=today]");
        await page.waitForTimeout(1200);
        const file = await page.video().path();
        await ctx.close();
        fs.renameSync(file, path.join(vdir, `${s}.webm`));
    }
    console.log("shot", s);
}
await browser.close();
