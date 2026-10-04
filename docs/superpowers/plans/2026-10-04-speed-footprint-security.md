# Speed, Footprint and Security Pass Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make page changes feel instant on an iPhone over the Cloudflare Tunnel, cut the app container's memory roughly in half, and harden login and the HTTP surface, without changing the stack.

**Architecture:** Navigation moves from full document loads to a small same-origin "soft navigation" script (`nav.js`) that prefetches on touch, swaps the top bar, main content and overlays inside a same-document View Transition, and falls back to a normal page load on any doubt. The server switches to workstation GC and trims per-request queries. Security adds a header/CSP middleware, tighter login and AI-endpoint limits, `__Host-` cookies, container hardening and passkey (Face ID) sign-in through ASP.NET Core Identity's built-in .NET 10 support.

**Tech Stack:** Unchanged: .NET 10, ASP.NET Core MVC + Razor, htmx 2.0.11, EF Core + Npgsql, PostgreSQL 18, ASP.NET Core Identity, xUnit + FluentAssertions + Testcontainers. No new NuGet packages and no new front-end libraries. Dev-only verification reuses `playwright-core` already installed under `design-samples/tools/node_modules`.

**Spec:** `SPEC.md` sections 2 (stack), 8 (PWA and resilience), 9 (security), 10 (deployment). There is no separate design doc; the measurements and decisions below are the design. Task 15 updates SPEC.md to match.

## Measured baseline (2026-10-03)

| What | Result | Meaning |
|---|---|---|
| Server render time, local, 7 main pages | 2 to 6 ms TTFB | Razor and EF are not the bottleneck |
| Production `GET /account/login` through Cloudflare | 85 to 330 ms TTFB (5 samples) | Each navigation waits on the tunnel round trip |
| Navigation model | Full document load per tap, cross-document View Transition (`@view-transition { navigation: auto }`), `--dur-page: 380ms` | The old page stays frozen with no feedback until the new document is ready, then a 380 ms animation runs: 450 to 700 ms from tap to settled, longer on gym mobile data |
| Page responses | `Cache-Control: no-cache, no-store` (set by antiforgery because the layout generates a token) | Correct for personalized pages, but rules out HTTP-cache-based prefetching such as the htmx preload extension |
| Service worker static strategy | Stale-while-revalidate keyed by path, so every page load fires about 8 background requests for CSS/JS/fonts | Wasted tunnel round trips and server work on every tap |
| `session.js` | `setInterval(tick, 250)` on every page, even with no rest timer | Constant wakeups on every screen |
| `gymtracker-app` memory on wizard | 197.9 MiB of a 256 MiB limit, Server GC on 4 cores | Server GC sizes heaps per core; two users don't need it |
| `gymtracker-db` memory | 70.9 MiB | Fine |
| Today page queries (from code) | About 15 round trips; the profile is read 4 times, bodyweights twice | Cheap locally, adds up on wizard |
| Security headers in production | No HSTS, CSP, `nosniff`, `Referrer-Policy`, `Permissions-Policy`; `X-Frame-Options: SAMEORIGIN` from antiforgery only | Missing baseline hardening |
| Login | Unknown emails return without hashing (timing reveals which emails exist); per-IP limit only; no limit on password change or AI parsing | Gaps listed in Part D |
| CSP blockers | 8 inline `on*=` handlers in views | Must go before a script CSP without `'unsafe-inline'` |

## Decisions (review these first)

1. **Custom `nav.js` instead of `hx-boost` + the htmx preload extension.** The preload extension relies on the browser HTTP cache, which `no-store` pages bypass, and relaxing that would serve stale pages after a POST-redirect. A 200-line script with its own one-shot, 4-second prefetch slot avoids both problems, keeps htmx for in-page partials, and adds no library.
2. **Soft navigation also covers plain POST forms** (log weight, add food, save settings), so the commonest flows animate too. Any form with `hx-*`, `data-full-nav`, or under `/account/` still does a normal submit.
3. **Workstation, non-concurrent GC plus ReadyToRun.** Expected steady state is 70 to 110 MiB, measured on wizard before `mem_limit` is lowered (SPEC 10 says adjust from Beszel readings).
4. **CSP goes out report-only for one deploy, then enforces.** `style-src` keeps `'unsafe-inline'` because the views carry 111 `style=""` attributes; removing them is a separate cleanup with little security value next to a strict `script-src`.
5. **Lockout stays at 5 attempts / 15 minutes** (SPEC 9). Known trade-off: anyone who knows an account's email can keep it locked out. The new global login limiter and passkeys reduce exposure but don't remove it. Say so if you want per-IP lockout instead.
6. **Passkeys are added alongside passwords, not replacing them.** Task 13 is the largest single task; it can be dropped or moved to its own plan without affecting the rest.

## Global Constraints

- `GymTracker.Core` has no references to EF Core, ASP.NET Core or HttpClient.
- Every user-owned query is scoped to the current user; never trust IDs from the client.
- No business logic in controllers or views.
- No secrets in the repository; configuration from environment variables; keep `docker/.env.example` current.
- Async EF calls with `CancellationToken` throughout.
- No new NuGet packages or front-end libraries.
- No CDNs. Everything served from `wwwroot`, precached by the service worker.
- Mobile-first: verify at 390 px width; touch targets at least 44 px.
- Respect `prefers-reduced-motion` in every new animation.
- Every new user-facing string gets a Bulgarian entry in `src/GymTracker.Web/Resources/strings.bg.json` (and `strings.keys.json` if the key list is maintained there).
- Anything on wizard that needs sudo, and every Cloudflare dashboard change, is written up as an exact step for Antoan to perform.
- Run `dotnet build` and `dotnet test` before calling a task done; report actual results.

## Review Focus

1. **A set logged during a soft navigation.** Tapping a tab while a set is still pending must not drop or duplicate it; the write-ahead queue keeps running because the document is never unloaded. Covered by the airplane-mode check in Task 6 step 3.
2. **Session expiry mid-navigation.** A soft navigation whose fetch is redirected to `/account/login` must fall back to a full navigation to the login page, not swap the login form into the app shell. Covered in Task 4's nav check.
3. **Offline tap.** A soft navigation with no signal must fall back to a normal navigation so the service worker serves `offline.html`. Covered in Task 4's nav check.
4. **Back button after a POST.** After logging weight (soft POST), Back must show the previous page, re-fetched, not a resubmission. Covered in Task 4's nav check.
5. **Barcode scanner under CSP.** The ZXing WASM build must still decode with the enforced CSP (needs `'wasm-unsafe-eval'`); camera must stop when navigating away. Covered in Task 10 step "scanner under CSP" and Task 3's cleanup hook.

---

## Part A: Measure

### Task 1: Server-Timing and the navigation benchmark

**Files:**
- Create: `src/GymTracker.Web/Features/Diagnostics/ServerTiming.cs`
- Modify: `src/GymTracker.Web/Program.cs`
- Create: `design-samples/tools/nav-timing.mjs`
- Test: `tests/GymTracker.Web.Tests/HttpSecurityTests.cs` (new test in a new class `ServerTimingTests` in the same folder: `tests/GymTracker.Web.Tests/ServerTimingTests.cs`)

**Interfaces:**
- Produces: response header `Server-Timing: app;dur=<ms>, db;dur=<ms>;desc="<n> queries"` on every controller response. Later tasks read the query count from it.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/GymTracker.Web.Tests/ServerTimingTests.cs
using System.Text.RegularExpressions;
using FluentAssertions;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class ServerTimingTests(PostgresFixture db) : IDisposable
{
    private readonly AppFactory app = new(db);

    public void Dispose() => app.Dispose();

    [Fact]
    public async Task Pages_report_app_and_database_time()
    {
        var (email, password) = await app.CreateUserAsync();
        var client = app.NewClient();
        await AppFactory.LoginAsync(client, email, password);

        var response = await client.GetAsync("/today");

        var header = string.Join(",", response.Headers.GetValues("Server-Timing"));
        header.Should().MatchRegex(@"app;dur=\d+(\.\d+)?");
        Regex.Match(header, "db;dur=[0-9.]+;desc=\"(\\d+) queries\"").Success.Should().BeTrue(header);
    }
}
```

- [ ] **Step 2: Run it and see it fail**

Run: `dotnet test --filter ServerTimingTests`
Expected: FAIL, header `Server-Timing` not present.

- [ ] **Step 3: Implement**

```csharp
// src/GymTracker.Web/Features/Diagnostics/ServerTiming.cs
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace GymTracker.Web.Features.Diagnostics;

/// <summary>Database time and query count for the current request, filled in by <see cref="DbTimingInterceptor"/>.</summary>
public sealed class RequestDbStats
{
    public int Queries;
    public double Milliseconds;
}

/// <summary>Counts EF commands per request. Registered as a singleton; the per-request totals live in HttpContext.Items.</summary>
public sealed class DbTimingInterceptor(IHttpContextAccessor accessor) : DbCommandInterceptor
{
    private void Record(CommandExecutedEventData data)
    {
        if (accessor.HttpContext?.Items[typeof(RequestDbStats)] is RequestDbStats stats)
        {
            stats.Queries++;
            stats.Milliseconds += data.Duration.TotalMilliseconds;
        }
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c, CommandExecutedEventData d, DbDataReader r, CancellationToken ct = default)
    { Record(d); return ValueTask.FromResult(r); }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand c, CommandExecutedEventData d, object? r, CancellationToken ct = default)
    { Record(d); return ValueTask.FromResult(r); }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken ct = default)
    { Record(d); return ValueTask.FromResult(r); }
}

/// <summary>Adds a Server-Timing header so page cost shows in Safari's and Chrome's network panels and in the nav benchmark.</summary>
public sealed class ServerTimingMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var start = Stopwatch.GetTimestamp();
        var stats = new RequestDbStats();
        context.Items[typeof(RequestDbStats)] = stats;
        context.Response.OnStarting(() =>
        {
            var app = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context.Response.Headers.Append("Server-Timing",
                FormattableString.Invariant($"app;dur={app:0.0}, db;dur={stats.Milliseconds:0.0};desc=\"{stats.Queries} queries\""));
            return Task.CompletedTask;
        });
        return next(context);
    }
}
```

In `Program.cs`, register the interceptor and add it to the DbContext, and add the middleware right after `UseForwardedHeaders()`:

```csharp
builder.Services.AddSingleton<DbTimingInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, o) => o
    .UseNpgsql(config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not set."))
    .AddInterceptors(sp.GetRequiredService<DbTimingInterceptor>()));
// ...
app.UseForwardedHeaders();
app.UseMiddleware<ServerTimingMiddleware>();
```

- [ ] **Step 4: Run the test and see it pass**

Run: `dotnet test --filter ServerTimingTests`
Expected: PASS. Note the query count printed by a temporary `Console.WriteLine(header)` for `/today` (expected about 15) and record it in the commit message; remove the line.

- [ ] **Step 5: Write the navigation benchmark**

`design-samples/tools/nav-timing.mjs` drives Chrome with network emulation (150 ms RTT, roughly a decent 4G connection to wizard) and measures two numbers per tap: time to first visible feedback (the tapped tab shows as active) and time to settled (new `data-tab` on `<html>` and no `data-nav` left, meaning the transition finished). It works for both the current full-load navigation and the soft navigation, so it gives a before and after.

```js
// Measures tap-to-feedback and tap-to-settled for tab and push navigations, with emulated latency.
// Usage: node design-samples/tools/nav-timing.mjs [baseUrl] [--rtt 150]
import { chromium } from "playwright-core";

const base = process.argv[2] && !process.argv[2].startsWith("--") ? process.argv[2] : "http://localhost:5130";
const rttArg = process.argv.indexOf("--rtt");
const rtt = rttArg > 0 ? Number(process.argv[rttArg + 1]) : 150;

const browser = await chromium.launch({ channel: "chrome" });
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, hasTouch: true, isMobile: true });
const page = await context.newPage();
await page.goto(`${base}/account/login`);
await page.fill('input[name="email"]', "demo@example.test");
await page.fill('input[name="password"]', "demo-password-123");
await Promise.all([page.waitForURL(/\/today/), page.click('button[type="submit"]')]);

const cdp = await context.newCDPSession(page);
await cdp.send("Network.emulateNetworkConditions", { offline: false, latency: rtt, downloadThroughput: 1.5e6, uploadThroughput: 750e3 });

async function measure(label, href, tab) {
    const t0 = Date.now();
    await page.tap(`a[href="${href}"]`);
    await page.waitForSelector(`.tab[href="/${tab}"].is-active, .tab[href="/${tab}"][aria-current="page"]`);
    const feedback = Date.now() - t0;
    await page.waitForFunction((t) => document.documentElement.dataset.tab === t && !document.documentElement.dataset.nav, tab);
    const settled = Date.now() - t0;
    console.log(`${label.padEnd(28)} feedback ${String(feedback).padStart(4)} ms   settled ${String(settled).padStart(4)} ms`);
    return settled;
}

const runs = [];
for (let i = 0; i < 3; i++) {
    runs.push(await measure("today -> train", "/train", "train"));
    runs.push(await measure("train -> food", "/food", "food"));
    runs.push(await measure("food -> progress", "/progress", "progress"));
    runs.push(await measure("progress -> today", "/today", "today"));
}
runs.sort((a, b) => a - b);
console.log(`median settled ${runs[Math.floor(runs.length / 2)]} ms at ${rtt} ms RTT`);
await browser.close();
```

- [ ] **Step 6: Record the baseline**

Run: `dotnet run --project src/GymTracker.Web --urls http://localhost:5130` (dev DB up, demo data seeded), then `node design-samples/tools/nav-timing.mjs`.
Expected: feedback roughly equal to settled minus the animation (no early feedback today), median settled around 550 to 750 ms. Paste the output into the commit message.

- [ ] **Step 7: Commit**

```bash
git add src/GymTracker.Web/Features/Diagnostics/ServerTiming.cs src/GymTracker.Web/Program.cs tests/GymTracker.Web.Tests/ServerTimingTests.cs design-samples/tools/nav-timing.mjs
git commit -m "Add Server-Timing with per-request query counts and a navigation benchmark"
```

---

## Part B: Navigation and motion

### Task 2: Service worker serves versioned assets from cache without revalidating

**Files:**
- Modify: `src/GymTracker.Web/wwwroot/sw.js`
- Test: existing `Every_file_the_service_worker_precaches_exists` keeps passing; manual check below.

**Interfaces:**
- Produces: cache name `gym-static-v7`. Later tasks that add JS files to the precache list bump the version.

- [ ] **Step 1: Replace the static branch of the fetch handler**

Every `<link>`/`<script>` in the layout carries `?v=<content hash>` from `asp-append-version`, so a cached copy for the exact URL can never be stale. Serve it straight from cache; only go to the network on a miss, and drop older versions of the same path when a new one arrives.

```js
const CACHE = "gym-static-v7";
// ... PRECACHE unchanged ...

function isStatic(url) {
    return url.origin === self.location.origin && /^\/(css|js|lib)\//.test(url.pathname);
}

async function staticResponse(request, url) {
    const cache = await caches.open(CACHE);
    // Exact URL first: versioned URLs (?v=hash) never change, so no revalidation is needed.
    const exact = await cache.match(request);
    if (exact) {
        return exact;
    }
    try {
        const response = await fetch(request);
        if (response.ok) {
            const keys = await cache.keys();
            await Promise.all(keys
                .filter((k) => new URL(k.url).pathname === url.pathname && k.url !== request.url)
                .map((k) => cache.delete(k)));
            await cache.put(request, response.clone());
        }
        return response;
    } catch {
        // Offline and this version was never fetched: any cached copy of the file beats nothing.
        return (await cache.match(request, { ignoreSearch: true })) || Response.error();
    }
}

self.addEventListener("fetch", (event) => {
    const request = event.request;
    if (request.method !== "GET") {
        return;
    }
    const url = new URL(request.url);
    if (isStatic(url)) {
        event.respondWith(staticResponse(request, url));
        return;
    }
    if (request.mode === "navigate") {
        event.respondWith(fetch(request).catch(() => caches.match(OFFLINE)));
    }
});
```

The precache step keeps the unversioned paths; the first page load after install fetches each `?v=` URL once and from then on serves from cache, and the old unversioned entries are dropped by the cleanup above.

- [ ] **Step 2: Verify**

Run: `dotnet test --filter Every_file_the_service_worker_precaches_exists` → PASS.
Manual: load `/today` in Chrome with DevTools, reload twice, then click through tabs with the Network panel filtered to `css|js|lib`. Expected: every static request shows "(ServiceWorker)" with no follow-up network request.

- [ ] **Step 3: Commit**

```bash
git add src/GymTracker.Web/wwwroot/sw.js
git commit -m "Serve versioned static files from the service worker cache without revalidating"
```

### Task 3: Page modules and no inline handlers

Soft navigation (Task 4) keeps one document alive across pages, so page scripts can no longer rely on "the script ran once when the page loaded". This task gives them a mount/unmount lifecycle and removes the inline handlers that would block a strict CSP.

**Files:**
- Create: `src/GymTracker.Web/wwwroot/js/pages.js`
- Modify: `src/GymTracker.Web/wwwroot/js/progress.js`, `src/GymTracker.Web/wwwroot/js/scanner.js`, `src/GymTracker.Web/wwwroot/js/site.js`, `src/GymTracker.Web/wwwroot/js/motion.js`
- Modify: `src/GymTracker.Web/Views/Shared/_Layout.cshtml`, `Views/Progress/Index.cshtml`, `Views/Food/Add.cshtml`, `Views/Settings/Index.cshtml`, `Views/Shared/_ExerciseCard.cshtml`, `Views/Food/Entry.cshtml`, `Views/Train/Session.cshtml`, `Views/Templates/Edit.cshtml`
- Modify: `src/GymTracker.Web/wwwroot/sw.js` (add `/js/pages.js`, bump to `gym-static-v8`)
- Test: `tests/GymTracker.Web.Tests/ViewHygieneTests.cs`

**Interfaces:**
- Produces: `window.gymModule(name: string, init: (el: HTMLElement) => (void | () => void))`. Elements with `data-module="name"` are mounted on first load and after each soft navigation; the returned function runs before the next swap.
- Produces: DOM events `gym:beforeswap` (dispatched on `document` before content is replaced) and `gym:page` (after new content is in place). Task 4 dispatches them.
- Produces: `data-confirm="<text>"` on a form shows `confirm(text)` before submitting.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/GymTracker.Web.Tests/ViewHygieneTests.cs
using System.Text.RegularExpressions;
using FluentAssertions;

namespace GymTracker.Web.Tests;

public class ViewHygieneTests
{
    private static string ViewsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GymTracker.slnx")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir!.FullName, "src", "GymTracker.Web", "Views");
    }

    [Fact]
    public void Views_have_no_inline_event_handlers()
    {
        // Inline handlers need 'unsafe-inline' in the CSP script-src, which would undo most of its value.
        var offenders = Directory.EnumerateFiles(ViewsRoot(), "*.cshtml", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(x => Regex.IsMatch(x.line, @"\son[a-z]+\s*=\s*""", RegexOptions.IgnoreCase))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();

        offenders.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run it and see it fail**

Run: `dotnet test --filter ViewHygieneTests`
Expected: FAIL listing 8 locations.

- [ ] **Step 3: Replace the inline handlers**

In each of the 7 forms with `onsubmit="return confirm(this.dataset.confirm)"`, delete the `onsubmit` attribute and keep the existing `data-confirm`. In `Views/Progress/Index.cshtml:94` replace `onchange="this.form.submit()"` by putting `data-autosubmit` on the enclosing `<form>` (site.js already submits `form[data-autosubmit]` on change).

Add to `site.js`, inside the IIFE, before the `data-busy` submit listener so a cancelled confirm never shows the busy state:

```js
    // Destructive forms ask first. Capture phase, so it runs before the busy state and soft navigation.
    document.addEventListener("submit", (event) => {
        const form = event.target.closest("form[data-confirm]");
        if (form && !window.confirm(form.dataset.confirm)) {
            event.preventDefault();
            event.stopImmediatePropagation();
        }
    }, true);
```

- [ ] **Step 4: Add the module runner**

```js
// src/GymTracker.Web/wwwroot/js/pages.js
// Per-page setup that survives soft navigation. A page marks an element with data-module="name"; the module's
// init runs once per element and may return a cleanup that runs before the content is swapped out.
(function () {
    "use strict";

    const modules = new Map();
    let cleanups = [];

    function mount(scope) {
        scope.querySelectorAll("[data-module]").forEach((el) => {
            const init = modules.get(el.dataset.module);
            if (!init || el.gymMounted) {
                return;
            }
            el.gymMounted = true;
            const cleanup = init(el);
            if (typeof cleanup === "function") {
                cleanups.push(cleanup);
            }
        });
    }

    window.gymModule = (name, init) => {
        modules.set(name, init);
        mount(document);
    };

    document.addEventListener("gym:beforeswap", () => {
        cleanups.forEach((c) => {
            try {
                c();
            } catch (e) {
                console.error(e);
            }
        });
        cleanups = [];
    });
    document.addEventListener("gym:page", () => mount(document));
})();
```

Load it in `_Layout.cshtml` before `motion.js`: `<script src="~/js/pages.js" asp-append-version="true" defer></script>`.

- [ ] **Step 5: Convert progress.js and scanner.js**

Wrap each file's body in a module. For `progress.js`, put `data-module="progress"` on the Progress page's outer `<section>` (or a wrapping `<div>`), keep the `<script src>` tags at the bottom of the view (Task 4 loads them once), and change the file's top level to:

```js
window.gymModule("progress", (root) => {
    const charts = [];
    // existing setup, with every `new Chart(...)` pushed into `charts` and every
    // document.getElementById(...) changed to root.querySelector("#...")
    return () => charts.forEach((c) => c.destroy());
});
```

For `scanner.js`, put `data-module="scanner"` on the scanner sheet in `Views/Food/Add.cshtml` and return a cleanup that stops every camera track:

```js
window.gymModule("scanner", (root) => {
    let stream = null;
    // existing setup; wherever getUserMedia resolves, keep the stream in `stream`
    return () => {
        if (stream) {
            stream.getTracks().forEach((t) => t.stop());
            stream = null;
        }
    };
});
```

- [ ] **Step 6: Re-run per-page parts of motion.js on `gym:page`**

In `motion.js`, move the large-title observer into a function and run it for the first load and every `gym:page`; run `reveal` for the new content:

```js
    let titleObserver = null;
    function bindLargeTitle() {
        if (titleObserver) {
            titleObserver.disconnect();
            titleObserver = null;
        }
        const bar = document.querySelector("[data-topbar]");
        const title = document.querySelector(".large-title");
        if (!bar) {
            return;
        }
        bar.classList.remove("is-compact", "always-title");
        if (title && "IntersectionObserver" in window) {
            titleObserver = new IntersectionObserver(([entry]) => {
                bar.classList.toggle("is-compact", !entry.isIntersecting);
            }, { rootMargin: `-${bar.offsetHeight}px 0px 0px 0px` });
            titleObserver.observe(title);
        } else if (!title) {
            bar.classList.add("is-compact", "always-title");
        }
    }
    bindLargeTitle();
    document.addEventListener("gym:page", () => {
        bindLargeTitle();
        reveal(document.getElementById("main"));
    });
```

- [ ] **Step 7: Verify**

Run: `dotnet build && dotnet test` → all pass, including `ViewHygieneTests`.
Manual (full page loads still, since nav.js is not in yet): Progress charts render and the exercise picker changes the chart; the barcode sheet opens the camera; delete/finish/archive forms still ask for confirmation and cancelling does nothing.

- [ ] **Step 8: Commit**

```bash
git add src/GymTracker.Web/wwwroot src/GymTracker.Web/Views tests/GymTracker.Web.Tests/ViewHygieneTests.cs
git commit -m "Give page scripts a mount and cleanup lifecycle and drop inline event handlers"
```

### Task 4: Soft navigation with touch prefetch

**Files:**
- Create: `src/GymTracker.Web/wwwroot/js/nav.js`
- Modify: `src/GymTracker.Web/Views/Shared/_Layout.cshtml`
- Modify: `src/GymTracker.Web/wwwroot/css/app.css` (loading bar, `main:focus` outline)
- Modify: `src/GymTracker.Web/wwwroot/sw.js` (add `/js/nav.js`, bump to `gym-static-v9`)
- Create: `design-samples/tools/nav-check.mjs`

**Interfaces:**
- Consumes: `gym:beforeswap`, `gym:page` and `gymModule` from Task 3.
- Produces: `window.gymNav(url: string, options?: { push?: boolean })`. Elements marked `data-full-nav` (links or forms) opt out.
- Produces: layout wrappers `#overlays` (around `RenderSectionAsync("Overlays")`) and `tabindex="-1"` on `<main id="main">`.

- [ ] **Step 1: Layout changes**

In `_Layout.cshtml`:
- Wrap the overlays section: `<div id="overlays">@await RenderSectionAsync("Overlays", required: false)</div>`.
- Add `tabindex="-1"` to `<main class="page" id="main">`.
- Load `nav.js` last: `<script src="~/js/nav.js" asp-append-version="true" defer></script>`.
- Mark the logout form in `Views/Settings/Index.cshtml` and the `logout-everywhere` form with `data-full-nav` (they end on the login page, which must be a full load).

- [ ] **Step 2: Write nav.js**

```js
// Soft navigation: same-origin links and plain POST forms fetch the next page and swap the top bar, main
// content and overlays inside a View Transition, so the tab bar, scripts and the set queue stay alive.
// Anything unexpected falls back to a normal navigation. Pages are never cached; a prefetch started on
// touch is used once, within a few seconds, and dropped after any write.
(function () {
    "use strict";

    if (!window.fetch || !window.DOMParser || !history.pushState) {
        return;
    }

    const root = document.documentElement;
    const reduce = window.matchMedia("(prefers-reduced-motion: reduce)");
    const PREFETCH_MS = 4000;
    const SLOW_MS = 120;
    let prefetched = null;
    let current = null;
    const loaded = new Set(Array.from(document.scripts, (s) => s.src && new URL(s.src).pathname).filter(Boolean));

    history.scrollRestoration = "manual";

    function eligibleLink(a, event) {
        if (!a || event.defaultPrevented || event.button > 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
            return null;
        }
        if (a.target || a.hasAttribute("download") || a.closest("[data-full-nav]") || a.hasAttribute("hx-get")) {
            return null;
        }
        const url = new URL(a.href, location.href);
        if (url.origin !== location.origin || url.pathname.startsWith("/account/") || /\.[a-z0-9]+$/i.test(url.pathname)) {
            return null;
        }
        if (url.pathname === location.pathname && url.search === location.search && url.hash) {
            return null;
        }
        return url;
    }

    function request(url, init) {
        return fetch(url, Object.assign({ credentials: "same-origin", headers: { "X-Gym-Nav": "1" } }, init));
    }

    function takePrefetch(url) {
        const p = prefetched;
        prefetched = null;
        return p && p.url === url.href && performance.now() - p.at < PREFETCH_MS ? p.promise : null;
    }

    // Start loading on touch: on iOS the click arrives 100 to 150 ms after the finger goes down.
    document.addEventListener("pointerdown", (event) => {
        const url = eligibleLink(event.target.closest("a[href]"), event);
        if (url && !(prefetched && prefetched.url === url.href)) {
            const promise = request(url);
            promise.catch(() => {});
            prefetched = { url: url.href, at: performance.now(), promise };
        }
    }, { passive: true });

    // Any write makes a prefetched page stale.
    document.addEventListener("htmx:beforeRequest", (event) => {
        if ((event.detail.requestConfig.verb || "get") !== "get") {
            prefetched = null;
        }
    });

    function navType(fromTab, fromDepth, toTab, toDepth, isBack) {
        if (reduce.matches || fromTab !== toTab || fromDepth === toDepth) {
            return "tab";
        }
        return isBack || toDepth < fromDepth ? "back" : "forward";
    }

    function loadScript(src) {
        const path = new URL(src, location.href).pathname;
        if (loaded.has(path)) {
            return Promise.resolve();
        }
        loaded.add(path);
        return new Promise((resolve, reject) => {
            const s = document.createElement("script");
            s.src = src;
            s.onload = resolve;
            s.onerror = reject;
            document.head.appendChild(s);
        });
    }

    function markActiveTab(tab) {
        document.querySelectorAll(".tabbar .tab").forEach((t) => {
            const on = t.getAttribute("href") === "/" + tab;
            t.classList.toggle("is-active", on);
            if (on) {
                t.setAttribute("aria-current", "page");
            } else {
                t.removeAttribute("aria-current");
            }
        });
    }

    async function swapIn(doc, finalUrl, { push, replace, isBack, scrollY }) {
        const next = doc.documentElement;
        const nextShell = doc.querySelector(".app-scale");
        const shell = document.querySelector(".app-scale");
        if (!nextShell || !shell) {
            throw new Error("not an app page");
        }

        // Scripts in the incoming content load once, before the content is shown, then modules mount it.
        const scripts = Array.from(nextShell.querySelectorAll("script[src]")).concat(Array.from(doc.querySelectorAll("#overlays script[src]")));
        scripts.forEach((s) => s.remove());
        for (const s of scripts) {
            await loadScript(s.getAttribute("src"));
        }

        const nav = navType(root.dataset.tab, Number(root.dataset.depth || 0), next.dataset.tab, Number(next.dataset.depth || 0), isBack);
        const hasTabs = !!shell.querySelector(".tabbar");
        const nextHasTabs = !!nextShell.querySelector(".tabbar");

        const apply = () => {
            document.dispatchEvent(new CustomEvent("gym:beforeswap"));
            if (hasTabs && nextHasTabs) {
                shell.querySelector(".topbar").replaceWith(nextShell.querySelector(".topbar"));
                shell.querySelector("#main").replaceWith(nextShell.querySelector("#main"));
                markActiveTab(next.dataset.tab);
            } else {
                shell.replaceWith(nextShell);
            }
            document.getElementById("overlays").replaceWith(doc.getElementById("overlays"));
            document.body.className = doc.body.className;
            document.body.setAttribute("hx-headers", doc.body.getAttribute("hx-headers") || "");
            ["data-tab", "data-depth", "data-theme", "lang"].forEach((a) => root.setAttribute(a, next.getAttribute(a) || ""));
            document.title = doc.title;
            if (push) {
                history.pushState({ gym: true }, "", finalUrl);
            } else if (replace) {
                history.replaceState({ gym: true }, "", finalUrl);
            }
            window.scrollTo(0, scrollY || 0);
            const main = document.getElementById("main");
            htmx.process(document.querySelector(".app-scale"));
            htmx.process(document.getElementById("overlays"));
            main.focus({ preventScroll: true });
            document.dispatchEvent(new CustomEvent("gym:page", { detail: { nav } }));
        };

        if (current) {
            current.skipTransition();
        }
        if (!document.startViewTransition) {
            apply();
            return;
        }
        root.dataset.nav = nav;
        current = document.startViewTransition(apply);
        current.finished.finally(() => {
            current = null;
            delete root.dataset.nav;
        });
    }

    async function go(url, { push = true, init = null, isBack = false, scrollY = 0, fallback } = {}) {
        // Remember where we were, for Back.
        history.replaceState(Object.assign({}, history.state, { gym: true, scrollY: window.scrollY }), "");
        const slow = setTimeout(() => root.classList.add("is-navigating"), SLOW_MS);
        try {
            const response = await ((!init && takePrefetch(url)) || request(url, init || undefined));
            const finalUrl = new URL(response.url);
            const html = (response.headers.get("Content-Type") || "").includes("text/html");
            if (!html || finalUrl.pathname.startsWith("/account/") || (response.status >= 500)) {
                throw new Error("needs a full load");
            }
            const doc = new DOMParser().parseFromString(await response.text(), "text/html");
            const samePage = finalUrl.href === location.href;
            await swapIn(doc, finalUrl.href, { push: push && !samePage, replace: !push || samePage, isBack, scrollY });
        } catch {
            fallback();
        } finally {
            clearTimeout(slow);
            root.classList.remove("is-navigating");
        }
    }

    window.gymNav = (href, options) => go(new URL(href, location.href), Object.assign({ fallback: () => { location.href = href; } }, options));

    document.addEventListener("click", (event) => {
        const a = event.target.closest("a[href]");
        const url = eligibleLink(a, event);
        if (!url) {
            return;
        }
        event.preventDefault();
        if (a.closest(".tabbar")) {
            markActiveTab(url.pathname.split("/")[1] || "today");
        }
        go(url, { fallback: () => { location.href = url.href; } });
    });

    // Plain POST forms (no htmx, not account pages): submit with fetch, follow the redirect, swap the result.
    document.addEventListener("submit", (event) => {
        const form = event.target;
        if (event.defaultPrevented || !(form instanceof HTMLFormElement) || (form.method || "get").toLowerCase() !== "post") {
            return;
        }
        if (form.closest("[data-full-nav]") || form.hasAttribute("hx-post") || form.target || form.enctype === "multipart/form-data") {
            return;
        }
        const action = new URL(form.action, location.href);
        if (action.origin !== location.origin || action.pathname.startsWith("/account/")) {
            return;
        }
        event.preventDefault();
        prefetched = null;
        const body = new FormData(form, event.submitter);
        go(action, {
            push: true,
            init: { method: "POST", body: new URLSearchParams(body) },
            fallback: () => {
                // Network error or unexpected response: hand the form to the browser so nothing is lost.
                form.setAttribute("data-full-nav", "");
                form.requestSubmit(event.submitter);
            },
        });
    });

    window.addEventListener("popstate", (event) => {
        const state = event.state || {};
        go(new URL(location.href), { push: false, isBack: true, scrollY: state.scrollY || 0, fallback: () => location.reload() });
    });
})();
```

Notes for the implementer:
- `go()` with `init` set never uses the prefetch slot, so a POST is never answered by a stale GET.
- The `is-navigating` class only appears when the response takes more than 120 ms, so fast swaps show no spinner.
- The view transition starts only after the response is parsed. Before that the user already sees the tapped tab turn active (synchronous `markActiveTab`) and, on slow responses, the loading bar.
- htmx's own `htmx:afterRequest` → `flush()` in session.js keeps working; the queue never stops because the document stays alive.

- [ ] **Step 3: Loading bar and focus CSS**

Append to `app.css`:

```css
/* Soft navigation: a hairline at the top when a page takes longer than 120 ms. */
html.is-navigating::before {
    content: ""; position: fixed; inset: 0 0 auto 0; height: 2px; z-index: 1000;
    background: var(--accent); transform-origin: 0 50%;
    animation: nav-progress 1.2s var(--ease-out) forwards;
}
html.is-navigating #main { opacity: 0.72; transition: opacity 160ms var(--ease-out) 60ms; }
@keyframes nav-progress { from { transform: scaleX(0); } to { transform: scaleX(0.85); } }
#main:focus { outline: none; }
@media (prefers-reduced-motion: reduce) { html.is-navigating::before { animation: none; transform: scaleX(1); opacity: 0.6; } }
```

- [ ] **Step 4: Write the navigation check**

`design-samples/tools/nav-check.mjs` logs in, stamps the document (`window.__doc = Math.random()`), then exercises: each tab; a push page (`/train/history`) and Back; logging a weight on Today (soft POST) and Back; the session-expiry case (delete the `gym.auth` cookie, tap a tab, expect a full load of `/account/login`); and the offline case (`context.setOffline(true)`, tap a tab, expect `offline.html` content). After each step it asserts the URL, `document.title`, `html[data-tab]`, the active tab, and whether `window.__doc` survived (it must survive every case except expiry and offline). It fails on any `pageerror` or `console.error`.

```js
import { chromium } from "playwright-core";
import assert from "node:assert/strict";

const base = process.argv[2] || "http://localhost:5130";
const browser = await chromium.launch({ channel: "chrome" });
const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
const page = await context.newPage();
const errors = [];
page.on("pageerror", (e) => errors.push(e.message));
page.on("console", (m) => m.type() === "error" && errors.push(m.text()));

await page.goto(`${base}/account/login`);
await page.fill('input[name="email"]', "demo@example.test");
await page.fill('input[name="password"]', "demo-password-123");
await Promise.all([page.waitForURL(/\/today/), page.click('button[type="submit"]')]);
await page.evaluate(() => { window.__doc = 1; });

const sameDoc = () => page.evaluate(() => window.__doc === 1);
const settled = (tab) => page.waitForFunction((t) => document.documentElement.dataset.tab === t && !document.documentElement.dataset.nav, tab);

for (const tab of ["train", "food", "progress", "settings", "today"]) {
    await page.tap(`.tabbar a[href="/${tab}"]`);
    await settled(tab);
    assert.equal(new URL(page.url()).pathname, `/${tab}`);
    assert.ok(await page.$(`.tabbar a[href="/${tab}"][aria-current="page"]`), `${tab} tab active`);
    assert.ok(await sameDoc(), `${tab} was a soft navigation`);
}

await page.tap('.tabbar a[href="/train"]'); await settled("train");
await page.tap('a[href="/train/history"]');
await page.waitForURL(/\/train\/history/);
await page.goBack(); await page.waitForURL(/\/train$/);
assert.ok(await sameDoc(), "push and back stay in the document");

await page.tap('.tabbar a[href="/today"]'); await settled("today");
await page.fill('input[name="weightKg"]', "81.4");
await page.press('input[name="weightKg"]', "Enter");
await page.waitForFunction(() => document.body.innerText.includes("81.4"));
assert.ok(await sameDoc(), "weight POST was soft");
await page.goBack();
assert.ok(!page.url().includes("/today/weight"), "Back never lands on the POST url");

await context.clearCookies({ name: "gym.auth" });
await context.clearCookies({ name: "__Host-gym.auth" });
await page.tap('.tabbar a[href="/food"]');
await page.waitForURL(/\/account\/login/);
assert.ok(!(await sameDoc()), "expired session falls back to a full load");

assert.deepEqual(errors, []);
console.log("nav-check passed");
await browser.close();
```

(The offline case runs in a second page after logging in again: `await context.setOffline(true)`, tap a tab, `await page.waitForSelector("text=No connection")`.)

- [ ] **Step 5: Verify**

Run: `dotnet build && dotnet test` → all pass.
Run: `node design-samples/tools/nav-check.mjs` → `nav-check passed`.
Run: `node design-samples/tools/nav-timing.mjs` → feedback under 50 ms; median settled at least 150 ms below the Task 1 baseline. Paste both outputs into the commit message.
Manual on iPhone (local network or a test deploy): tabs, history push/back, the iOS edge-swipe back gesture, logging a set then switching tabs before it confirms.

- [ ] **Step 6: Commit**

```bash
git add src/GymTracker.Web/wwwroot src/GymTracker.Web/Views design-samples/tools/nav-check.mjs
git commit -m "Navigate within the page: prefetch on touch, swap content in a view transition, fall back to full loads"
```

### Task 5: Motion pass

**Files:**
- Modify: `src/GymTracker.Web/wwwroot/css/app.css` (lines 270 to 280 tab bar, 555 to 593 view transitions)
- Modify: `src/GymTracker.Web/wwwroot/css/tokens.css` (durations)
- Modify: `src/GymTracker.Web/wwwroot/js/motion.js` (entrance stagger)
- Modify: `src/GymTracker.Web/wwwroot/sw.js` (bump to `gym-static-v10`)

**Interfaces:**
- Consumes: `gym:page` event with `detail.nav` in `"tab" | "forward" | "back"` (Task 4).

What changes and why:
- With a persistent document the root no longer needs to animate. Only the page content (`main`) and the top bar animate; the tab bar stays perfectly still, as on iOS.
- Tab switches get faster and more physical: 120 ms out with a slight scale-down, 260 ms in with an 8 px rise on the snappy spring. Push and pop keep the iOS slide but shorten to 340 ms.
- The tapped tab icon gives a small spring "pop" the moment it's pressed, which is the first feedback the user sees.
- On tab and forward navigations the first eight blocks of the new page rise in with a 24 ms stagger. Back navigations skip it: returning should feel like going back, not like a new page.
- Reduced motion keeps short crossfades only.

- [ ] **Step 1: Tokens**

In `tokens.css` change `--dur-page: 380ms;` to `--dur-page: 340ms;` and add `--dur-tab-in: 260ms; --dur-tab-out: 120ms;`.

- [ ] **Step 2: View transition rules**

Replace `app.css` lines 558 to 571 (the `@view-transition` block through the `large-title` rules) with:

```css
/* Full document loads (login, offline fallback) keep a plain crossfade. Soft navigations name their parts. */
@view-transition { navigation: auto; }
#main { view-transition-name: page; }
.large-title { view-transition-name: large-title; }
.topbar { view-transition-name: topbar; }

::view-transition-group(root) { animation: none; }
::view-transition-old(root), ::view-transition-new(root) { animation: none; }
::view-transition-group(*) { animation-duration: var(--dur-page); animation-timing-function: var(--ease-gentle); }

/* Tabs: quick out, springy in. */
html[data-nav="tab"]::view-transition-old(page) { animation: var(--dur-tab-out) var(--ease-in-out) both vt-tab-out; }
html[data-nav="tab"]::view-transition-new(page) { animation: var(--dur-tab-in) var(--ease-snappy) 40ms both vt-tab-in; }
html[data-nav="tab"]::view-transition-group(page) { animation: none; }

/* Push and pop: the page slides; the top bar crossfades its title. */
html[data-nav="forward"]::view-transition-group(page), html[data-nav="back"]::view-transition-group(page) { animation: none; }
html[data-nav="forward"]::view-transition-old(page) { animation: var(--dur-page) var(--ease-gentle) both vt-push-out; }
html[data-nav="forward"]::view-transition-new(page) { animation: var(--dur-page) var(--ease-gentle) both vt-push-in; }
html[data-nav="back"]::view-transition-old(page) { animation: var(--dur-page) var(--ease-gentle) both vt-pop-out; z-index: 2; }
html[data-nav="back"]::view-transition-new(page) { animation: var(--dur-page) var(--ease-gentle) both vt-pop-in; }
html[data-nav]::view-transition-old(topbar) { animation: 120ms var(--ease-out) both vt-fade-out; }
html[data-nav]::view-transition-new(topbar) { animation: 200ms var(--ease-out) 60ms both vt-fade-in; }
html[data-nav="forward"]::view-transition-group(large-title), html[data-nav="back"]::view-transition-group(large-title) { animation: none; }
html[data-nav="forward"]::view-transition-old(large-title), html[data-nav="back"]::view-transition-old(large-title) { animation: 120ms var(--ease-out) both vt-fade-out; }
html[data-nav="forward"]::view-transition-new(large-title) { animation: var(--dur-page) var(--ease-gentle) both vt-push-in; }
html[data-nav="back"]::view-transition-new(large-title) { animation: 200ms var(--ease-out) both vt-fade-in; }

@keyframes vt-tab-out { to { opacity: 0; transform: scale(0.985); } }
@keyframes vt-tab-in { from { opacity: 0; transform: translateY(8px); } }

/* Tapped tab icon. */
.tab.is-active .tab-icon, .tab.is-active > svg { animation: tab-pop 360ms var(--ease-snappy); }
@keyframes tab-pop { 0% { transform: scale(1); } 35% { transform: scale(0.86); } 100% { transform: scale(1); } }

/* Entrance for the first blocks of a newly shown page; motion.js sets --i. */
#main.is-entering > * { animation: appear 320ms var(--ease-snappy) both; animation-delay: calc(var(--i, 0) * 24ms); }
```

Check that the tab icon selector matches the markup `Ui.Icon(...)` renders inside `.tab` (an `<svg>`); keep whichever of the two selectors matches and delete the other.

In the existing reduced-motion block (around line 588), add:

```css
    html[data-nav]::view-transition-old(page), html[data-nav]::view-transition-new(page) { animation: 150ms linear both vt-fade-in !important; }
    html[data-nav]::view-transition-old(page) { animation-name: vt-fade-out !important; }
    .tab.is-active > svg, .tab.is-active .tab-icon { animation: none; }
    #main.is-entering > * { animation: none; }
```

- [ ] **Step 3: Stagger in motion.js**

```js
    // New page content rises in, first eight blocks staggered. Not on Back, not with reduced motion.
    document.addEventListener("gym:page", (event) => {
        const main = document.getElementById("main");
        if (!main || reduce.matches || event.detail.nav === "back") {
            return;
        }
        Array.from(main.children).slice(0, 8).forEach((el, i) => el.style.setProperty("--i", String(i)));
        main.classList.add("is-entering");
        setTimeout(() => main.classList.remove("is-entering"), 600);
    });
```

- [ ] **Step 4: Verify**

Run: `node design-samples/tools/motion-frames.mjs` against the app (slowed 10×) and review the frames for: tab bar static during tab switches, no flash of the old page after the new one appears, push/pop slide direction correct, title crossfade clean.
Run: `node design-samples/tools/nav-check.mjs` → passes.
Manual on iPhone: Safari Web Inspector → Timelines → record a tab switch; no frame over 16.7 ms during the transition. If the blurred top bar or tab bar causes long frames, set `view-transition-name: none` on `.tabbar` (it no longer needs its own group since it doesn't move).
Manual with Settings → Accessibility → Reduce Motion on: only crossfades.

- [ ] **Step 5: Commit**

```bash
git add src/GymTracker.Web/wwwroot
git commit -m "Animate page content instead of the whole document: faster tab switches, tab icon pop, staggered entrance"
```

### Task 6: Rest timer only ticks while it runs

**Files:**
- Modify: `src/GymTracker.Web/wwwroot/js/session.js` (around lines 280 to 295 and 465 to 480)
- Modify: `src/GymTracker.Web/Views/Train/Session.cshtml` (add `data-module="session"` to the session root element)

**Interfaces:**
- Consumes: `gymModule` (Task 3).

- [ ] **Step 1: Replace the always-on interval**

Keep the queue code global (it must outlive page changes). Replace the bottom "start" block's `setInterval(tick, 250)` with a timer that runs only while a rest end time is stored:

```js
    let tickTimer = null;
    function ensureTicking() {
        const running = !!read(REST_KEY);
        if (running && !tickTimer) {
            tickTimer = setInterval(() => {
                tick();
                if (!read(REST_KEY)) {
                    clearInterval(tickTimer);
                    tickTimer = null;
                }
            }, 250);
        }
        tick();
    }
```

Call `ensureTicking()` where the rest timer is started (the check-button handler that writes `REST_KEY`), in the `+30 s` handler, on `visibilitychange` to visible, and once at start. `read` and `REST_KEY` are the helpers already used in this file; if the read helper has a different name, use that one.

- [ ] **Step 2: Move session-page setup into a module**

Wrap the wake lock and the `requestAnimationFrame(frame)` start in `gymModule("session", (root) => { ...; return () => { if (wakeLock) wakeLock.release(); wakeLock = null; sessionActive = false; }; })`, so leaving the session page releases the wake lock and stops the animation loop, and returning re-acquires it.

- [ ] **Step 3: Verify**

Manual in Chrome DevTools Performance panel on `/today` with no timer running: no 250 ms task pattern. On a session page: log a set, timer counts down and plays the sound at zero; switch to Food and back mid-timer, the timer shows the correct remaining time; airplane-mode set logging still queues and flushes (SPEC 8 checks).
Run: `node design-samples/tools/nav-check.mjs` → passes.

- [ ] **Step 4: Commit**

```bash
git add src/GymTracker.Web/wwwroot/js/session.js src/GymTracker.Web/Views/Train/Session.cshtml
git commit -m "Run the rest timer tick only while a timer is running and release the wake lock off the session page"
```

---

## Part C: Server memory and CPU

### Task 7: Workstation GC, ReadyToRun, leaner Postgres

**Files:**
- Modify: `src/GymTracker.Web/GymTracker.Web.csproj`
- Modify: `docker/Dockerfile`
- Modify: `docker/docker-compose.yml`
- Modify: `src/GymTracker.Web/Program.cs` (one startup log line)

- [ ] **Step 1: Runtime settings**

Add to the csproj `PropertyGroup`:

```xml
    <!-- Two users: one GC heap is enough. Server GC on wizard's 4 cores held ~200 MiB. -->
    <ServerGarbageCollector>false</ServerGarbageCollector>
    <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
    <!-- Precompiled code: less JIT work and memory at startup in the container. -->
    <PublishReadyToRun>true</PublishReadyToRun>
```

- [ ] **Step 2: Dockerfile**

ReadyToRun needs a runtime identifier at restore and publish:

```dockerfile
RUN dotnet restore src/GymTracker.Web/GymTracker.Web.csproj -r linux-x64
COPY src/ src/
RUN dotnet publish src/GymTracker.Web/GymTracker.Web.csproj -c Release -o /app --no-restore -r linux-x64 --self-contained false -p:UseAppHost=false
```

- [ ] **Step 3: Startup log**

In `Program.cs`, after `var app = builder.Build();`:

```csharp
app.Logger.LogInformation("GC: server={Server}, latency={Latency}, heap limit={Limit} MiB",
    System.Runtime.GCSettings.IsServerGC, System.Runtime.GCSettings.LatencyMode,
    GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
```

- [ ] **Step 4: Compose**

```yaml
  db:
    command: ["postgres", "-c", "shared_buffers=32MB", "-c", "max_connections=15", "-c", "jit=off"]
  app:
    environment:
      ConnectionStrings__Default: "Host=db;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD};Maximum Pool Size=8"
    cpus: "1.0"
```

`jit=off`: Postgres JIT only pays off for large analytic queries and costs CPU when it kicks in. `Maximum Pool Size=8` stays well under `max_connections=15` with headroom for `pg_dump` and psql.

- [ ] **Step 5: Verify locally**

Run: `dotnet build && dotnet test` → pass.
Run: `docker build -f docker/Dockerfile -t gymtracker-app:r2r .` → builds.

- [ ] **Step 6: Commit**

```bash
git add src/GymTracker.Web/GymTracker.Web.csproj docker/Dockerfile docker/docker-compose.yml src/GymTracker.Web/Program.cs
git commit -m "Use workstation GC and ReadyToRun, turn off Postgres JIT, cap the connection pool"
```

- [ ] **Step 7: Measure on wizard (after deploy, Antoan or the deploy session)**

```bash
ssh wizard 'docker logs gymtracker-app 2>&1 | grep "GC: server"'
# Use the app for a few minutes (both users, a workout and food logging), then:
ssh wizard 'docker stats --no-stream gymtracker-app gymtracker-db'
```

Expected: `server=False`; app memory 70 to 110 MiB. After a week of Beszel readings, set `mem_limit` to about 1.5× the observed peak (likely `160m`) in a separate commit.

### Task 8: Fewer queries per page

**Files:**
- Modify: `src/GymTracker.Web/Features/Profile/ProfileService.cs`
- Modify: `src/GymTracker.Web/Features/Profile/UserTime.cs`
- Modify: `src/GymTracker.Web/Features/Localization/UserPreferences.cs`
- Modify: `src/GymTracker.Web/Features/Body/BodyweightService.cs`, `Features/Body/BodyAnalytics.cs`, `Features/Food/NutritionTargets.cs`
- Modify: `src/GymTracker.Web/Features/Food/FoodLogService.cs` (`DayAsync`)
- Test: `tests/GymTracker.Web.Tests/ServerTimingTests.cs`

**Interfaces:**
- Produces: `ProfileService.GetAsync` returns the same tracked `UserProfile` instance for the rest of the request after the first call.
- Produces: `BodyweightService.AllWeighInsAsync(CancellationToken): Task<IReadOnlyList<WeighIn>>`, loaded once per request.

- [ ] **Step 1: Write the failing test**

Add to `ServerTimingTests`:

```csharp
    [Fact]
    public async Task Today_page_stays_within_its_query_budget()
    {
        var (email, password) = await app.CreateUserAsync();
        var client = app.NewClient();
        await AppFactory.LoginAsync(client, email, password);
        await client.GetAsync("/today"); // first load may store the initial maintenance estimate

        var response = await client.GetAsync("/today");

        var header = string.Join(",", response.Headers.GetValues("Server-Timing"));
        var queries = int.Parse(Regex.Match(header, "desc=\"(\\d+) queries\"").Groups[1].Value);
        queries.Should().BeLessThanOrEqualTo(10, header);
    }
```

- [ ] **Step 2: Run it and see it fail**

Run: `dotnet test --filter Today_page_stays_within_its_query_budget`
Expected: FAIL with the current count (about 15).

- [ ] **Step 3: Memoize the profile per request**

```csharp
public class ProfileService(AppDbContext db, TimeProvider time)
{
    private UserProfile? profile;

    /// <summary>The current user's profile, read once per request. Every user gets one when created.</summary>
    public async Task<UserProfile> GetAsync(CancellationToken ct) =>
        profile ??= await db.Profiles.SingleAsync(ct);
    // UpdateAsync and TodayAsync unchanged; they now reuse the cached instance.
}
```

`UserTime` takes `ProfileService` instead of `AppDbContext` and reads `TimeZoneId` from `GetAsync`. `UserPreferencesMiddleware.InvokeAsync` takes `ProfileService profiles` instead of `AppDbContext db` and builds the preferences from the loaded profile:

```csharp
    public async Task InvokeAsync(HttpContext context, ProfileService profiles)
    {
        // ...
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var profile = await profiles.FindAsync(context.RequestAborted);
            if (profile is not null)
            {
                prefs = new UserPreferences(profile.Theme, profile.Language);
                Remember(context.Response, context.Request.Cookies, prefs);
            }
        }
```

with `ProfileService.FindAsync` returning `profile ??= await db.Profiles.SingleOrDefaultAsync(ct)` (nullable, since a session can outlive a deleted profile).

- [ ] **Step 4: Load weigh-ins once**

```csharp
// BodyweightService
    private IReadOnlyList<WeighIn>? all;

    /// <summary>Every weigh-in, oldest first, read once per request (the trend needs the whole history).</summary>
    public async Task<IReadOnlyList<WeighIn>> AllWeighInsAsync(CancellationToken ct) =>
        all ??= await db.Bodyweights.AsNoTracking().OrderBy(b => b.Date).Select(b => new WeighIn(b.Date, b.WeightKg)).ToListAsync(ct);
```

Inject `BodyweightService` into `BodyAnalytics` and `NutritionTargets` and replace their own weigh-in queries with `AllWeighInsAsync(ct)`. `SetAsync` must reset `all = null` after saving.

- [ ] **Step 5: No tracking for display-only reads**

`FoodLogService.DayAsync`: add `.AsNoTracking()`. Check its callers first: if any caller modifies the returned entries and saves, leave it tracked and note it in the commit. Do the same for `TemplateService.ListAsync` and `SessionService.LastCompletedTemplateIdAsync` if they return entities used only for display.

- [ ] **Step 6: Run the tests**

Run: `dotnet test`
Expected: all pass, including the query budget and every `UserIsolationTests` case (the query filters still apply; nothing here bypasses them).

- [ ] **Step 7: Commit**

```bash
git add src/GymTracker.Web/Features tests/GymTracker.Web.Tests/ServerTimingTests.cs
git commit -m "Read the profile and weigh-ins once per request; Today drops from ~15 queries to 10 or fewer"
```

---

## Part D: Security

### Task 9: Security headers, host filtering and request limits

**Files:**
- Create: `src/GymTracker.Web/Features/Security/SecurityHeaders.cs`
- Create: `src/GymTracker.Web/Controllers/CspReportController.cs`
- Modify: `src/GymTracker.Web/Program.cs`
- Modify: `src/GymTracker.Web/Views/Shared/_Layout.cshtml` (htmx config)
- Modify: `docker/docker-compose.yml`, `docker/.env.example`
- Modify: `tests/GymTracker.Web.Tests/AppFactory.cs` (optional extra settings)
- Test: `tests/GymTracker.Web.Tests/SecurityHeaderTests.cs`

**Interfaces:**
- Produces: `SecurityHeaders.Policy` (the CSP string) and config key `Security:CspEnforce` (bool, default `false` = report-only).
- Produces: `AppFactory(PostgresFixture db, int loginLimit = 1000, IDictionary<string, string?>? settings = null)`; each entry is applied with `builder.UseSetting`.

- [ ] **Step 1: Extend AppFactory**

```csharp
public sealed class AppFactory(PostgresFixture db, int loginLimit = 1000, IDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // ... existing settings ...
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }
    }
```

- [ ] **Step 2: Write the failing tests**

```csharp
// tests/GymTracker.Web.Tests/SecurityHeaderTests.cs
using System.Net;
using FluentAssertions;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class SecurityHeaderTests(PostgresFixture db)
{
    [Theory]
    [InlineData("/account/login")]
    [InlineData("/css/app.css")]
    public async Task Responses_carry_the_security_headers(string path)
    {
        using var app = new AppFactory(db, settings: new Dictionary<string, string?> { ["Security:CspEnforce"] = "true" });
        var response = await app.NewClient().GetAsync(path);

        var h = response.Headers;
        h.GetValues("Content-Security-Policy").Single().Should().Contain("script-src 'self' 'wasm-unsafe-eval'").And.Contain("frame-ancestors 'none'");
        h.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        h.GetValues("Referrer-Policy").Single().Should().Be("same-origin");
        h.GetValues("Permissions-Policy").Single().Should().Contain("camera=(self)");
        h.GetValues("X-Frame-Options").Single().Should().Be("DENY");
        h.GetValues("Strict-Transport-Security").Single().Should().StartWith("max-age=31536000");
        h.Contains("Server").Should().BeFalse();
    }

    [Fact]
    public async Task Csp_is_report_only_until_enforced()
    {
        using var app = new AppFactory(db);
        var response = await app.NewClient().GetAsync("/account/login");

        response.Headers.Contains("Content-Security-Policy").Should().BeFalse();
        response.Headers.GetValues("Content-Security-Policy-Report-Only").Single().Should().Contain("report-uri /csp-report");
    }

    [Fact]
    public async Task Csp_reports_are_accepted_without_login_or_token()
    {
        using var app = new AppFactory(db);
        var content = new StringContent("{\"csp-report\":{\"violated-directive\":\"script-src\"}}", System.Text.Encoding.UTF8, "application/csp-report");

        var response = await app.NewClient().PostAsync("/csp-report", content);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Unknown_hosts_are_rejected()
    {
        using var app = new AppFactory(db, settings: new Dictionary<string, string?> { ["AllowedHosts"] = "localhost" });
        var request = new HttpRequestMessage(HttpMethod.Get, "/account/login");
        request.Headers.Host = "evil.example";

        (await app.NewClient().SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 3: Run them and see them fail**

Run: `dotnet test --filter SecurityHeaderTests`
Expected: FAIL (headers missing; `/csp-report` redirects to login). `Unknown_hosts_are_rejected` may already pass because host filtering reads `AllowedHosts`; keep it as a regression test.

- [ ] **Step 4: Implement the middleware**

```csharp
// src/GymTracker.Web/Features/Security/SecurityHeaders.cs
namespace GymTracker.Web.Features.Security;

/// <summary>Baseline browser hardening on every response. The CSP is report-only until Security:CspEnforce is true.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment env, IConfiguration config)
{
    // 'wasm-unsafe-eval': the barcode ponyfill compiles ZXing's WebAssembly. Inline styles stay allowed (style="" in views).
    public const string Policy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; media-src 'self' blob:; font-src 'self'; connect-src 'self'; worker-src 'self'; " +
        "manifest-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private readonly bool enforce = config.GetValue("Security:CspEnforce", false);

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            if (enforce)
            {
                h.ContentSecurityPolicy = Policy;
            }
            else
            {
                h["Content-Security-Policy-Report-Only"] = Policy + "; report-uri /csp-report";
            }
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "same-origin";
            h["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=(), payment=(), usb=(), interest-cohort=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            if (!env.IsDevelopment())
            {
                // Only this host; other peychev.uk subdomains are not covered.
                h.StrictTransportSecurity = "max-age=31536000";
            }
            return Task.CompletedTask;
        });
        return next(context);
    }
}
```

```csharp
// src/GymTracker.Web/Controllers/CspReportController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GymTracker.Web.Controllers;

/// <summary>Logs CSP violations reported by browsers while the policy is report-only.</summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[Route("csp-report")]
public class CspReportController(ILogger<CspReportController> logger) : Controller
{
    [HttpPost("")]
    [EnableRateLimiting(RateLimits.CspReport)]
    [RequestSizeLimit(8 * 1024)]
    public async Task<IActionResult> Report(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        logger.LogWarning("CSP violation: {Report}", body.Length > 2000 ? body[..2000] : body);
        return NoContent();
    }
}
```

In `Program.cs`:

```csharp
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    // Nothing in the app uploads files; barcode photos are decoded in the browser.
    k.Limits.MaxRequestBodySize = 1024 * 1024;
});
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "RequestVerificationToken";
    o.SuppressXFrameOptionsHeader = true; // SecurityHeadersMiddleware sends DENY
});
// in AddRateLimiter:
    o.AddPolicy(RateLimits.CspReport, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
// pipeline, right after UseForwardedHeaders():
app.UseMiddleware<SecurityHeadersMiddleware>();
```

Add `public const string CspReport = "csp-report";` to `RateLimits` in `Features/FeatureExceptionFilter.cs`. Remove the existing `AddAntiforgery` line it replaces.

In `_Layout.cshtml`, tighten htmx: `<meta name="htmx-config" content='{"defaultSwapStyle":"outerHTML","historyCacheSize":0,"allowEval":false,"includeIndicatorStyles":false,"selfRequestsOnly":true}' />`. Check that no view uses `hx-on`, `hx-vars` or `js:` values (they need eval); the earlier grep found none.

Compose `app.environment`: `AllowedHosts: "gym.peychev.uk;localhost"` and `Security__CspEnforce: "${CSP_ENFORCE:-false}"`. Add `CSP_ENFORCE=false` with a comment to `.env.example`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test`
Expected: all pass.

- [ ] **Step 6: Scanner under CSP**

Run the app with `Security__CspEnforce=true`, open Food → Add → Barcode in Chrome, decode a barcode image with the photo button. Expected: decodes, no CSP errors in the console. If the ponyfill creates a `blob:` worker or fetches the `.wasm` via `data:`, add exactly that source to the matching directive and note it in a comment.

- [ ] **Step 7: Commit**

```bash
git add src/GymTracker.Web docker tests/GymTracker.Web.Tests
git commit -m "Add CSP (report-only), HSTS and other security headers, host filtering and a 1 MB body limit"
```

- [ ] **Step 8: After deploy (Antoan)**

1. Confirm `AllowedHosts` matches what cloudflared sends: `curl -sI https://gym.peychev.uk/account/login | head -1` must be `HTTP/2 200`. If it is 400, cloudflared is sending the container name as Host; add `gymtracker-app` to `AllowedHosts` in compose and redeploy.
2. Cloudflare dashboard → gym.peychev.uk zone: turn off Rocket Loader, Email Address Obfuscation and automatic Web Analytics injection for this hostname (each injects scripts the CSP would block). Under Caching → Configuration, set Browser Cache TTL to "Respect Existing Headers".
3. Use the app on both phones for a few days, then `ssh wizard 'docker logs gymtracker-app 2>&1 | grep "CSP violation"'`. If empty, set `CSP_ENFORCE=true` in `docker/.env` and `docker compose -f docker/docker-compose.yml up -d`.

### Task 10: Login hardening

**Files:**
- Modify: `src/GymTracker.Web/Controllers/AccountController.cs`
- Modify: `src/GymTracker.Web/Program.cs` (cookie names, global login limiter, sensitive-action limiter)
- Modify: `src/GymTracker.Web/Controllers/SettingsController.cs` (rate limit on password and API key changes)
- Modify: `src/GymTracker.Web/Features/FeatureExceptionFilter.cs` (`RateLimits`)
- Modify: `tests/GymTracker.Web.Tests/HttpSecurityTests.cs` (cookie name assertion)
- Test: `tests/GymTracker.Web.Tests/LoginHardeningTests.cs`

**Interfaces:**
- Produces: `RateLimits.Sensitive = "sensitive"` (per-user, 5 per 15 minutes), config keys `RateLimits:LoginGlobalPerWindow` (default 30 per 15 minutes) and `RateLimits:SensitivePerWindow` (default 5).
- Produces: cookie names `__Host-gym.auth` and `__Host-gym.af` outside Development.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/GymTracker.Web.Tests/LoginHardeningTests.cs
using System.Net;
using FluentAssertions;
using GymTracker.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GymTracker.Web.Tests;

[Collection(PostgresCollection.Name)]
public class LoginHardeningTests(PostgresFixture db)
{
    private sealed class CountingHasher : PasswordHasher<AppUser>
    {
        public int Verifies;

        public override PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref Verifies);
            return base.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    [Fact]
    public async Task Unknown_emails_cost_the_same_password_hash_as_known_ones()
    {
        var hasher = new CountingHasher();
        using var app = new AppFactory(db);
        using var withHasher = app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IPasswordHasher<AppUser>>(hasher)));
        var client = withHasher.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

        await AppFactory.LoginAsync(client, "nobody@test.local", "some long password");

        hasher.Verifies.Should().Be(1, "a missing account must still run one hash so timing doesn't reveal it");
    }

    [Fact]
    public async Task Login_attempts_are_limited_across_all_addresses()
    {
        using var app = new AppFactory(db, loginLimit: 1000, settings: new Dictionary<string, string?> { ["RateLimits:LoginGlobalPerWindow"] = "3" });
        var client = app.NewClient();

        for (var i = 0; i < 3; i++)
        {
            (await AppFactory.LoginAsync(client, "nobody@test.local", "whatever password")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await AppFactory.LoginAsync(client, "nobody@test.local", "whatever password")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Password_changes_are_rate_limited_per_user()
    {
        using var app = new AppFactory(db, settings: new Dictionary<string, string?> { ["RateLimits:SensitivePerWindow"] = "2" });
        var (email, password) = await app.CreateUserAsync();
        var client = app.NewClient();
        await AppFactory.LoginAsync(client, email, password);
        var token = await AppFactory.TokenAsync(client, "/settings");
        var wrong = new Dictionary<string, string> { ["currentPassword"] = "not it at all", ["newPassword"] = "another long password", ["confirmPassword"] = "another long password" };

        await AppFactory.PostAsync(client, "/settings/password", token, wrong);
        await AppFactory.PostAsync(client, "/settings/password", token, wrong);

        (await AppFactory.PostAsync(client, "/settings/password", token, wrong)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
```

Update `Login_succeeds_and_ignores_external_return_urls` to expect `c.StartsWith("__Host-gym.auth=")` and `c.Contains("path=/")`.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --filter "LoginHardeningTests|Login_succeeds"`
Expected: FAIL on all four.

- [ ] **Step 3: Equalize timing in AccountController**

```csharp
public class AccountController(SignInManager<AppUser> signIn, UserManager<AppUser> users, IPasswordHasher<AppUser> hasher, ILogger<AccountController> logger) : Controller
{
    // A real hash to verify against when the email is unknown, so both paths take the same time.
    private static string? dummyHash;

    [AllowAnonymous]
    [HttpPost("login")]
    [EnableRateLimiting(RateLimits.Login)]
    public async Task<IActionResult> Login(string email, string password, string? returnUrl)
    {
        email = email?.Trim() ?? "";
        password ??= "";
        var user = await users.FindByEmailAsync(email);
        Microsoft.AspNetCore.Identity.SignInResult result;
        if (user is null)
        {
            var probe = new AppUser();
            dummyHash ??= hasher.HashPassword(probe, Guid.NewGuid().ToString());
            hasher.VerifyHashedPassword(probe, dummyHash, password);
            result = Microsoft.AspNetCore.Identity.SignInResult.Failed;
        }
        else
        {
            // Always persistent: the app is used as an installed PWA and should stay signed in.
            result = await signIn.PasswordSignInAsync(user, password, isPersistent: true, lockoutOnFailure: true);
        }

        if (result.Succeeded)
        {
            return LocalRedirect(SafeReturnUrl(returnUrl));
        }

        logger.LogWarning("Failed sign-in for {Email} from {Ip}: {Reason}", email, HttpContext.Connection.RemoteIpAddress,
            user is null ? "unknown email" : result.IsLockedOut ? "locked out" : "wrong password");
        var error = result.IsLockedOut
            ? "Too many failed attempts. Try again in 15 minutes."
            : "Email or password is wrong.";
        return View(new LoginViewModel(email, returnUrl, error));
    }
```

Note: the first unknown-email attempt pays one extra `HashPassword`; after that the cached hash is reused. That one-off difference is acceptable.

- [ ] **Step 4: Cookies and limiters in Program.cs**

```csharp
var hostCookies = !builder.Environment.IsDevelopment(); // __Host- needs Secure, which local http lacks
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = hostCookies ? "__Host-gym.auth" : "gym.auth";
    o.Cookie.Path = "/";
    // ... rest unchanged ...
});
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "RequestVerificationToken";
    o.SuppressXFrameOptionsHeader = true;
    o.Cookie.Name = hostCookies ? "__Host-gym.af" : "gym.af";
    o.Cookie.SecurePolicy = hostCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddRateLimiter(o =>
{
    // ... existing Login and CspReport policies ...

    // Distributed guessing: all sign-in attempts together, whatever the source address.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        HttpMethods.IsPost(http.Request.Method) && http.Request.Path.StartsWithSegments("/account/login")
            ? RateLimitPartition.GetSlidingWindowLimiter("login-all", _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = config.GetValue("RateLimits:LoginGlobalPerWindow", 30),
                Window = TimeSpan.FromMinutes(15),
                SegmentsPerWindow = 3,
                QueueLimit = 0,
            })
            : RateLimitPartition.GetNoLimiter("other"));

    // Password and API key changes: per signed-in user.
    o.AddPolicy(RateLimits.Sensitive, http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimits:SensitivePerWindow", 5),
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0,
        }));
});
```

Add `[EnableRateLimiting(RateLimits.Sensitive)]` to `SettingsController.ChangePassword`, `openrouter-key`, `openrouter-key/test` and `openrouter-key/remove`. Add `public const string Sensitive = "sensitive";` to `RateLimits`. Because `UseRateLimiter()` runs before `UseAuthentication()` in the pipeline, move `app.UseRateLimiter();` to after `app.UseAuthorization();` so `http.User` is populated for the per-user partition (the per-IP login policy does not depend on the order).

Deploying this signs both users out once (the auth cookie is renamed). Say so in the release note to Antoan.

- [ ] **Step 5: Run the tests**

Run: `dotnet test`
Expected: all pass, including the existing lockout and per-IP rate limit tests.

- [ ] **Step 6: Commit**

```bash
git add src/GymTracker.Web tests/GymTracker.Web.Tests
git commit -m "Harden sign-in: constant-cost unknown emails, global login limit, per-user limit on password and key changes, __Host- cookies"
```

### Task 11: Limit AI parsing

**Files:**
- Modify: `src/GymTracker.Web/Controllers/FoodController.cs` (`Parse`, line 47)
- Modify: `src/GymTracker.Web/Views/Food/Add.cshtml` (textarea `maxlength`)
- Modify: `src/GymTracker.Web/Program.cs`, `Features/FeatureExceptionFilter.cs` (`RateLimits.AiParse`)
- Modify: `src/GymTracker.Web/Resources/strings.bg.json`
- Test: `tests/GymTracker.Web.Tests/NutritionTests.cs`

**Interfaces:**
- Produces: `FoodParsing.MaxTextLength = 1000` in `GymTracker.Core/Nutrition/FoodParsing.cs` (Core holds the rule; the controller only calls it).

Why: the parse endpoint spends the shared OpenRouter balance. A stolen session or a stuck client could run it in a loop with long text.

- [ ] **Step 1: Write the failing tests**

Add to the Web `NutritionTests` class (it already has a logged-in client helper; reuse whatever helper the existing parse tests use):

```csharp
    [Fact]
    public async Task Food_text_over_the_limit_is_rejected_before_the_parser_runs()
    {
        var (client, token) = await SignedInAsync();
        var response = await AppFactory.PostAsync(client, "/food/parse", token, new()
        {
            ["date"] = "2026-10-04", ["meal"] = "Lunch", ["text"] = new string('a', 1001),
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
```

and a rate-limit test with `["RateLimits:AiParsePerWindow"] = "2"` asserting the third call returns 429, following `Password_changes_are_rate_limited_per_user`.

- [ ] **Step 2: Run and see them fail**

Run: `dotnet test --filter Food_text_over_the_limit`
Expected: FAIL.

- [ ] **Step 3: Implement**

In Core `FoodParsing.cs`:

```csharp
    /// <summary>Longest food text sent to the parser; a day's meal fits comfortably.</summary>
    public const int MaxTextLength = 1000;
```

In `FoodController.Parse`, first line:

```csharp
        if (text is { Length: > FoodParsing.MaxTextLength })
        {
            throw new ValidationException(new LocalText("Keep the text under {0} characters.", FoodParsing.MaxTextLength));
        }
```

Add `[EnableRateLimiting(RateLimits.AiParse)]` with a per-user fixed window (`RateLimits:AiParsePerWindow`, default 30 per 10 minutes), registered like `Sensitive`. Add `maxlength="1000"` to the textarea. Add `"Keep the text under {0} characters.": "Текстът трябва да е под {0} знака."` to `strings.bg.json`.

- [ ] **Step 4: Run tests and commit**

Run: `dotnet test` → all pass.

```bash
git add src tests
git commit -m "Cap food text at 1,000 characters and rate-limit AI parsing per user"
```

### Task 12: Container hardening

**Files:**
- Modify: `docker/docker-compose.yml`
- Modify: `README.md` (deployment section)

- [ ] **Step 1: Compose**

```yaml
  app:
    read_only: true
    tmpfs:
      - /tmp:size=16m
    cap_drop: [ALL]
    security_opt: ["no-new-privileges:true"]
    pids_limit: 200
  db:
    security_opt: ["no-new-privileges:true"]
    pids_limit: 200
```

ASP.NET Core writes nothing to the image filesystem besides `/tmp` and the bind-mounted `/keys`. The db container keeps its default capabilities (the Postgres entrypoint changes ownership at first start).

- [ ] **Step 2: Verify locally**

Run: `docker compose -f docker/docker-compose.local.yml` equivalent with the same keys (or temporarily `docker run --read-only --tmpfs /tmp --cap-drop ALL ...` on the built image). Expected: the app starts, logs in, logs a set, saves an OpenRouter key (writes to the DB, and Data Protection writes to `/keys`).

- [ ] **Step 3: Commit**

```bash
git add docker/docker-compose.yml README.md
git commit -m "Run the app container read-only with no capabilities and no privilege escalation"
```

- [ ] **Step 4: For Antoan on wizard (one-off, needs sudo)**

```bash
sudo chmod 700 ~/docker/gymtracker/keys
sudo chown -R 1000:1000 ~/docker/gymtracker/keys
```

The keys directory can forge sign-in cookies and decrypt the stored OpenRouter key. It is also copied by the nightly backup to the HDD and the PC; keep those destinations private.

### Task 13: Passkey sign-in (Face ID)

ASP.NET Core Identity in .NET 10 has passkeys built in (`SignInManager.MakePasskeyCreationOptionsAsync`, `MakePasskeyRequestOptionsAsync`, `PerformPasskeyAttestationAsync`, `PasskeySignInAsync`, `UserManager.AddOrUpdatePasskeyAsync`, schema `IdentitySchemaVersions.Version3`). No package is added. Before writing code, open the .NET 10 Blazor Web App template's `IdentityComponentsEndpointRouteBuilderExtensions.cs` (linked from the "Passkeys in ASP.NET Core" docs) and copy the exact signatures; the names above come from those docs.

**Files:**
- Modify: `src/GymTracker.Web/Program.cs` (`o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;`, passkey options with server domain from config `Passkeys:ServerDomain`)
- Create: migration `AddPasskeys` via `dotnet ef migrations add AddPasskeys --project src/GymTracker.Web --output-dir Data/Migrations`
- Create: `src/GymTracker.Web/Controllers/PasskeyController.cs`
- Create: `src/GymTracker.Web/wwwroot/js/passkey.js` (add to `sw.js` precache, bump version)
- Modify: `Views/Account/Login.cshtml` (a "Sign in with Face ID" button; `autocomplete="username webauthn"` on the email field for autofill)
- Modify: `Views/Settings/Index.cshtml` (a "Passkeys" row: add, list with created date, remove)
- Modify: `src/GymTracker.Web/Resources/strings.bg.json`
- Modify: `docker/docker-compose.yml`, `docker/.env.example` (`Passkeys__ServerDomain: gym.peychev.uk`)
- Test: `tests/GymTracker.Web.Tests/PasskeyTests.cs`

**Interfaces:**
- Produces endpoints:
  - `POST /account/passkey/creation-options` (signed in, antiforgery, `RateLimits.Sensitive`) → JSON creation options.
  - `POST /account/passkey/register` (signed in, antiforgery, `RateLimits.Sensitive`) body `{ credential: string, name: string }` → 204.
  - `POST /account/passkey/request-options` (anonymous, antiforgery, `RateLimits.Login`) → JSON request options.
  - `POST /account/passkey/login` (anonymous, antiforgery, `RateLimits.Login`) body `{ credential: string, returnUrl?: string }` → `{ redirect: string }` or 400 with the localized "Email or password is wrong." equivalent "That passkey didn't work."
  - `POST /settings/passkeys/{id}/remove` (signed in).
- Produces JS: `gymPasskey.register(): Promise<void>`, `gymPasskey.signIn(conditional: boolean): Promise<void>`. Uses `PublicKeyCredential.parseCreationOptionsFromJSON` / `parseRequestOptionsFromJSON` and `credential.toJSON()` when available, else a base64url fallback (both functions written out in passkey.js, about 30 lines).

- [ ] **Step 1: Write the failing tests**

WebAuthn ceremonies can't be completed without an authenticator, so the tests pin the server contract and the security properties:

```csharp
[Collection(PostgresCollection.Name)]
public class PasskeyTests(PostgresFixture db) : IDisposable
{
    private readonly AppFactory app = new(db);

    public void Dispose() => app.Dispose();

    [Fact]
    public async Task Creation_options_need_a_signed_in_user()
    {
        var client = app.NewClient();
        var token = await AppFactory.TokenAsync(client, "/account/login");
        var response = await AppFactory.PostAsync(client, "/account/passkey/creation-options", token);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Creation_options_name_the_relying_party_and_the_user()
    {
        var (email, password) = await app.CreateUserAsync();
        var client = app.NewClient();
        await AppFactory.LoginAsync(client, email, password);
        var token = await AppFactory.TokenAsync(client, "/settings");

        var json = await (await AppFactory.PostAsync(client, "/account/passkey/creation-options", token)).Content.ReadAsStringAsync();

        json.Should().Contain("\"challenge\"").And.Contain(email);
    }

    [Fact]
    public async Task A_garbage_assertion_does_not_sign_in()
    {
        var client = app.NewClient();
        var token = await AppFactory.TokenAsync(client, "/account/login");
        await AppFactory.PostAsync(client, "/account/passkey/request-options", token);
        var request = new HttpRequestMessage(HttpMethod.Post, "/account/passkey/login")
        {
            Content = JsonContent.Create(new { credential = "{\"id\":\"AAAA\",\"type\":\"public-key\",\"response\":{}}" }),
        };
        request.Headers.Add("RequestVerificationToken", token);

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AppFactory.IsRedirectTo(await client.GetAsync("/today"), "https://localhost/account/login").Should().BeTrue();
    }

    [Fact]
    public async Task One_user_cannot_remove_another_users_passkey()
    {
        // Insert a passkey row for user A directly through UserManager.AddOrUpdatePasskeyAsync with a fake
        // credential, sign in as B, POST /settings/passkeys/{A's id}/remove, expect 404 and A's row still present.
    }
}
```

Write the body of the last test using the `UserPasskeyInfo` constructor from the template source (it takes the credential id, public key, created date, sign count, transports, flags and attestation data); use random bytes for the key fields.

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test --filter PasskeyTests` → FAIL (404s).

- [ ] **Step 3: Schema and options**

In `AddIdentity(o => ...)` add `o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;`. Configure the passkey server domain from `Passkeys:ServerDomain` (unset in Development, where the request host `localhost` is used). Run the migration command above; review the generated migration (it should only add the `AspNetUserPasskeys` table).

- [ ] **Step 4: Controller**

`PasskeyController` is thin: each action calls the SignInManager/UserManager method and maps the result to JSON or a status code. Passkey sign-in goes through `signIn.PasskeySignInAsync(credentialJson)`, which applies the same lockout as passwords; log failures the same way as Task 10. The remove action loads the passkey through `users.GetPasskeysAsync(currentUser)` and only removes an id found in that list (ownership check), else returns 404.

- [ ] **Step 5: Front end**

`passkey.js` exposes `gymPasskey.register()` (Settings) and `gymPasskey.signIn()` (Login). On the login page, if `PublicKeyCredential.isConditionalMediationAvailable()` resolves true, start a conditional request so iOS offers the passkey in the keyboard's autofill bar; the explicit button does a modal request. Both send the antiforgery token from the page's hidden field. On success, navigate with `location.href = redirect` (full load: the login page is outside soft navigation).

- [ ] **Step 6: Verify**

Run: `dotnet test` → all pass.
Manual on an iPhone against a test deploy at `https://gym.peychev.uk` (WebAuthn needs the real domain on iOS): Settings → Add passkey → Face ID; sign out; Sign in with Face ID; the keyboard autofill bar offers the passkey; remove the passkey in Settings and confirm it no longer signs in. Repeat on the second account.

- [ ] **Step 7: Commit**

```bash
git add src tests docker
git commit -m "Add passkey (Face ID) sign-in using ASP.NET Core Identity's built-in WebAuthn support"
```

### Task 14: Dependency and image hygiene

**Files:**
- Modify: `src/GymTracker.Web/GymTracker.Web.csproj`, `src/GymTracker.Core/GymTracker.Core.csproj`
- Modify: `docker/Dockerfile`, `docker/docker-compose.yml`

- [ ] **Step 1: Fail the build on known-vulnerable packages**

Add to both project files:

```xml
    <NuGetAudit>true</NuGetAudit>
    <NuGetAuditMode>all</NuGetAuditMode>
    <WarningsAsErrors>$(WarningsAsErrors);NU1903;NU1904</WarningsAsErrors>
```

(NU1903/NU1904 are high and critical advisories, including transitive ones.)

- [ ] **Step 2: Pin base images by digest**

Resolve the current digests and pin them, keeping the tag for readability:

```bash
docker buildx imagetools inspect mcr.microsoft.com/dotnet/sdk:10.0 --format '{{json .Manifest.Digest}}'
docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra --format '{{json .Manifest.Digest}}'
docker buildx imagetools inspect postgres:18 --format '{{json .Manifest.Digest}}'
```

Write them as `FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:<digest>` etc. Add a README line: refresh the digests monthly for security patches (`docker buildx imagetools inspect ...`, update, rebuild).

- [ ] **Step 3: Verify and commit**

Run: `dotnet build && dotnet test && docker build -f docker/Dockerfile .` → all succeed.

```bash
git add src/*/GymTracker.*.csproj docker README.md
git commit -m "Fail builds on high and critical NuGet advisories and pin base images by digest"
```

### Task 15: Spec, README and env example

**Files:**
- Modify: `SPEC.md` sections 2, 8, 9, 10
- Modify: `README.md`
- Modify: `docker/.env.example`

- [ ] **Step 1: Update SPEC.md**

- Section 2 (UI row): page changes use a small soft-navigation module (`nav.js`) with View Transitions; htmx remains for partial updates.
- Section 8: the service worker serves versioned static files cache-first; page modules mount and clean up per page.
- Section 9: add CSP (report-only then enforced), HSTS, `nosniff`, `Referrer-Policy`, `Permissions-Policy`, `X-Frame-Options: DENY`, `AllowedHosts`, 1 MB body limit; constant-cost unknown-email sign-in; global login limiter (30 per 15 minutes); per-user limits on password/API key changes and AI parsing; `__Host-` cookies; passkeys alongside passwords; container runs read-only without capabilities.
- Section 10: `cpus: 1.0`, workstation GC and ReadyToRun, Postgres `jit=off`, `max_connections=15`, connection pool 8, image digests.

- [ ] **Step 2: README and .env.example**

README: the Cloudflare dashboard steps from Task 9 step 8, the keys-folder permissions from Task 12, enabling CSP enforcement, adding a passkey on each iPhone. `.env.example`: `CSP_ENFORCE=false` with a comment.

- [ ] **Step 3: Final verification and commit**

Run: `dotnet build && dotnet test` and `node design-samples/tools/nav-check.mjs` and `node design-samples/tools/nav-timing.mjs`. Report the actual numbers against the Task 1 baseline.

```bash
git add SPEC.md README.md docker/.env.example
git commit -m "Document the navigation, footprint and security changes in the spec and README"
```

---

## Order and checkpoints

Parts can ship separately. Recommended order and deploy points:

1. Tasks 1, 2 → deploy (baseline numbers, immediate win from the service worker).
2. Tasks 3 to 6 → deploy, test on both iPhones for a few days (largest user-visible change).
3. Tasks 7, 8 → deploy, read `docker stats`, later lower `mem_limit`.
4. Tasks 9 to 12, 14 → deploy (signs both users out once because of the cookie rename), then CSP enforcement after a clean report period.
5. Task 13 (passkeys) → deploy on its own.
6. Task 15 alongside whichever deploy comes last.
