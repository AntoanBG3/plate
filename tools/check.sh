#!/bin/bash
# The same checks run in CI and before any production deployment.
set -euo pipefail
cd "$(dirname "$0")/.."

# CI puts the two slower suites on separate runners. Without arguments, run the entire deployment gate: every test
# project, one after another. Through the solution, the Web and browser suites ran at once, and their per-class
# databases together filled Docker Desktop's disk ("No space left on device", 2026-11-14 and 2026-11-15).
run_js=true
install_browser_deps=false
test_settings=""
all="tests/Plate.Core.Tests tests/Plate.Web.Tests tests/Plate.E2E.Tests"
case "${1:-}" in
    "") set -- $all ;;
    --browser-deps)
        install_browser_deps=true
        set -- $all
        ;;
    --ci-core-web) set -- tests/Plate.Core.Tests tests/Plate.Web.Tests ;;
    --ci-browser)
        run_js=false
        install_browser_deps=true
        # GitHub's runners have fewer cores than the three browser test classes that xunit.runner.json runs at once;
        # starved, WebKit dropped taps and ran tests ten times slower, failing a different one on most runs.
        test_settings="xUnit.MaxParallelThreads=2"
        set -- tests/Plate.E2E.Tests
        ;;
    *) echo "Usage: $0 [--browser-deps | --ci-core-web | --ci-browser]" >&2; exit 2 ;;
esac

# The integration and browser tests start Postgres through Testcontainers; without Docker every one of them fails.
docker info > /dev/null 2>&1 || { echo "Docker isn't running. Start Docker Desktop and run the checks again." >&2; exit 1; }

if [ "$run_js" = true ]; then
    node --test tests/js/*.test.cjs
fi
for project in "$@"; do
    dotnet restore "$project"
    dotnet build "$project" --configuration Release --no-restore
    if [ "$install_browser_deps" = true ] && [ "$project" = tests/Plate.E2E.Tests ]; then
        pwsh tests/Plate.E2E.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium webkit
    fi
    dotnet test "$project" --configuration Release --no-build --no-restore ${test_settings:+-- $test_settings}
done
