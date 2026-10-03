# Markup contract for the redesign

Everything here is read by JavaScript, htmx, the service worker or the tests. Restyle and restructure around it freely; rename or remove only together with every caller and test, in the same commit.

## Layout (`Shared/_Layout.cshtml`)
- `body[hx-headers]` carries the anti-forgery token as JSON `{"RequestVerificationToken": "..."}`. session.js parses it at send time.
- `meta[name=htmx-config]`: `defaultSwapStyle: outerHTML`, `historyCacheSize: 0`.
- `#toast[role=alert][hidden]`: site.js and session.js set its text and toggle `hidden`.
- `ViewData["HideTabs"]` hides the tab bar (login). `ViewData["Title"]` sets `<title>`.
- TempData error/notice paragraphs (`.error[role=alert]`, `.notice[role=status]`).

## Session (`Train/Session.cshtml`, `_ExerciseCard`, `_SetRow`), read by `js/session.js`
- `#session[data-session-id][data-active="true|false"][hidden]`.
- `#queue-status[role=status][hidden]`: text and `hidden` set by session.js.
- `.exercise[data-rest]` with `id="ex-{id}"`: the rest seconds for the timer; found with `form.closest(".exercise")`.
- `#warmups-{sessionExerciseId}` and `#rows-{sessionExerciseId}`: containers for set rows; pending sets are restored into the first `form[data-log-set]:not(.pending)` there; htmx `+ Warmup` / `+ Set` append into them (`hx-get=".../row?warmup=true|false"`, `hx-swap="beforeend"`).
- Blank row: `form.set-row[data-log-set][data-client-id]` with inputs `clientId`, `sessionExerciseId`, `isWarmup` (hidden), `weightKg`, `reps`, and a `button[type=submit]`. session.js intercepts the submit, adds `.pending`, disables inputs and buttons.
- Logged row: `form.set-row.done[data-client-id]` with `hx-post="/train/sets/{id}/update" hx-target="this" hx-swap="outerHTML"`, inputs `weightKg`, `reps`; delete button `hx-post="/train/sets/{id}/delete" hx-target="closest form" hx-swap="outerHTML" hx-confirm`.
- `.warmup` modifier class on warmup rows.
- Rest timer: `#rest-timer[hidden]` (toggled; gets `.done` at zero), `#rest-time[role=timer]` (text `m:ss`), `.rest-fill` inside `#rest-timer` (style width set as a percentage), `#rest-add`, `#rest-skip`.
- localStorage keys `gym.pendingSets.v1` and `gym.restTimer.v1` (shape `{sessionId, end, total, beeped}`).
- Server partial `_SetRow` is returned by `POST /train/sets`, `GET /train/session-exercises/{id}/row`, `POST /train/sets/{id}/update`, `POST /train/sets/{id}/delete`; session.js swaps its first element in for the pending row.

## Food, read by `js/site.js` and `js/scanner.js`
- `[data-per100="kcal,protein"]` on portion forms/confirm sections, with an input named `grams` or ending in `Grams`, and a `[data-totals]` output.
- Scanner: `#scanner` containing `video`, `[data-start]`, `[data-photo]` (file input), `[data-status]`; `#barcode-form` with `input[name=code]` (submitted with `requestSubmit`).
- Confirm form field names `items[i].Include|Description|Grams|Kcal|Protein|Carbs|Fat|FoodItemId`.

## Progress, read by `js/progress.js`
- `#weight-data` + `#weight-chart`, `#e1rm-data` + `#e1rm-chart`, `#calories-data` + `#calories-chart`, `#volume-data` + `#volume-chart` (JSON in `script[type=application/json]`, canvas).
- `#exerciseId` select submits its form on change.

## Service worker (`sw.js`)
- `PRECACHE` lists every static path; each must exist or install fails. Cache name `gym-static-v4` must be bumped when CSS/JS change.
- `/offline.html` is standalone (links `/lib/fonts/fonts.css` and `/css/site.css` directly, no asp-append-version).

## Tests (`tests/GymTracker.Web.Tests`)
- `/today` contains the text `Logged today: 82.4 kg, trend 82.4 kg`.
- `/settings` contains `Food text parser` and `No Gemini key`.
- `/progress` contains `id="weight-data"` and `Trend now: <strong>82.4 kg</strong>`.
- Login after lockout contains `Too many failed attempts`.
- `/manifest.webmanifest` contains `"start_url": "/today"`.

## Forms posted by plain HTML
Every `form[action]` keeps its action, method, field names and `asp-antiforgery`. Inline `onsubmit="return confirm(...)"` guards exist on finish, delete session, remove exercise, delete entry, archive template and log out everywhere.
