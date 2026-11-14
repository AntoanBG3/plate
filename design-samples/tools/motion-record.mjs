// Records the running app's motion at iPhone size, for comparing before and after a change:
//   <out>/tour-<engine>.webm  a real-speed walk through the main interactions
//   <out>/<name>.png          one frame strip per interaction, animations slowed 10× (Chromium only)
//   <out>/timeline.json       every animation each interaction starts: target, delay, duration, easing, and when
//                             the interaction settles (last frame with something still moving)
//   node design-samples/tools/motion-record.mjs <outDir> [tour,strips,timeline] [chromium|webkit] [name pattern]
// Needs playwright (NODE_PATH or a local install) and the local dev app with seed-demo.sql. Never run against wizard.
// Logging a set and deleting a food entry change the demo data, so re-run seed-demo.sql between runs.
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { appUrl, signIn } from "./app.mjs";

const require = createRequire(process.env.NODE_PATH ? path.join(process.env.NODE_PATH, "x.js") : import.meta.url);
const playwright = require("playwright");

const BASE = appUrl();
const out = process.argv[2] || "design-samples/screenshots/motion/before";
const parts = (process.argv[3] || "tour,strips,timeline").split(",");
const engine = process.argv[4] || "chromium";
const only = new RegExp(process.argv[5] || ".");
const SLOW = 10;
fs.mkdirSync(out, { recursive: true });

const browser = await playwright[engine].launch();
const phone = { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: engine === "chromium", hasTouch: true, colorScheme: "dark", serviceWorkers: "block" };

// Timers follow the slow-motion rate, so a class removed after 600 ms or an htmx swap delay doesn't cut a slowed
// animation short. __trace samples document.getAnimations() every frame from the next pointerdown until nothing has
// moved for 250 ms.
const instrument = () => {
    const timeout = window.setTimeout;
    window.setTimeout = (fn, ms, ...rest) => timeout(fn, (ms || 0) * (window.__rate || 1), ...rest);

    const describe = (el) => {
        if (!el || !el.tagName) {
            return "?";
        }
        const cls = typeof el.className === "string" && el.className ? "." + el.className.trim().split(/\s+/).slice(0, 3).join(".") : "";
        return el.tagName.toLowerCase() + (el.id ? "#" + el.id : "") + cls;
    };

    window.__trace = (maxMs) => new Promise((resolve) => {
        const seen = new Map();
        let down = null;
        let page = null;
        let last = 0;
        const began = performance.now();
        window.addEventListener("pointerdown", (e) => { down = e.timeStamp; }, { capture: true, once: true });
        document.addEventListener("gym:page", () => { page = performance.now(); }, { once: true });
        const tick = (now) => {
            for (const a of document.getAnimations()) {
                const effect = a.effect;
                if (!effect || a.playState !== "running") {
                    continue;
                }
                const timing = effect.getComputedTiming();
                if (timing.iterations === Infinity) {
                    continue;
                }
                if (!seen.has(a)) {
                    const props = new Set();
                    effect.getKeyframes().forEach((k) => Object.keys(k).forEach((p) => !["offset", "easing", "composite", "computedOffset"].includes(p) && props.add(p)));
                    seen.set(a, {
                        target: describe(effect.target) + (effect.pseudoElement || ""),
                        name: a.animationName || (a.transitionProperty ? "transition:" + a.transitionProperty : "script"),
                        props: [...props].join(","),
                        delay: Math.round(timing.delay), duration: Math.round(timing.duration),
                        easing: effect.getTiming().easing.slice(0, 40),
                        from: now, to: now,
                    });
                }
                seen.get(a).to = now;
                last = now;
            }
            const idleSince = Math.max(last, down || now);
            if ((down !== null && now - idleSince > 250) || now - began > maxMs) {
                const rel = (t) => (t === null || down === null ? null : Math.round(t - down));
                resolve({
                    pageAt: rel(page), settledAt: last ? rel(last) : 0,
                    animations: [...seen.values()].map((s) => Object.assign(s, { from: rel(s.from), to: rel(s.to) })).sort((x, y) => x.from - y.from),
                });
                return;
            }
            requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
    });

    // A dot where the finger lands, so the tour shows what was tapped.
    window.addEventListener("pointerdown", (e) => {
        if (!window.__dots) {
            return;
        }
        const dot = document.createElement("div");
        dot.style.cssText = `position:fixed;left:${e.clientX - 14}px;top:${e.clientY - 14}px;width:28px;height:28px;border-radius:50%;background:rgb(255 255 255 / .45);box-shadow:0 0 0 1.5px rgb(0 0 0 / .35);pointer-events:none;z-index:99999;transition:opacity .35s, transform .35s`;
        document.documentElement.appendChild(dot);
        requestAnimationFrame(() => { dot.style.opacity = "0"; dot.style.transform = "scale(1.6)"; });
        timeout(() => dot.remove(), 400);
    }, true);
};

async function open(options = {}) {
    const ctx = await browser.newContext(Object.assign({}, phone, options));
    await ctx.addInitScript(instrument);
    const page = await ctx.newPage();
    await signIn(page, BASE);
    return { ctx, page };
}

const settle = (page, ms = 600) => page.waitForTimeout(ms);
const tab = (name) => `.tabbar .tab[href="/${name}"]`;

// The interactions. `at` is where to start (a path, or a function that gets the page there); `act` is the one
// gesture that is recorded; `frames`/`every` size the strip (real ms between frames; animation time is a tenth).
async function sessionPath(page) {
    await page.goto(`${BASE}/train`);
    return page.locator('#main a[href^="/train/sessions/"]').first().getAttribute("href");
}
async function addFoodPath(page) {
    await page.goto(`${BASE}/food`);
    return page.locator('#main a[href^="/food/add"]').first().getAttribute("href");
}
async function swipeOpen(page) {
    const row = page.locator("[data-swipe-row] .swipe-content").first();
    // Mid-screen: a row brought just into view sits under the tab bar.
    await row.evaluate((el) => el.scrollIntoView({ block: "center" }));
    await page.waitForTimeout(200);
    const box = await row.boundingBox();
    const y = box.y + box.height / 2;
    await page.mouse.move(box.x + box.width - 40, y);
    await page.mouse.down();
    for (let x = 40; x <= 140; x += 20) {
        await page.mouse.move(box.x + box.width - x, y);
        await page.waitForTimeout(16);
    }
    await page.mouse.up();
}

// Press and hold a button, then slide off it before letting go, so nothing is opened.
async function press(page, holdMs) {
    const b = await page.locator("#main .btn").first().boundingBox();
    await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2);
    await page.mouse.down();
    await page.waitForTimeout(holdMs);
    await page.mouse.move(b.x + b.width / 2, b.y + b.height + 120);
    await page.mouse.up();
}

const scenarios = [
    { name: "tab-next", at: "/today", act: (p) => p.tap(tab("train")) },
    { name: "tab-prev", at: "/train", act: (p) => p.tap(tab("today")) },
    { name: "tab-far", at: "/today", act: (p) => p.tap(tab("settings")) },
    { name: "push", at: "/train", act: (p) => p.tap('#main a[href^="/train/sessions/"]') },
    { name: "pop", at: sessionPath, act: (p) => p.tap(".topbar-back") },
    { name: "push-settings", at: "/settings", act: (p) => p.tap('#main a[href="/settings/general"]') },
    { name: "menu-open", at: sessionPath, act: (p) => p.tap("#main [data-menu-trigger]"), frames: 10, every: 350 },
    { name: "menu-close", at: sessionPath, before: async (p) => { await p.tap("#main [data-menu-trigger]"); await settle(p); }, act: (p) => p.touchscreen.tap(60, 500), frames: 8, every: 350 },
    { name: "set-done", at: sessionPath, before: (p) => p.locator("form.set-row:not(.done) .done-btn").first().scrollIntoViewIfNeeded(), act: (p) => p.locator("form.set-row:not(.done) .done-btn").first().tap(), frames: 12, every: 400 },
    { name: "sheet-open", at: "/food", act: (p) => p.locator('#main [data-sheet-open="quick-sheet"]').first().tap(), frames: 12, every: 400 },
    { name: "sheet-close", at: "/food", before: async (p) => { await p.locator('#main [data-sheet-open="quick-sheet"]').first().tap(); await settle(p, 900); }, act: (p) => p.touchscreen.tap(195, 30), frames: 12, every: 400 },
    { name: "segment-slide", at: addFoodPath, act: (p) => p.locator(".segmented[data-slide] a.segment:not(.is-active)").first().tap() },
    { name: "segment-range", at: "/progress", act: (p) => p.locator(".segmented a.segment:not(.is-active)").first().tap() },
    { name: "switch", at: "/settings/general", before: (p) => p.locator(".switch").first().scrollIntoViewIfNeeded(), act: (p) => p.locator(".switch").first().tap(), frames: 8, every: 350 },
    { name: "swipe-delete", at: "/food", before: async (p) => { await swipeOpen(p); await settle(p); }, act: (p) => p.locator("[data-swipe-row].is-open .set-delete, [data-swipe-row].is-open .set-delete-form button").first().tap(), frames: 12, every: 400 },
    { name: "press", at: "/today", act: (p) => press(p, 300), slowAct: (p) => press(p, 1500), frames: 8, every: 300 },
];

async function prepare(page, s) {
    const target = typeof s.at === "function" ? await s.at(page) : s.at;
    await page.goto(BASE + target);
    await settle(page, 1300);
    if (s.before) {
        await s.before(page);
        await settle(page, 300);
    }
}

async function makeStrip(name, shots, stepMs) {
    const cells = shots.map((b, i) => `<figure style="margin:0"><img src="data:image/jpeg;base64,${b}" style="width:195px;display:block"><figcaption style="font:11px system-ui;color:#fff;text-align:center">${Math.round(i * stepMs)} ms</figcaption></figure>`).join("");
    const p = await browser.newPage({ viewport: { width: 6 + shots.length * 201, height: 448 } });
    await p.setContent(`<body style="margin:0;background:#666;display:flex;gap:6px;padding:6px">${cells}</body>`);
    await p.screenshot({ path: path.join(out, `${name}.png`) });
    await p.close();
}

if (parts.includes("timeline")) {
    const { ctx, page } = await open();
    const timeline = {};
    for (const s of scenarios.filter((x) => only.test(x.name))) {
        try {
            await prepare(page, s);
            // Listening before the gesture is sent: a bare touchscreen tap doesn't wait for anything.
            await page.evaluate(() => { window.__tracing = window.__trace(4000); });
            await s.act(page);
            timeline[s.name] = await page.evaluate(() => window.__tracing);
            const t = timeline[s.name];
            console.log(`${s.name.padEnd(14)} page ${String(t.pageAt).padStart(4)} ms  settled ${String(t.settledAt).padStart(4)} ms  ${t.animations.length} animations`);
        } catch (error) {
            timeline[s.name] = { error: String(error).split("\n")[0] };
            console.log(`${s.name}: ${timeline[s.name].error}`);
        }
        await settle(page, 500);
    }
    const file = path.join(out, "timeline.json");
    const earlier = process.argv[5] && fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, "utf8")) : {};
    fs.writeFileSync(file, JSON.stringify(Object.assign(earlier, timeline), null, 1));
    await ctx.close();
}

if (parts.includes("strips") && engine === "chromium") {
    const { ctx, page } = await open();
    const cdp = await ctx.newCDPSession(page);
    await cdp.send("Animation.enable");
    for (const s of scenarios.filter((x) => only.test(x.name))) {
        try {
            await prepare(page, s);
            const frames = s.frames || 14;
            const every = s.every || 300;
            await page.evaluate((rate) => { window.__rate = rate; }, SLOW);
            await cdp.send("Animation.setPlaybackRate", { playbackRate: 1 / SLOW });
            const shots = [(await page.screenshot({ type: "jpeg", quality: 70, scale: "css" })).toString("base64")];
            const acting = (s.slowAct || s.act)(page).catch(() => {});
            const start = Date.now();
            for (let i = 1; i < frames; i++) {
                await page.waitForTimeout(Math.max(0, start + i * every - Date.now()));
                shots.push((await page.screenshot({ type: "jpeg", quality: 70, scale: "css" })).toString("base64"));
            }
            await acting;
            await cdp.send("Animation.setPlaybackRate", { playbackRate: 1 });
            await page.evaluate(() => { window.__rate = 1; }).catch(() => {});
            await makeStrip(s.name, shots, every / SLOW);
            console.log("strip", s.name);
        } catch (error) {
            await cdp.send("Animation.setPlaybackRate", { playbackRate: 1 }).catch(() => {});
            console.log(`strip ${s.name}: ${String(error).split("\n")[0]}`);
        }
        await settle(page, 800);
    }
    await ctx.close();
}

if (parts.includes("tour")) {
    const dir = path.join(out, "video-tmp");
    const { ctx, page } = await open({ recordVideo: { dir, size: { width: 390, height: 844 } }, deviceScaleFactor: 1 });
    await ctx.addInitScript(() => { window.__dots = true; });
    const pause = (ms = 900) => page.waitForTimeout(ms);
    const step = async (label, fn) => {
        try {
            await fn();
        } catch (error) {
            console.log(`tour ${label}: ${String(error).split("\n")[0]}`);
        }
        await pause();
    };
    await page.goto(`${BASE}/today`);
    await pause(1800);
    await step("scroll today", async () => { await page.mouse.wheel(0, 700); await pause(700); await page.mouse.wheel(0, -700); });
    for (const t of ["train", "food", "progress", "settings", "today", "train"]) {
        await step(`tab ${t}`, () => page.tap(tab(t)));
    }
    await step("open session", () => page.tap('#main a[href^="/train/sessions/"]'));
    await step("log a set", async () => { const b = page.locator("form.set-row:not(.done) .done-btn").first(); await b.scrollIntoViewIfNeeded(); await pause(400); await b.tap(); });
    await step("log another", () => page.locator("form.set-row:not(.done) .done-btn").first().tap());
    await step("menu", () => page.locator("#main [data-menu-trigger]").first().tap());
    await step("menu away", () => page.touchscreen.tap(60, 420));
    await step("rest sheet", () => page.locator('[data-sheet-open="rest-sheet"]').first().tap());
    await step("rest sheet away", () => page.touchscreen.tap(195, 30));
    await step("back", () => page.tap(".topbar-back"));
    await step("history", () => page.tap('#main a[href="/train/history"]'));
    await step("back", () => page.tap(".topbar-back"));
    await step("food", () => page.tap(tab("food")));
    await step("meal menu", () => page.locator("#main [data-menu-trigger]").first().tap());
    await step("menu away", () => page.touchscreen.tap(60, 300));
    await step("quick add", () => page.locator('#main [data-sheet-open="quick-sheet"]').first().tap());
    await step("sheet away", () => page.touchscreen.tap(195, 30));
    await step("add food", () => page.locator('#main a[href^="/food/add"]').first().tap());
    for (let i = 0; i < 3; i++) {
        await step("segment", () => page.locator(".segmented[data-slide] a.segment.is-active + a.segment").tap());
    }
    await step("back", () => page.tap(".topbar-back"));
    await step("entry", () => page.locator('#main a[href^="/food/entries/"]').first().tap());
    await step("back", () => page.tap(".topbar-back"));
    await step("swipe", () => swipeOpen(page));
    await step("delete", () => page.locator("[data-swipe-row].is-open .set-delete, [data-swipe-row].is-open .set-delete-form button").first().tap());
    await pause(1200);
    await step("progress", () => page.tap(tab("progress")));
    await step("range", () => page.locator(".segmented a.segment:not(.is-active)").first().tap());
    await step("settings", () => page.tap(tab("settings")));
    await step("general", () => page.tap('#main a[href="/settings/general"]'));
    await step("switch", async () => { const sw = page.locator(".switch").first(); await sw.scrollIntoViewIfNeeded(); await pause(300); await sw.tap(); await pause(500); await sw.tap(); });
    await step("back", () => page.tap(".topbar-back"));
    const video = page.video();
    await ctx.close();
    const file = path.join(out, `tour-${engine}.webm`);
    fs.renameSync(await video.path(), file);
    fs.rmSync(dir, { recursive: true, force: true });
    console.log("tour", file);
}

await browser.close();
