#!/usr/bin/env bash
# Interactive Telegram configuration generator for AgentBridge (Linux/macOS).
# Asks a few questions in English and produces/updates telegram.json — the SAME
# file the TUI /telegram config commands and the TelegramBridge read and write.
#
# Usage:
#   bash setup-telegram.sh                          # writes ./telegram.json
#   bash setup-telegram.sh path/to/telegram.json    # updates an existing file
#   TELEGRAM_CONFIG=path/to/telegram.json bash setup-telegram.sh   # same via environment
#
# After configuring, start the bridge from the TUI (/telegram status, then set
# Enabled true) and complete the first login with /telegram login-code <code>.
set -euo pipefail

ask() { # ask <prompt> <default>
    local prompt="$1" default="$2" answer
    read -r -p "${prompt} [${default}]: " answer
    echo "${answer:-$default}"
}

TARGET="${TELEGRAM_CONFIG:-}"
if [ -z "$TARGET" ] && [ "${1:-}" != "" ]; then
    TARGET="$1"
fi
if [ -z "$TARGET" ]; then
    TARGET=./telegram.json
fi

ENABLED=$(ask "Enable the Telegram bridge at startup? (y/n)" "y")
case "$ENABLED" in y|Y|yes|YES|true|1) ENABLED=true ;; *) ENABLED=false ;; esac
APIID=$(ask "App api_id (from https://my.telegram.org/apps)" "")
APIHASH=$(ask "App api_hash (from https://my.telegram.org/apps)" "")
PHONE=$(ask "Account phone number, international format (e.g. +393331234567)" "")
SESSION=$(ask "Session file name (auth keys persist here after the first login)" "telegram.session")
ALLOWED=$(ask "Allowed users, comma separated (numeric ids or @usernames; empty = all private chats)" "")

cat > "$TARGET.tmp" <<EOF
{
  "Enabled": ${ENABLED},
  "ApiId": ${APIID},
  "ApiHash": "${APIHASH}",
  "PhoneNumber": "${PHONE}",
  "SessionPath": "${SESSION}",
  "AllowedUsers": [],
  "Agent": "default-agent"
}
EOF

# Build the AllowedUsers array from the comma-separated answer (empty → []).
python3 - "$TARGET.tmp" "$ALLOWED" <<'PYEOF'
import json, sys
path, raw = sys.argv[1], sys.argv[2]
cfg = json.load(open(path, encoding='utf-8'))
cfg['AllowedUsers'] = [u.strip() for u in raw.split(',') if u.strip()]
json.dump(cfg, open(path, 'w', encoding='utf-8'), indent=2)
PYEOF
mv "$TARGET.tmp" "$TARGET"

echo
echo "Telegram configuration written to $TARGET"
echo
echo "Next steps:"
echo "  1. Run AgentBridge and open the TUI."
echo "  2. /telegram status            — see the bridge state."
echo "  3. /telegram config set Enabled true   — start the bridge (first login requires"
echo "     a verification code from Telegram; send it with /telegram login-code <code>)."
echo "  4. /telegram allow <user>      — optionally restrict who can talk to the agent."
echo "More: docs/telegram.md"
