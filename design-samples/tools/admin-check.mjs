// Drives the admin area in the running app at 390 px and saves screenshots (SPEC 12): the read-only AI section for
// the other account, the passkey unlock with a virtual authenticator, every admin page, a status with a reply, a
// What's new line added and removed, and a model change. The demo account needs the Dev role first:
//   dotnet run --project src/Plate.Web -- role grant dev --email demo@example.test
//   node design-samples/tools/admin-check.mjs [base url] [screenshot dir]
import { chromium } from "playwright-core";
import { appUrl, collectErrors, signIn } from "./app.mjs";

const base = appUrl(process.argv[2]);
const out = process.argv[3] || ".";
const browser = await chromium.launch({ channel: "chrome", headless: true });
const check = (ok, what) => console.log(`${ok ? "ok  " : "FAIL"} ${what}`);
const noSideScroll = (page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);

async function newPage() {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 });
    const page = await context.newPage();
    const errors = [];
    collectErrors(page, errors);
    return { context, page, errors };
}

// The other account: read-only AI models, and no admin area.
{
    const { context, page, errors } = await newPage();
    // A plain account made for this check: create-user --email other@dev.test --name Other (password other-account-pass).
    await page.goto(`${base}/account/login`);
    await page.fill('input[name="email"]', process.env.GYM_OTHER_EMAIL || "other@dev.test");
    await page.fill('input[name="password"]', process.env.GYM_OTHER_PASSWORD || "other-account-pass");
    await Promise.all([page.waitForURL(/\/today/), page.click('button[type="submit"]')]);
    await page.goto(`${base}/settings`);
    await page.locator("text=AI models").first().scrollIntoViewIfNeeded();
    await page.screenshot({ path: `${out}/admin-0-settings-other.png` });
    check(await page.locator("input[name=apiKey]").count() === 0, "other account: no key field in Settings");
    check(await page.locator('a[href="/admin"]').count() === 0, "other account: no Admin row");
    const response = await page.goto(`${base}/admin`);
    check(response.status() === 404, `other account: /admin is ${response.status()}`);
    // The 404 for /admin itself shows as a failed load in the console; nothing else may.
    check(errors.filter((e) => !/status of 404/.test(e)).length === 0, `other account: no script errors ${errors.join(" | ")}`);
    await context.close();
}

const { page, errors } = await newPage();
const cdp = await page.context().newCDPSession(page);
await cdp.send("WebAuthn.enable");
await cdp.send("WebAuthn.addVirtualAuthenticator", { options: { protocol: "ctap2", transport: "internal", hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });
await signIn(page, base);

// A passkey for this account, then the unlock.
await page.goto(`${base}/settings`);
await page.click("[data-module=passkey-add]");
await page.waitForTimeout(1500);
await page.goto(`${base}/admin/feedback`);
check(page.url().includes("/admin/unlock"), "locked: sent to the unlock page");
await page.screenshot({ path: `${out}/admin-1-unlock.png` });
await Promise.all([page.waitForURL(/\/admin\/feedback$/), page.click("[data-module=passkey-unlock]")]);
check(true, "unlocked with the passkey and back on Reports");

const pages = [["2-overview", "/admin"], ["3-reports", "/admin/feedback?status=all&since=365d"], ["5-parses", "/admin/parses?since=365d"], ["6-errors", "/admin/errors?since=90d"],
    ["7-changelog", "/admin/changelog"], ["8-ai", "/admin/ai"], ["9-eval", "/admin/ai/eval"], ["10-users", "/admin/users"], ["12-model", "/admin/ai/model/matcher"]];
for (const [name, path] of pages) {
    const response = await page.goto(`${base}${path}`);
    check(response.status() === 200 && await noSideScroll(page), `${path}: ${response.status()}, no sideways scroll`);
    await page.waitForTimeout(300);
    await page.screenshot({ path: `${out}/admin-${name}.png`, fullPage: true });
}

// A report: status and reply.
await page.goto(`${base}/admin/feedback?status=all&since=365d`);
const first = page.locator("a.feedback-item").first();
if (await first.count()) {
    await Promise.all([page.waitForURL(/\/admin\/feedback\/\d+$/), first.click()]);
    await page.waitForTimeout(800);
    check(await noSideScroll(page), "report page: no sideways scroll");
    await page.screenshot({ path: `${out}/admin-4-report.png`, fullPage: true });
    await page.selectOption("#status", "Planned");
    await page.fill("#reply", "Planned for next week (admin-check).");
    await Promise.all([page.waitForURL(/\/admin\/feedback\/\d+/), page.click('form[action$="/status"] button[type=submit]')]);
    check(await page.locator("#reply").inputValue() === "Planned for next week (admin-check).", "status and reply saved");
}

// What's new: add and remove.
await page.goto(`${base}/admin/changelog`);
await page.fill("#textEn", "admin-check line");
await page.fill("#textBg", "ред от admin-check");
// Soft navigation posts with fetch and the URL stays the same, so wait for the new line itself.
await page.click('form[action="/admin/changelog"] button[type=submit]');
await page.locator(".feedback-item", { hasText: "admin-check line" }).first().waitFor({ timeout: 5000 }).catch(() => {});
const added = await page.locator(".feedback-item", { hasText: "admin-check line" }).count();
check(added === 1, `What's new line added (${added})`);
page.once("dialog", (d) => d.accept());
await page.locator(".feedback-item", { hasText: "admin-check line" }).locator("button").click();
await page.waitForTimeout(1000);
check(await page.locator(".feedback-item", { hasText: "admin-check line" }).count() === 0, "What's new line removed");

// The audit shows it all.
await page.goto(`${base}/admin/audit`);
await page.screenshot({ path: `${out}/admin-11-audit.png`, fullPage: true });
const audit = await page.textContent("main");
check(/unlock/.test(audit) && /changelog-add/.test(audit) && /changelog-remove/.test(audit), "audit lists the unlock and the changes");

// Lock now.
await page.goto(`${base}/admin`);
await Promise.all([page.waitForURL(/\/settings/), page.click('form[action="/admin/lock"] button')]);
await page.goto(`${base}/admin`);
check(page.url().includes("/admin/unlock"), "Lock now: sent to the unlock page again");

check(errors.length === 0, `no script errors ${errors.join(" | ")}`);
await browser.close();
