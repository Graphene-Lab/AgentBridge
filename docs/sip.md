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
   RTP in (G.711 8 kHz) ─► G.711 decode ─► PCM16 ─┴──► VAD ─► AIOffice.VoiceAgent --transcribe
                                                                          │ whisper
                                                                          ▼
                                                  SessionStore + AgentHarness.ExecuteAction
                                                                          │
   RTP out ◄── resample+encode ◄── PCM 24 kHz ◄── TtsEngine (Kokoro, in-process)
```

- **One call at a time.** A second incoming call gets `486 Busy Here`; `/sip call` while a
  call is active is refused.
- **Speech-to-text** runs as the `AIOffice.VoiceAgent` subprocess (`--transcribe <wav>`,
  whisper.net). While the agent's TTS is playing, the caller's capture is paused — a
  hands-free caller would otherwise echo the reply back and the VAD would transcribe our
  own answer into an endless loop.
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
| `AllowedCallers` | `[]` | Trusted caller URIs that skip the PIN in `allowlist` mode |
| `Agent` | `"default-agent"` | Agent set used for the conversation (`default`, `multi`, `word`, ...) |
| `Lang` | system language | Two-letter ISO language for STT/TTS and the announcements (Italian default when empty on an Italian machine) |
| `SttExePath` | `<server>\voiceagent-stt\` | Path to the `AIOffice.VoiceAgent` executable |
| `RtpPortRange` | `""` | Fixed RTP port range, e.g. `"40000-41000"` (firewalled deployments); empty = ephemeral ports |

## TUI commands

| Command | Meaning |
|---|---|
| `/sip status` | Live state: enabled/listening, registered, answer gate, call phase, PIN attempts left, lockout expiry, STT/TTS availability |
| `/sip call <sip-uri>` | Place an outgoing call (`sip:user@host`, or a bare number routed via the registrar) |
| `/sip answer on\|off` | Toggle the incoming-call auto-answer gate |
| `/sip hangup` | Hang up the active call |

The status bar shows a `sip:` segment (✓ idle, `ring`, `pin`, `call`) refreshed by a
3-second poll. The same operations are exposed over HTTP for scripts/clients:

| Endpoint | Body | Purpose |
|---|---|---|
| `GET /v1/sip/status` | — | Same state as `/sip status` |
| `POST /v1/sip/call` | `{"uri": "sip:user@host"}` | Outgoing call (blocks until answered/failed, ring timeout 60 s) |
| `POST /v1/sip/hangup` | `{}` | Hang up |
| `POST /v1/sip/answer` | `{"on": false}` | Toggle the answer gate |

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
     PIN on the keypad (DTMF, RFC 4733 — deterministic, no speech recognition).
4. **PIN validation** — each 5-digit (or PIN-length) burst is one attempt; overflow digits
   stay in the buffer for the next attempt. Wrong → "attempts left: N". After
   `MaxPinAttempts` wrong attempts **in total** (across calls — hanging up and redialing
   does not reset the counter) the server announces the lockout, hangs up and persists
   `locked_until` to `%LocalAppData%\agent\sipstate.json` (survives restarts). Only a
   correct PIN or the lockout expiry resets the counter; the digit buffer is cleared on
   every new call so a partially typed PIN never concatenates across calls.
5. **Conversation** — the caller's speech is transcribed per utterance (VAD: adaptive noise
   floor + 700 ms silence) and fed to `AgentHarness.ExecuteAction` with the configured
   agent set; the reply is chunked into sentences and spoken back. Replies are sanitized
   before synthesis (emoji/symbols stripped — the Kokoro phonemizer rejects them).

## Robustness

- **Self-healing user agent**: SIPSorcery's shared user agent occasionally fails to clear
  its internal dialog state after a hangup, which would silently drop every later INVITE.
  A watchdog (5 s) rebuilds it whenever the server and the agent disagree on the call state
  — both when the agent still believes a call is active without one, and when the server
  still holds an orphaned call the agent reports as inactive. The transport-level busy
  handler clears the same orphaned state (and rebuilds the agent) if an INVITE arrives
  before the watchdog fires. New calls are never lost for long.
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

## Known limitations

- Only G.711 (PCMU/PCMA) is offered — universal, but not Opus/G.722; the caller's endpoint
  must support at least one of them.
- One call at a time.
- No SRTP, no SIP-over-TLS, no attended transfer, no voicemail.
- While the agent's reply is spoken, the caller's speech is not captured (no barge-in).
- The PIN prompt is spoken; keypad confirmation beeps are not emitted.
