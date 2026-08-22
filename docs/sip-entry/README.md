# Minimal SIP entry point — guide

Turn a low-end Linux VPS into a **pure SIP relay** between AgentBridge and a smartphone.
The box does **no codec handling, no transcoding, no voicemail** — it only forwards SIP
signalling (Kamailio) and relays RTP (rtpengine, userspace). Both AgentBridge and the
phone act as *clients* of this box, so neither needs open ports at its own location.

```
A = AgentBridge (home PC, NAT)   B = this server   C = smartphone (mobile NAT)
        A ──REGISTER──► B ◄──INVITE── C          signalling (UDP 5060)
        A ────RTP──────► B ◄────RTP─── C          media relay (UDP 40000-41000, no codecs)
```

## Requirements

- Debian 12 / Ubuntu (bookworm+) VPS with a **public IP**, root access, ~256 MB RAM is enough.
- Access to the **provider's firewall panel** (cloud panel, e.g. IONOS): you must open
  `UDP 5060` (SIP) and `UDP 40000-41000` (RTP relay) — without this, inbound UDP is
  silently dropped and nothing works. This is the #1 pitfall.
- The AgentBridge side needs: `Registrar sip:<this-host>:5060`, `Username`, shared
  `Password`, and a non-standard `ListenPort` **if the AgentBridge ISP drops inbound UDP
  on 5060** (common on home lines — see troubleshooting).

## Quick start (unattended)

Copy the whole folder to the server and run:

```bash
sudo bash setup-entrypoint.sh
```

The script installs `kamailio kamailio-extra-modules rtpengine`, writes
`/etc/kamailio/kamailio.cfg`, `/etc/rtpengine/rtpengine.conf`, enables the services,
auto-generates the shared secret and prints everything you need:

```text
SIP entry point ready
  server:        195.20.235.5:5060/udp (SIP signalling)
  media relay:   UDP 40000-41000 (rtpengine, userspace)
  AgentBridge:   Username=agent  Password=<printed>  RegisterExpiry=60
  smartphone:    proxy 195.20.235.5:5060 (UDP), ANY non-empty password, dial sip:agent@195.20.235.5
  REMEMBER:      open UDP 5060 and UDP 40000-41000 in the provider firewall
```

Customize with environment variables (all optional):

```bash
SIP_SECRET=mysecret SIP_USER=agent SIP_PUBLIC_IP=1.2.3.4 SIP_PORT=5060 sudo bash setup-entrypoint.sh
```

## What the script does, step by step

1. `apt install kamailio kamailio-extra-modules rtpengine`
   (`kamailio-extra-modules` provides the `rtpengine` module; on Debian 12 there is **no
   `rtpproxy` package** — rtpengine is the equivalent userspace media relay).
2. **rtpengine** → `/etc/rtpengine/rtpengine.conf`: userspace-only (`table = -1`), media
   ports `40000-41000`, control socket `udp:127.0.0.1:2223`. A systemd drop-in empties the
   stock `ExecStartPre`/`ExecStartPost` (they call `rtpengine-iptables-setup`, part of a
   package we do not need in userspace mode).
3. **Kamailio** → `/etc/kamailio/kamailio.cfg` (template in this folder):
   - `REGISTER` for the AgentBridge AOR (`__USER__`) is protected by **digest auth**
     (shared secret, HA1 precomputed, `pv_auth_check` + `www_challenge` 401 flow).
   - `REGISTER` for **any other user** (the phone) is answered `200 OK` but **not stored**
     — the phone registers happily but can never hijack the AgentBridge AOR.
   - Every `INVITE` is `rtpengine_offer/answer("relay")` (media forced through the relay)
     and routed via `lookup("location")` to the AgentBridge registration. **NAT media
     handling**: rtpengine's **endpoint learning must stay ON** (the default) — the relay
     learns each leg's real NAT-mapped address from the first RTP packets and forwards
     there. Do NOT set `endpoint-learning = off`, and do NOT rely on `fix_nated_sdp()`:
     in Kamailio 5.6 it needs specific flags and empirically did not rewrite the private
     SDP addresses, so with learning off the relay forwards to the unreachable private IPs
     and the media dies (this bit us live — welcome message not heard, no DTMF feedback).
   - `nat_bflag 6` + `received_avp`/`fix_nated_register()`: registrations behind NAT keep
     the real source address so replies and INVITEs are routed back through the NAT.
   - `DTMF INFO` (RFC 2976) for the AgentBridge AOR is forwarded straight to the
     registration before the generic dialog routing (some clients build an incomplete Route
     set and `loose_route` would drop them with 404) — the PIN gate is the real security.
   - `listen=udp:0.0.0.0:<port> advertise <public-ip>:<port>`: Via/Contact/Record-Route must
     carry the public IP, never the 0.0.0.0 bind address (the global `advertised_address`
     core parameter does NOT affect `record_route()`).
4. **Firewall**: the script cleans a leftover kernel-module iptables hook. The actual
   UDP opening must be done in the **provider panel** (cloud firewalls sit outside the VM).

## Configure AgentBridge (the A side)

On the machine running AgentBridge, from the TUI:

```
/sip config set Enabled true
/sip config set Registrar sip:<this-host>:5060
/sip config set Username agent
/sip config set Password <shared secret from the script>
/sip config set ListenPort 6070        # only if the home ISP blocks inbound UDP 5060
/sip config set RegisterExpiry 60      # keep NAT mappings alive on consumer routers
/sip config set Pin 12345
/sip config set Lang it
```

`/sip status` shows `registered on` once the entry point accepted the REGISTER. Every
`set` persists to `appsettings.json` and restarts the SIP transport automatically.
(There are also non-interactive config scripts: `AgentBridge/scripts/sip-config.bat` and
`sip-config.sh` — same result without the TUI.)

## Configure the smartphone (the C side)

Any standard SIP client (Linphone, Zoiper, …):
- Account: username anything (e.g. `phone`), **any non-empty password**, domain/proxy
  `<this-host>` port `5060`, transport **UDP**.
- Dial: `sip:<user>@<this-host>` → spoken welcome → type the PIN → conversation.

## Testing the full chain

`AgentBridge/e2e/SipClientTest` is a console softphone that dials, answers the PIN with
DTMF, speaks an Italian TTS greeting and captures the agent's reply:

```bash
dotnet run --project e2e\SipClientTest            # Windows, defaults to sip:agent@<host>
dotnet run --project e2e/SipClientTest -- sip:agent@<host> 12345 5071 45
```

Success looks like: `answered: Ok`, PIN sent, greeting spoken, and the agent log shows
`SIP caller said: Ciao, mi senti?` → `SIP agent replied: …` (the agent speaks the reply
back over RTP).

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| REGISTER reaches the server but never completes (client retransmits) | **Provider firewall** not opened (UDP 5060), or the **home ISP drops inbound UDP 5060** → move AgentBridge to a non-standard `ListenPort` (e.g. 6070) |
| Calls fail intermittently (408) between successful ones | Home **NAT mapping times out** before the REGISTER refresh → set `RegisterExpiry 60` (the mapping is re-created every minute) |
| Phone registers but the call goes nowhere | The phone is registering with the AgentBridge AOR user — only `sip:<user>@…` routes to the agent; other registrations are acknowledged but not stored |
| Call connects, no audio | RTP range `40000-41000` blocked in the provider firewall; or a client-side firewall drops the relayed media; or `endpoint-learning = off` was set on rtpengine (relay forwards to the private SDP IPs) → keep it ON |
| No welcome message heard, no DTMF feedback | Same media-path cause as above (verify with `e2e/SipClientTest` — it must receive the agent's speech over RTP) |
| `kamcmd` unavailable on the server | The `ctl` module was added to the template after the first deploy — rerun the script or add `loadmodule "ctl.so"` + `modparam("ctl","binrpc","unix:/run/kamailio/kamailio.sock")` and restart |
