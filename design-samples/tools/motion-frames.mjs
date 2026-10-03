// Captures motion as frame strips: animations slowed 10× through CDP, a frame every 40 ms of real time
// (4 ms of animation time), then laid out side by side in one PNG per motion.
import { chromium } from "playwright-core";
import fs from "node:fs";
import path from "node:path";

const dir = path.join(path.dirname(new URL(import.meta.url).pathname), "..");
const out = path.join(dir, "screenshots", "motion");
fs.mkdirSync(out, { recursive: true });
const browser = await chromium.launch({ channel: "chrome", headless: true });

async function capture(page, cdp, frames, every, act) {
    const shots = [];
    await cdp.send("Animation.setPlaybackRate", { playbackRate: 0.1 });
    act();
    for (let i = 0; i < frames; i++) {
        shots.push((await page.screenshot({ type: "jpeg", quality: 80 })).toString("base64"));
        await page.waitForTimeout(every);
    }
    await cdp.send("Animation.setPlaybackRate", { playbackRate: 1 });
    await page.waitForTimeout(300);
    return shots;
}

async function strip(name, shots) {
    const html = `<body style="margin:0;background:#888;display:flex;gap:6px;padding:6px">${shots.map((b) => `<img src="data:image/jpeg;base64,${b}" style="width:195px">`).join("")}</body>`;
    const p = await browser.newPage({ viewport: { width: 6 + shots.length * 201, height: 434 } });
    await p.setContent(html);
    await p.screenshot({ path: path.join(out, `${name}.png`) });
    await p.close();
}

for (const s of ["a-fitness", "b-mono", "c-apple"]) {
    const page = await browser.newPage({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, colorScheme: "dark" });
    const cdp = await page.context().newCDPSession(page);
    await cdp.send("Animation.enable");
    await page.goto(`file://${dir}/${s}.html?timer=off`);
    await page.waitForTimeout(1500);

    await strip(`${s}-push`, await capture(page, cdp, 8, 500, () => page.click("[data-go=session][data-dir=forward]")));
    await page.evaluate(() => scrollTo(0, 120));
    const target = page.locator("[data-set]:not(.is-done) [data-done]").first();
    await strip(`${s}-set-done`, await capture(page, cdp, 8, 550, () => target.click()));
    await strip(`${s}-sheet`, await capture(page, cdp, 8, 550, () => page.click("[data-timer-pill]", { position: { x: 20, y: 20 } })));
    await page.click("[data-sheet-close]");
    await page.waitForTimeout(700);
    await strip(`${s}-tab`, await capture(page, cdp, 6, 400, () => page.click("[data-tab=today]")));
    await page.close();
    console.log("frames", s);
}
await browser.close();
