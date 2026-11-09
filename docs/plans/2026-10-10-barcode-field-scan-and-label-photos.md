# Phase 15: Scan into the barcode field, and up to three label photos — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user fill the manual form's barcode field with the camera instead of typing it (and be offered the food the app already has for that code), and let a label read use up to three photos of the same package.

**Architecture:** Two independent workstreams on one branch. (A) The scanner sheet moves into a partial rendered on both the Scan and Manual tabs; `scanner.js` learns a second mode where a found code goes into a field instead of being looked up, and owns the barcode field's behaviour (share row, the "already in the app" card from a new local-only `GET /food/barcode/known`). (B) `ILabelReader` takes a list of images; `LabelScan` gets up to three ordered `LabelScanPhoto` rows; `POST /food/label` accepts one to three photos; under the photo button an "Add more photos" press opens three slots (the table and two optional ones) that hold shrunk photos on the phone until "Read the label" sends them all in one call. Without the press, one photo still reads at once.

**Tech Stack:** .NET 10, ASP.NET Core MVC + Razor, EF Core + Npgsql (PostgreSQL 18), OpenRouter chat completions (vision), barcode-detector ponyfill (already vendored), xUnit + FluentAssertions + Testcontainers, Playwright (E2E project).

**Spec:** `SPEC.md` sections 4 (LabelScanPhoto), 6.6, 7 (Food, manual food), 9, 11, 12 and a new Phase 15, written in Task 1 of this plan. The source request is two items from Antoan's list (2026-10-10): "add small scan option so user doesn't have to manually write out barcode in 'manual' log mode and when scanning food" and "add 2 more slots for more photos (extra info) when logging with photos".

## Decisions already made (Antoan, 2026-10-10)

- **Photo flow: all photos at once, extra slots on request.** The photo button still reads one photo at once. A press on "Add more photos" swaps it for three slots: "Nutrition table" and two "Optional" ones. Picking into a slot sends nothing; "Read the label" sends every filled slot in one model call, so one `LabelScan` and one count toward the 20-per-10-minutes limit per read. There is no re-read after the fact. (Changed 2026-10-10 from an earlier "read first, add more" draft.)
- **Known barcode: offer it, easy to reject.** When the field holds a code the app already has (own food first, then shared; never Open Food Facts), a card asks "Is this the same food?" with "Yes, use it" and "No, it's different". Rejecting keeps the manual entry and adjusts the form so Save can't hit the "barcode taken" error.
- **"When scanning food"** is read as the manual form that opens on the Scan tab from "Photograph a label" (it has the same barcode field). The main "Scan a barcode" button and the typed-digits field on the Scan tab are unchanged.

## Global Constraints

- `Plate.Core` has no references to EF Core, ASP.NET Core or HttpClient. `LabelImage` is a plain record in Core.
- Every user-owned query is scoped to the current user. `/food/barcode/known` relies on the `FoodItems` query filter (own and shared foods only) and never reveals another user's private food.
- No new NuGet packages or front-end libraries. Reuse `wwwroot/lib/barcode-detector` and `window.gymImage.shrink`.
- No CDNs. Any changed JS file stays in `wwwroot/sw.js` `PRECACHE`; bump `CACHE` (currently `gym-static-v23`) to `gym-static-v24` once, in Task 2.
- Mobile-first at 390 px; every tap target at least 44 × 44 px (the scan button, the photo slots and their remove buttons, the card's buttons).
- Limits: at most 3 photos per read (`LabelScanPhoto.MaxPerScan = 3`), each at most 2 MB (`LabelScanPhoto.MaxBytes`, unchanged), `POST /food/label` body up to 7 MB, 20 label reads per user per 10 minutes (unchanged; a read counts once whatever its photo count). Phone resize unchanged: 2,000 px long side at JPEG 0.85 stepping down to under 2 MB.
- Model call unchanged: 20 s timeout per attempt, one retry on 5xx only, no retry on timeout.
- Retention unchanged: label photos 30 days, LabelScans 180 days, in `SoftDeletePurge`; data export never includes photos.
- Attribution text is exactly "Added by another user" ("Добавено от друг потребител"); never a name, email or id.
- Every user-facing string is in `Resources/strings.bg.json`, Bulgarian in the informal "ти". Regenerate keys with `GYM_WRITE_KEYS=1 dotnet test tests/Plate.Web.Tests --filter CatalogueTests`; `CatalogueTests` and `BulgarianCatalogueTests` stay green.
- Numbers render with a dot in both languages (SPEC 8).
- Test cadence: after each task run the new or affected E2E tests (filtered) plus all Core and Web tests (`dotnet test tests/Plate.Core.Tests && dotnet test tests/Plate.Web.Tests`). Run the full E2E suite at the end of each workstream (after Task 3 and Task 7) and before merging. A failing test run must stop the task; never pipe test output through `grep` without keeping the exit code.
- Each task is one commit (unless a step says otherwise), message ending with the session's attribution lines. Work on branch `scan-and-photos`; do not push.

## Review Focus

1. **The scanner sheet opened from the barcode field must not look the code up.** A found code (live camera or the sheet's "Take a photo instead") fills the field and closes the sheet; the URL stays `/food/add`. The Scan tab's main button must still navigate to `/food/barcode`. Pinned in Task 2 (`The_scan_button_fills_the_manual_forms_barcode_field`, and the existing `A_photo_of_a_barcode_is_read_and_looked_up` stays green).
2. **The share row without the label feature.** Today `toggleShare` lives in `label.js`, which only loads when a label model is set, so on an account without AI typing a barcode never shows "Share with other users" while the hidden, ticked checkbox still posts `share=true`. Moving the toggle to `scanner.js` fixes that; pinned in Task 2 (`The_share_row_follows_the_barcode_field_without_the_label_model`).
3. **Rejecting a known food must leave a form that saves.** Own food: barcode cleared. Shared food: share unticked, so the private index takes it. Pinned in Task 3 (Web: `Saving_privately_after_rejecting_a_shared_match_succeeds`; E2E: `Rejecting_a_shared_match_unticks_share_and_saves`).
4. **The one-photo path must stay one tap.** The slots are optional; without "Add more photos" a picked photo reads at once, exactly as today. Pinned by the existing `LabelPhotoTests` (unchanged) and Task 7 (`The_extra_slots_open_on_a_press_and_fit_the_phone` checks the slots start closed).
5. **A failed read, an unreadable photo, a double press.** A failed read keeps the slots filled so one photo can be swapped and read again; a photo the browser can't decode leaves its slot empty with a message; slots, remove buttons and Read are disabled while a read runs, so it can't be sent twice. Pinned in Task 7 (`A_failed_read_keeps_the_slots_filled`, `A_photo_the_browser_cannot_open_leaves_its_slot_empty`, `Slots_and_read_are_disabled_while_reading`).
6. **Three large photos through Kestrel.** The real server's 1 MB default must be raised for this endpoint only; the Web test host doesn't enforce Kestrel's limit. Pinned in Task 6 (`Three_photos_near_2_MB_each_get_through_Kestrels_request_limit`, E2E).
7. **Rolling the migration back with multi-photo scans present.** The old unique index on `LabelScanId` would fail; `Down` deletes photos with `Position > 0` first. Pinned in Task 5 (`Rolling_back_label_photo_slots_keeps_the_first_photo`).

---

### Task 1: Spec

**Files:**
- Modify: `SPEC.md` (sections 4, 6.6, 7, 9, 11, 12, 13)

**Interfaces:** none.

- [ ] **Step 1: Create the branch.** `git switch -c scan-and-photos`.

- [ ] **Step 2: Section 4, LabelScanPhoto.** Replace the `LabelScanPhoto` bullet (around line 175) with:

```markdown
- **LabelScanPhoto**: one to three photos per scan, in the order taken (`Position` 0 to 2, unique per scan; `ContentType`, `Width`, `Height`, `Bytes`), in their own table so lists never load images. Deleted 30 days after the scan; the scan row is kept 180 days, like a ParseRun.
```

- [ ] **Step 3: Section 6.6.** Make these edits:
  - In **Photo**, change "The server accepts JPEG, PNG or WebP by their first bytes, up to 2 MB; `POST /food/label` is the one endpoint whose request body may be up to 3 MB (section 9)." to "The server accepts one to three photos, each JPEG, PNG or WebP by its first bytes and up to 2 MB; `POST /food/label` is the one endpoint whose request body may be up to 7 MB (section 9)."
  - After **Photo**, add a paragraph:

```markdown
**More photos.** A label often splits what's needed across sides of the pack: the table on the back, the name and serving weight on the front. Under the photo button (and under "Photograph a label" on the Scan tab), "Add more photos" swaps the button for three slots, "Nutrition table" and two "Optional", with the line "Add the front or another side of the pack if the table doesn't show the name or serving." Picking into a slot (camera or library) shrinks the photo like a single one and shows its thumbnail with a remove button; nothing is sent. "Read the label" is enabled once a slot has a photo and sends every filled slot, in slot order, in one read; "Use one photo" closes the slots and drops their photos. Without the press, one photo reads at once as before. Slots and Read are disabled while a read runs; a failed read leaves the slots filled so a photo can be changed and read again; a successful one closes them. The barcode detector runs on the photos in order while the barcode field is empty. A read is one LabelScan with all of its photos and counts once toward the limit.
```

  - In **Model call**, change "The photo is an `image_url` content part with a base64 `data:` URL." to "Each photo is an `image_url` content part with a base64 `data:` URL, in the order taken."
  - In **Prompt rules**, append: "Up to three photos show the same package: take each value from whichever photo prints it clearly, and the earlier photo's when two print different numbers for the same row. `is_label` is false only when no photo shows a nutrition table. `name` and `brand` may come from any photo."

- [ ] **Step 4: Section 7, Manual food.** In the "Manual food" bullet, replace "Except after a barcode miss, where the scanned code is already known, an optional barcode field comes after the name." with:

```markdown
Except after a barcode miss, where the scanned code is already known, an optional barcode field comes after the name, with a small scan button beside it that opens the same scanner sheet and puts the code in the field instead of looking it up (the sheet's "Take a photo instead" too). When the field holds a code the app already has as a food (the user's own first, then a shared one; Open Food Facts is not asked), a card under it shows that food: "Already in the app", its name, kcal and protein per 100 g, "Added by another user" when that applies, and "Is this the same food?" with "Yes, use it" (the barcode result screen for that code) and "No, it's different". Rejecting keeps the form: for the user's own food the barcode is cleared ("You already have a food with this barcode, so this one is saved without it."); for a shared food the share choice is unticked ("Another food already shares this barcode, so this one is kept for you only."). The check runs when the field changes (a scan, or typing and leaving the field) and when a label photo found the code; offline it is skipped and the form works without it.
```

- [ ] **Step 5: Section 9.** Change "except label photos (`POST /food/label`, 3 MB; section 6.6)" to "except label photos (`POST /food/label`, 7 MB for up to three photos; section 6.6)".

- [ ] **Step 6: Section 11 CLI.** Change "`label-photo <id>` (raw bytes, while the photo is kept)" to "`label-photo <id> [n]` (the nth photo's raw bytes, 1 by default, while the photos are kept)".

- [ ] **Step 7: Section 12 Pages.** Change "a scan shows the photo (while kept)" to "a scan shows its photos (while kept)".

- [ ] **Step 8: Section 13.** Add after Phase 14:

```markdown
### Phase 15: Barcode field scan and more label photos
- The scan button beside the manual form's barcode field, the "already in the app" card and its reject rules (section 7).
- Up to three photos per label read: the optional slots behind "Add more photos", one read for all of them, `LabelScanPhoto.Position`, the 7 MB request limit, and the admin page and `label-photo <id> [n]` for each photo (sections 4, 6.6, 9, 11, 12).
- **Done when:** on an iPhone at 390 px, in English and Bulgarian, the scan button fills the barcode field on the Manual tab and in the form opened by "Photograph a label", with the camera and with "Take a photo instead", without leaving the page; a code the account already saved shows the card and "Yes, use it" opens its grams screen; rejecting a shared match saves a private food; an account without a label model sees the share row appear when a barcode is typed; the extra slots stay closed until "Add more photos" and a single photo still reads at once; a table photo and a front-of-pack photo read together fill the name and serving the table lacked; the admin page shows all three photos and `label-photo <id> 3` prints the third; and the twenty-first read in 10 minutes still returns 429.
```

- [ ] **Step 9: Commit.**

```bash
git add SPEC.md
git commit -m "Spec Phase 15: scan into the barcode field and up to three label photos"
```

---

### Task 2: Scan into the barcode field

**Files:**
- Create: `src/Plate.Web/Views/Food/_ScannerSheet.cshtml`
- Modify: `src/Plate.Web/Views/Food/Add.cshtml:249-279` (Overlays section)
- Modify: `src/Plate.Web/Views/Food/_ManualFoodForm.cshtml:47-50` (barcode row)
- Modify: `src/Plate.Web/wwwroot/js/scanner.js` (whole file)
- Modify: `src/Plate.Web/wwwroot/js/label.js` (remove `toggleShare` and the `input` listener)
- Modify: `src/Plate.Web/wwwroot/css/app.css` (after line 180)
- Modify: `src/Plate.Web/wwwroot/sw.js:4` (cache version)
- Modify: `src/Plate.Web/Resources/strings.bg.json`, `strings.keys.json` (regenerated)
- Create: `tests/Plate.E2E.Tests/BarcodeFieldTests.cs`
- Test: `tests/Plate.Web.Tests/LabelScanTests.cs` (render checks)

**Interfaces:**
- Consumes: `window.gymSheet.show/hide`, `window.gymModule`, `UntestedAreasTests.Ean13Png(string)` (internal static, E2E).
- Produces: markup contract used by Task 3 and Task 7: the barcode input keeps `data-barcode-input`; the scan button is `button[data-scan-into]` inside the same `.field-row`; `scanner.js` dispatches `input` and `change` (both bubbling) on the field after a scan, and listens on `document` for `input` on `[data-barcode-input]` to toggle `[data-share]` rows. `scanner.js` also exposes nothing global.

- [ ] **Step 1: Write the failing Web render tests.** Add to `LabelScanTests.cs` (it already has `AddPage` and `NewApp`):

```csharp
[Fact]
public async Task The_manual_tab_has_a_scan_button_beside_the_barcode_field_and_the_scanner_sheet()
{
    var (app, _) = NewApp();
    await using var _app = app;
    var a = await SignInAsync(app);

    var html = await a.Client.GetStringAsync(AddPage);

    html.Should().MatchRegex("data-barcode-input[^>]*/>\\s*<button type=\"button\" class=\"icon-btn\" data-scan-into");
    html.Should().Contain("id=\"scanner\"").And.Contain("/js/scanner.js").And.Contain("barcode-detector-3.2.2.ponyfill.js");
}

[Fact]
public async Task The_form_after_a_barcode_miss_has_no_scan_button()
{
    var (app, _) = NewApp();
    await using var _app = app;
    var a = await SignInAsync(app);

    var html = await a.Client.GetStringAsync("/food/barcode?date=2026-10-05&meal=Lunch&code=4006381333931");

    html.Should().NotContain("data-scan-into");
}
```

- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Plate.Web.Tests --filter "FullyQualifiedName~LabelScanTests.The_manual_tab_has_a_scan_button|FullyQualifiedName~LabelScanTests.The_form_after_a_barcode_miss"`. Expected: the first FAILS (no `data-scan-into`, no `id="scanner"` on the manual tab); the second passes already and guards the miss page.

- [ ] **Step 3: Move the sheet into a partial.** Create `Views/Food/_ScannerSheet.cshtml` with the markup now at `Add.cshtml:251-266`, unchanged:

```cshtml
@* The camera sheet (SPEC 7). scanner.js opens it from "Scan a barcode" (looks the code up) or from the scan button beside a barcode field (fills the field). *@
<div class="sheet" id="scanner" data-module="scanner" hidden>
    <div class="sheet-scrim"></div>
    <div class="sheet-panel full scanner-panel" role="dialog" aria-modal="true" aria-labelledby="scanner-title">
        <div class="sheet-grabber" aria-hidden="true" style="background: rgb(255 255 255 / 0.4)"></div>
        <div class="sheet-head"><span></span><span class="sheet-title" id="scanner-title">@L["Scan a barcode"]</span><button type="button" class="sheet-close" data-sheet-close>@L["Cancel"]</button></div>
        <div class="viewfinder">
            <video playsinline muted hidden></video>
            <div class="viewfinder-frame" aria-hidden="true"></div>
            <p class="viewfinder-status" data-status role="status">@L["Point the camera at the barcode."]</p>
        </div>
        <div class="scanner-actions">
            <label class="btn btn-secondary">@Ui.Icon("photo")@L["Take a photo instead"]
                <input type="file" accept="image/*" capture="environment" data-photo class="sr-only" />
            </label>
        </div>
    </div>
</div>
```

In `Add.cshtml`, replace the whole `@if (Model.Tab == "barcode") { ... }` block and the `@if (Model.LabelAvailable) { ... }` block in `@section Overlays` with:

```cshtml
    @if (Model.Tab is "barcode" or "manual")
    {
        <partial name="_ScannerSheet" />
        @* Also reads a barcode out of a label photo (label.js), so it loads before label.js. *@
        <script src="~/lib/barcode-detector/barcode-detector-3.2.2.ponyfill.js" asp-append-version="true"></script>
        <script src="~/js/scanner.js" asp-append-version="true"></script>
    }
    @if (Model.LabelAvailable)
    {
        <script src="~/js/label.js" asp-append-version="true"></script>
    }
```

- [ ] **Step 4: Add the button.** In `_ManualFoodForm.cshtml`, replace the barcode `field-row` with:

```cshtml
<div class="field-row"><label for="m-barcode">@L["Barcode"]</label><input id="m-barcode" class="num" type="text" name="barcode" value="@Model.BarcodeValue" inputmode="numeric" pattern="[0-9 -]{8,16}" autocomplete="off" placeholder="@L["Optional"]" data-barcode-input /><button type="button" class="icon-btn" data-scan-into aria-label="@L["Scan the barcode"]">@Ui.Icon("barcode")</button></div>
```

In `app.css`, after the `.field-row.stacked input` rule (line 180), add:

```css
/* The scan button beside a barcode field: 44 px to tap, reaching into the row's padding so the row keeps its height. */
.field-row .icon-btn { margin: -6px calc(-1 * var(--s-2)) -6px calc(-1 * var(--s-1)); }
```

- [ ] **Step 5: Rewrite `scanner.js`.** Replace the file with:

```js
// Barcode scanning on the Food add page (SPEC 7). Safari ships BarcodeDetector disabled, so this uses the
// barcode-detector ponyfill (ZXing-C++ in WebAssembly), served from this app rather than a CDN.
// The camera opens in a full-screen sheet without changing the URL, since a route change can stop the
// stream or re-prompt on iOS. Closing the sheet stops the camera. "Scan a barcode" looks the code up; the scan
// button beside a barcode field puts the code in that field instead. This module also runs the barcode field itself:
// the share row shows only once there is a barcode (SPEC 4).
window.gymModule("scanner", (root) => {
    "use strict";

    // Others find a shared food by its barcode, so the share choice shows only once there is one (SPEC 4).
    function toggleShare(form) {
        const input = form && form.querySelector("[data-barcode-input]");
        if (!input) {
            return;
        }
        const show = input.value.replace(/\D/g, "").length >= 8;
        form.querySelectorAll("[data-share]").forEach((el) => { el.hidden = !show; });
    }

    const onInput = (event) => {
        if (event.target.matches("[data-barcode-input]")) {
            toggleShare(event.target.closest("form"));
        }
    };
    document.addEventListener("input", onInput);

    const api = window.BarcodeDetectionAPI;
    if (!api) {
        return () => document.removeEventListener("input", onInput);
    }

    api.prepareZXingModule({
        overrides: {
            locateFile: (path, prefix) => path.endsWith(".wasm") ? "/lib/barcode-detector/zxing_reader.wasm" : prefix + path,
        },
    });

    const detector = new api.BarcodeDetector({ formats: ["ean_13", "ean_8", "upc_a", "upc_e"] });
    // After a soft navigation (nav.js) the sheet's markup was parsed by DOMParser, and iOS keeps a <video> made
    // there black even while the camera runs. A video created in this document plays, however the page loaded.
    const video = document.createElement("video");
    video.muted = true;
    video.playsInline = true;
    video.hidden = true;
    root.querySelector("video").replaceWith(video);
    const status = root.querySelector("[data-status]");
    let stream = null;
    let timer = null;
    // The field a found code goes into; null means look it up (the Scan tab's main button).
    let target = null;

    function say(text) {
        status.textContent = text;
    }

    function stop() {
        clearInterval(timer);
        timer = null;
        if (stream) {
            stream.getTracks().forEach((t) => t.stop());
            stream = null;
        }
        video.hidden = true;
    }

    function found(code) {
        stop();
        const field = target;
        if (field) {
            target = null;
            field.value = code;
            field.dispatchEvent(new Event("input", { bubbles: true }));
            field.dispatchEvent(new Event("change", { bubbles: true }));
            window.gymSheet.hide();
            return;
        }
        const form = document.getElementById("barcode-form");
        say(t("Found {0}. Looking it up…", code));
        form.querySelector('input[name="code"]').value = code;
        form.requestSubmit();
    }

    async function open(field) {
        target = field;
        window.gymSheet.show(root);
        say(t("Point the camera at the barcode."));
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            say(t("This browser can't open the camera. Type the digits or take a photo instead."));
            return;
        }
        try {
            stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: "environment" } }, audio: false });
        } catch {
            say(t("Camera access was refused. Type the digits or take a photo instead."));
            return;
        }

        video.srcObject = stream;
        video.hidden = false;
        await video.play().catch(() => {});

        let busy = false;
        timer = setInterval(async () => {
            if (busy || video.readyState < 2) {
                return;
            }
            busy = true;
            try {
                const codes = await detector.detect(video);
                if (codes.length) {
                    found(codes[0].rawValue);
                }
            } catch {
                // Frame not ready yet; try the next one.
            } finally {
                busy = false;
            }
        }, 250);
    }

    // Delegated: the manual form is replaced after a label photo (label.js), and its scan button with it.
    const onClick = (event) => {
        const button = event.target.closest("[data-start], [data-scan-into]");
        if (!button) {
            return;
        }
        open(button.matches("[data-scan-into]") ? button.closest(".field-row").querySelector("[data-barcode-input]") : null);
    };
    document.addEventListener("click", onClick);

    root.addEventListener("sheet:close", () => {
        stop();
        target = null;
    });

    root.querySelector("[data-photo]").addEventListener("change", async (event) => {
        const file = event.target.files && event.target.files[0];
        event.target.value = "";
        if (!file) {
            return;
        }
        stop();
        say(t("Reading the photo…"));
        try {
            const codes = await detector.detect(await createImageBitmap(file));
            if (codes.length) {
                found(codes[0].rawValue);
            } else {
                say(t("No barcode found in that photo. Try again closer, or type the digits."));
            }
        } catch {
            say(t("Couldn't read that photo. Type the digits instead."));
        }
    });

    window.addEventListener("pagehide", stop);
    return () => {
        stop();
        document.removeEventListener("input", onInput);
        document.removeEventListener("click", onClick);
        window.removeEventListener("pagehide", stop);
    };
});
```

Note the one behaviour change on the photo input: `event.target.value = ""` so picking the same photo twice fires `change` again. The `found()` field branch dispatches `change` so Task 3's known check runs.

- [ ] **Step 6: Remove the duplicate from `label.js`.** Delete `toggleShare`, the `onInput` handler, its `root.addEventListener("input", onInput)` and `root.removeEventListener("input", onInput)`, and the `toggleShare(placed.querySelector("form"));` call after the swap. The server already renders `[data-share]` hidden or shown from the barcode it knows (`hasBarcode` in `_ManualFoodForm.cshtml`), so the swapped form is right without it. On the barcode miss page (`Barcode.cshtml`) there is no barcode input, so nothing is lost there.

- [ ] **Step 7: Strings.** Add to `strings.bg.json`: `"Scan the barcode": "Сканирай баркода"`. Regenerate keys: `GYM_WRITE_KEYS=1 dotnet test tests/Plate.Web.Tests --filter CatalogueTests`.

- [ ] **Step 8: Bump the service worker cache.** In `wwwroot/sw.js` change `const CACHE = "gym-static-v23";` to `const CACHE = "gym-static-v24";`. Do not bump again in later tasks; the branch ships as one deploy.

- [ ] **Step 9: Write the E2E tests.** Create `tests/Plate.E2E.Tests/BarcodeFieldTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Playwright;

namespace Plate.E2E.Tests;

/// <summary>The scan button beside the manual form's barcode field (SPEC 7, Phase 15).</summary>
[Collection(E2ECollection.Name)]
public class BarcodeFieldTests(E2EFixture app)
{
    private static string Today => Plate.Core.Time.UserClock.Today(DateTimeOffset.UtcNow, Plate.Core.Domain.UserProfile.DefaultTimeZoneId).ToString("yyyy-MM-dd");

    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", name));

    /// <summary>A random valid EAN-13, so runs never collide on shared foods.</summary>
    internal static string NewEan()
    {
        var digits = Enumerable.Range(0, 12).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)).ToArray();
        var sum = digits.Select((d, i) => i % 2 == 0 ? d : d * 3).Sum();
        return string.Concat(digits) + (10 - sum % 10) % 10;
    }

    /// <summary>Taps the scan button and answers the sheet with a photo of the code, as the "Take a photo instead" fallback does.</summary>
    internal static async Task ScanIntoFieldAsync(Phone phone, string code, string scope = "")
    {
        await phone.Page.Locator($"{scope} [data-scan-into]".Trim()).First.TapAsync();
        await phone.Page.Locator("#scanner:not([hidden])").WaitForAsync();
        await phone.Page.Locator("#scanner [data-photo]").SetInputFilesAsync(new FilePayload
        {
            Name = "barcode.png", MimeType = "image/png", Buffer = UntestedAreasTests.Ean13Png(code),
        });
        await phone.Page.WaitForFunctionAsync("code => document.querySelector('[data-barcode-input]')?.value === code", code);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_scan_button_fills_the_manual_forms_barcode_field(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        var button = phone.Page.Locator("[data-scan-into]");
        var box = await Phone.BoxAsync(button);
        box.Width.Should().BeGreaterThanOrEqualTo(44);
        box.Height.Should().BeGreaterThanOrEqualTo(44);
        (box.X + box.Width).Should().BeLessThanOrEqualTo(390);
        var code = NewEan();

        await ScanIntoFieldAsync(phone, code);

        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food/add");
        await phone.Page.Locator("#scanner").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        (await phone.Page.Locator("form [data-share]").First.IsVisibleAsync()).Should().BeTrue();
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_scan_button_works_in_the_form_from_photograph_a_label(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=barcode");
        await phone.Page.Locator("[data-label-start]").SetInputFilesAsync(Fixture("label.jpg"));
        await phone.Page.Locator("[data-label-slot] .banner").WaitForAsync();
        var code = NewEan();

        await ScanIntoFieldAsync(phone, code, "[data-label-slot]");

        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food/add");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task The_share_row_follows_the_barcode_field_without_the_label_model(string browser)
    {
        // label.js doesn't load without a label model; the share row must still appear (Review Focus 2).
        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        (await phone.Page.Locator("form [data-share]").First.IsVisibleAsync()).Should().BeFalse();

        await phone.Page.FillAsync("#m-barcode", NewEan());

        (await phone.Page.Locator("form [data-share]").First.IsVisibleAsync()).Should().BeTrue();
    }
}
```

- [ ] **Step 10: Run the tests.**

```bash
dotnet build
dotnet test tests/Plate.Web.Tests --filter "FullyQualifiedName~LabelScanTests"
dotnet test tests/Plate.E2E.Tests --filter "FullyQualifiedName~BarcodeFieldTests|FullyQualifiedName~UntestedAreasTests.A_photo_of_a_barcode|FullyQualifiedName~LabelPhotoTests"
dotnet test tests/Plate.Core.Tests && dotnet test tests/Plate.Web.Tests
```

Expected: all PASS. `LabelPhotoTests.The_share_choice_follows_the_barcode_field` must stay green now that `scanner.js` owns the toggle.

- [ ] **Step 11: Commit.**

```bash
git add -A src/Plate.Web tests
git commit -m "Scan a barcode into the manual form's barcode field

The scanner sheet is now on the Manual tab too, and a code found from the
field's scan button fills the field instead of being looked up. The share
row toggle moves from label.js to scanner.js so it also works on accounts
without a label model."
```

---

### Task 3: Offer the food the app already has for a scanned or typed barcode

**Files:**
- Modify: `src/Plate.Web/Features/Food/FoodItemService.cs:112-132` (extract `FindByBarcodeAsync`)
- Modify: `src/Plate.Web/Controllers/FoodController.cs` (new action after `Barcode`)
- Modify: `src/Plate.Web/ViewModels/ViewModels.cs` (new `KnownBarcodeModel`)
- Create: `src/Plate.Web/Views/Food/_KnownBarcode.cshtml`
- Modify: `src/Plate.Web/Views/Food/_ManualFoodForm.cshtml` (a `data-known` slot after the name/barcode list)
- Modify: `src/Plate.Web/wwwroot/js/scanner.js` (check and reject)
- Modify: `src/Plate.Web/wwwroot/js/label.js` (announce a barcode found in the photo)
- Modify: `src/Plate.Web/wwwroot/css/app.css`
- Modify: `src/Plate.Web/Resources/strings.bg.json`, `strings.keys.json`
- Test: `tests/Plate.Web.Tests/SharedFoodTests.cs`, `tests/Plate.E2E.Tests/BarcodeFieldTests.cs`

**Interfaces:**
- Consumes: Task 2's `data-barcode-input`, the `change` event `scanner.js` dispatches after a scan; `FoodItemService.AddedByAnother(FoodItem, string?)` (existing static).
- Produces:
  - `Task<FoodItem?> FoodItemService.FindByBarcodeAsync(string barcode, CancellationToken ct)`: own food first, then shared; never calls Open Food Facts.
  - `GET /food/barcode/known?date=yyyy-MM-dd&meal=Lunch&code=<digits>`: `204 No Content` when the code is invalid or unknown; otherwise `200` with `_KnownBarcode`, whose root is `[data-known-food]` with `data-own="true|false"` and `data-reject-note="<localized note>"`, a link to `/food/barcode?...`, and `button[data-known-reject]`.
  - `label.js` dispatches `new CustomEvent("label:barcode", { bubbles: true })` on the placed `[data-label-form]` when the barcode in the new form came from the photo.

- [ ] **Step 1: Write the failing Web tests.** Add to `SharedFoodTests.cs` (it has `NewApp`, `SignInAsync`, `SaveManualAsync`, `PageAsync`, `NewEan`, `Unique`, `Mark`):

```csharp
private static string Known(string code) => $"/food/barcode/known?date=2026-10-05&meal=Lunch&code={code}";

[Fact]
public async Task A_known_barcode_offers_the_users_own_food()
{
    await using var app = NewApp();
    var a = await SignInAsync(app);
    var code = NewEan();
    var name = Unique("Own known qsf");
    await SaveManualAsync(a, name, code, share: false);

    var response = await a.Client.GetAsync(Known(code));

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    html.Should().Contain(name).And.Contain("Is this the same food?").And.Contain("data-own=\"true\"").And.NotContain(Mark);
    html.Should().Contain($"/food/barcode?date=2026-10-05&meal=Lunch&code={code}");
    html.Should().Contain("You already have a food with this barcode, so this one is saved without it.");
}

[Fact]
public async Task A_known_barcode_shared_by_another_account_carries_the_mark_and_no_name()
{
    await using var app = NewApp();
    var a = await SignInAsync(app, "Zelda Quixote");
    var b = await SignInAsync(app);
    var code = NewEan();
    var name = Unique("Shared known qsf");
    await SaveManualAsync(a, name, code, share: true);

    var html = WebUtility.HtmlDecode(await (await b.Client.GetAsync(Known(code))).Content.ReadAsStringAsync());

    html.Should().Contain(name).And.Contain(Mark).And.Contain("data-own=\"false\"").And.NotContain("Zelda");
    html.Should().Contain("Another food already shares this barcode, so this one is kept for you only.");
}

[Fact]
public async Task Another_accounts_private_food_is_not_offered()
{
    await using var app = NewApp();
    var a = await SignInAsync(app);
    var b = await SignInAsync(app);
    var code = NewEan();
    await SaveManualAsync(a, Unique("Private known qsf"), code, share: false);

    (await b.Client.GetAsync(Known(code))).StatusCode.Should().Be(HttpStatusCode.NoContent);
}

[Fact]
public async Task An_unknown_or_invalid_barcode_is_no_content_and_open_food_facts_is_not_asked()
{
    var off = new FakeFoodDatabase();
    await using var app = new AppFactory(fixture, services: s => s.AddSingleton<IFoodDatabase>(off));
    var a = await SignInAsync(app);

    (await a.Client.GetAsync(Known(NewEan()))).StatusCode.Should().Be(HttpStatusCode.NoContent);
    (await a.Client.GetAsync(Known("123"))).StatusCode.Should().Be(HttpStatusCode.NoContent);
    off.Lookups.Should().Be(0);
}

[Fact]
public async Task Saving_privately_after_rejecting_a_shared_match_succeeds()
{
    // What the reject button leaves: the same barcode with share unticked (Review Focus 3).
    await using var app = NewApp();
    var a = await SignInAsync(app);
    var b = await SignInAsync(app);
    var code = NewEan();
    await SaveManualAsync(a, Unique("Theirs reject qsf"), code, share: true);

    (await SaveManualAsync(b, Unique("Mine reject qsf"), code, share: false)).StatusCode.Should().Be(HttpStatusCode.Redirect);
}
```

The last test passes already (the private index allows it); it pins the rule the client relies on.

- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Plate.Web.Tests --filter "FullyQualifiedName~SharedFoodTests"`. Expected: the four `Known` tests FAIL with 404 (no route).

- [ ] **Step 3: Extract the local lookup.** In `FoodItemService.cs`, add above `LookupBarcodeAsync` and use it there:

```csharp
/// <summary>The user's own food with this barcode, else a shared one. Never asks Open Food Facts.</summary>
public async Task<FoodItem?> FindByBarcodeAsync(string barcode, CancellationToken ct) =>
    // The query filter hides other users' private foods (SPEC 4).
    await db.FoodItems.Where(f => f.Barcode == barcode).OrderBy(f => f.OwnerUserId == null).FirstOrDefaultAsync(ct);
```

and in `LookupBarcodeAsync` replace the `var cached = await db.FoodItems...FirstOrDefaultAsync(ct);` statement (and its comment) with `var cached = await FindByBarcodeAsync(barcode, ct);`.

- [ ] **Step 4: The model and the action.** In `ViewModels.cs`, after `ManualFoodFormModel`:

```csharp
/// <summary>A food the app already has for a barcode in the manual form, offered instead of a new one (SPEC 7).</summary>
/// <param name="Own">The user's own private food; rejecting it clears the barcode, since one account can't have two foods with it.</param>
public record KnownBarcodeModel(DateOnly Date, Meal Meal, string Code, FoodItem Item, bool Own, bool AddedByAnother);
```

In `FoodController.cs`, after the `Barcode` action:

```csharp
/// <summary>The food the app already has for a barcode typed or scanned into the manual form (SPEC 7). Local only: no Open Food Facts call.</summary>
[HttpGet("barcode/known")]
public async Task<IActionResult> KnownBarcode(DateOnly date, Meal meal, string? code, CancellationToken ct)
{
    var digits = Barcodes.Normalize(code);
    if (!Barcodes.IsValid(digits) || await foods.FindByBarcodeAsync(digits, ct) is not { } item)
    {
        return NoContent();
    }

    return PartialView("_KnownBarcode", new KnownBarcodeModel(date, meal, digits, item,
        Own: item.OwnerUserId is not null && item.OwnerUserId == currentUser.UserId,
        AddedByAnother: FoodItemService.AddedByAnother(item, currentUser.UserId)));
}
```

`ViewModels.cs` already imports `Plate.Core.Domain`.

- [ ] **Step 5: The partial.** Create `Views/Food/_KnownBarcode.cshtml`:

```cshtml
@model KnownBarcodeModel
@using Plate.Core.Nutrition
@{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var item = Model.Item;
    var date = Model.Date.ToString("yyyy-MM-dd", inv);
    string note = Model.Own
        ? L["You already have a food with this barcode, so this one is saved without it."]
        : L["Another food already shares this barcode, so this one is kept for you only."];
}
@* Offered when the manual form's barcode is already a food (SPEC 7). Rejecting is handled in scanner.js. *@
<div class="card known-food" role="status" data-known-food data-own="@(Model.Own ? "true" : "false")" data-reject-note="@note">
    <p class="footnote">@L["Already in the app"]</p>
    <p class="headline">@item.DisplayName()</p>
    @if (Model.AddedByAnother)
    {
        <p class="footnote">@L["Added by another user"]</p>
    }
    <p class="footnote num">@L["{0} kcal · {1} g protein", Math.Round(item.KcalPer100g), item.ProteinPer100g.ToString("0.#", inv)] · @L["Per 100 g"]</p>
    <p style="margin-top: var(--s-3)">@L["Is this the same food?"]</p>
    <div class="known-actions">
        <a class="btn btn-tinted" href="/food/barcode?date=@date&amp;meal=@Model.Meal&amp;code=@Model.Code">@L["Yes, use it"]</a>
        <button type="button" class="btn btn-plain" data-known-reject>@L["No, it's different"]</button>
    </div>
</div>
```

In `_ManualFoodForm.cshtml`, directly after the closing `</div>` of the first `<div class="list">` (the one with name and barcode), add:

```cshtml
@if (Model.BarcodeField)
{
    <div data-known hidden></div>
}
```

CSS, after the rule added in Task 2:

```css
.known-food { margin-top: var(--s-3); }
.known-actions { display: flex; gap: var(--s-2); margin-top: var(--s-3); }
.known-actions .btn { flex: 1; min-height: 44px; }
```

- [ ] **Step 6: Strings.** Add to `strings.bg.json`:

```json
"Already in the app": "Вече е в приложението",
"Is this the same food?": "Същата храна ли е?",
"Yes, use it": "Да, използвай я",
"No, it's different": "Не, друга е",
"You already have a food with this barcode, so this one is saved without it.": "Вече имаш храна с този баркод, затова тази ще се запази без него.",
"Another food already shares this barcode, so this one is kept for you only.": "С този баркод вече има споделена храна, затова тази остава само за теб."
```

Regenerate keys as in Task 2 Step 7.

- [ ] **Step 7: Run the Web tests.** `dotnet test tests/Plate.Web.Tests --filter "FullyQualifiedName~SharedFoodTests"`. Expected: PASS.

- [ ] **Step 8: The client check and reject.** In `scanner.js`, after `document.addEventListener("input", onInput);` (before the `api` check, so it works even without the detector), add:

```js
    // The food the app already has for this barcode, if any (SPEC 7). Only the latest check's answer is shown.
    let known = null;

    async function checkKnown(input) {
        const form = input.closest("form");
        const slot = form && form.querySelector("[data-known]");
        if (!slot) {
            return;
        }
        known?.abort();
        known = null;
        slot.replaceChildren();
        slot.hidden = true;
        const digits = input.value.replace(/\D/g, "");
        if (digits.length < 8 || !navigator.onLine) {
            return;
        }
        const controller = new AbortController();
        known = controller;
        try {
            const query = new URLSearchParams({ date: form.elements.date.value, meal: form.elements.meal.value, code: digits });
            const response = await fetch(`/food/barcode/known?${query}`, { headers: { "HX-Request": "true" }, signal: controller.signal });
            if (response.status !== 200) {
                return;
            }
            const card = new DOMParser().parseFromString(await response.text(), "text/html").querySelector("[data-known-food]");
            if (card && known === controller) {
                slot.replaceChildren(document.importNode(card, true));
                slot.hidden = false;
            }
        } catch {
            // Offline or replaced by a newer check: the form works without it.
        }
    }

    // "No, it's different": keep the manual entry and make sure it can be saved (SPEC 7).
    function reject(button) {
        const card = button.closest("[data-known-food]");
        const form = card.closest("form");
        if (card.dataset.own === "true") {
            const input = form.querySelector("[data-barcode-input]");
            input.value = "";
            input.dispatchEvent(new Event("input", { bubbles: true }));
        } else {
            const share = form.querySelector("input[type=checkbox][name=share]");
            if (share) {
                share.checked = false;
            }
        }
        const note = document.createElement("p");
        note.className = "section-footer";
        note.textContent = card.dataset.rejectNote;
        card.replaceWith(note);
    }

    const onChange = (event) => {
        if (event.target.matches("[data-barcode-input]")) {
            checkKnown(event.target);
        }
    };
    const onRejectClick = (event) => {
        const button = event.target.closest("[data-known-reject]");
        if (button) {
            reject(button);
        }
    };
    const onLabelBarcode = (event) => {
        const input = event.target.querySelector("[data-barcode-input]");
        if (input && input.value) {
            checkKnown(input);
        }
    };
    document.addEventListener("change", onChange);
    document.addEventListener("click", onRejectClick);
    document.addEventListener("label:barcode", onLabelBarcode);
    const offField = () => {
        known?.abort();
        document.removeEventListener("input", onInput);
        document.removeEventListener("change", onChange);
        document.removeEventListener("click", onRejectClick);
        document.removeEventListener("label:barcode", onLabelBarcode);
    };
```

Then change the early return to `return offField;` and, in the final cleanup function, replace `document.removeEventListener("input", onInput);` with `offField();`.

- [ ] **Step 9: Announce a barcode read from a label photo.** In `label.js` `read()`, `knownBarcode` is the field's value before the read. After `const placed = document.importNode(fresh, true);` and the `slot`/`host` placement, add:

```js
            // A barcode the detector found in the photo may already be a food (SPEC 7); scanner.js offers it.
            if (!knownBarcode && placed.querySelector("[data-barcode-input]")?.value) {
                placed.dispatchEvent(new CustomEvent("label:barcode", { bubbles: true }));
            }
```

- [ ] **Step 10: Write the E2E tests.** Add to `BarcodeFieldTests.cs`:

```csharp
    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_scanned_code_the_account_already_saved_is_offered_and_yes_opens_it(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var code = NewEan();
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        await SaveManualAsync(phone, "Known kefir e2e", code, share: false);

        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        await ScanIntoFieldAsync(phone, code);
        var card = phone.Page.Locator("[data-known-food]");
        await card.WaitForAsync();
        (await card.InnerTextAsync()).Should().Contain("Known kefir e2e").And.Contain("Is this the same food?");

        await phone.NavigateByAsync(() => card.Locator("a", new() { HasText = "Yes, use it" }).TapAsync());
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food/barcode");
        (await phone.Page.Locator("main").InnerTextAsync()).Should().Contain("Known kefir e2e");
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Rejecting_a_shared_match_unticks_share_and_saves(string browser)
    {
        var code = NewEan();
        await using (var other = await app.SignedInPhoneAsync(browser))
        {
            await other.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
            await SaveManualAsync(other, "Shared boza e2e", code, share: true);
        }

        await using var phone = await app.SignedInPhoneAsync(browser);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        await phone.Page.FillAsync("#m-name", "My boza e2e");
        await ScanIntoFieldAsync(phone, code);
        await phone.Page.Locator("[data-known-food]").WaitForAsync();
        (await phone.Page.Locator("[data-known-food]").InnerTextAsync()).Should().Contain("Added by another user");

        await phone.Page.Locator("[data-known-reject]").TapAsync();

        (await phone.Page.Locator("form input.switch[name=share]").IsCheckedAsync()).Should().BeFalse();
        (await phone.Page.InputValueAsync("#m-barcode")).Should().Be(code);
        (await phone.Page.Locator("[data-known]").InnerTextAsync()).Should().Contain("kept for you only");
        await FillNumbersAsync(phone);
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/items'] button[type=submit]").TapAsync());
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task Rejecting_your_own_match_clears_the_barcode(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser);
        var code = NewEan();
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        await SaveManualAsync(phone, "Own ayran e2e", code, share: false);

        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=manual");
        await ScanIntoFieldAsync(phone, code);
        await phone.Page.Locator("[data-known-reject]").TapAsync();

        (await phone.Page.InputValueAsync("#m-barcode")).Should().BeEmpty();
        (await phone.Page.Locator("form [data-share]").First.IsVisibleAsync()).Should().BeFalse();
    }

    private static async Task FillNumbersAsync(Phone phone)
    {
        await phone.Page.FillAsync("#m-kcal", "60");
        await phone.Page.FillAsync("#m-protein", "3");
        await phone.Page.FillAsync("#m-carbs", "4");
        await phone.Page.FillAsync("#m-fat", "3");
        await phone.Page.FillAsync("#m-grams", "200");
    }

    /// <summary>Saves a manual food with a barcode through the form, as a user would.</summary>
    private static async Task SaveManualAsync(Phone phone, string name, string code, bool share)
    {
        await phone.Page.FillAsync("#m-name", name);
        await phone.Page.FillAsync("#m-barcode", code);
        await FillNumbersAsync(phone);
        await phone.Page.Locator("form input.switch[name=share]").SetCheckedAsync(share);
        await phone.NavigateByAsync(() => phone.Page.Locator("form[action='/food/items'] button[type=submit]").TapAsync());
    }
```

Filling `#m-barcode` with `FillAsync` and then tapping Save blurs the field, which fires `change` and a known check; that's harmless before a save.

- [ ] **Step 11: Run the tests, then the full E2E suite (end of workstream A).**

```bash
dotnet build
dotnet test tests/Plate.E2E.Tests --filter "FullyQualifiedName~BarcodeFieldTests|FullyQualifiedName~LabelPhotoTests"
dotnet test tests/Plate.Core.Tests && dotnet test tests/Plate.Web.Tests
dotnet test tests/Plate.E2E.Tests
```

Expected: all PASS. If `NavigationTests.Quick_add_shows_the_meal_it_went_into` (WebKit) flakes, rerun it alone three times before treating it as a failure (known flake).

- [ ] **Step 12: Commit.**

```bash
git add -A src/Plate.Web tests
git commit -m "Offer the food the app already has for a barcode in the manual form

A scanned or typed barcode that is already the user's own food or a shared
one shows a card asking if it is the same food. Yes opens its grams
screen; No keeps the manual entry and clears the barcode (own food) or
unticks sharing (shared food) so the save can't collide. Local lookup only,
no Open Food Facts call."
```

---

### Task 4: The label reader takes up to three images

**Files:**
- Modify: `src/Plate.Core/Nutrition/LabelReading.cs:33-38`
- Modify: `src/Plate.Web/Integrations/OpenRouterLabelReader.cs` (instructions, `TestAsync`, `ReadAsync`, `BodyAsync`, `SwitchingLabelReader.ReadAsync`)
- Modify: `src/Plate.Web/Features/Food/LabelScanService.cs:30` (call site only)
- Modify: `tests/Plate.Web.Tests/FakeLabelReader.cs`, `tests/Plate.E2E.Tests/AiStubs.cs:52-72`
- Test: `tests/Plate.Web.Tests/OpenRouterTests.cs` (`OpenRouterLabelReaderTests`)

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `public record LabelImage(byte[] Bytes, string ContentType);` in `Plate.Core.Nutrition`.
  - `ILabelReader.ReadAsync(IReadOnlyList<LabelImage> images, CancellationToken ct)` (replaces the `byte[]`/`string` overload).
  - `OpenRouterLabelReader.ReadAsync(IReadOnlyList<LabelImage> images, string apiKey, string model, CancellationToken ct)`.
  - `FakeLabelReader.LastImages` (`IReadOnlyList<LabelImage>?`), used by Task 6's tests.
  - E2E `StubLabelReader`: one image reads as before ("Айрян", no brand, no serving); two or more read as `"Айрян Верея"`, brand `"Верея"`, `ServingGrams` 250. Task 7's E2E tests rely on this.

- [ ] **Step 1: Write the failing reader tests.** In `OpenRouterLabelReaderTests`, change `Sends_the_photo_as_a_data_url_with_a_strict_schema` to call `ReadAsync([new LabelImage(Jpeg, "image/jpeg")], "sk-test", ...)` and add:

```csharp
private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

[Fact]
public async Task One_photo_asks_to_read_this_nutrition_label()
{
    var stub = new StubOpenRouter(completion: StubOpenRouter.Completion(Ayran));

    await stub.Label().ReadAsync([new LabelImage(Jpeg, "image/jpeg")], "k", "google/gemini-3.5-flash-lite", default);

    var content = stub.SentBody!["messages"]![1]!["content"]!.AsArray();
    content.Count.Should().Be(2);
    content[0]!["text"]!.GetValue<string>().Should().Be("Read this nutrition label.");
}

[Fact]
public async Task Several_photos_go_in_order_as_image_parts_of_one_message()
{
    var stub = new StubOpenRouter(completion: StubOpenRouter.Completion(Ayran));

    await stub.Label().ReadAsync([new LabelImage(Jpeg, "image/jpeg"), new LabelImage(Png, "image/png"), new LabelImage(Jpeg, "image/jpeg")],
        "k", "google/gemini-3.5-flash-lite", default);

    var content = stub.SentBody!["messages"]![1]!["content"]!.AsArray();
    content.Count.Should().Be(4);
    content[0]!["text"]!.GetValue<string>().Should().Be("These 3 photos show the same package. Read its nutrition label, using every photo.");
    content[1]!["image_url"]!["url"]!.GetValue<string>().Should().StartWith("data:image/jpeg;base64,");
    content[2]!["image_url"]!["url"]!.GetValue<string>().Should().StartWith("data:image/png;base64,");
    content[3]!["image_url"]!["url"]!.GetValue<string>().Should().StartWith("data:image/jpeg;base64,");
    stub.SentBody!["messages"]![0]!["content"]!.GetValue<string>().Should().Contain("Up to three photos show the same package");
}
```

Update every other `stub.Label().ReadAsync(Jpeg, "image/jpeg", ...)` call in the file to the list form.

- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Plate.Web.Tests --filter "FullyQualifiedName~OpenRouterLabelReaderTests"`. Expected: compile FAIL (`LabelImage` doesn't exist).

- [ ] **Step 3: Core types.** In `LabelReading.cs`, before `ILabelReader`:

```csharp
/// <summary>One photo for the label model: its bytes and the content type sniffed from them.</summary>
public record LabelImage(byte[] Bytes, string ContentType);
```

and change the interface method to:

```csharp
    /// <summary>Reads one to three photos of the same package in one call (SPEC 6.6).</summary>
    Task<LabelReadResult> ReadAsync(IReadOnlyList<LabelImage> images, CancellationToken ct);
```

- [ ] **Step 4: The reader.** In `OpenRouterLabelReader.cs`:
  - In `Instructions`, change `- name and brand only when printed in this photo, as written; otherwise null.` to `- name and brand only when printed in one of the photos, as written; otherwise null.`, change `- is_label is false when the photo doesn't show a nutrition table.` to `- is_label is false only when no photo shows a nutrition table.`, and add before it:

```text
        - Up to three photos show the same package: the nutrition table and other sides of it. Use them together:
          take each value from the photo that prints it clearly. If two photos print different numbers for the same
          row, use the earlier photo's.
```

  - `TestAsync`: `var result = await ReadAsync([new LabelImage(buffer.ToArray(), "image/jpeg")], apiKey, model, ct);`
  - `ReadAsync` signature: `public async Task<LabelReadResult> ReadAsync(IReadOnlyList<LabelImage> images, string apiKey, string model, CancellationToken ct)` and its first line `var body = await BodyAsync(images, model, ct);`.
  - `BodyAsync(IReadOnlyList<LabelImage> images, string model, CancellationToken ct)`: build the user content as:

```csharp
        var content = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = images.Count == 1
                ? "Read this nutrition label."
                : $"These {images.Count} photos show the same package. Read its nutrition label, using every photo.",
        });
        foreach (var image in images)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Bytes)}" },
            });
        }
```

    and use `new JsonObject { ["role"] = "user", ["content"] = content }` as the second message.
  - `SwitchingLabelReader.ReadAsync(IReadOnlyList<LabelImage> images, CancellationToken ct)`, passing `images` through.

- [ ] **Step 5: The call site.** In `LabelScanService.ReadAsync`, change the reader call to `await reader.ReadAsync([new LabelImage(bytes, type)], CancellationToken.None);` (Task 6 makes the service take several photos).

- [ ] **Step 6: The fakes.** `FakeLabelReader`:

```csharp
    public IReadOnlyList<LabelImage>? LastImages { get; private set; }

    public Task<LabelReadResult> ReadAsync(IReadOnlyList<LabelImage> images, CancellationToken ct)
    {
        // Like the real HTTP call: a cancelled request never gets an answer.
        ct.ThrowIfCancellationRequested();
        Calls++;
        LastImages = images;
        return Task.FromResult(Next);
    }
```

`StubLabelReader` in `AiStubs.cs`: change the signature to `ReadAsync(IReadOnlyList<LabelImage> images, CancellationToken ct)`, update its doc comment with "A second or third photo stands for the front of the pack: it adds the brand and a 250 g serving.", and change the final return to:

```csharp
        var more = images.Count > 1;
        return new(new LabelReading(true, slow ? "Slow ayran" : more ? "Айрян Верея" : "Айрян", more ? "Верея" : null, LabelBasis.Per100ml,
            more ? 250 : null, 38, 159, 1.8m, 2.6m, 2.2m, 1.4m, null), "{}", null, "test/label");
```

- [ ] **Step 7: Run the tests.**

```bash
dotnet build
dotnet test tests/Plate.Web.Tests --filter "FullyQualifiedName~OpenRouterLabelReaderTests|FullyQualifiedName~LabelScanTests|FullyQualifiedName~LabelReaderSwitchTests"
dotnet test tests/Plate.Core.Tests && dotnet test tests/Plate.Web.Tests
dotnet test tests/Plate.E2E.Tests --filter "FullyQualifiedName~LabelPhotoTests"
```

Expected: all PASS; single-photo behaviour is unchanged.

- [ ] **Step 8: Commit.**

```bash
git add -A src tests
git commit -m "Let the label reader send up to three photos in one call"
```

---

### Task 5: Store up to three photos per scan

**Files:**
- Modify: `src/Plate.Core/Domain/LabelScan.cs`
- Modify: `src/Plate.Web/Data/AppDbContext.cs:290-300`
- Create: `src/Plate.Web/Data/Migrations/<timestamp>_LabelPhotoSlots.cs` (generated, then edited)
- Modify: `src/Plate.Web/Features/Food/LabelScanService.cs:41` (`Photo =` → `Photos =`)
- Modify: `src/Plate.Web/Features/Feedback/LabelScanReport.cs`
- Modify: `src/Plate.Web/Controllers/AdminLabelsController.cs`
- Modify: `src/Plate.Web/Views/AdminLabels/Scan.cshtml:13-20`, `Views/AdminLabels/List.cshtml:41`
- Modify: `src/Plate.Web/Cli/FeedbackCommand.cs:31, 208-219, 496-499`
- Modify: `src/Plate.Web/Views/AdminAi/Model.cshtml:70`
- Modify tests that build `Photo = new LabelScanPhoto {...}`: `ExportTests.cs:78`, `FeedbackExtrasTests.cs:144-158`, `AdminLabelsTests.cs:36`, `LabelDataTests.cs:87`, `AccountCliTests.cs:216`, `LabelScanTests.cs:150-152`
- Test: `tests/Plate.Web.Tests/AdminLabelsTests.cs`, `tests/Plate.Web.Tests/MigrationTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `LabelScan.Photos` (`List<LabelScanPhoto>`, replaces `Photo`), `LabelScanPhoto.Position` (`int`, 0-based), `LabelScanPhoto.MaxPerScan = 3`.
  - `LabelScanRow.PhotoCount` (`int`, replaces `HasPhoto`).
  - `LabelScanReport.PhotoAsync(int id, int number, CancellationToken ct)`, `number` 1-based. Audit target stays `label scan #{id}` for photo 1 and is `label scan #{id} photo {number}` for the others.
  - Admin route `GET /admin/labels/{id}/photo/{number=1}`; CLI `feedback label-photo <id> [n]`.

- [ ] **Step 1: Write the failing tests.** In `AdminLabelsTests.cs`, change `ScanAsync`'s `bool photo = true` parameter to `int photos = 1` and its photo line to:

```csharp
            Photos = Enumerable.Range(0, photos).Select(i => new LabelScanPhoto
            {
                Position = i, ContentType = "image/jpeg", Width = 10, Height = 10, Bytes = [.. Photo, (byte)i],
            }).ToList(),
```

Update the existing assertions that compare bytes with `Photo` to `[.. Photo, 0]`, the existing `src="/admin/labels/{id}/photo"` expectation to `src="/admin/labels/{id}/photo/1"`, and any `photo: false` caller to `photos: 0`. Then add:

```csharp
[Fact]
public async Task A_scan_with_three_photos_shows_and_serves_each_and_audits_each_download()
{
    await using var app = new AppFactory(fixture);
    var admin = await AdminTests.UnlockedDevAsync(app);
    var scan = await ScanAsync(Unique("three"), photos: 3);

    var page = WebUtility.HtmlDecode(await admin.GetStringAsync($"/admin/labels/{scan.Id}"));
    page.Should().Contain($"src=\"/admin/labels/{scan.Id}/photo/1\"").And.Contain($"src=\"/admin/labels/{scan.Id}/photo/3\"");
    (await admin.GetByteArrayAsync($"/admin/labels/{scan.Id}/photo/3")).Should().Equal([.. Photo, 2]);
    (await admin.GetByteArrayAsync($"/admin/labels/{scan.Id}/photo")).Should().Equal([.. Photo, 0]);
    (await admin.GetAsync($"/admin/labels/{scan.Id}/photo/4")).StatusCode.Should().Be(HttpStatusCode.NotFound);

    var list = WebUtility.HtmlDecode(await admin.GetStringAsync("/admin/labels?since=7d"));
    list.Should().Contain("3 photos");
    await using var db = fixture.CreateContext(null);
    (await db.AdminAudit.AnyAsync(a => a.Action == AuditActions.LabelPhotoDownload && a.Target == $"label scan #{scan.Id} photo 3")).Should().BeTrue();
}

[Fact]
public async Task The_cli_prints_the_nth_photo()
{
    await using var app = new AppFactory(fixture);
    var scan = await ScanAsync(Unique("cli-nth"), photos: 2);

    using var second = new MemoryStream();
    (await FeedbackCommand.RunAsync(app.Services, ["feedback", "label-photo", scan.Id.ToString(), "2"], new StringWriter(), second)).Should().Be(0);
    second.ToArray().Should().Equal([.. Photo, 1]);

    using var none = new MemoryStream();
    (await FeedbackCommand.RunAsync(app.Services, ["feedback", "label-photo", scan.Id.ToString(), "3"], new StringWriter(), none)).Should().Be(1);
}
```

In `MigrationTests.cs` add:

```csharp
[Fact]
public async Task Rolling_back_label_photo_slots_keeps_the_first_photo()
{
    var connection = await fixture.NewDatabaseAsync("label_slots_rollback");
    await using var db = fixture.CreateContext(null, connection);
    await db.Database.MigrateAsync();
    db.Users.Add(new AppUser { Id = "someone", UserName = "someone@test", Email = "someone@test" });
    await db.SaveChangesAsync();
    await db.Database.ExecuteSqlRawAsync("""
        INSERT INTO "LabelScans" ("Id", "UserId", "CreatedAt", "Model", "LatencyMs", "Edited") VALUES (1, 'someone', now(), 'test/label', 1, false);
        INSERT INTO "LabelScanPhotos" ("LabelScanId", "Position", "ContentType", "Width", "Height", "Bytes")
        VALUES (1, 0, 'image/jpeg', 1, 1, '\x01'), (1, 1, 'image/jpeg', 1, 1, '\x02'), (1, 2, 'image/jpeg', 1, 1, '\x03');
        """);

    await db.GetService<IMigrator>().MigrateAsync("UnnamedFoods");

    var left = await db.Database.SqlQueryRaw<int>("""SELECT COUNT(*)::int AS "Value" FROM "LabelScanPhotos" WHERE "LabelScanId" = 1""").SingleAsync();
    left.Should().Be(1);
}
```

Fix the remaining `Photo = new LabelScanPhoto { ... }` sites in the listed tests to `Photos = [new LabelScanPhoto { ... }]` and `Include(s => s.Photo)` to `Include(s => s.Photos)`; in `FeedbackExtrasTests` assert `left[fresh.Id].Photos.Should().NotBeEmpty();` and `left[month.Id].Photos.Should().BeEmpty("the photo is kept 30 days, the scan 180");`; in `LabelScanTests.Each_read_stores_a_scan_and_its_photo` use `var photo = scan.Photos.Single();` and `(photo.Position, photo.ContentType, photo.Bytes.Length, photo.Width).Should().Be((0, "image/jpeg", 5000, 1200));`.

- [ ] **Step 2: Run them and see them fail.** `dotnet build tests/Plate.Web.Tests`. Expected: compile FAIL (`Photos`, `Position` don't exist).

- [ ] **Step 3: Domain.** In `LabelScan.cs`, replace `public LabelScanPhoto? Photo { get; set; }` with `public List<LabelScanPhoto> Photos { get; set; } = [];`, change the `LabelScanPhoto` summary to "One of a scan's photos (up to three, in the order taken), in its own table so lists never load them. Deleted after 30 days.", and add:

```csharp
    /// <summary>A label read takes up to three photos of the same package (SPEC 6.6).</summary>
    public const int MaxPerScan = 3;

    /// <summary>0-based order in which the photos were taken.</summary>
    public int Position { get; set; }
```

- [ ] **Step 4: Model configuration.** In `AppDbContext.cs`, replace the `HasOne(x => x.Photo).WithOne(...)` line with:

```csharp
            e.HasMany(x => x.Photos).WithOne(p => p.LabelScan).HasForeignKey(p => p.LabelScanId).OnDelete(DeleteBehavior.Cascade);
```

and in the `LabelScanPhoto` block add `e.HasIndex(x => new { x.LabelScanId, x.Position }).IsUnique();`.

- [ ] **Step 5: Generate and edit the migration.**

```bash
dotnet tool restore
dotnet ef migrations add LabelPhotoSlots --project src/Plate.Web --output-dir Data/Migrations
```

Read the generated `Up`: it should drop `IX_LabelScanPhotos_LabelScanId`, add `Position integer NOT NULL DEFAULT 0`, and create unique `IX_LabelScanPhotos_LabelScanId_Position`. Existing photos get `Position` 0. In `Down`, before the statement that recreates the unique `IX_LabelScanPhotos_LabelScanId` index, add:

```csharp
            // The old schema allowed one photo per scan; keep the first (Review Focus 7).
            migrationBuilder.Sql("""DELETE FROM "LabelScanPhotos" WHERE "Position" > 0;""");
```

Check the SQL against a dev database: `docker compose -f docker/docker-compose.dev.yml up -d db`, then `dotnet ef migrations script UnnamedFoods LabelPhotoSlots --project src/Plate.Web` and read it.

- [ ] **Step 6: Report, admin, CLI.** In `LabelScanReport.cs`:

```csharp
public record LabelScanRow(int Id, DateTimeOffset CreatedAt, string Reporter, string Model, int LatencyMs, string? Error, bool Saved, bool Edited, int PhotoCount);
```

In `ListAsync` use `photos.Count(p => p.LabelScanId == s.Id)` for the last argument; in `GetAsync` replace `hasPhoto` with `var photoCount = await db.LabelScanPhotos.IgnoreQueryFilters().CountAsync(p => p.LabelScanId == id, ct);`. Replace `PhotoAsync`:

```csharp
    /// <summary>The scan's nth photo (1-based) while it is kept (30 days). Every download is audited: it is a picture from someone's kitchen.</summary>
    public async Task<LabelScanPhoto?> PhotoAsync(int id, int number, CancellationToken ct)
    {
        var photo = await db.LabelScanPhotos.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(p => p.LabelScanId == id && p.Position == number - 1, ct);
        if (photo is not null)
        {
            await audit.WriteAsync(AuditActions.LabelPhotoDownload, number == 1 ? $"label scan #{id}" : $"label scan #{id} photo {number}", null, ct);
        }

        return photo;
    }
```

`AdminLabelsController.Photo`:

```csharp
    [HttpGet("{id:int}/photo/{number:int=1}")]
    public async Task<IActionResult> Photo(int id, int number, CancellationToken ct)
    {
        var photo = await labels.PhotoAsync(id, number, ct) ?? throw new NotFoundException();
```

(rest unchanged). `Scan.cshtml`:

```cshtml
    @if (scan.PhotoCount > 0)
    {
        @for (var n = 1; n <= scan.PhotoCount; n++)
        {
            <img src="/admin/labels/@scan.Id/photo/@n" alt="Label photo @n" style="width: 100%; border-radius: var(--r-md); margin-bottom: var(--s-3)" />
        }
    }
    else
    {
        <p class="footnote">Photos deleted after 30 days.</p>
    }
```

`List.cshtml:41`: replace `@(scan.HasPhoto ? "" : " · photo deleted")` with `@(scan.PhotoCount == 0 ? " · photo deleted" : scan.PhotoCount > 1 ? $" · {scan.PhotoCount} photos" : "")`.

`FeedbackCommand.cs`: the usage line becomes `          label-photo <id> [n]            the nth photo's raw bytes (1 by default) to stdout, while kept (30 days); audited`; the `label-photo` case reads the number and passes it:

```csharp
                case "label-photo":
                    var number = rest.Length > 2 && int.TryParse(rest[2], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;
                    var photo = await labels.PhotoAsync(Id(rest), number, ct);
                    if (photo is null)
                    {
                        Console.Error.WriteLine($"Label scan #{Id(rest)} has no photo {number} (deleted after 30 days, or no such scan or photo).");
                        return 1;
                    }
```

and `LabelLine` ends with `+ (s.PhotoCount == 0 ? " (photo deleted)" : s.PhotoCount > 1 ? $" ({s.PhotoCount} photos)" : "");`.

`AdminAi/Model.cshtml:70`: change "One label photo is about two thousand tokens." to "One label photo is about two thousand tokens; a read with three photos about six thousand."

`LabelScanService.cs:41`: `Photos = [new LabelScanPhoto { Position = 0, ContentType = type, Width = ..., Height = ..., Bytes = bytes }],`.

`SoftDeletePurge` (`p.LabelScan.CreatedAt < photoCutoff`) and `UserDeleter` (cascade) need no change; the retention and `delete-user` tests prove it.

- [ ] **Step 7: Run the tests.**

```bash
dotnet build
dotnet test tests/Plate.Web.Tests --filter "FullyQualifiedName~AdminLabelsTests|FullyQualifiedName~MigrationTests|FullyQualifiedName~LabelScanTests|FullyQualifiedName~LabelDataTests|FullyQualifiedName~FeedbackExtrasTests|FullyQualifiedName~ExportTests|FullyQualifiedName~AccountCliTests"
dotnet test tests/Plate.Core.Tests && dotnet test tests/Plate.Web.Tests
```

Expected: all PASS, including `LabelDataTests` (a third account sees no other account's photos) and the export test (no photo bytes).

- [ ] **Step 8: Update the feedback skill if it calls `label-photo`.** `grep -rn "label-photo" ~/.agent/skills .agent 2>/dev/null`. If the `/feedback` skill documents the command, add the optional `[n]`; it is outside the repo, so mention it in the final report rather than committing it.

- [ ] **Step 9: Commit.**

```bash
git add -A src tests
git commit -m "Store up to three photos per label scan

LabelScanPhoto gets a Position and the one-to-one becomes one-to-many.
The admin scan page shows each photo, /admin/labels/{id}/photo/{n} and
feedback label-photo <id> [n] serve the nth, and every download is
audited. Rolling back keeps each scan's first photo."
```

---

### Task 6: Read one to three photos at `POST /food/label`

**Files:**
- Modify: `src/Plate.Web/Features/Food/LabelScanService.cs:12-46`
- Modify: `src/Plate.Web/Controllers/FoodController.cs:163-197`
- Modify: `src/Plate.Web/Resources/strings.bg.json`, `strings.keys.json`
- Test: `tests/Plate.Web.Tests/LabelScanTests.cs`, `tests/Plate.E2E.Tests/LabelPhotoTests.cs` (size limit)

**Interfaces:**
- Consumes: `LabelImage`, `ILabelReader.ReadAsync(IReadOnlyList<LabelImage>, ...)` (Task 4); `LabelScan.Photos`, `LabelScanPhoto.Position`, `LabelScanPhoto.MaxPerScan` (Task 5); `FakeLabelReader.LastImages`.
- Produces:
  - `public record LabelPhotoUpload(byte[] Bytes, int Width, int Height);` and `LabelScanService.ReadAsync(IReadOnlyList<LabelPhotoUpload> photos, CancellationToken ct)`.
  - The endpoint takes repeated multipart fields `photo`, `width`, `height`, in slot order. One `photo` field behaves exactly as today. Task 7's client relies on this.

- [ ] **Step 1: Write the failing Web tests.** In `LabelScanTests.cs`, rename the existing `ReadAsync` helper to `ReadManyAsync(Account a, IReadOnlyList<byte[]> photos, string? name = null, string? barcode = null, bool barcodeField = true, bool? share = null, string? grams = null, string? servingGrams = null)`. Remove the fixed `width`/`height` entries from its dictionary initializer and replace the single file part with:

```csharp
        for (var i = 0; i < photos.Count; i++)
        {
            var file = new ByteArrayContent(photos[i]);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "photo", $"label{i}.jpg");
            form.Add(new StringContent((1200 - 200 * i).ToString()), "width");
            form.Add(new StringContent("900"), "height");
        }
```

Keep a one-photo wrapper so existing tests don't change:

```csharp
    private static Task<HttpResponseMessage> ReadAsync(Account a, byte[] photo, string? name = null, string? barcode = null, bool barcodeField = true, bool? share = null,
        string? grams = null, string? servingGrams = null) =>
        ReadManyAsync(a, [photo], name, barcode, barcodeField, share, grams, servingGrams);
```

Then add:

```csharp
[Fact]
public async Task Three_photos_are_read_together_and_stored_in_order()
{
    var (app, reader) = NewApp();
    await using var _app = app;
    var a = await SignInAsync(app);

    var response = await ReadManyAsync(a, [Jpeg(5000), Jpeg(4000), Jpeg(3000)]);

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    (await HtmlAsync(response)).Should().Contain("Read from the photo. Check each value against the label.");
    reader.Calls.Should().Be(1);
    reader.LastImages!.Select(i => i.Bytes.Length).Should().Equal(5000, 4000, 3000);
    await using var db = fixture.CreateContext(a.UserId);
    var scan = await db.LabelScans.Include(s => s.Photos).SingleAsync();
    scan.Photos.OrderBy(p => p.Position).Select(p => (p.Position, p.Bytes.Length, p.Width)).Should().Equal((0, 5000, 1200), (1, 4000, 1000), (2, 3000, 800));
}

[Fact]
public async Task A_fourth_photo_is_refused_before_the_model_is_called()
{
    var (app, reader) = NewApp();
    await using var _app = app;
    var a = await SignInAsync(app);

    var response = await ReadManyAsync(a, [Jpeg(100), Jpeg(100), Jpeg(100), Jpeg(100)]);

    response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    (await response.Content.ReadAsStringAsync()).Should().Contain("Attach one to three photos.");
    reader.Calls.Should().Be(0);
}

[Fact]
public async Task One_bad_photo_among_three_refuses_the_read()
{
    var (app, reader) = NewApp();
    await using var _app = app;
    var a = await SignInAsync(app);

    var response = await ReadManyAsync(a, [Jpeg(100), "not an image"u8.ToArray(), Jpeg(100)]);

    response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    reader.Calls.Should().Be(0);
}

[Fact]
public async Task A_three_photo_read_counts_once_toward_the_limit()
{
    var (app, _) = NewApp(new Dictionary<string, string?> { ["RateLimits:AiLabelPerWindow"] = "2" });
    await using var _app = app;
    var a = await SignInAsync(app);

    (await ReadManyAsync(a, [Jpeg(100), Jpeg(100), Jpeg(100)])).StatusCode.Should().Be(HttpStatusCode.OK);
    (await ReadAsync(a, Jpeg(100))).StatusCode.Should().Be(HttpStatusCode.OK);
    (await ReadAsync(a, Jpeg(100))).StatusCode.Should().Be((HttpStatusCode)429);
}
```

The last test uses the same `RateLimits:AiLabelPerWindow` override as the existing rate-limit test in this file.

- [ ] **Step 2: Run them and see them fail.** `dotnet test tests/Plate.Web.Tests --filter "FullyQualifiedName~LabelScanTests"`. Expected: the new tests FAIL (only the first photo is bound).

- [ ] **Step 3: The service.** In `LabelScanService.cs` add `public record LabelPhotoUpload(byte[] Bytes, int Width, int Height);` next to `LabelScanResult`, change the class summary to "Reads one to three label photos, stores the scan with its photos, and records what the user saved (SPEC 6.6).", and replace `ReadAsync` with:

```csharp
    public async Task<LabelScanResult> ReadAsync(IReadOnlyList<LabelPhotoUpload> photos, CancellationToken ct)
    {
        if (photos.Count is 0 or > LabelScanPhoto.MaxPerScan)
        {
            throw new ValidationException("Attach one to three photos.");
        }

        var images = new List<LabelImage>(photos.Count);
        foreach (var photo in photos)
        {
            if (photo.Bytes.Length > LabelScanPhoto.MaxBytes)
            {
                throw new ValidationException("That photo is too large.");
            }

            images.Add(new LabelImage(photo.Bytes, ImageSniffer.ContentType(photo.Bytes) ?? throw new ValidationException("Attach a photo (JPEG, PNG or WebP).")));
        }

        var started = time.GetTimestamp();
        // Not cancelled with the request: a second photo or leaving the page aborts it, but the model may already
        // have been paid for, and every read is recorded (SPEC 6.6). The reader's own timeouts bound the wait.
        var result = await reader.ReadAsync(images, CancellationToken.None);
        var outcome = result.Reading is { } reading ? LabelValues.Normalize(reading) : new LabelOutcome(null, result.Error);
        var scan = new LabelScan
        {
            UserId = currentUser.UserId ?? throw new InvalidOperationException("No signed-in user."),
            CreatedAt = time.GetUtcNow(),
            Model = string.IsNullOrEmpty(result.Model) ? "unknown" : result.Model,
            LatencyMs = (int)time.GetElapsedTime(started).TotalMilliseconds,
            Error = (result.Error ?? outcome.Error)?.ToString(),
            Reading = result.Raw is null ? null : ValidJson(result.Raw),
            Values = outcome.Values is null ? null : JsonSerializer.Serialize(StoredLabelValues.From(outcome.Values), Json),
            Photos = photos.Select((p, i) => new LabelScanPhoto
            {
                Position = i,
                ContentType = images[i].ContentType,
                Width = Math.Clamp(p.Width, 0, 10_000),
                Height = Math.Clamp(p.Height, 0, 10_000),
                Bytes = p.Bytes,
            }).ToList(),
        };
        db.LabelScans.Add(scan);
        await db.SaveChangesAsync(CancellationToken.None);
        return new LabelScanResult(scan.Id, outcome.Values, outcome.Error);
    }
```

- [ ] **Step 4: The endpoint.** Replace `FoodController.Label` with:

```csharp
    /// <summary>Reads one to three label photos and answers with the manual food form filled from them (SPEC 6.6).</summary>
    [HttpPost("label")]
    [EnableRateLimiting(RateLimits.AiLabel)]
    // The one endpoint above the 1 MB request limit (SPEC 9): up to three photos the phone shrank to under 2 MB each.
    [RequestSizeLimit(7 * 1024 * 1024)]
    public async Task<IActionResult> Label(DateOnly date, Meal meal, string? barcode, bool barcodeField, string? name, bool? share, decimal? grams, decimal? servingGrams,
        List<int> width, List<int> height, List<IFormFile> photo, CancellationToken ct)
    {
        if (!await labels.IsAvailableAsync(ct))
        {
            return NotFound();
        }

        if (photo.Count == 0 || photo.Any(p => p.Length == 0))
        {
            throw new ValidationException("Attach a photo (JPEG, PNG or WebP).");
        }

        if (photo.Count > LabelScanPhoto.MaxPerScan)
        {
            throw new ValidationException("Attach one to three photos.");
        }

        if (photo.Any(p => p.Length > LabelScanPhoto.MaxBytes))
        {
            throw new ValidationException("That photo is too large.");
        }

        var uploads = new List<LabelPhotoUpload>(photo.Count);
        for (var i = 0; i < photo.Count; i++)
        {
            using var buffer = new MemoryStream((int)photo[i].Length);
            await photo[i].CopyToAsync(buffer, ct);
            uploads.Add(new LabelPhotoUpload(buffer.ToArray(), width.ElementAtOrDefault(i), height.ElementAtOrDefault(i)));
        }

        var result = await labels.ReadAsync(uploads, ct);
        if (result.Values is null)
        {
            // Only the message goes back: the form the user already filled in stays as it is (SPEC 6.6).
            throw new ValidationException(result.Error ?? "Couldn't read the label. Enter the values by hand.");
        }

        barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();
        return PartialView("_ManualFoodForm", new ManualFoodFormModel(date, meal, barcodeField ? null : barcode, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), barcodeField,
            Label: result.Values, LabelScanId: result.ScanId, LabelAvailable: true,
            BarcodeValue: barcodeField ? barcode : null, Share: share ?? true, TypedGrams: grams, TypedServing: servingGrams));
    }
```

- [ ] **Step 5: Strings.** Add `"Attach one to three photos.": "Прикачи от една до три снимки."` to `strings.bg.json` and regenerate keys.

- [ ] **Step 6: The Kestrel limit, in the browser.** In `LabelPhotoTests.cs`, next to `A_photo_between_1_and_2_MB_gets_through_Kestrels_request_limit`:

```csharp
    [Fact]
    public async Task Three_photos_near_2_MB_each_get_through_Kestrels_request_limit()
    {
        await using var phone = await app.SignedInPhoneAsync(E2EFixture.Chromium, ai: true);
        await phone.GoAsync(Manual);

        var status = await phone.Page.EvaluateAsync<int>("""
            async () => {
              const data = new FormData();
              for (let i = 0; i < 3; i++) {
                const bytes = new Uint8Array(1_950_000);
                bytes.set([0xFF, 0xD8, 0xFF]);
                data.append("photo", new Blob([bytes], { type: "image/jpeg" }), `label${i}.jpg`);
                data.append("width", "2000");
                data.append("height", "1500");
              }
              for (const [k, v] of [["date", document.querySelector("[name=date]").value], ["meal", "Lunch"], ["barcodeField", "true"]]) data.set(k, v);
              const r = await fetch("/food/label", { method: "POST", body: data, headers: { "RequestVerificationToken": window.gymToken(), "HX-Request": "true" } });
              return r.status;
            }
            """);

        status.Should().Be(200);
    }
```

- [ ] **Step 7: Run the tests.**

```bash
dotnet build
dotnet test tests/Plate.Web.Tests --filter "FullyQualifiedName~LabelScanTests"
dotnet test tests/Plate.E2E.Tests --filter "FullyQualifiedName~LabelPhotoTests"
dotnet test tests/Plate.Core.Tests && dotnet test tests/Plate.Web.Tests
```

Expected: all PASS. The existing single-photo E2E tests still pass because `label.js` still sends one `photo` field.

- [ ] **Step 8: Commit.**

```bash
git add -A src tests
git commit -m "Read one to three label photos in one request

POST /food/label takes up to three photos (7 MB body), reads them in one
model call and stores them in order on one scan."
```

---

### Task 7: The client — optional extra slots, read together

**Files:**
- Create: `src/Plate.Web/Views/Food/_LabelPhotoSlots.cshtml`
- Modify: `src/Plate.Web/Views/Food/_ManualFoodForm.cshtml:15-24` (the `label-pick` block)
- Modify: `src/Plate.Web/Views/Food/Add.cshtml:51-70` (Scan tab)
- Modify: `src/Plate.Web/wwwroot/js/label.js`
- Modify: `src/Plate.Web/wwwroot/css/app.css` (after line 412)
- Modify: `src/Plate.Web/Resources/strings.bg.json`, `strings.keys.json`
- Test: `tests/Plate.Web.Tests/LabelScanTests.cs` (markup), `tests/Plate.E2E.Tests/LabelPhotoTests.cs`

**Interfaces:**
- Consumes: Task 6's repeated `photo`/`width`/`height` fields; Task 4's E2E stub (one image: "Айрян", no serving; two or more: "Айрян Верея", serving 250; typed name "no label" fails, "slow" answers slowly); Task 3's `label:barcode` dispatch (kept as is).
- Produces: nothing other tasks use. Markup contract inside this task:
  - `[data-label-photos]` wraps one single-photo button (`[data-label-single]`, holding `input[data-label-photo]`) and one `_LabelPhotoSlots` partial. The class `is-more` on the wrapper hides the single button and shows the panel.
  - `_LabelPhotoSlots` renders `button[data-label-more]` and `[data-label-panel]` with three `[data-label-slot-index="0|1|2"]` slots (each `input[data-label-slot-photo]`, `[data-label-thumb]`, `button[data-label-remove]`), `button[data-label-read]` (with `data-label-start` on the Scan tab) and `button[data-label-less]`.

**Behaviour (SPEC 6.6 "More photos", Task 1):** Without a press, the photo button reads one photo at once, exactly as today. "Add more photos" swaps that button for three slots: "Nutrition table", "Optional", "Optional". Picking into a slot only shrinks the photo and shows its thumbnail; nothing is sent. "Read the label" is enabled once any slot has a photo and sends all filled slots, in slot order, in one read. "Use one photo" closes the slots and drops their photos. While a read runs, slots, remove buttons and Read are disabled. A failed read leaves the slots filled. A successful read redraws the form with the slots closed (on the Scan tab, the panel closes and the filled form opens below, as for one photo).

- [ ] **Step 1: Write the failing markup test.** Add to `LabelScanTests.cs`:

```csharp
[Fact]
public async Task The_extra_photo_slots_are_there_but_closed_until_asked_for()
{
    var (app, _) = NewApp();
    await using var _app = app;
    var a = await SignInAsync(app);

    foreach (var path in new[] { AddPage, "/food/add?date=2026-10-05&meal=Lunch&tab=barcode" })
    {
        var html = WebUtility.HtmlDecode(await a.Client.GetStringAsync(path));
        html.Should().Contain("data-label-more").And.Contain("Add more photos").And.Contain("Nutrition table");
        System.Text.RegularExpressions.Regex.Matches(html, "data-label-slot-photo").Count.Should().Be(3, path);
        html.Should().MatchRegex("data-label-read[^>]*disabled");
    }
}
```

- [ ] **Step 2: Write the failing E2E tests.** Add to `LabelPhotoTests.cs`:

```csharp
    private static ILocator Panel(Phone phone, string scope = "[data-label-form]") => phone.Page.Locator($"{scope} [data-label-panel]");

    private static Task PickIntoSlotAsync(Phone phone, int slot, string file = "label.jpg", string scope = "[data-label-form]") =>
        phone.Page.Locator($"{scope} [data-label-slot-index=\"{slot}\"] [data-label-slot-photo]").SetInputFilesAsync(Fixture(file));

    private static Task ThumbAsync(Phone phone, int slot, string scope = "[data-label-form]") =>
        phone.Page.Locator($"{scope} [data-label-slot-index=\"{slot}\"] [data-label-thumb] img").WaitForAsync();

    [Theory]
    [InlineData(E2EFixture.Chromium, false)]
    [InlineData(E2EFixture.WebKit, false)]
    [InlineData(E2EFixture.Chromium, true)]
    public async Task The_extra_slots_open_on_a_press_and_fit_the_phone(string browser, bool bulgarian)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.WithTargetsAsync(app, bulgarian);
        await phone.GoAsync(Manual);
        (await Panel(phone).IsVisibleAsync()).Should().BeFalse();

        var more = phone.Page.Locator("[data-label-form] [data-label-more]");
        (await Phone.BoxAsync(more)).Height.Should().BeGreaterThanOrEqualTo(44);
        await more.TapAsync();

        (await Panel(phone).IsVisibleAsync()).Should().BeTrue();
        (await phone.Page.Locator("[data-label-form] [data-label-single]").IsVisibleAsync()).Should().BeFalse();
        (await phone.Page.Locator("[data-label-form] [data-label-read]").IsDisabledAsync()).Should().BeTrue();
        for (var i = 0; i < 3; i++)
        {
            var box = await Phone.BoxAsync(phone.Page.Locator($"[data-label-form] [data-label-slot-index=\"{i}\"] .label-slot-pick"));
            box.Height.Should().BeGreaterThanOrEqualTo(44);
            (box.X + box.Width).Should().BeLessThanOrEqualTo(390);
        }
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task Photos_in_the_slots_are_read_together_when_read_is_pressed(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        await phone.Page.Locator("[data-label-form] [data-label-more]").TapAsync();

        await PickIntoSlotAsync(phone, 0);
        await ThumbAsync(phone, 0);
        await PickIntoSlotAsync(phone, 1);
        await ThumbAsync(phone, 1);
        (await phone.Page.Locator("[data-label-form] .banner").CountAsync()).Should().Be(0, "picking into a slot sends nothing");

        await phone.Page.Locator("[data-label-form] [data-label-read]").TapAsync();
        await FilledAsync(phone);

        (await phone.Page.InputValueAsync("#m-name")).Should().Be("Айрян Верея");
        (await phone.Page.InputValueAsync("#m-serving")).Should().Be("250");
        (await Panel(phone).IsVisibleAsync()).Should().BeFalse("a read closes the slots");
        await phone.NavigateByAsync(() => phone.Page.Locator("[data-label-form] button[type=submit]").TapAsync());
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food");
        phone.ShouldHaveNoErrors();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task A_removed_photo_is_left_out(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        await phone.Page.Locator("[data-label-form] [data-label-more]").TapAsync();
        await PickIntoSlotAsync(phone, 0);
        await PickIntoSlotAsync(phone, 2);
        await ThumbAsync(phone, 2);

        await phone.Page.Locator("[data-label-form] [data-label-slot-index=\"2\"] [data-label-remove]").TapAsync();
        await phone.Page.Locator("[data-label-form] [data-label-read]").TapAsync();
        await FilledAsync(phone);

        (await phone.Page.InputValueAsync("#m-name")).Should().Be("Айрян", "only one photo was sent");
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task Use_one_photo_closes_the_slots_and_drops_them(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        var more = phone.Page.Locator("[data-label-form] [data-label-more]");
        await more.TapAsync();
        await PickIntoSlotAsync(phone, 1);
        await ThumbAsync(phone, 1);

        await phone.Page.Locator("[data-label-form] [data-label-less]").TapAsync();
        (await phone.Page.Locator("[data-label-form] [data-label-single]").IsVisibleAsync()).Should().BeTrue();
        (await Panel(phone).IsVisibleAsync()).Should().BeFalse();

        await more.TapAsync();
        (await phone.Page.Locator("[data-label-form] [data-label-thumb] img").CountAsync()).Should().Be(0);
        (await phone.Page.Locator("[data-label-form] [data-label-read]").IsDisabledAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task A_failed_read_keeps_the_slots_filled(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        // The stub reads no table when the typed name is "no label".
        await phone.Page.FillAsync("#m-name", "no label");
        await phone.Page.Locator("[data-label-form] [data-label-more]").TapAsync();
        await PickIntoSlotAsync(phone, 0);
        await PickIntoSlotAsync(phone, 1);
        await ThumbAsync(phone, 1);

        await phone.Page.Locator("[data-label-form] [data-label-read]").TapAsync();
        await phone.Page.Locator("#toast", new() { HasText = "Couldn't find a nutrition table in that photo." }).WaitForAsync();

        (await phone.Page.Locator("[data-label-form] [data-label-thumb] img").CountAsync()).Should().Be(2);
        (await phone.Page.Locator("[data-label-form] [data-label-read]").IsEnabledAsync()).Should().BeTrue();
        (await phone.Page.InputValueAsync("#m-name")).Should().Be("no label");
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task Slots_and_read_are_disabled_while_reading(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        // The stub answers slowly when the typed name is "slow".
        await phone.Page.FillAsync("#m-name", "slow");
        await phone.Page.Locator("[data-label-form] [data-label-more]").TapAsync();
        await PickIntoSlotAsync(phone, 0);
        await ThumbAsync(phone, 0);

        await phone.Page.Locator("[data-label-form] [data-label-read]").TapAsync();

        (await phone.Page.Locator("[data-label-form] [data-label-read]").IsDisabledAsync()).Should().BeTrue();
        (await phone.Page.Locator("[data-label-form] [data-label-slot-index=\"1\"] [data-label-slot-photo]").IsDisabledAsync()).Should().BeTrue();
        await FilledAsync(phone);
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    public async Task A_photo_the_browser_cannot_open_leaves_its_slot_empty(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync(Manual);
        await phone.Page.Locator("[data-label-form] [data-label-more]").TapAsync();

        await PickIntoSlotAsync(phone, 0, "not-an-image.jpg");

        await phone.Page.Locator("#toast", new() { HasText = "Couldn't read that picture. Try another photo." }).WaitForAsync();
        (await phone.Page.Locator("[data-label-form] [data-label-thumb] img").CountAsync()).Should().Be(0);
        (await phone.Page.Locator("[data-label-form] [data-label-read]").IsDisabledAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData(E2EFixture.Chromium)]
    [InlineData(E2EFixture.WebKit)]
    public async Task More_photos_on_the_scan_tab_open_the_filled_form_below(string browser)
    {
        await using var phone = await app.SignedInPhoneAsync(browser, ai: true);
        await phone.GoAsync($"/food/add?date={Today}&meal=Lunch&tab=barcode");
        const string scan = "[data-label-photos]:not([data-label-form] *)";
        await phone.Page.Locator($"{scan} [data-label-more]").TapAsync();
        (await phone.Page.Locator($"{scan} [data-label-single]").IsVisibleAsync()).Should().BeFalse();
        (await phone.Page.Locator("[data-start]").IsVisibleAsync()).Should().BeTrue("Scan a barcode stays");

        await PickIntoSlotAsync(phone, 0, scope: scan);
        await PickIntoSlotAsync(phone, 1, scope: scan);
        await ThumbAsync(phone, 1, scan);
        await phone.Page.Locator($"{scan} [data-label-read]").TapAsync();
        await phone.Page.Locator("[data-label-slot] .banner").WaitForAsync();

        (await phone.Page.InputValueAsync("[data-label-slot] #m-serving")).Should().Be("250");
        (await Panel(phone, scan).IsVisibleAsync()).Should().BeFalse();
        new Uri(phone.Page.Url).AbsolutePath.Should().Be("/food/add");
    }
```

- [ ] **Step 3: Run them and see them fail.** `dotnet test tests/Plate.Web.Tests --filter "FullyQualifiedName~The_extra_photo_slots"` and `dotnet test tests/Plate.E2E.Tests --filter "FullyQualifiedName~LabelPhotoTests"`. Expected: the new tests FAIL (no `data-label-more`); the existing label tests pass.

- [ ] **Step 4: The slots partial.** Create `Views/Food/_LabelPhotoSlots.cshtml`:

```cshtml
@model bool
@using Plate.Core.Domain
@* Up to three photos read together (SPEC 6.6). Closed until "Add more photos"; then the single photo button gives way to
   the slots and nothing is sent until "Read the label". The model is true on the Scan tab, where the filled form opens below. *@
<button type="button" class="btn btn-plain btn-block label-more" data-label-more aria-expanded="false">@Ui.Icon("plus")@L["Add more photos"]</button>
<div class="label-panel" data-label-panel>
    <div class="label-slots">
        @for (var i = 0; i < LabelScanPhoto.MaxPerScan; i++)
        {
            <div class="label-slot" data-label-slot-index="@i">
                <label class="label-slot-pick">
                    <span class="label-slot-thumb" data-label-thumb></span>
                    <span class="label-slot-name">@(i == 0 ? L["Nutrition table"] : L["Optional"])</span>
                    <input type="file" accept="image/*" class="sr-only" data-label-slot-photo />
                </label>
                <button type="button" class="label-slot-remove" data-label-remove aria-label="@L["Remove photo"]" hidden>@Ui.Icon("close", "icon-sm")</button>
            </div>
        }
    </div>
    <p class="section-footer">@L["Add the front or another side of the pack if the table doesn't show the name or serving."]</p>
    <button type="button" class="btn btn-primary btn-block" data-label-read data-label-start="@(Model ? "" : null)" disabled>@L["Read the label"]</button>
    <button type="button" class="btn btn-plain btn-block" data-label-less>@L["Use one photo"]</button>
</div>
```

- [ ] **Step 5: Place it.** In `_ManualFoodForm.cshtml`, change the `label-pick` block to:

```cshtml
    @if (Model.LabelAvailable)
    {
        <div class="label-pick" data-label data-label-photos>
            <label class="btn btn-secondary btn-block" data-label-button data-label-single>@Ui.Icon("photo")@L["Read the label from a photo"]
                <input type="file" accept="image/*" class="sr-only" data-label-photo />
            </label>
            <partial name="_LabelPhotoSlots" model="false" />
            <p class="section-footer">@L["The photo is sent to an AI model through OpenRouter and kept for 30 days so misreads can be checked."]</p>
            <p class="thinking" role="status"><i></i><i></i><i></i><span>@L["Reading the label…"]</span></p>
        </div>
    }
```

In `Add.cshtml` (Scan tab), wrap the `btn-stack` and add the partial; the privacy line, status and slot stay outside the wrapper so the form that opens in the slot has its own `[data-label-photos]`:

```cshtml
        <div data-module="label">
        <div data-label-photos>
            <div class="btn-stack">
                <button type="button" class="btn btn-primary" data-start>@Ui.Icon("barcode")@L["Scan a barcode"]</button>
                @if (Model.LabelAvailable)
                {
                    <label class="btn btn-secondary" data-label-button data-label-single>@Ui.Icon("photo")@L["Photograph a label"]
                        <input type="file" accept="image/*" class="sr-only" data-label-photo data-label-start />
                    </label>
                }
            </div>
            @if (Model.LabelAvailable)
            {
                <partial name="_LabelPhotoSlots" model="true" />
            }
        </div>
```

(the rest of the Scan tab block is unchanged).

CSS, after line 412 (tokens from `wwwroot/css/tokens.css`):

```css
/* Extra label photos (SPEC 6.6): closed until asked for, then they replace the single photo button. */
[data-label-photos]:not(.is-more) .label-panel, [data-label-photos].is-more [data-label-single], [data-label-photos].is-more .label-more { display: none; }
.label-more { margin-top: var(--s-2); min-height: var(--tap); }
.label-slots { display: grid; grid-template-columns: repeat(3, 1fr); gap: var(--s-2); }
.label-slot { position: relative; }
.label-slot-pick { position: relative; display: grid; place-items: center; aspect-ratio: 1; min-height: var(--tap); border: 1.5px dashed var(--separator); border-radius: var(--r-md); overflow: hidden; color: var(--label-2); font-size: var(--text-footnote); text-align: center; cursor: pointer; }
.label-slot-thumb { position: absolute; inset: 0; }
.label-slot-thumb img { width: 100%; height: 100%; object-fit: cover; display: block; }
.label-slot.is-filled .label-slot-pick { border-style: solid; }
.label-slot.is-filled .label-slot-name { display: none; }
.label-slot-pick:has(input:disabled) { opacity: 0.5; cursor: default; }
.label-slot-remove { position: absolute; top: 0; right: 0; width: var(--tap); height: var(--tap); display: grid; place-items: center; color: var(--label); }
.label-slot-remove .icon { padding: 3px; border-radius: 50%; background: var(--material); }
.label-panel .btn + .btn, .label-panel .section-footer + .btn { margin-top: var(--s-2); }
```

- [ ] **Step 6: Strings.** Add to `strings.bg.json`:

```json
"Add more photos": "Добави още снимки",
"Nutrition table": "Таблица",
"Remove photo": "Махни снимката",
"Read the label": "Прочети етикета",
"Use one photo": "Само една снимка",
"Add the front or another side of the pack if the table doesn't show the name or serving.": "Добави предната или друга страна на опаковката, ако таблицата не показва името или порцията."
```

`"Optional"` already exists ("По желание"). Regenerate keys.

- [ ] **Step 7: `label.js`.** Change the header comment's first sentence to "Label photos for the manual food form (SPEC 6.6): shrink the photos on the phone, look for a barcode in them, send them to /food/label and swap in the form the server filled. One photo reads at once; with the extra slots open, up to three are read together when the user asks." Then make these changes.

Add after `let detector = null;`:

```js
    const SHRINK = { maxSide: 2000, quality: 0.85, maxBytes: 2 * 1024 * 1024, steps: [[2000, 0.7], [1600, 0.7], [1200, 0.6]] };

    // Photos picked into the extra slots, by panel (the Scan tab and the form that opens under it each have one).
    const picks = new WeakMap();

    function pickedIn(panel) {
        if (!picks.has(panel)) {
            picks.set(panel, [null, null, null]);
        }
        return picks.get(panel);
    }

    function forget(panel) {
        pickedIn(panel).forEach((shot) => shot && URL.revokeObjectURL(shot.url));
        picks.delete(panel);
    }

    // Thumbnails, remove buttons and the Read button follow what is picked, the connection and a running read.
    function drawPanel(panel) {
        const list = pickedIn(panel);
        const reading = pending !== null;
        panel.querySelectorAll("[data-label-slot-index]").forEach((slot) => {
            const shot = list[Number(slot.dataset.labelSlotIndex)];
            const thumb = slot.querySelector("[data-label-thumb]");
            if (shot) {
                const img = document.createElement("img");
                img.src = shot.url;
                img.alt = "";
                thumb.replaceChildren(img);
            } else {
                thumb.replaceChildren();
            }
            slot.classList.toggle("is-filled", !!shot);
            const remove = slot.querySelector("[data-label-remove]");
            remove.hidden = !shot;
            remove.disabled = reading;
            slot.querySelector("[data-label-slot-photo]").disabled = reading;
        });
        panel.querySelector("[data-label-read]").disabled = reading || !navigator.onLine || !list.some(Boolean);
    }

    function drawPanels() {
        root.querySelectorAll("[data-label-panel]").forEach(drawPanel);
    }

    function setMore(scope, open) {
        scope.classList.toggle("is-more", open);
        scope.querySelector("[data-label-more]").setAttribute("aria-expanded", String(open));
        const panel = scope.querySelector("[data-label-panel]");
        if (!open) {
            forget(panel);
        }
        drawPanel(panel);
    }

    async function pickInto(input) {
        const file = input.files && input.files[0];
        input.value = "";
        if (!file) {
            return;
        }
        const panel = input.closest("[data-label-panel]");
        const index = Number(input.closest("[data-label-slot-index]").dataset.labelSlotIndex);
        let shot;
        try {
            shot = await window.gymImage.shrink(file, SHRINK);
        } catch {
            toast(t("Couldn't read that picture. Try another photo."));
            return;
        }
        const list = pickedIn(panel);
        if (list[index]) {
            URL.revokeObjectURL(list[index].url);
        }
        list[index] = { ...shot, url: URL.createObjectURL(shot.blob) };
        drawPanel(panel);
    }
```

In `setOnline`, add `drawPanels();` at the end.

Change `read(input)` to `read(items, trigger)`, where `items` is an array of `File`s (the single button) or of shrunk shots (the slots), and `trigger` is the element that started it:
  - Replace the first lines (`const file = ...; input.value = ""; if (!file) return;`) with nothing; callers handle that.
  - `const slot = trigger.matches("[data-label-start]") ? root.querySelector("[data-label-slot]") : null;` and `const host = slot ? null : trigger.closest("[data-label-form]");` (were based on `input`).
  - After `busy.classList.add("is-busy");` add `drawPanels();` (so the slots and Read disable; `pending` is already set).
  - Replace the shrink block with:

```js
            let shots;
            try {
                // A File is shrunk here; a slot's photo was shrunk when it was picked.
                shots = await Promise.all(items.map((item) => item instanceof Blob ? window.gymImage.shrink(item, SHRINK) : item));
            } catch {
                if (pending === controller) {
                    toast(t("Couldn't read that picture. Try another photo."));
                }
                return;
            }
```

  - Replace `data.append("photo", shot.blob, "label.jpg"); data.set("width", ...); data.set("height", ...);` with:

```js
            shots.forEach((shot, i) => {
                data.append("photo", shot.blob, `label${i + 1}.jpg`);
                data.append("width", String(shot.width));
                data.append("height", String(shot.height));
            });
```

  - Replace the barcode line with:

```js
            let found = knownBarcode;
            if (!found && barcodeField) {
                for (const shot of shots) {
                    found = await barcodeIn(shot.blob);
                    if (found) {
                        break;
                    }
                }
            }
            data.set("barcode", found || "");
```

  - After placing the new form (and before the Task 3 `label:barcode` dispatch), close the panel the read came from: `const scope = trigger.closest("[data-label-photos]"); if (scope && trigger.matches("[data-label-read]")) { setMore(scope, false); }`. On the manual form the scope was replaced and this only frees the photos; on the Scan tab it closes the panel above the slot.
  - In `finally`, inside `if (pending === controller) { ... }`, add `drawPanels();` after `pending = null;`. A failed read reaches here with the panel's photos untouched.
  - Remove the `toggleShare(...)` call if Task 2 left any.

Replace the `onChange` handler and add a click handler:

```js
    const onChange = (event) => {
        const input = event.target;
        if (input.matches("[data-label-slot-photo]")) {
            pickInto(input);
        } else if (input.matches("[data-label-photo]")) {
            const file = input.files && input.files[0];
            input.value = "";
            if (file) {
                read([file], input);
            }
        }
    };
    const onClick = (event) => {
        const button = event.target.closest("[data-label-more], [data-label-less], [data-label-remove], [data-label-read]");
        if (!button || button.disabled) {
            return;
        }
        const scope = button.closest("[data-label-photos]");
        if (button.matches("[data-label-more]")) {
            setMore(scope, true);
        } else if (button.matches("[data-label-less]")) {
            setMore(scope, false);
        } else if (button.matches("[data-label-remove]")) {
            const panel = button.closest("[data-label-panel]");
            const list = pickedIn(panel);
            const index = Number(button.closest("[data-label-slot-index]").dataset.labelSlotIndex);
            if (list[index]) {
                URL.revokeObjectURL(list[index].url);
                list[index] = null;
            }
            drawPanel(panel);
        } else {
            const shots = pickedIn(button.closest("[data-label-panel]")).filter(Boolean);
            if (shots.length) {
                read(shots, button);
            }
        }
    };
```

Register `root.addEventListener("click", onClick);` next to the `change` listener, remove it in the cleanup, and in the cleanup also call `root.querySelectorAll("[data-label-panel]").forEach(forget);`.

Keep the existing behaviours: only the latest request's answer is used, a photo picked from the single button while another read runs aborts it, a failed read toasts and leaves the form alone.

- [ ] **Step 8: Run the tests.**

```bash
dotnet build
dotnet test tests/Plate.Web.Tests --filter "FullyQualifiedName~LabelScanTests"
dotnet test tests/Plate.E2E.Tests --filter "FullyQualifiedName~LabelPhotoTests|FullyQualifiedName~BarcodeFieldTests"
dotnet test tests/Plate.Core.Tests && dotnet test tests/Plate.Web.Tests
```

Expected: all PASS, including the existing `A_second_photo_replaces_a_first_one_still_being_read` and `A_file_the_browser_cannot_open_leaves_the_form_alone` (single-button path unchanged).

- [ ] **Step 9: Check the layout by eye at 390 px.** Run locally (`docker compose -f docker/docker-compose.dev.yml up -d db`, `dotnet run --project src/Plate.Web`), open the Manual tab and the Scan tab in Chromium at 390 × 844 with the label model on, open the slots, fill two, and screenshot in light and dark and in Bulgarian. Nothing scrolls sideways; slots are square; the remove button doesn't cover the slot's label; "Таблица" and "По желание" fit their slots.

- [ ] **Step 10: Run the full E2E suite (end of workstream B).** `dotnet test tests/Plate.E2E.Tests`. Expected: all PASS (rerun the known WebKit flake alone if it fails).

- [ ] **Step 11: Commit.**

```bash
git add -A src tests
git commit -m "Optional extra label photos, read together

The photo button still reads one photo at once. Add more photos swaps it
for three slots (table plus two optional), and Read the label sends all
filled slots in one read. A failed read keeps the slots; slots and Read
are disabled while a read runs."
```

---

### Task 8: Verify, merge, deploy and announce

**Files:**
- Modify: `README.md` only if it documents `label-photo` or the 3 MB limit (`grep -n "label-photo\|3 MB" README.md`).

**Interfaces:** none.

- [ ] **Step 1: Full verification on the branch.**

```bash
dotnet build
dotnet test
dotnet test tests/Plate.E2E.Tests
git status
```

Report the actual counts. Every test must pass; the tree must be clean.

- [ ] **Step 2: Review.** Run `/code-review` on the branch, or dispatch the whole-branch reviewer if executing with subagents. Fix anything confirmed, with tests, as extra commits.

- [ ] **Step 3: Merge.** `git switch main && git merge --no-ff scan-and-photos -m "Merge branch 'scan-and-photos'"`. Do not push unless Antoan asks.

- [ ] **Step 4: Pre-deploy dump on wizard.** The migration changes `LabelScanPhotos`. Take a dump into `~` (never the stack folder; rsync `--delete` removes it), using the command in `README.md` "Backups" with the output `~/plate-pre-label-photo-slots-20261010.dump`.

- [ ] **Step 5: Deploy.** Follow `README.md` "Updating" exactly: the rsync line with its `P` filters, then the build on wizard with `APP_VERSION` set to the merge commit's short sha so reports record it.

- [ ] **Step 6: Verify live.**

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://plate.peychev.uk/account/login
ssh wizard docker logs --since 5m plate-app 2>&1 | grep -iE "error|fail|exception" || true
ssh wizard docker exec plate-app dotnet Plate.Web.dll ai test label
```

Expected: `200`, no new errors, and the sample label read correctly. Then check `/food/add?tab=manual` on the live site at 390 px shows the scan button.

- [ ] **Step 7: What's new.** Two user-visible changes, two entries (match the tone of `feedback changelog list`; Bulgarian uses "ти" and the app's own names from `strings.bg.json`):

```bash
ssh wizard docker exec plate-app dotnet Plate.Web.dll feedback changelog add \
  --en "Manual: tap the barcode icon next to Barcode to scan it instead of typing it. If the app already has that product, it asks whether it's the same food." \
  --bg "Ръчно: докосни иконата до Баркод, за да го сканираш, вместо да го пишеш. Ако продуктът вече е в приложението, ще те попита дали е същата храна." \
  --version <sha>
ssh wizard docker exec plate-app dotnet Plate.Web.dll feedback changelog add \
  --en "Reading a label from a photo: tap Add more photos to add the front or another side of the pack as well, then Read the label to read them all together." \
  --bg "Четене на етикет от снимка: докосни „Добави още снимки“, за да добавиш и предната или друга страна на опаковката, после „Прочети етикета“, за да се прочетат всички заедно." \
  --version <sha>
```

- [ ] **Step 8: Memory and report.** Save a project memory (`phase-15-scan-and-photos.md`: merged sha, deploy date, dump path, iPhone check pending) and add its line to `MEMORY.md`. Report to Antoan: test counts, deploy result, the What's new ids, and the iPhone checks from SPEC Phase 15's "Done when" that only he can run (camera scan into the field on the installed app, a real three-photo label in Bulgarian).

- [ ] **Step 9: Clean up.** `git branch -d scan-and-photos`.
