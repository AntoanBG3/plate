#!/usr/bin/env bash
# Renders sample-label.html to the JPEGs the app and the browser tests use (SPEC 6.6), with the Chromium that
# Playwright installed for the browser tests and macOS sips for the JPEG step. Run from the repository root.
set -euo pipefail
chrome=$(find ~/Library/Caches/ms-playwright -type f -name chrome-headless-shell -perm +111 | head -1)
out=$(mktemp -d)
page="file://$PWD/tools/labels/sample-label.html"
"$chrome" --headless --hide-scrollbars --window-size=900,560 --screenshot="$out/plain.png" "$page" 2>/dev/null
"$chrome" --headless --hide-scrollbars --window-size=900,780 --screenshot="$out/barcode.png" "$page?barcode=1" 2>/dev/null
sips -s format jpeg -s formatOptions 85 "$out/plain.png" --out src/Plate.Web/Integrations/Assets/sample-label.jpg >/dev/null
mkdir -p tests/Plate.E2E.Tests/Fixtures
cp src/Plate.Web/Integrations/Assets/sample-label.jpg tests/Plate.E2E.Tests/Fixtures/label.jpg
sips -s format jpeg -s formatOptions 85 "$out/barcode.png" --out tests/Plate.E2E.Tests/Fixtures/label-with-barcode.jpg >/dev/null
echo "wrote sample-label.jpg, Fixtures/label.jpg and Fixtures/label-with-barcode.jpg"
