#!/bin/sh
# AgentBridge launcher (Linux/macOS)
# Opens the terminal UI (chat + slash commands) while the OpenAI-compatible HTTP
# server keeps answering API calls in the same process. Add --headless for the
# plain server console (scripts/CI).
cd "$(dirname "$0")"

echo "========================================"
echo " AGENT BRIDGE - LAUNCHER"
echo "========================================"
echo
echo "Starting AgentBridge..."
echo "  Terminal UI: chat, /commands, voice, model switch, help"
echo "  OpenAI-compatible API:  http://localhost:5290"
echo "  Health check:           http://localhost:5290/health"
echo "  Server-only (no UI):    ./start.sh --headless"
echo "  Press Ctrl+C to stop."
echo

exec dotnet run --project AgentBridge.csproj -- "$@"
