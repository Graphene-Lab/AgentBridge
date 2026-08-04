#!/bin/sh
# MinimalChatApi launcher (Linux/macOS)
# Starts the OpenAI-compatible HTTP server exposing AgentOrchestrator.
cd "$(dirname "$0")"

echo "========================================"
echo " MINIMAL CHAT API - LAUNCHER"
echo "========================================"
echo
echo "Starting MinimalChatApi (dotnet run)..."
echo "  OpenAI-compatible API:  http://localhost:5290"
echo "  Health check:           http://localhost:5290/health"
echo "  Press Ctrl+C to stop."
echo

exec dotnet run --project MinimalChatApi.csproj
