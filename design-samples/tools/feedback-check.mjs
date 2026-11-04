// Drives the feedback features in the running app at 390 px and saves screenshots (SPEC 11):
// parse ratings and the reasons sheet, a report with a large screenshot, a report sent offline, and a browser error.
//   GYM_EMAIL=... GYM_PASSWORD=... node design-samples/tools/feedback-check.mjs [base url] [screenshot dir]
import { chromium } from "playwright-core";
import { appUrl, collectErrors } from "./app.mjs";

const base = appUrl(process.argv[2]);
const out = process.argv[3] || ".";
const email = process.env.GYM_EMAIL || "demo@example.test";
const password = process.env.GYM_PASSWORD || "demo-password-123";

const browser = await chromium.launch({ channel: "chrome", headless: true });
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 });
const page = await context.newPage();
const errors = [];
// The thrown test error and the failed loads while offline are expected.
const expected = (text) => /feedback-check|ERR_INTERNET_DISCONNECTED/.test(text);
collectErrors(page, errors, expected);
const shot = (name) => page.screenshot({ path: `${out}/${name}.png` });
const queue = () => page.evaluate(() => JSON.parse(localStorage.getItem("gym.pendingFeedback.v2." + encodeURIComponent(document.documentElement.dataset.userId)) || "[]").length);
const check = (ok, what) => console.log(`${ok ? "ok  " : "FAIL"} ${what}`);

await page.goto(`${base}/account/login`);
await page.fill('input[name="email"]', email);
await page.fill('input[name="password"]', password);
await Promise.all([page.waitForURL(/\/today/), page.click('button[type="submit"]')]);

// Ratings on Check foods.
await page.goto(`${base}/food/add?tab=text&meal=Lunch`);
await page.fill("textarea[name=text]", "200g chicken breast raw, a banana");
await Promise.all([page.waitForSelector("[data-rating]:not([hidden])"), page.click("form[data-busy] button[type=submit]")]);
await page.waitForTimeout(500);
await shot("1-confirm");
await page.click('[data-rating] [data-rate="Up"]');
check(await page.getAttribute('[data-rating] [data-rate="Up"]', "aria-pressed") === "true", "thumbs up is pressed");
await page.click('[data-rate="Down"][data-item="0"]');
await page.waitForSelector("#parse-sheet:not([hidden])");
await page.waitForTimeout(600);
await shot("2-sheet");
await page.click('#parse-sheet label:has(input[value="WrongFood"])');
await page.fill("#parse-sheet textarea", "It was thigh (feedback-check)");
await page.click("#parse-sheet [data-send-details]");
await page.waitForTimeout(1500);
check(await queue() === 0, "rating and reasons sent");
await page.uncheck('input[name="items[1].Include"]');
await page.fill('input[name="items[0].Grams"]', "300");
await Promise.all([page.waitForURL(/\/food\?date=/), page.click("form[data-module=parse-rating] button[type=submit]")]);
check(true, "saved the corrected list");

// Settings section.
await page.goto(`${base}/settings`);
await page.locator("text=Send feedback").first().scrollIntoViewIfNeeded();
await shot("3-settings");

// A report with a large picture: shrunk on the phone before sending.
await page.goto(`${base}/feedback/new`);
const picture = await page.evaluate(async () => {
    const canvas = document.createElement("canvas");
    canvas.width = 3000;
    canvas.height = 2200;
    const g = canvas.getContext("2d");
    const image = g.createImageData(canvas.width, canvas.height);
    for (let i = 0; i < image.data.length; i += 4) {
        image.data[i] = (i * 7) % 255;
        image.data[i + 1] = Math.random() * 255;
        image.data[i + 2] = (i / 4) % 255;
        image.data[i + 3] = 255;
    }
    g.putImageData(image, 0, 0);
    const blob = await new Promise((r) => canvas.toBlob(r, "image/png"));
    return Array.from(new Uint8Array(await blob.arrayBuffer()));
});
console.log(`     picked picture: ${Math.round(picture.length / 1024)} KB`);
await page.selectOption("#area", "LayoutAndDesign");
await page.fill("#message", "The rest timer overlaps the last set row (feedback-check)");
await page.setInputFiles('input[type="file"]', { name: "big.png", mimeType: "image/png", buffer: Buffer.from(picture) });
await page.waitForSelector("[data-shot-preview]:not([hidden])");
await shot("4-form");
let uploaded = 0;
page.on("request", async (r) => {
    if (r.url().endsWith("/feedback") && r.method() === "POST") {
        uploaded = Number((await r.allHeaders())["content-length"] || 0);
    }
});
await Promise.all([page.waitForURL(/\/feedback\?sent=1/), page.click("form[data-module=feedback-form] button[type=submit]")]);
check(uploaded > 0 && uploaded < 1024 * 1024, `upload is ${Math.round(uploaded / 1024)} KB, under 1 MB`);
await page.waitForTimeout(400);
await shot("5-list");

// Offline: kept on the phone, sent once back online.
await page.goto(`${base}/feedback/new`);
await context.setOffline(true);
await page.evaluate(() => window.dispatchEvent(new Event("offline")));
await page.fill("#message", "Sent from airplane mode (feedback-check)");
check(await page.isDisabled('input[type="file"]'), "screenshot picker is off while offline");
await page.click("form[data-module=feedback-form] button[type=submit]");
await page.waitForSelector("[data-queued]:not([hidden])");
check(await queue() === 1, "report waits on the phone");
await shot("6-offline");
await context.setOffline(false);
await page.evaluate(() => window.dispatchEvent(new Event("online")));
await page.waitForTimeout(1500);
check(await queue() === 0, "report sent when back online");

// A browser error is reported.
await page.evaluate(() => setTimeout(() => { throw new Error("feedback-check error"); }));
await page.waitForTimeout(1000);

// Bulgarian and light theme.
await page.goto(`${base}/settings`);
await page.click('label.segment:has(input[value="Bulgarian"])');
await page.waitForTimeout(800);
await page.click('label.segment:has(input[value="Light"])');
await page.waitForTimeout(800);
await page.goto(`${base}/feedback/new`);
await shot("7-form-bg-light");
await page.goto(`${base}/feedback`);
await shot("8-list-bg-light");
await page.goto(`${base}/settings`);
await page.click('label.segment:has(input[value="English"])');
await page.waitForTimeout(800);
await page.click('label.segment:has(input[value="Dark"])');
await page.waitForTimeout(800);

const unexpected = errors.filter((e) => !expected(e));
check(unexpected.length === 0, `no script errors${unexpected.length ? ": " + unexpected.join(" | ") : ""}`);
await browser.close();
