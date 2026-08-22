#!/usr/bin/env bash
# Interactive SIP configuration generator for AgentBridge (Linux/macOS).
# Asks a few questions in English and produces the "Sip" configuration — the SAME
# structure the TUI /sip config commands read and write (appsettings.json → Sip).
#
# Usage:
#   bash sip-config.sh                      # writes ./sip.json (always)
#   bash sip-config.sh --appsettings path/to/appsettings.json   # also merges into that file
#   APP_SETTINGS=path/to/appsettings.json bash sip-config.sh    # same via environment
#
# Merging needs jq or python3; without them sip.json is still produced (copy the Sip
# section manually, or apply the same keys with /sip config set in the TUI).
set -euo pipefail

ask() { # ask <prompt> <default>
    local prompt="$1" default="$2" answer
    read -r -p "${prompt} [${default}]: " answer
    echo "${answer:-$default}"
}

ENABLED=$(ask "Enable the SIP server? (y/n)" "y")
case "$ENABLED" in y|Y|yes|YES|true|1) ENABLED=true ;; *) ENABLED=false ;; esac
REGISTRAR=$(ask "Registrar / SIP entry point (e.g. sip:195.20.235.5:5060; empty = direct dial only)" "")
USERNAME=$(ask "Username used to REGISTER at the entry point (e.g. agent)" "agent")
PASSWORD=$(ask "Password / shared secret for the REGISTER" "")
LISTENPORT=$(ask "Local SIP listen port (use a non-standard port if your ISP drops inbound UDP 5060)" "6070")
REGISTEREXPIRY=$(ask "REGISTER refresh interval in seconds (60 keeps home-NAT mappings alive)" "60")
ANSWERMODE=$(ask "Incoming-call gate (pin | allowlist | none)" "pin")
PIN=$(ask "DTMF PIN" "12345")
LANG=$(ask "STT/TTS language, two-letter ISO (it, en, ...)" "it")

cat > sip.json <<EOF
{
  "Enabled": ${ENABLED},
  "ListenPort": ${LISTENPORT},
  "Registrar": "${REGISTRAR}",
  "Username": "${USERNAME}",
  "Password": "${PASSWORD}",
  "AnswerMode": "${ANSWERMODE}",
  "Pin": "${PIN}",
  "MaxPinAttempts": 3,
  "LockoutHours": 24,
  "RegisterExpiry": ${REGISTEREXPIRY},
  "AllowedCallers": [],
  "Agent": "default-agent",
  "Lang": "${LANG}",
  "SttExePath": "",
  "RtpPortRange": ""
}
EOF
echo
echo "Sip section written to ./sip.json"

TARGET="${APP_SETTINGS:-}"
if [ -z "$TARGET" ] && [ "${1:-}" = "--appsettings" ]; then
    TARGET="${2:-}"
fi
if [ -z "$TARGET" ] && [ -f ./appsettings.json ]; then
    TARGET=./appsettings.json
fi

if [ -n "$TARGET" ] && [ -f "$TARGET" ]; then
    if command -v jq >/dev/null 2>&1; then
        jq --argjson sip "$(cat sip.json)" '.Sip = $sip' "$TARGET" > "$TARGET.tmp" && mv "$TARGET.tmp" "$TARGET"
        echo "Merged the Sip section into $TARGET (jq)"
    elif command -v python3 >/dev/null 2>&1; then
        python3 - "$TARGET" <<'PYEOF'
import json, sys
p = sys.argv[1]
with open('sip.json', encoding='utf-8') as f:
    sip = json.load(f)
with open(p, encoding='utf-8') as f:
    cfg = json.load(f)
cfg['Sip'] = sip
with open(p, 'w', encoding='utf-8') as f:
    json.dump(cfg, f, indent=2)
PYEOF
        echo "Merged the Sip section into $TARGET (python3)"
    else
        echo "jq/python3 not found: merge the Sip section from sip.json into $TARGET manually,"
        echo "or apply the same keys with the TUI: /sip config set <key> <value>"
    fi
else
    echo "No appsettings.json found: copy the Sip section into your appsettings.json, or"
    echo "apply the same keys with the TUI: /sip config set <key> <value>"
fi
