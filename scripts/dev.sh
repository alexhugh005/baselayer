#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if [ ! -f "$ROOT/frontend/.env.local" ] || [ ! -f "$ROOT/backend/src/BaseLayer.Api/appsettings.Local.json" ]; then
  echo 'Configure frontend/.env.local and backend/src/BaseLayer.Api/appsettings.Local.json first. See README.md.'
  exit 1
fi
cleanup() { kill "${API_PID:-}" "${WEB_PID:-}" 2>/dev/null || true; }
trap cleanup EXIT INT TERM
(cd "$ROOT/backend/src/BaseLayer.Api" && ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5080 dotnet run --no-launch-profile) &
API_PID=$!
(cd "$ROOT/frontend" && npm run dev) &
WEB_PID=$!
echo 'Base Layer: http://localhost:5173 • API: http://localhost:5080'
wait
