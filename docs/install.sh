#!/usr/bin/env bash
# AgentBridge one-line installer (Linux / macOS).
#
#   curl -fsSL https://graphene-lab.github.io/AgentBridge/install.sh | bash
#
# Downloads the latest release archive for the detected platform from GitHub,
# extracts it into ~/.agentbridge (override with AGENTBRIDGE_HOME) and prints
# how to start the agent. The archives are self-contained (~460 MB): no .NET
# installation needed, Kokoro TTS voices included.

set -euo pipefail

REPO="Graphene-Lab/AgentBridge"
BASE="https://github.com/$REPO/releases/latest/download"
DEST="${AGENTBRIDGE_HOME:-$HOME/.agentbridge}"

os="$(uname -s | tr '[:upper:]' '[:lower:]')"
arch="$(uname -m | tr '[:upper:]' '[:lower:]')"

case "$os" in
  linux)  os="linux" ;;
  darwin) os="osx" ;;
  *)
    echo "AgentBridge: unsupported OS '$os'. On Windows, run in PowerShell:" >&2
    echo "  irm https://graphene-lab.github.io/AgentBridge/install.ps1 | iex" >&2
    exit 1
    ;;
esac

case "$arch" in
  x86_64|amd64) arch="x64" ;;
  aarch64|arm64) arch="arm64" ;;
  *)
    echo "AgentBridge: unsupported architecture '$arch' (x64 and arm64 only)." >&2
    exit 1
    ;;
esac

command -v curl >/dev/null 2>&1 || { echo "AgentBridge: curl is required." >&2; exit 1; }
command -v tar >/dev/null 2>&1 || { echo "AgentBridge: tar is required." >&2; exit 1; }

asset="agentbridge-$os-$arch.tar.gz"
echo "Installing AgentBridge ($os-$arch) into $DEST ..."
echo "Downloading $BASE/$asset"

mkdir -p "$DEST"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

curl -fL --retry 3 -o "$tmp/$asset" "$BASE/$asset"
tar -xzf "$tmp/$asset" -C "$tmp"
rm -f "$tmp/$asset"
cp -R "$tmp"/. "$DEST"/
chmod +x "$DEST/agent"

echo
echo "AgentBridge installed to $DEST."
echo "Run:  $DEST/agent"
