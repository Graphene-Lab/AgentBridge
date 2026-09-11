#!/usr/bin/env bash
# One-time install of the AgentBridge MSI streaming proxy on the AIOffice VPS.
# Idempotent: safe to re-run. Needs root (run with: echo <pw> | sudo -S bash install.sh).
set -euo pipefail
SRC=/home/agent/store-proxy
SITE=/etc/nginx/sites-enabled/aitechnology.it

if [ ! -f "$SRC/mirror-msi.py" ]; then
  echo "FATAL: $SRC/mirror-msi.py missing — stage the repo's tools/store/vps files first" >&2
  exit 1
fi
if [ ! -f "$SITE" ]; then
  echo "FATAL: $SITE not found" >&2
  exit 1
fi

install -m 644 "$SRC/agentbridge-mirror.service" /etc/systemd/system/agentbridge-mirror.service
systemctl daemon-reload
systemctl enable agentbridge-mirror
# restart (not "enable --now"): a re-run after staging a new mirror-msi.py must load it.
systemctl restart agentbridge-mirror
sleep 1
if ! systemctl is-active --quiet agentbridge-mirror; then
  echo "FATAL: agentbridge-mirror did not start" >&2
  systemctl --no-pager --lines=15 status agentbridge-mirror >&2 || true
  exit 1
fi

install -m 644 "$SRC/agentbridge-msi.nginx.conf" /etc/nginx/snippets/agentbridge-msi.conf
if ! grep -q 'agentbridge-msi.conf' "$SITE"; then
  # Insert the include only inside the :443 server block (after its cert key line).
  sed -i '/ssl_certificate_key \/etc\/letsencrypt\/live\/aitechnology.it\/privkey.pem;/a\    include /etc/nginx/snippets/agentbridge-msi.conf;' "$SITE"
fi
nginx -t
systemctl reload nginx

echo "INSTALL-OK"
curl -fsS http://127.0.0.1:8686/healthz && echo
# The Store downloader requires HEAD and a Content-Length (2026-09 certification
# failure 10.3.4): verify both, warning only so a GitHub hiccup cannot fail the install.
if curl -fsSI --max-time 60 http://127.0.0.1:8686/msi | grep -qi '^content-length:'; then
  echo "proxy: HEAD + Content-Length OK"
else
  echo "WARN: /msi did not answer HEAD with a Content-Length (is GitHub reachable?)" >&2
fi
