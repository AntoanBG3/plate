// Registers a passkey in Settings with Chrome's virtual authenticator, signs out, signs in with it, removes it.
// Usage: node design-samples/tools/passkey-check.mjs [screenshotDir]   (demo account, no passkeys yet)
import { chromium } from "playwright-core";
import assert from "node:assert/strict";
const SP = process.argv[2] || ".", base = "http://localhost:5130";
const browser = await chromium.launch({ channel: "chrome" });
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, colorScheme: "dark" });
const page = await context.newPage();
const errors = [];
page.on("pageerror", (e) => errors.push(e.message));
page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
const cdp = await context.newCDPSession(page);
await cdp.send("WebAuthn.enable");
await cdp.send("WebAuthn.addVirtualAuthenticator", { options: { protocol: "ctap2", transport: "internal", hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });

await page.goto(`${base}/account/login`);
await page.screenshot({ path: `${SP}/login-passkey.png` });
await page.fill('input[name="email"]', "demo@example.test");
await page.fill('input[name="password"]', "demo-password-123");
await Promise.all([page.waitForURL(/\/today/), page.click('button[type="submit"]')]);

await page.goto(`${base}/settings`);
await page.click("[data-passkey-add], [data-module=passkey-add]");
await page.waitForSelector('form[action^="/settings/passkeys/"]', { timeout: 10000 });
console.log("registered; banner:", await page.locator(".banner").first().innerText().catch(() => "none"));
await page.waitForTimeout(1200);
await page.evaluate(() => document.querySelector('form[action^="/settings/passkeys/"]').scrollIntoView({ block: "center" }));
await page.waitForTimeout(300);
await page.screenshot({ path: `${SP}/settings-passkey.png` });

await context.clearCookies();
await page.goto(`${base}/account/login`);
await page.waitForSelector("[data-module=passkey-login]:not([hidden])");
// Chrome's virtual authenticator may answer the autofill (conditional) request on its own; either path signs in.
page.click("[data-module=passkey-login]", { timeout: 3000 }).catch(() => {});
await page.waitForURL(/\/today/, { timeout: 10000 });
console.log("signed in with passkey:", page.url());

await page.goto(`${base}/settings`);
page.on("dialog", (d) => d.accept());
const count = () => page.$$eval('form[action^="/settings/passkeys/"]', (f) => f.length);
assert.equal(await count(), 1, "one passkey listed");
await page.evaluate(() => document.querySelector('form[action^="/settings/passkeys/"]').requestSubmit());
await page.waitForFunction(() => !document.querySelector('form[action^="/settings/passkeys/"]') && !document.documentElement.dataset.nav, null, { timeout: 10000 });
console.log("removed; banner:", await page.locator(".banner").first().innerText().catch(() => "none"));
console.log("errors:", errors);
await browser.close();
