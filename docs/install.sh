#!/usr/bin/env bash
# AgentBridge one-line installer (Linux / macOS).
#
#   curl -fsSL https://graphene-lab.github.io/AgentBridge/install.sh | bash
#
# Downloads the latest release archive for the detected platform from GitHub,
# extracts it to /opt/agentbridge (override with AGENTBRIDGE_HOME) and installs
# it as a systemd service that auto-starts at boot. The archives are
# self-contained (~460 MB): no .NET runtime needed, Kokoro TTS voices included.
#
# Environment overrides:
#   AGENTBRIDGE_HOME          install directory (default: /opt/agentbridge)
#   AGENTBRIDGE_VERSION       release tag to install, e.g. v1.26.08.28 (default: latest)
#   AGENTBRIDGE_NO_SERVICE=1  extract only, no systemd service
#
# Missing dependencies (curl, tar) are installed automatically with sudo.
# The service runs as root; to run as another user, set User= in the unit.

set -euo pipefail

REPO="Graphene-Lab/AgentBridge"
VERSION="${AGENTBRIDGE_VERSION:-latest}"
DEST="${AGENTBRIDGE_HOME:-/opt/agentbridge}"
NO_SERVICE="${AGENTBRIDGE_NO_SERVICE:-0}"

if [ "$VERSION" = "latest" ]; then
  BASE="https://github.com/$REPO/releases/latest/download"
else
  BASE="https://github.com/$REPO/releases/download/$VERSION"
fi

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

# --- dependencies (curl, tar) ---
missing=""
command -v curl >/dev/null 2>&1 || missing="$missing curl"
command -v tar >/dev/null 2>&1 || missing="$missing tar"
if [ -n "$missing" ]; then
  echo "AgentBridge: installing missing dependencies:$missing"
  if command -v apt-get >/dev/null 2>&1; then
    sudo apt-get update -qq && sudo apt-get install -y -qq curl tar
  elif command -v dnf >/dev/null 2>&1; then
    sudo dnf install -y curl tar
  elif command -v yum >/dev/null 2>&1; then
    sudo yum install -y curl tar
  else
    echo "AgentBridge: please install curl and tar, then re-run." >&2
    exit 1
  fi
fi

# .NET globalization (libicu): the self-contained app falls back to invariant
# globalization on old/missing ICU, but a completely absent ICU breaks startup.
# Best-effort install on Debian/Ubuntu (non-fatal if unavailable).
if command -v dpkg >/dev/null 2>&1; then
  if ! ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so'; then
    echo "AgentBridge: libicu not found - installing (best-effort)..."
    sudo apt-get update -qq && sudo apt-get install -y -qq libicu-dev || \
      echo "AgentBridge: libicu install failed (the app may need it on this distro)." >&2
  fi
fi

asset="agentbridge-$os-$arch.tar.gz"
echo "Installing AgentBridge ($os-$arch, $VERSION) into $DEST ..."
echo "Downloading $BASE/$asset"

sudo mkdir -p "$DEST"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

curl -fL --retry 3 -o "$tmp/$asset" "$BASE/$asset"
tar -xzf "$tmp/$asset" -C "$tmp"
rm -f "$tmp/$asset"

# The archive files may carry a foreign uid (the CI build user); force a sane owner
# so the app can write its runtime state (logs/) without permission errors.
sudo cp -R "$tmp"/. "$DEST"/
sudo chown -R root:root "$DEST"
sudo chmod +x "$DEST/agent"

# --- systemd service (Linux only) ---
if [ "$NO_SERVICE" = "0" ] && [ "$os" = "linux" ] && [ -d /run/systemd/system ]; then
  echo "Installing systemd service (auto-start at boot)..."
  sudo tee /etc/systemd/system/agentbridge.service >/dev/null <<EOF
[Unit]
Description=AgentBridge AI Agent Server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$DEST
ExecStart=$DEST/agent --headless --environment Production
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
EOF
  sudo systemctl daemon-reload
  sudo systemctl enable agentbridge
  sudo systemctl restart agentbridge
  sleep 3
  if systemctl is-active --quiet agentbridge; then
    echo "AgentBridge service is active."
  else
    echo "WARNING: service did not start - check: systemctl status agentbridge" >&2
    journalctl -u agentbridge -n 10 --no-pager >&2 || true
  fi
fi

echo
echo "AgentBridge installed to $DEST."
echo "HTTP API: http://localhost:5290  (health: /health, models: /v1/models)"
if [ "$NO_SERVICE" = "1" ]; then
  echo "Start manually: $DEST/agent"
fi
