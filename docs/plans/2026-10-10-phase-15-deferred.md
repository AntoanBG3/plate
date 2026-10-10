# Phase 15 deferred issues

Minor findings from the review of the barcode field scan and label photo slots work (deployed as `cc717be` on 2026-10-10), left unfixed. None blocks normal use.

## Label photos

1. **"Use one photo" stays tappable during a read.** While a multi-photo read is running, pressing it drops the slot photos and brings back the single photo button, but the read still finishes and fills the form. Disable it while `is-busy`.
2. **Slot thumbnails are not freed.** Object URLs are not revoked when the slot panel is replaced without `forget`. A pick that finishes loading after "Use one photo" lands in the closed panel and reappears the next time "Add more photos" is opened.

## Barcode field and known-barcode card

3. **Card not re-offered after a label read.** If the barcode was typed by hand first and a label photo is read afterwards, the "Is this the same food?" card is not shown again. Save then fails with the "already shared" error instead of offering the existing food.
4. **Wrong card wording for your own shared food.** When the match is the user's own shared food, the card says "Another food already shares this barcode". The behaviour is correct (rejecting clears the barcode); only the text is wrong.
5. **Silent scan button.** The barcode icon next to the Barcode field does nothing if the barcode ponyfill failed to load. It should show a message or go straight to "Take a photo instead".

## CLI

6. **`feedback label-photo <id> <n>` with a non-number `n`** silently falls back to photo 1. It should print an error and exit 1.

## Code tidy-up

7. Stale "3 MB" comments (the limit is now 7 MB): `tests/GymTracker.Web.Tests/LabelScanTests.cs:246` and `tests/GymTracker.E2E.Tests/LabelPhotoTests.cs:247`.
8. `LabelScanReport.PhotoAsync` in `src/GymTracker.Web/Features/Feedback/LabelScanReport.cs` is indented one level too deep.
9. SPEC 6.6 "Model call" schema line still says `name` and `brand` come "only when printed in the photo"; it should read "in any of the photos".

## Pre-existing

10. Bulgarian "По желание" placeholders are cut off in the number rows of the food form at 390 px.

## Still to check on the iPhone (SPEC Phase 15, "Done when")

- Camera scan and "Take a photo instead" fill the Barcode field in the installed app.
- A real Bulgarian label read from two or three photos.
