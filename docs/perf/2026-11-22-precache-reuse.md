# P12: reusing unchanged precache files across deploys

The 2026-11-05 plan deferred P12 until a measurement showed it paid. This is that measurement and what was built.

## What a cache-name bump costs

Every bump of `CACHE` in `sw.js` makes the new worker fill a new cache with all 35 precached files. Measured on 2026-11-22 against the browser tests' local server, by running the new worker's `cache.addAll` of the same list from a page that the current worker already controlled:

| Engine | Requests | Conditional | Bytes downloaded |
|---|---:|---:|---:|
| WebKit (Playwright, the iPhones' engine) | 36 | 0 | 1,789,348 |
| Chromium | 51 | 21 | 293,013 (most answered from its HTTP cache) |

WebKit re-downloaded every file in full; the files have ETags with `Cache-Control: no-cache`, but no request was conditional. The November audit saw the same in Chrome through its throttling proxy (2,009,340 bytes, all 200). The files under `wwwroot/lib` are 1,474,492 of WebKit's 1,789,348 bytes: the scanner's WASM alone is 1,093,289. They almost never change (the WASM changed once since October). `CACHE` went from v26 in mid-October to v32 on 2026-11-22, about one bump a week.

The install runs in the background while the old worker serves, so this is bandwidth on mobile data, not a wait. Playwright's WebKit is not iPhone Safari; a real iPhone was not measured.

## What was built

Not the plan's content-hash manifest: the app files are 315 KB of a bump and change with most deploys, so hashing them would save little for the complexity. Instead the libraries got their own cache:

- `LIB` (`gym-lib-v1`) holds the precached `/lib/` files and survives a `CACHE` bump. A new worker fetches only the library files `LIB` lacks, and activation deletes every cache except `CACHE` and `LIB`.
- A versioned library URL (`?v=`) is stored next to the precached copy instead of replacing it, so the next worker still finds the file.
- `LIB` must be bumped when a file under `wwwroot/lib` changes. `tests/js/sw-lib.test.cjs` compares the files' hashes with `tests/js/sw-lib.hashes.json` and fails until it is, and also covers install, a bump, the upgrade from the single cache, versioned files, offline fallback and a failed download.

Expected effect per bump in WebKit: about 315 KB instead of 1.79 MB. The first worker after this change still downloads the libraries once, into the new cache. In real Chromium and WebKit the installed worker ends with `gym-lib-v1` holding 13 library entries (the 11 precached files plus the versioned htmx and Chart.js the page loaded) and `gym-static-v33` none.
