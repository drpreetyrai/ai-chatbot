#!/usr/bin/env bash
# Starts all three processes and streams their logs. Ctrl-C stops everything.
set -euo pipefail
cd "$(dirname "$0")/.."

[ -f .env ] || { echo "No .env found — copying .env.example"; cp .env.example .env; }

echo "Building…"
dotnet build -v q --nologo

cleanup() { echo; echo "Stopping…"; kill 0 2>/dev/null || true; }
trap cleanup EXIT INT TERM

dotnet run --project src/Aria.Api     --no-build 2>&1 | sed 's/^/[api]     /' &
dotnet run --project src/Aria.Workers --no-build 2>&1 | sed 's/^/[workers] /' &
( cd web && npm run dev 2>&1 | sed 's/^/[web]     /' ) &

sleep 6
echo
echo "  ARIA is running:"
echo "    Web        http://localhost:5173"
echo "    API        http://localhost:5199"
echo "    API (TLS)  https://localhost:7001   ← Google's OAuth callback lands here"
echo
echo "  Sign in:     admin@northbridge.health / AriaAdmin!2026"
echo "  Then approve the two waiting registrations to unlock the doctor and patient."
echo
wait
