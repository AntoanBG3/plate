// Usage: NODE_TLS_REJECT_UNAUTHORIZED=0 node tools/perf/concurrency.mjs [search,parse]
// One against five simultaneous requests for food search and the fallback parser's parse, against the isolated
// local perf stack (tools/perf/setup.sh) only, with what Postgres's active sessions wait on during the concurrent
// runs. It first prints the database container's CPU cap and when the foods table was analyzed, the two things
// that explained the audit's tenfold c5 slowdown (docs/perf/2026-11-23-c5-slowdown.md).
import { performance } from "node:perf_hooks";
import { execFile, execFileSync } from "node:child_process";
const base = "https://localhost:5443";
const cap = Number(execFileSync("docker", ["inspect", "plate-perf-db", "--format", "{{.HostConfig.NanoCpus}}"], { encoding: "utf8" }).trim()) / 1e9;
const analyzed = execFileSync("docker", ["exec", "plate-perf-db", "psql", "-U", "gym", "-d", "gym_perf", "-tAc",
  `select coalesce(greatest(last_analyze, last_autoanalyze)::text, 'never') from pg_stat_user_tables where relname = 'FoodItems'`], { encoding: "utf8" }).trim();
console.log(`database CPU cap: ${cap ? cap + " CPU" : "none"}; foods table analyzed: ${analyzed}`);
const cookies = new Map(); let token = "";
async function request(path, form) {
  const start = performance.now();
  const r = await fetch(base + path, { method: form ? "POST" : "GET", redirect: "manual",
    headers: { Cookie: [...cookies].map(([k, v]) => `${k}=${v}`).join("; "),
      ...(form ? { "Content-Type": "application/x-www-form-urlencoded", RequestVerificationToken: token, "HX-Request": "true" } : {}) },
    body: form ? new URLSearchParams(form) : undefined });
  for (const c of r.headers.getSetCookie()) { const p = c.split(";")[0], i = p.indexOf("="); cookies.set(p.slice(0, i), p.slice(i + 1)); }
  const html = await r.text(), t = r.headers.get("server-timing") || "";
  return { status: r.status, ms: performance.now() - start, app: +(/app;dur=([\d.]+)/.exec(t)?.[1] || 0), db: +(/db;dur=([\d.]+)/.exec(t)?.[1] || 0), html };
}
const login = await request("/account/login");
token = /name="__RequestVerificationToken"[^>]*value="([^"]+)"/.exec(login.html)[1];
await request("/account/login", { email: "demo@example.test", password: "demo-password-123", __RequestVerificationToken: token });
const today = await request("/today");
token = /name="__RequestVerificationToken"[^>]*value="([^"]+)"/.exec(today.html)?.[1] || token;
const cases = {
  search: () => request("/food/add?meal=Lunch&tab=search&q=chicken%20breast"),
  parse: () => request("/food/parse", { date: "2026-11-05", meal: "Lunch", text: "200 g chicken breast, 150 g rice, 1 banana" }),
};
const median = (a) => { const s = [...a].sort((x, y) => x - y); return s[Math.floor(s.length / 2)]; };
const waits = new Map();
function sampleWaits() {
  return new Promise((res) => execFile("docker", ["exec", "plate-perf-db", "psql", "-U", "gym", "-d", "gym_perf", "-tAc",
    "select coalesce(wait_event_type,'cpu')||':'||coalesce(wait_event,'-') from pg_stat_activity where state='active' and pid<>pg_backend_pid()"],
    (e, out) => { for (const l of (out || "").split("\n").filter(Boolean)) waits.set(l, (waits.get(l) || 0) + 1); res(); }));
}
const which = process.argv[2] ? process.argv[2].split(",") : Object.keys(cases);
for (const name of which) {
  for (const c of [1, 5]) {
    for (let w = 0; w < 2; w++) await Promise.all(Array.from({ length: c }, cases[name]));
    waits.clear();
    let sampling = c > 1; const sampler = (async () => { while (sampling) await sampleWaits(); })();
    const rows = [];
    for (let b = 0; b < 5; b++) rows.push(...await Promise.all(Array.from({ length: c }, cases[name])));
    sampling = false; await sampler;
    console.log(`${name} c${c}: http ${median(rows.map(r => r.ms)).toFixed(0)} ms, app ${median(rows.map(r => r.app)).toFixed(0)}, db ${median(rows.map(r => r.db)).toFixed(0)}, statuses ${[...new Set(rows.map(r => r.status))]}` + (c > 1 ? `\n  pg waits: ${[...waits].sort((a, b) => b[1] - a[1]).slice(0, 6).map(([k, v]) => k + "=" + v).join(" ")}` : ""));
  }
}
