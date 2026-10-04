// Registers a passkey in Settings with Chrome's virtual authenticator, signs out, signs in with it, removes it.
// Usage: node design-samples/tools/passkey-check.mjs [baseUrl] [screenshotDir]   (demo account, no passkeys yet)
import { chromium } from "playwright-core";
import { appUrl, collectErrors, signIn } from "./app.mjs";
import assert from "node:assert/strict";
const base = appUrl(process.argv[2]);
const SP = process.argv.slice(2).find((a) => !/^https?:/.test(a)) || ".";
const browser = await chromium.launch({ channel: "chrome" });
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, colorScheme: "dark" });
const page = await context.newPage();
const errors = [];
collectErrors(page, errors);
// Chrome's virtual authenticator answers the autofill (conditional) request by itself, racing the button.
// A phone only completes it when the user picks the passkey, so test the button path alone.
await context.addInitScript(() => {
    if (window.PublicKeyCredential) {
        PublicKeyCredential.isConditionalMediationAvailable = async () => false;
    }
});
const cdp = await context.newCDPSession(page);
await cdp.send("WebAuthn.enable");
await cdp.send("WebAuthn.addVirtualAuthenticator", { options: { protocol: "ctap2", transport: "internal", hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });

await page.goto(`${base}/account/login`);
await page.screenshot({ path: `${SP}/login-passkey.png` });
await signIn(page, base);

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
await Promise.all([page.waitForURL(/\/today/, { timeout: 10000 }), page.click("[data-module=passkey-login]")]);
console.log("signed in with passkey:", page.url());
const auth = (await context.cookies()).find((c) => /gym\.auth$/.test(c.name));
console.log("auth cookie expires:", auth.expires === -1 ? "session only" : new Date(auth.expires * 1000).toISOString());
assert.notEqual(auth.expires, -1, "passkey sign-in must stay signed in like a password sign-in (persistent cookie)");

await page.goto(`${base}/settings`);
page.on("dialog", (d) => d.accept());
const count = () => page.$$eval('form[action^="/settings/passkeys/"]', (f) => f.length);
assert.equal(await count(), 1, "one passkey listed");
await page.evaluate(() => document.querySelector('form[action^="/settings/passkeys/"]').requestSubmit());
await page.waitForFunction(() => !document.querySelector('form[action^="/settings/passkeys/"]') && !document.documentElement.dataset.nav, null, { timeout: 10000 });
console.log("removed; banner:", await page.locator(".banner").first().innerText().catch(() => "none"));
assert.deepEqual(errors, []);
console.log("passkey-check passed");
await browser.close();
