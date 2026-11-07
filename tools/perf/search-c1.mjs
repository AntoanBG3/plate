// Usage: NODE_TLS_REJECT_UNAUTHORIZED=0 node tools/perf/search-c1.mjs baseline output.json
// Requires the audit branch's isolated synthetic stack at https://localhost:5443.
// Sequential only: five batches of ten requests per case; no production or model calls.
import { writeFileSync } from "node:fs";
import { performance } from "node:perf_hooks";

const [label, output] = process.argv.slice(2);
if (!label || !output) throw new Error("Supply a label and output path");
const base = "https://localhost:5443";
const cookies = new Map();
let token = "";
async function request(path, form) {
    const start = performance.now();
    const response = await fetch(base + path, {
        method: form ? "POST" : "GET", redirect: "manual", signal: AbortSignal.timeout(45000),
        headers: {
            Cookie: [...cookies].map(([key, value]) => `${key}=${value}`).join("; "),
            ...(form ? { "Content-Type": "application/x-www-form-urlencoded", RequestVerificationToken: token, "HX-Request": "true" } : {}),
        },
        body: form ? new URLSearchParams(form) : undefined,
    });
    for (const cookie of response.headers.getSetCookie()) {
        const pair = cookie.split(";")[0], at = pair.indexOf("=");
        cookies.set(pair.slice(0, at), pair.slice(at + 1));
    }
    const html = await response.text(), timing = response.headers.get("server-timing") || "";
    return {
        status: response.status, ms: performance.now() - start,
        app: Number(/app;dur=([\d.]+)/.exec(timing)?.[1] || 0),
        db: Number(/db;dur=([\d.]+)/.exec(timing)?.[1] || 0),
        queries: Number(/desc="(\d+) queries/.exec(timing)?.[1] || 0), html,
    };
}
const login = await request("/account/login");
token = /name="__RequestVerificationToken"[^>]*value="([^"]+)"/.exec(login.html)?.[1];
if ((await request("/account/login", { email: "demo@example.test", password: "demo-password-123", __RequestVerificationToken: token })).status !== 302) throw new Error("Synthetic sign-in failed");
const today = await request("/today");
if (today.status !== 200) throw new Error("Synthetic Today failed");
token = /name="__RequestVerificationToken"[^>]*value="([^"]+)"/.exec(today.html)?.[1] || token;
const version = /data-version="([^"]+)"/.exec(today.html)?.[1];
const cases = ["chicken", "banana", "ориз", "кисело мляко", "chikcen"].map(query => ({
    name: "search:" + query, path: "/food/add?tab=search&meal=Lunch&q=" + encodeURIComponent(query),
}));
cases.push({ name: "parse", path: "/food/parse", form: { date: "2026-11-05", meal: "Lunch", text: "200g chicken breast, 150g cooked rice, a banana" } });
const samples = [];
for (const entry of cases) {
    for (let warmup = 0; warmup < 5; warmup++) {
        if ((await request(entry.path, entry.form)).status !== 200) throw new Error("Warmup failed: " + entry.name);
    }
    for (let batch = 1; batch <= 5; batch++) {
        for (let index = 0; index < 10; index++) {
            const { html, ...sample } = await request(entry.path, entry.form);
            if (sample.status !== 200) throw new Error("Request failed: " + entry.name);
            const ids = [...html.matchAll(/name="(?:foodItemId|items\[\d+\]\.FoodItemId)"[^>]*value="(\d+)"/gi)].map(match => Number(match[1]));
            samples.push({ name: entry.name, batch, index, c: 1, ...sample, ids });
        }
    }
    writeFileSync(output, JSON.stringify({ label, version, environment: "native linux-arm64 Release; app 1 CPU/256 MiB; PG18 256 MiB/no CPU cap; pool 8; 5 synthetic users/730 days; warm buffers; no concurrent workload", samples }, null, 2) + "\n");
    console.log(entry.name + " complete");
}
