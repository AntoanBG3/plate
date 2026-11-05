# Client and PWA evidence notes, 2026-11-05

Audit-only evidence at source baseline `462ef118827b00839941eab87cceb2a106c93065`. No app files changed. `tools/perf/client-probes.cjs` injects failures into the unchanged scripts in a Node VM; its results are deterministic control-flow observations, not device timing. `design-samples/tools/perf-audit.mjs` measures the local synthetic app at 390 × 844 and records its own environment.

## Journey map and hypotheses recorded before browser timing

| Journey | Entry, scripts, and server contract | Hypothesis to measure |
|---|---|---|
| Log a set during poor signal | Session form → `session.js:246` → account-scoped `queue.js:7` → `POST /train/sets` (`session.js:150`) → server partial replaces pending row | Rendering is optimistic, so ordinary RTT should not delay acknowledgement. A request that never settles may block replay despite reconnect/retry events. |
| Barcode / manual food | Food add Scan/Manual → ponyfill + `scanner.js` → local `GET /food/barcode/known` or barcode form submission; label script supplied when feature enabled | Scanner WASM dominates install bytes; async camera permission may outlive sheet/page teardown. |
| Text/search/repeat food | Food forms and htmx, existing soft navigation for plain POSTs | Network round trips still dominate ordinary navigation; first Progress navigation may fetch versioned scripts despite bare precache. |
| Today / main tabs | `_Layout.cshtml:55-65` shell → `nav.js:65-75` touch prefetch → `go:254` → `swapIn:183` | Warm transitions retain shell and incur no static network requests once exact versions cached. |
| Progress charts | `Views/Progress/Index.cshtml:214-215` → Chart.js + `progress.js`, mounted by page lifecycle | First chart visit may add sequential scripts (`nav.js:194-198`); warm visits should reuse them. |
| First PWA launch / update | `site.js:465-466` registers `/sw.js`; `sw.js:44-52` precaches and activates immediately | Every changed cache version downloads all 35 bare assets; first page uses hashed URLs that initially miss bare cache; intermittent failure of any precache asset keeps old worker active. |

## Reproduced findings

### Client A: stalled request blocks set replay

- Area: logging sets on gym signal. A single pending `fetch` holds `sending=true` for the entire queue (`session.js:112-128`). The fetch has no AbortSignal/deadline (`150-160`); later reconnect/foreground/15-second triggers (`422-440`) exit immediately.
- Evidence: `node tools/perf/client-probes.cjs`, `tools/perf/results/client-probes.json`: four simulated 15-second callbacks plus an `online` event produced **1 request**, **no AbortSignal**, and **1 retained pending set** while the injected request never resolved. This is a deliberately controlled blackhole, not a measured frequency in production or an actual 60-second wall-clock test.
- Cause verified in control flow; incidence on iPhone/network remains unmeasured. Data is retained in durable storage. Users can see later sets waiting even after connectivity returns until the browser settles the old request or the page reloads.
- Severity: medium user-visible reliability; especially relevant to the highest-priority journey.
- Independent change: abort only stalled send attempts after an explicit deadline, release single-flight guard, retain original ClientId and retry on existing schedule. No new storage keys needed. Verify replay after headers stall/body stall, idempotent duplicate success, 401/409 and in-memory storage failure; tests start with existing v2 data. Rollback restores old send function without touching persisted data. SPEC 8 needs a send-deadline sentence.

### Client B: camera can start after its sheet and page were closed

- Area: barcode scanning. `getUserMedia` is awaited (`scanner.js:187`) and then starts the stream/video/interval unconditionally (`193-213`). Closing the sheet calls `stop` (`226-229`); leaving the module also calls `stop` (`252-256`), but both run before the pending permission/device promise yields a stream.
- Evidence: same deterministic probe resolves the permission promise after both close and module cleanup: **0 track stops**, **1 video.play**, **1 started scanner interval**. No physical camera was opened. The late async continuation has no lifecycle generation/closed check.
- Cause verified; real-device incidence unmeasured. A user who closes or leaves during the permission prompt/device startup can get an invisible active camera and polling work until a later pagehide event/document close.
- Severity: medium user-visible/resource reliability. Independent fix: invalidate a generation/closed flag on close/cleanup; immediately stop any late returned stream and do not start video/timer. Tests resolve getUserMedia after close, after navigation, and after a second open; check all tracks stopped. Rollback isolated to scanner lifecycle, no data/schema/translation change; SPEC update unnecessary (implements existing lifecycle intent).

### Client C: earlier navigation response can overwrite a later choice

- Area: rapid tab switches on bad signal. `go()` awaits each independent request then calls `swapIn` without a request generation/latest-intent check (`nav.js:256-283`). Skipping `current` transitions only affects animations (`237-241`), not pending network responses.
- Deterministic VM evidence in `client-probes.json`: requested Train then Food; Food response delivered first and showed Food; delivering Train afterwards changed the final tab and URL to Train. DOM is stubbed, so the local-browser harness also repeats this with a 1-second injected Train response delay. Production frequency remains unknown.
- Severity: medium user-visible reliability. Independent change: last-navigation generation guard for GET intents; ignore stale responses before scripts/swap/fallback, preserving plain POST completion semantics and pending set requests. Aborting superseded read fetches is optional; never abort a POST and assume it did not save. Add E2E tests for reversed GET responses, Back while loading, and slow POST followed by GET; rollback confined to navigation control flow, no storage or DB changes; SPEC 2/8 may document last-chosen-page behavior.

## Static inventory and update observations to measure

- `sw.js:6-42`: **35 assets, 2,005,839 decoded bytes** (filesystem inventory, reproducible probe). WASM **1,093,289 B / 54.5%**; Chart.js **208,522 B / 10.4%**; 512px icon **207,111 B / 10.3%**. This verifies the supplied approximate baseline. These are not gzip/Brotli transfer sizes.
- `sw.js:45` uses one `cache.addAll(PRECACHE)`: installation depends on all assets succeeding. On cache-name updates, a fresh cache fetches every bare URL even if its content is unchanged. `activate:50-52` deletes old caches then claims clients. New worker failure normally leaves the old worker active: this is reliability protection, not evidence of data loss.
- `_Layout.cshtml:51-65` requests hashed CSS/JS while precache uses bare paths. `sw.js:72-85` uses exact matching online and only ignores search after network failure, so initial bare cached shell files do not satisfy the first online hashed request. `store:63-66` removes other URL versions once the exact version arrives. Browser run will quantify extra downloads; this is a known tradeoff, not a reason to remove offline assets without updating SPEC 2/8.
- No proposal to lazy-load/remove scanner precaching by default: SPEC 2/8 explicitly requires self-hosted pinned scanner WASM precached. A manifest keyed by actual content hashes could keep offline guarantees while reusing unchanged assets across deploys. Prove gain with initial install and simulated-deploy cache bytes and offline scan test; add installation failure and open-old-client update tests. Changes touch service-worker persistence, require careful cache migration and SPEC 8 update; rollback can restore old worker with a new cache name.

## Measured/code-inspected and fine

- Optimistic set UI marks pending immediately, disables duplicate submit, and writes ahead before send (`session.js:264-282`); ClientId preserved across retries. Existing Node tests exercise denied storage, account changes, legacy adoption and retry (`tests/js/offline-queue.test.cjs`). Server ownership guard runs before antiforgery (`Features/Security/QueueOwnerMiddleware.cs:12-33`), including old tabs without owner header (503) and account mismatch (409); queue does not discard those.
- No HTML/data page cache: service worker handles statics and offline navigation fallback only (`sw.js:89-104`). htmx history cache is disabled (`_Layout.cshtml:49`), reducing stale personal-page exposure.
- Soft navigation forces full reload when build version, user identity, language or signed-in shell changes (`nav.js:187-190`), so the app does not intentionally keep an old script shell across new server pages.
- Module scripts are loaded once and camera/timer/chart cleanup is explicit; ordinary rest timer loops run only during an active timer/session (`session.js:490-508`, cleanup `665-691`), so do not re-propose the completed 250ms global polling fix from October.
- Existing `design-samples/tools/sw-check.mjs` checks zero worker network requests for warm tab navigations, but it is a standalone script, not in `tools/check.sh`. E2E `UntestedAreasTests.cs:132-151` covers offline fallback. No automated cold installation/update-race coverage found.

## Browser measurement caveats and command

`node design-samples/tools/perf-audit.mjs https://localhost:5443 > tools/perf/results/browser.json` (set `PERF_EMAIL` / `PERF_PASSWORD` for local synthetic account). `playwright-core` must resolve from `design-samples/tools/node_modules`; Chrome must be installed. For HTTPS use the scratch certificate/key in `PERF_TLS_DIR` (default `/private/tmp/repa-perf-tls`); local self-signed certificates are accepted by the measurement browser and upstream client. Five fresh contexts each at 100ms and 400ms proxy response delay, shared 1.5 MB/s decoded download schedule, 4× CDP CPU slowdown. The proxy applies network cost to service-worker-owned fetches too; applying page-only CDP conditions would fail to throttle those. Complete response bodies are buffered and delivered at the simulated completion time, upload is not throttled, conditional cache validation disabled, identity encoding requested. Results are controlled relative comparisons, not a calibrated iPhone radio simulation or production transfer measurements. A synthetic deployment changes only the local proxy's worker cache-name suffix; unchanged assets still represent the current worker's full reinstall policy.
