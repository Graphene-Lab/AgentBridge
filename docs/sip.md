# SIP telephony — AgentBridge as a phone endpoint

The server can act as a **SIP user agent**: incoming calls are auto-answered behind a
DTMF-PIN gate (or a trusted-caller allow-list), outgoing calls can be placed from the TUI,
and while a call is up the caller talks to the agents by voice — speech is recognized
(whisper), sent through the exact same `AgentHarness` path as the HTTP API, and the
agent replies are spoken back over the RTP audio with the in-process Kokoro TTS.

Implemented with [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) 10.0.15
(BSD 3-Clause): the SIP signalling (UAS/UAC, REGISTER, RFC 4733 DTMF) and the RTP transport
(G.711 codecs) are library-provided; the speech/agent loop is `SipBridge.cs`.

## Architecture

```
[Softphone / PSTN trunk] ⇄ SIP INVITE (UDP 5060) ⇄ SIPUserAgent ⇄ VoIPMediaSession
                                                  │
                 DTMF (RFC 4733) ─────────────────┤──► PIN gate (3 attempts, 24 h lockout)
                                                  │
   RTP in (G.711 8 kHz) ─► G.711 decode ─► PCM16 ─┴──► persistent voice subprocess
                                                              (AIOffice.VoiceAgent --pipe-audio:
                                                               VAD + whisper STT, Kokoro/SAPI TTS)
                                                                          │
                                                  SessionStore + AgentHarness.ExecuteAction
                                                                          │
   RTP out ◄── resample+encode ◄── PCM 24 kHz ◄── {"cmd":"speak","render":true} chunks
```

- **One call at a time.** A second incoming call gets `486 Busy Here`; `/sip call` while a
  call is active is refused.
- **Wideband-first codecs.** The media offers **Opus (RFC 7587, mono) → G.722 → PCMA → PCMU**
  so the STT hears real 16 kHz audio (50 Hz–7 kHz) instead of the upsampled narrowband G.711
  band. Whisper is trained on 16 kHz; G.711's 300 Hz–3.4 kHz band cuts the fricative energy
  (4–8 kHz) that matters for consonants — the same reason Google Cloud STT ships a dedicated
  `telephony` model for 8 kHz and rates 16 kHz as "optimal". G.722 (payload type 9) is the
  classic SIP wideband codec (16 kHz, 64 kbps — same bandwidth as G.711); Opus decodes to
  16 kHz mono. The entry point relays RTP transparently, so the codec is negotiated
  end-to-end with the softphone (enable G.722/Opus in the client's codec list); narrowband
  clients/trunks fall back to G.711 automatically. The in-band DTMF detector adapts to the
  negotiated sample rate (8 or 16 kHz).
- **Speech engines live in ONE subprocess** — architectural rule (see AIOrchestrator
  ARCHITECTURE.md "Media as I/O"): the media is only transport. `SipBridge` runs the
  cross-platform `AIOffice.VoiceAgent --pipe-audio` as a **persistent** process: decoded RTP
  PCM (16 kHz straight through for G.722/Opus; G.711 is upsampled 8→16 kHz) is pushed via
  `{"cmd":"audio"}`; **VAD + whisper stay in the
  subprocess** (no duplicated VAD in the bridge); replies are rendered with
  `{"cmd":"speak","render":true}` and the 24 kHz PCM chunks come back as `{"type":"audio"}`.
  The process lives for the whole server lifetime → the whisper model stays loaded
  (**persistent STT**: no per-utterance model-load cost). **Recognition is continuous**: the
  subprocess never stops/restarts the recognizer between turns (whisper.net has no streaming
  mode — the continuous part is the capture+VAD, which stays up); instead the caller's capture
  is **muted** while the agent's TTS plays (RTP packets are dropped — a hands-free caller would
  otherwise echo the reply back and the VAD would transcribe our own answer into an endless
  loop) and for **500 ms after** the last chunk, because the echo of the reply comes back
  delayed by network + phone latency. The VAD state ("speech"/"end") is streamed to the bridge
  as `{"type":"vad","state":…}` so it can time the processing indicator.
- The **conversation** is the SHARED agentic voice conversation (`AIOrchestrator
  ExecuteActionStream` with `isVoiceChat: true` — see `VoiceConversation.StreamToMediaAsync`):
  the same loop the AIOffice Voice panel uses, so the phone gets the same tools, the same
  concise "speakable" prompt, the same markdown/emoji stripping and sentence splitting. The
  only difference between Voice and SIP is the renderer (RTP TTS here, subprocess there) and
  the input path. The conversation session is created per call (`SessionStore.Create`) and
  disposed by the normal idle cleanup: the SIP path never touches the HTTP sessions, so a
  chat going on via the API cannot be polluted by phone calls (and vice versa).
- The **PIN gate** is the shared `PinAuthGate` (AIOrchestrator) — media-agnostic, wired here
  for DTMF; the lockout persistence (sipstate.json) stays in SipBridge.

## Configuration (`appsettings.json` → `Sip`)

Every key is overridable from the command line (`--Sip:Pin 12345`, ...).

| Key | Default | Description |
|---|---|---|
| `Enabled` | `false` | Master switch — the SIP channel binds only when true |
| `ListenPort` | `5060` | UDP port for SIP signalling |
| `Registrar` | `""` | Optional provider/trunk, e.g. `sip:provider.example:5060`. When set, the server REGISTERs with `Username`/`Password` and receives/places calls through the provider (see [Provider/NAT](#provider-trunk-and-nat)) |
| `Username` / `Password` | `""` | Credentials for REGISTER and authenticated calls |
| `AnswerMode` | `"pin"` | Incoming-call gate: `pin` (default), `allowlist` (P-Asserted-Identity), `none` |
| `Pin` | `""` | The 5-digit DTMF PIN (any length; DTMF validates when the buffer matches it) |
| `MaxPinAttempts` | `3` | Wrong-PIN attempts before the lockout — **cumulative across calls**: a redial does not reset the counter; only a correct PIN or the lockout expiry does |
| `LockoutHours` | `24` | Lockout duration after the attempts are exhausted |
| `RegisterExpiry` | `60` | REGISTER expiry/refresh interval in seconds. Keep it low (60) behind home NAT: consumer routers drop the NAT mapping long before the SIP default 300 s, and inbound calls between refreshes would go unanswered |
| `PinTimeoutSeconds` | `60` | Seconds to wait for the PIN before the server hangs up the call (min 10). A call left in the PIN gate with no digits ends itself with a spoken notice |
| `IndicatorDelaySeconds` | `2` | Seconds after the caller's speech ends before the looped "data processing" cue starts (the processing indicator). The cue is armed by the subprocess VAD `end` event (speech ended → STT/LLM processing began) — never by the transcript, which can arrive seconds later or not at all if background noise keeps the utterance open |
| `AllowedCallers` | `[]` | Trusted caller URIs that skip the PIN in `allowlist` mode |
| `Agent` | `"default-agent"` | Agent set used for the conversation (`default`, `multi`, `word`, ...) |
| `Lang` | system language | Two-letter ISO language for STT/TTS and the announcements (Italian default when empty on an Italian machine) |
| `SttExePath` | `<server>\voiceagent-stt\` | Path to the `AIOffice.VoiceAgent` executable |
| `SttModel` | `small` | Whisper model for the STT subprocess (`tiny/base/small/medium/largev2/largev3`). **`base` is the latency pick** — re-tested 2026-08-22 with the current pipeline (8→16 kHz upsample + adaptive VAD + multicore): it transcribes real Italian correctly and is ~2x faster than small-q8_0 (VadTest: 2.2–4.4 s vs 7.0–8.3 s whisper inference per 7.4 s utterance). `small` keeps an extra accuracy margin on very noisy lines. Never go below base — tiny is unusable |
| `SttQuant` | `q8_0` | Whisper quantization (`empty/q4_0/q4_1/q5_0/q5_1/q8_0`). `q8_0` is ~15% faster than FP16 with minimal accuracy loss — kept as the default; the bigger win is the **multicore** transcription (all logical processors; measured 8.3 s → 4.9 s per utterance on a 6c/12t CPU) |
| `SttDevice` | `auto` | STT accelerator passed to the voice subprocess as `AIOFFICE_WHISPER_DEVICE`: `auto` (default) lets the whisper.net loader probe Cuda → Cuda12 → Vulkan → CPU and **falls back to CPU automatically** when no usable GPU/driver is present; `cuda`/`vulkan` force that backend; `cpu` skips the probe (no one-time delay on machines whose GPU cannot run whisper, e.g. Turing sm_75). GPU is an acceleration, never a prerequisite — the distributed package works on every OS as-is (the CPU runtime is always bundled) |
| `RtpPortRange` | `""` | Fixed RTP port range, e.g. `"40000-41000"` (firewalled deployments); empty = ephemeral ports |

## TUI commands

| Command | Meaning |
|---|---|
| `/sip status` | Live state: enabled/listening, registered, answer gate, call phase, PIN attempts left, lockout expiry, STT/TTS availability |
| `/sip config` | Show the effective configuration (secrets masked) — keys match appsettings.json |
| `/sip config set <key> <value>` | Change one config key and persist it to appsettings.json (live-apply when possible; see [Configuring from the TUI](#configuring-from-the-tui)) |
| `/sip config reload` | Re-read the `Sip` section from appsettings.json (hand edits made outside the TUI) and apply it live |
| `/sip call <sip-uri>` | Place an outgoing call (`sip:user@host`, or a bare number routed via the registrar) |
| `/sip answer on\|off` | Toggle the incoming-call auto-answer gate |
| `/sip hangup` | Hang up the active call |

The status bar shows a `sip:` segment (✓ idle, `ring`, `pin`, `call`) refreshed by a
3-second poll. The same operations are exposed over HTTP for scripts/clients:

| Endpoint | Body | Purpose |
|---|---|---|
| `GET /v1/sip/status` | — | Same state as `/sip status` |
| `GET /v1/sip/config` | — | Same state as `/sip config` (secrets masked) |
| `POST /v1/sip/config` | `{"key": "Pin", "value": "12345"}` | Set one config key (persisted to appsettings.json) |
| `POST /v1/sip/config/reload` | `{}` | Re-read the `Sip` section from appsettings.json |
| `POST /v1/sip/call` | `{"uri": "sip:user@host"}` | Outgoing call (blocks until answered/failed, ring timeout 60 s) |
| `POST /v1/sip/hangup` | `{}` | Hang up |
| `POST /v1/sip/answer` | `{"on": false}` | Toggle the answer gate |

## Configuring from the TUI

The single source of truth is the `Sip` section of `appsettings.json` (a runtime file:
updates never overwrite it — see RELEASING.md). `/sip config` reads it through the server,
`/sip config set` writes it back, so TUI edits and manual JSON edits converge on the same
file:

```
/sip config                     → show the effective config (PIN/password masked)
/sip config set Pin 12345       → change + persist, live if possible
/sip config set AnswerMode none → change + persist
/sip config set AllowedCallers +393331234567,+390212345678   → comma-separated list
/sip config reload              → re-apply hand edits made directly in appsettings.json
```

- **Keys** are the appsettings.json property names, case-insensitive: `Enabled`,
  `ListenPort`, `Registrar`, `Username`, `Password`, `AnswerMode`, `Pin`,
  `MaxPinAttempts`, `LockoutHours`, `RegisterExpiry`, `PinTimeoutSeconds`,
  `IndicatorDelaySeconds`, `AllowedCallers`
  (comma-separated), `Agent`, `Lang`, `SttExePath`, `SttModel`, `SttQuant`, `SttDevice`,
  `RtpPortRange`. Booleans accept `true/false/1/on`.
- **Live vs restart.** PIN-policy keys (`Pin`, `MaxPinAttempts`, `LockoutHours`) and the
  per-call keys (`AnswerMode`, `AllowedCallers`, `Agent`, `Lang`, `SttExePath`) apply
  immediately to the next call. Transport-level keys (`Enabled`, `ListenPort`, `Registrar`,
  `Username`, `Password`, `RtpPortRange`, `RegisterExpiry`) restart the SIP channel
  automatically (a new REGISTER is sent, the UDP socket is re-bound); if a call is active
  the change is refused with "hang up first". A lockout in progress survives a PIN change
  (the 24 h counter is carried over); the wrong-attempt counter resets with a new PIN.
- **Secrets never leave the server**: `/sip config` reports only whether `Pin`/`Password`
  are set; `set` never echoes them back in the response message.

## Incoming call flow

1. INVITE → if another call is active, the server answers `486 Busy Here` at the transport
   level (the shared user agent alone would silently drop the INVITE); if the answer gate is
   off the same rejection is sent.
2. **Locked out** (attempts exhausted in the last 24 h) → the call is answered, a spoken
   notice explains the lockout, then the server hangs up.
3. Gate check:
   - `AnswerMode: "none"` → straight to the conversation.
   - `AnswerMode: "allowlist"` → the P-Asserted-Identity (set by an **authenticating**
     provider/trunk) is matched against `AllowedCallers` (full URI or user part). PAI is
     honored only when the INVITE actually comes from the configured `Registrar` host — in
     direct SIP it is ignored (spoofable). A match skips the PIN; otherwise it falls back
     to the PIN when one is configured. A startup warning is logged when `allowlist` is
     used without a registrar.
   - `AnswerMode: "pin"` (default) → the caller hears a welcome prompt and must type the
     PIN on the keypad (DTMF accepted from any transport — see step 4).
4. **PIN validation** — the keypad digits are accepted from **any transport**: RFC 4733 RTP
   events (the standard), SIP INFO bodies (`application/dtmf-relay` — handled at the
   transport level and routed by the entry point) and **in-band tones** (Goertzel detection,
   active only during the PIN phase — once the conversation starts, speech is never misread
   as digits). Each PIN-length burst is one attempt; overflow digits stay in the buffer for
   the next attempt. Wrong → "attempts left: N". After `MaxPinAttempts` wrong attempts
   **in total** (across calls — hanging up and redialing does not reset the counter) the
   server announces the lockout, hangs up and persists `locked_until` to
   `%LocalAppData%\agent\sipstate.json` (survives restarts). Only a correct PIN or the
   lockout expiry resets the counter; the digit buffer is cleared on every new call so a
   partially typed PIN never concatenates across calls. If no digit arrives within
   `PinTimeoutSeconds`, the call ends with a spoken notice.
5. **Conversation** — the caller's speech is transcribed per utterance (VAD: adaptive noise
   floor + 500 ms silence) and fed to `AgentHarness.ExecuteAction` with the configured
   agent set; the reply is chunked into sentences and spoken back. Replies are sanitized
   before synthesis (emoji/symbols stripped — the Kokoro phonemizer rejects them).
   **Robustness to background noise**: the decoded G.711 stream passes through an 80 Hz
   high-pass filter (removes hum/rumble) before the VAD; the audio is upsampled 8→16 kHz
   before transcription (whisper is trained on 16 kHz); and transcripts consisting ONLY of
   non-speech placeholders (`[Musica]`, `[Rumore]`, ...) are dropped — background music is
   never sent to the LLM, so the agent cannot "answer the music".
6. **Processing indicator** — while the agent computes (STT/LLM/tools, which can take several
   seconds), the caller would hear silence and wonder if the line dropped. So `IndicatorDelaySeconds`
   (default 2 s) after the caller's speech ENDS, a looped "data processing" cue
   (`assets/processing-indicator.wav`, 10 s, 24 kHz mono — trimmed from a freesound preview via
   `e2e/Mp3ToWav`) is sent to the caller over RTP, repeating until the first reply chunk arrives.
   The cue is **armed by the subprocess VAD `end` event** (speech closed → transcription began),
   not by the transcript: whisper can take seconds to return, and with background noise the
   utterance may never close at all — arming on the VAD guarantees the caller always gets feedback
   the moment processing starts. While the caller speaks again (VAD `speech`), the cue is paused
   so it never beeps over their own voice; a 25 s hard cap stops it if STT/LLM stalls. The cue is
   sent through the SAME media path as the replies (never played locally); the TTS queue tags the
   cue pieces and discards them the moment the real reply starts (400 ms pieces → the reply is
   delayed by at most ~400 ms). The asset is normalized to near-full-scale before playback (RTP
   has no volume knob — the PCM amplitude IS the volume); it ships with the server, and if missing
   the indicator is simply skipped.

## Robustness

- **Self-healing user agent**: SIPSorcery's shared user agent occasionally fails to clear
  its internal dialog state after a hangup, which would silently drop every later INVITE.
  A watchdog (5 s) rebuilds it whenever the server and the agent disagree on the call state
  — both when the agent still believes a call is active without one, and when the server
  still holds an orphaned call the agent reports as inactive. New calls are never lost for
  long. The orphan-clear only fires once the call is fully established (`MediaAttached`):
  between "Call registered" and "Answer/Attach complete" a fresh call has no dialog yet and
  must never be misread as an orphan — clearing it mid-setup would make the DTMF PIN gate go
  dead (reproduced by an immediate call colliding with the 5 s tick). The same guard skips
  the rebuild while an INVITE is being set up (the call gate is held for the whole setup).
- **Serialized TTS rendering**: the voice subprocess renders ONE speak at a time. A new speak
  can start while a previous one is still rendering (the PIN accepted while the welcome is
  still streaming, a lockout notice landing mid-announcement, an agent-initiative turn racing
  the reply loop). The newcomer WAITS for the in-flight render instead of failing: an
  exception there would fault a fire-and-forget task and silently kill the conversation
  (phase stays Conversation, `StartConversation` never runs, transcripts are dropped — the
  call goes dead with no reply and no processing indicator). The slot is cleared only by its
  owner (`ReferenceEquals`), so a newer speak can never lose its "done" resolution.
- **In-call DTMF** (RFC 4733 events) is filtered out of the speech path — keypad tones never
  reach the STT.
- **Hold re-INVITE** from the remote party is answered by the library and the call survives.

## Identity and security

- The SIP `From` header is **spoofable** by any client — it is never trusted alone.
- `P-Asserted-Identity` (RFC 3325) is trusted only when the INVITE comes from the
  configured `Registrar` host — a SIP domain that authenticated the caller before
  forwarding (provider/trunk). That is the only setup where the allow-list can replace the
  PIN with certainty; direct SIP ignores PAI (the code never trusts it from an arbitrary
  source address).
- Default configuration is `pin` mode: safe even when the server is dialled directly by IP.
- RTP/DTMF travel in clear text unless SRTP is negotiated; the lockout bounds brute force
  anyway. TLS for the signalling (provider requirement) is not implemented yet.

## Provider/trunk and NAT

- **Direct dial (public IP / LAN)**: bind `ListenPort` (e.g. 5060) and open the RTP range
  (`RtpPortRange`, e.g. 40000-41000 UDP) on the firewall. Softphones call
  `sip:agent@<host>`.
- **Via a provider/trunk (DID/PSTN or a VoIP trunk with authentication)**: set `Registrar`,
  `Username`, `Password`. The server REGISTERs and the provider routes calls to it — no
  public SIP port needed; set `AnswerMode: "allowlist"` with your own numbers to skip the
  PIN (PAID from the trunk is reliable).

## Minimal SIP entry point (smartphone → AgentBridge)

When AgentBridge lives behind NAT (home PC, no router access) and the smartphone is on
mobile data, a tiny VPS can act as the **SIP entry point**: a pure relay that **only
smista** signalling and RTP — **no codec handling, no transcoding, no voicemail** — so a
low-end box (~256 MB) is enough. Both sides are *clients* of the entry point: AgentBridge
REGISTERs to it (outbound — no inbound port needed on the home router), the smartphone
dials it, and the entry point routes the call to AgentBridge's registration. RTP is
force-relayed through **rtpengine**, so neither side ever needs to reach the other directly.

```
A = AgentBridge (PC, NAT)   B = 195.20.235.5 (entry point)   C = smartphone (mobile NAT)
        A ──REGISTER──► B ◄──INVITE── C          (signalling)
        A ────RTP──────► B ◄────RTP─── C          (media, relayed, no codecs)
        A ◄──────────── B ──────────► C
```

This is also the production architecture: AgentBridge behaves like a normal SIP client —
it initiates the connection and stays registered, so no firewall changes are ever needed
at its location.

> **Full guide + unattended installer**: see `docs/sip-entry/README.md` (step-by-step) and
> `docs/sip-entry/setup-entrypoint.sh` — one command installs Kamailio + rtpengine, writes
> the configs, generates the shared secret and prints the AgentBridge/phone settings.
> Interactive config generators for the AgentBridge side (same keys as `/sip config`):
> `scripts/sip-config.bat` / `sip-config.sh`.

**Software** (Debian/Ubuntu, on the entry point) — installs `kamailio kamailio-extra-modules
rtpengine` (rtpengine, not rtpproxy: Debian 12 has no rtpproxy package) and deploys the
configs, all in one command (the script in `docs/sip-entry/`, auto-generated credentials):

```bash
sudo bash docs/sip-entry/setup-entrypoint.sh
```

`kamailio-extra-modules` provides the `rtpengine`/`nathelper` modules. `rtpengine` runs as a
daemon (`systemctl enable --now rtpengine-daemon`).

The authoritative Kamailio config (digest-auth REGISTER for the AgentBridge AOR, any-user
200-OK registration for the phone, rtpengine relay, NAT handling) lives in
**`docs/sip-entry/kamailio.cfg`** and is deployed by `setup-entrypoint.sh` — use those
files, not a pasted copy.

**AgentBridge side** (on the home PC) — the server becomes a client of the entry point:

```
/sip config set Enabled true
/sip config set Registrar sip:195.20.235.5:5060
/sip config set Username agent
/sip config set Password <shared secret>      # must match the HA1 on the server
/sip config set ListenPort 6070               # non-standard port: many home ISPs (e.g. Italian
                                              # lines) silently drop INBOUND UDP on 5060 — the
                                              # REGISTER replies would never reach the agent
/sip config set RegisterExpiry 60             # keep the home-NAT mapping alive (see below)
/sip config set Pin 12345
/sip config set Lang it
```

Each `set` persists to `appsettings.json` and the transport restarts automatically (the
REGISTER goes out with the new credentials). `/sip status` shows `registered on` when the
entry point accepted it. **ListenPort matters**: the ISP/modem often blocks inbound UDP on
the standard SIP port 5060 while allowing it on any other port — if the registration never
completes (the agent keeps retransmitting REGISTER without ever receiving the 401/200),
move the local listener to a non-standard port (e.g. 6070) with `/sip config set ListenPort
6070`. The entry point routes replies to the port the REGISTER came from, so no change is
needed on the server side.

**Firewall** on the entry point: open `5060/udp` (SIP) and `40000-41000/udp` (RTP relay)
to the internet. On Debian 12 the relay is **rtpengine** (no `rtpproxy` package exists):
userspace-only (`table = -1`), control socket `udp:127.0.0.1:2223`, media ports
40000-41000 (see `docs/sip-entry/rtpengine.conf`). AgentBridge and the phone need no open
ports at all.

**Verification** from the entry point (needs the `ctl` module loaded — see
`docs/sip-entry/kamailio.cfg`; without it `kamcmd` is unavailable and the registration can
be confirmed from the agent side with `/sip status`):

```bash
sudo kamcmd usrloc.dump | grep -A3 agent   # AgentBridge's registration present
sudo kamcmd core.uptime
```

Then dial `sip:agent@195.20.235.5` from the phone (Linphone: account with the proxy set
to `195.20.235.5`, transport UDP, no auth for calls) — the PIN prompt is spoken, DTMF
keys validate (`/sip status` on the PC shows the phase: `pin` → `call`).

**Variant — static forwarding** (only when AgentBridge has a reachable IP, e.g. a public
VPS): skip the `usrloc`/`registrar` modules and forward every INVITE with
`$du = "sip:<agentbridge-ip>:5060"` instead of `lookup("location")`. No registration
needed on the AgentBridge side.

## Deploying the speech-to-text executable

`voiceagent-stt/` must contain the **cross-platform** `AIOffice.VoiceAgent` executable
(whisper-based — not the Windows microphone one). The whisper model (~75-490 MB depending
on `AIOFFICE_WHISPER_MODEL`) downloads automatically on first use.

- **Windows (dev)**: the build copies it automatically from the sibling
  `AIOffice.VoiceAgent` repo into `voiceagent-stt/`.
- **Linux/macOS (server)**: build/publish `AIOffice.VoiceAgent` for the target RID and copy
  the output folder to `voiceagent-stt/` next to `agent`. `GET /v1/sip/status` reports
  `stt_available`/`tts_available` so a missing executable is visible immediately.
- The server falls back to a plain text-only loop if STT or TTS is unavailable (the SIP
  signalling and PIN gate keep working).

## Testing

`e2e/SipSmoke` is a loopback end-to-end test: it launches the real `agent.exe` with SIP
enabled and acts as a SIPSorcery softphone. It covers the PIN gate (correct PIN,
partial-PIN buffer reset across calls, wrong-then-correct recovery, overflow digits,
attempts cumulative across calls, empty-PIN config), DTMF isolation, hold re-INVITE, busy
rejection (486), the answer-gate toggle, outbound calls (rejected and answering remotes,
`/v1/sip/hangup`), invalid destinations, the 24 h lockout (arming, locked-notice, restart
persistence, expiry), the allow-list, answer mode `none`, and the full voice loop (RTP
speech → whisper → agent → TTS → RTP, twice):

```bash
dotnet run --project e2e\SipSmoke -- path\to\agent.exe http://localhost:5390 5070
dotnet run --project e2e\SipSmoke -- --skip-voice path\to\agent.exe ...   # no LLM needed
dotnet run --project e2e\SipSmoke -- --voice-only path\to\agent.exe ...  # media-path iteration: test 18 only
```

Test 18 (the voice loop) requires the default LLM provider to be reachable and is skipped
with `--skip-voice`; the LLM bridge can stall or produce very long replies, so the voice
loop runs last — it never contaminates the other checks. `--voice-only` skips straight to
it (with the lockout state pre-expired) for fast iteration on the media path.

**`e2e/SipClientTest`** is a standalone softphone for testing a REMOTE chain (entry point +
AgentBridge): it dials `sip:agent@<host>`, answers the spoken PIN prompt with DTMF, speaks
a bundled Italian TTS greeting and captures the agent's reply (saved as `reply.wav`):

```bash
dotnet run --project e2e\SipClientTest                 # defaults: sip:agent@195.20.235.5, pin 12345
dotnet run --project e2e\SipClientTest -- sip:agent@host 12345 5071 45 --pin-wait=10
```

## Known limitations

- Codecs: Opus / G.722 / G.711 (PCMU/PCMA) are offered (wideband first); no G.729, no SRTP
  variants, and the caller's endpoint must support at least one of the four.
- One call at a time.
- No SRTP, no SIP-over-TLS, no attended transfer, no voicemail.
- While the agent's reply is spoken, the caller's speech is not captured (no barge-in).
- The PIN prompt is spoken; keypad confirmation beeps are not emitted.
- **GPU acceleration is opt-in**: build the STT subprocess with `-p:WhisperGpu=true` to bundle
  the whisper.cpp CUDA12/Vulkan runtimes (Vulkan is Windows-x64 only; on Linux the only GPU
  backend is CUDA). The default CPU-only build works on every OS with zero prerequisites;
  a GPU-enabled build still works on GPU-less machines (the whisper.net loader probes and
  falls back to CPU automatically). Tested 2026-08-23 on a GTX 1650 Ti (Turing sm_75): CUDA
  fails to load (as documented) and Vulkan loads but gives no speedup over CPU — the switch
  `SttDevice=cpu` skips the probe entirely.
