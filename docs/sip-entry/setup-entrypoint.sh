#!/usr/bin/env bash
# Provision a minimal SIP entry point (Kamailio + rtpengine, userspace) — UNATTENDED.
#
# Architecture: AgentBridge (behind NAT) and the smartphone are both SIP *clients* of this
# box. AgentBridge REGISTERs here with the shared secret and stays registered; the phone
# dials sip:<user>@<this-host>; calls are routed to AgentBridge's registration and media is
# force-relayed by rtpengine (payloads pass through untouched — no codec handling).
#
# Usage:
#   sudo bash setup-entrypoint.sh                    # shared secret auto-generated (printed)
#   SIP_SECRET=mysecret sudo bash setup-entrypoint.sh
#
# Environment (all optional):
#   SIP_SECRET      shared REGISTER password for the AgentBridge AOR (auto-generated if empty)
#   SIP_PUBLIC_IP   public IP of this server (default: auto-detected via api.ipify.org)
#   SIP_USER        AgentBridge AOR/username (default: agent)
#   SIP_PORT        SIP listen port (default: 5060)
#
# Requires: Debian/Ubuntu with apt. Installs kamailio + kamailio-extra-modules + rtpengine.
# The phone can register with ANY non-empty password (acknowledged but NOT stored); only the
# AgentBridge AOR requires the shared secret.
set -euo pipefail

SIP_PUBLIC_IP="${SIP_PUBLIC_IP:-$(curl -fsS -m 10 https://api.ipify.org 2>/dev/null || true)}"
SIP_PUBLIC_IP="${SIP_PUBLIC_IP:-195.20.235.5}"
SIP_USER="${SIP_USER:-agent}"
SIP_PORT="${SIP_PORT:-5060}"
SIP_SECRET="${SIP_SECRET:-}"
if [ -z "$SIP_SECRET" ]; then
    SIP_SECRET="$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 16)"
    echo "==> generated shared secret: ${SIP_SECRET}"
fi
HA1="$(printf '%s' "${SIP_USER}:${SIP_PUBLIC_IP}:${SIP_SECRET}" | md5sum | awk '{print $1}')"
echo "HA1 for ${SIP_USER}@${SIP_PUBLIC_IP} = ${HA1}"

SRC="$(cd "$(dirname "$0")" && pwd)"
export DEBIAN_FRONTEND=noninteractive

echo "==> installing kamailio + rtpengine"
apt-get update -qq
apt-get install -y kamailio kamailio-extra-modules rtpengine

echo "==> configuring rtpengine (userspace relay)"
sed -e "s/__PUBLIC_IP__/${SIP_PUBLIC_IP}/g" "$SRC/rtpengine.conf" > /etc/rtpengine/rtpengine.conf
mkdir -p /etc/systemd/system/rtpengine-daemon.service.d
cp "$SRC/rtpengine-daemon.override.conf" /etc/systemd/system/rtpengine-daemon.service.d/override.conf
systemctl daemon-reload
systemctl enable --now rtpengine-daemon >/dev/null 2>&1 || true
systemctl restart rtpengine-daemon

echo "==> configuring kamailio"
sed -e "s/__PUBLIC_IP__/${SIP_PUBLIC_IP}/g" \
    -e "s/__HA1__/${HA1}/g" \
    -e "s/__USER__/${SIP_USER}/g" \
    -e "s/__PORT__/${SIP_PORT}/g" \
    "$SRC/kamailio.cfg" > /etc/kamailio/kamailio.cfg
sed -i \
    -e 's/^#\?RUN_KAMAILIO=.*/RUN_KAMAILIO=yes/' \
    -e 's/^#\?SHM_MEMORY=.*/SHM_MEMORY=32/' \
    -e 's/^#\?PKG_MEMORY=.*/PKG_MEMORY=8/' \
    /etc/default/kamailio
kamailio -c >/dev/null 2>&1 || { echo "kamailio -c FAILED:"; kamailio -c; exit 1; }
systemctl enable --now kamailio >/dev/null 2>&1 || true
systemctl restart kamailio

# Best-effort cleanup of a leftover kernel-module iptables hook (rtpengine is userspace here).
iptables -D INPUT -p udp -j rtpengine 2>/dev/null || true
iptables -X rtpengine 2>/dev/null || true
modprobe -r xt_RTPENGINE 2>/dev/null || true

echo
echo "=============================================================="
echo " SIP entry point ready"
echo "   server:        ${SIP_PUBLIC_IP}:${SIP_PORT}/udp (SIP signalling)"
echo "   media relay:   UDP 40000-41000 (rtpengine, userspace)"
echo "   AgentBridge:   Username=${SIP_USER}  Password=${SIP_SECRET}  RegisterExpiry=60"
echo "   smartphone:    proxy ${SIP_PUBLIC_IP}:${SIP_PORT} (UDP), ANY non-empty password,"
echo "                  then dial sip:${SIP_USER}@${SIP_PUBLIC_IP}"
echo "   REMEMBER:      open UDP ${SIP_PORT} and UDP 40000-41000 in the provider firewall"
echo "                  (cloud panel), or inbound UDP will be silently dropped"
echo "=============================================================="
