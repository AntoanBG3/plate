#!/bin/bash
# The same checks run in CI and before any production deployment.
set -euo pipefail
cd "$(dirname "$0")/.."

# The integration and browser tests start Postgres through Testcontainers; without Docker every one of them fails.
docker info > /dev/null 2>&1 || { echo "Docker isn't running. Start Docker Desktop and run the checks again." >&2; exit 1; }

node --test tests/js/*.test.cjs
dotnet restore GymTracker.slnx
dotnet build GymTracker.slnx --configuration Release --no-restore
if [ "${1:-}" = --browser-deps ]; then
    pwsh tests/GymTracker.E2E.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium webkit
fi
dotnet test GymTracker.slnx --configuration Release --no-build --no-restore
