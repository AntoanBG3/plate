// Shared helpers for the tools that drive the running app (screens, nav-check, nav-timing, sw-check, passkey-check).

// The app's base URL: the first argument if it looks like one, else GYM_URL, else the local dev server.
export function appUrl(arg) {
    return arg && /^https?:/.test(arg) ? arg : process.env.GYM_URL || "http://localhost:5130";
}

// Signs in through the login form as one of the demo accounts from seed-demo.sql.
export async function signIn(page, base, email = "demo@example.test") {
    await page.goto(`${base}/account/login`);
    await page.fill('input[name="email"]', email);
    await page.fill('input[name="password"]', "demo-password-123");
    await Promise.all([page.waitForURL(/\/today/), page.click('button[type="submit"]')]);
}

// Collects script errors and console errors from a page into `errors`, skipping messages `ignore` accepts.
export function collectErrors(page, errors, ignore = () => false) {
    page.on("pageerror", (e) => errors.push(e.message));
    page.on("console", (m) => m.type() === "error" && !ignore(m.text()) && errors.push(m.text()));
}
