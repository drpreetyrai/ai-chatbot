#!/usr/bin/env bash
# Starts ARIA against the live Azure services in .env, with the two settings that
# a fresh Foundry project usually gets wrong corrected on the way past.
#
# Fix them properly in .env and you can just use ./scripts/start.sh instead.
set -euo pipefail
cd "$(dirname "$0")/.."

# 1 · Deployment names must match what actually exists in your Foundry project.
#     List yours:  az cognitiveservices account deployment list -n <resource> -g <rg>
export MODEL_REASONING_DEPLOYMENT="${MODEL_REASONING_DEPLOYMENT:-model-router}"
export MODEL_FAST_DEPLOYMENT="${MODEL_FAST_DEPLOYMENT:-model-router}"
export MODEL_CLASSIFY_DEPLOYMENT="${MODEL_CLASSIFY_DEPLOYMENT:-model-router}"

# 2 · model-router routes to a reasoning model, which is far slower than the
#     budgets the plan assumes for a small extraction model. Deploy something like
#     gpt-4o-mini and point the fast/classify aliases at it to get these back down.
export MODEL_TIMEOUT_FAST_SECONDS="${MODEL_TIMEOUT_FAST_SECONDS:-45}"
export MODEL_TIMEOUT_REASONING_SECONDS="${MODEL_TIMEOUT_REASONING_SECONDS:-90}"

# 3 · Entra SSO is not implemented. With AZURE_TENANT_ID set the API correctly
#     refuses dev sign-in, and there is no other way in — so clear it locally.
export AZURE_TENANT_ID=""
export AZURE_CLIENT_ID=""

echo "Building…"; dotnet build -v q --nologo

cleanup() { echo; echo "Stopping…"; kill 0 2>/dev/null || true; }
trap cleanup EXIT INT TERM

dotnet run --project src/Aria.Api     --no-build 2>&1 | sed 's/^/[api]     /' &
dotnet run --project src/Aria.Workers --no-build 2>&1 | sed 's/^/[workers] /' &
( cd web && npm run dev 2>&1 | sed 's/^/[web]     /' ) &

sleep 6
echo; echo "  ARIA (live Azure)   http://localhost:5173"; echo
wait
