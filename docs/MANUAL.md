# AgentBridge — user manual

A step-by-step guide for running AgentBridge: install it, configure the JSON files, use
the terminal UI, and connect a client to the local server.

- [1. Install](#1-install)
- [2. Start the server](#2-start-the-server)
- [3. Configure the JSON files](#3-configure-the-json-files)
- [4. Use the terminal UI (GUI from the console)](#4-use-the-terminal-ui-gui-from-the-console)
- [5. Features you can activate from the UI](#5-features-you-can-activate-from-the-ui)
- [6. Connect a client to localhost](#6-connect-a-client-to-localhost)
- [7. Where everything lives](#7-where-everything-lives)

---

## 1. Install

**One-line install** (downloads the latest release for your platform and extracts it into
`~/.agentbridge` / `%LOCALAPPDATA%\AgentBridge`):

- Windows (PowerShell): `irm https://graphenelab.it/AgentBridge/install.ps1 | iex`
- Linux / macOS: `curl -fsSL https://graphenelab.it/AgentBridge/install.sh | bash`

**Prebuilt executables.** Alternatively, download the archive for your platform from the
[Releases page](https://github.com/Graphene-Lab/AgentBridge/releases) — the
[auto-detect page](https://graphenelab.it/AgentBridge/download/) picks the right one
for your OS:

| Platform | Archive | Executable |
|---|---|---|
| Windows 64-bit | `agentbridge-win-x64.tar.gz` | `agent.exe` |
| Linux 64-bit | `agentbridge-linux-x64.tar.gz` | `agent` |
| Linux ARM64 (Raspberry Pi, etc.) | `agentbridge-linux-arm64.tar.gz` | `agent` |
| macOS Intel | `agentbridge-osx-x64.tar.gz` | `agent` |
| macOS Apple Silicon | `agentbridge-osx-arm64.tar.gz` | `agent` |

Extract the archive into a folder of your choice. No .NET installation is required
(self-contained single file), and the archive already includes the Kokoro TTS voices and
model (`voices/`, `kokoro.onnx`) — text-to-speech works out of the box.

On Linux/macOS, make the executable runnable:

```bash
chmod +x agent
```

**From source (developers):**

```bash
cd AgentBridge
dotnet run --project AgentBridge.csproj
```

---

## 2. Start the server

Run the executable. The console opens the **terminal UI** and the server listens on
`http://localhost:5290` in the same process.

| Mode | Command | When to use |
|---|---|---|
| Terminal UI (default) | `agent` | interactive use — chat, voice, files |
| Server only | `agent --headless` | scripts, CI, running as a service |
| Force UI | `agent --tui` | when the console is not detected as interactive |

```bash
curl http://localhost:5290/health   # {"status":"healthy","timestamp":"..."}
```

If a server is already running on the port, the UI connects to that instance instead of
failing — handy to attach a UI to a running service.

> **First start:** the server indexes the documents folder at startup (can take minutes on
> large folders). If you do not need document search, start with
> `agent --SkipIndexingOnStartup true`.

---

## 3. Configure the JSON files

Two JSON files control the server. Both live next to the executable.

### `appsettings.json` — server and default LLM

```json
{
  "Logging": { ... },
  "AllowedHosts": "*",
  "Urls": "http://localhost:5290",
  "SkipIndexingOnStartup": false,
  "LLM": {
    "Provider": "DeepSeekBridge",
    "Anonymize": false
  },
  "Voice": {
    "ExePath": ""
  },
  "Sip": {
    "Enabled": false,
    "ListenPort": 5060,
    "Registrar": "",
    "Username": "",
    "Password": "",
    "AnswerMode": "pin",
    "Pin": "12345",
    "MaxPinAttempts": 3,
    "LockoutHours": 24,
    "AllowedCallers": [],
    "Agent": "default-agent",
    "Lang": "",
    "SttExePath": "",
    "RtpPortRange": ""
  }
}
```

| Key | Values | Description |
|---|---|---|
| `Urls` | e.g. `http://localhost:5290` | Address the server listens on (see [Connect a client](#6-connect-a-client-to-localhost)) |
| `SkipIndexingOnStartup` | `true` / `false` | Skip the documents index build/refresh + file watcher at startup |
| `LLM:Provider` | `Ollama_Granite3b`, `DeepSeek`, `DeepSeekBridge`, `Zai`, `Gemini`, `ExllamaV2_Llama3b`, ... | **Default** LLM provider for the orchestrator; you can still switch it per session/request |
| `LLM:Anonymize` | `true` / `false` | Name/key anonymization |
| `Voice:ExePath` | path | Path to `AIOffice.VoiceAgent.Win.exe` for `POST /v1/voice/listen`. Empty = look next to the executable |
| `Sip:Enabled` | `true` / `false` | **SIP telephony master switch** — see [SIP telephony](#9-sip-telephony) |

Every key is overridable from the command line, e.g.:

```bash
agent --LLM:Provider Zai --SkipIndexingOnStartup true --Sip:Enabled true
```

Run `agent --help` for the full list of overrides.

### `providers.json` — the LLM providers

This file defines every LLM provider the server can talk to. It is copied next to the
executable at build time, and the server falls back to an embedded factory default if the
file is missing or corrupt. You can edit it freely (add a provider, change a model, point
at a local server); it is reloaded when the configuration changes.

```json
{
  "ProviderName": "Ollama_Granite3b",
  "Protocol": "OpenAI",
  "CacheType": "PrefixCache",
  "ModelName": "granite4.1:3b",
  "BaseAddress": "http://localhost:11434/",
  "EndPoint": "v1/chat/completions",
  "Timeout": "00:40:00",
  "PauseBetweenRequests": "00:00:00",
  "ContextWindow": 32000
}
```

| Key | Description |
|---|---|
| `ProviderName` | The name used in `LLM:Provider`, `/model`, and the API `model` field |
| `Protocol` | `OpenAI` (chat/completions), `Gemini` (generateContent), `Anthropic` (Messages API) |
| `CacheType` | `PrefixCache` (default) / `AnthropicCache` / `noCacheSupported` — how the provider caches the prompt prefix |
| `ModelName` | The model name sent to the provider |
| `BaseAddress` | Provider base URL — use `http://localhost:11434/` for Ollama, `http://127.0.0.1:5000/` for ExLlamaV2, the public URLs for DeepSeek/Z.ai/Gemini |
| `EndPoint` | The API path relative to `BaseAddress` |
| `Timeout` | Request timeout in .NET `TimeSpan` format, e.g. `"00:05:00"` = 5 minutes |
| `PauseBetweenRequests` | Pause between requests (rate limiting), same format |
| `ContextWindow` | Token window of the model — used by the context-window guard when switching |
| `AgentInteractionMode` | Optional `API` / `CLI` / `Default`. How the agent tools are exposed: `API` = one JSON tool per method; `CLI` = the agent drives the application terminal with `ClassName subcommand args`; `Default` (omitted) = CLI for small models (context window < 128 000 tokens), API for large ones |
| `ApiKey` | API key of this provider — empty for local providers (loopback endpoint). Set it here or via the `/modelsetup` provider dialog (masked) / AIOffice Settings panel |

> **Example — add an Anthropic provider:** copy the commented block at the top of
> `providers.json`, set `Protocol` to `Anthropic`, `CacheType` to `AnthropicCache`, and put
> its API key in the `ApiKey` field of the entry (or set it via the UI provider dialog).
> Then `agent --LLM:Provider Anthropic` or `/model Anthropic` in the UI.

#### How API keys work

- **One key per provider, stored in `providers.json`** — the `ApiKey` field of the
  provider's entry is the single source of truth. Set it via the `/modelsetup` **Edit**
  dialog (the field is masked while typing) or directly in the file.
- **Local providers need no key**: any provider whose `BaseAddress` points at loopback
  (`localhost` / `127.0.0.1` — Ollama, ExLlamaV2, the DeepSeekBridge) is treated as keyless
  regardless of name.
- Cloud keys are sent as `Authorization: Bearer` (OpenAI/Anthropic protocols) or in the
  query string (Gemini protocol).
- `providers.json` is excluded from updates, so configured keys survive every update.
- The same Z.ai key also enables image OCR: the attachment pipeline converts images via
  Z.ai GLM-OCR using the `Zai` provider's key. Without it, images are simply skipped.
- Legacy note: keys set through the older per-provider `Setup` properties (e.g.
  `%LocalAppData%\agent\setup.json`) still work as a fallback until a key is set on the
  provider itself.

---

## 4. Use the terminal UI (GUI from the console)

The default launch opens a full-screen chat in your console: menu bar, AGENT logo, a
streaming chat panel, an input line at the bottom and a status bar showing server,
provider, model, session and context usage.

**The two "magic" keys:**

| You type | What happens |
|---|---|
| a plain message + `Enter` | the agents reply, streaming into the conversation |
| `/` | command palette — filters as you type, `Tab` completes, `Enter` runs |
| `@` | file palette — toggle which uploaded file is attached to the chat |
| `?` | shortcuts overlay (empty input) |
| `F1` | full help page |

**Type `/help` inside the UI for the complete, always-up-to-date command list.** The key
concepts:

- **Commands** — everything is a command: `/model`, `/agent`, `/voice`, `/tts`,
  `/files`, `/new`, `/status`, ... Type `/` to see them all.
- **Streaming** — replies appear as they are generated. The conversation auto-follows
  while you are at the bottom; scrolling up pauses the follow, scrolling down resumes it.
- **History** — `Up`/`Down` for previous prompts, `Ctrl+R` for reverse-search.
- **Mouse** — menus, dialogs and lists are clickable; `Esc` always cancels a dialog.

See [docs/TUI.md](TUI.md) for the full reference (every command, shortcut and mouse
action).

---

## 5. Features you can activate from the UI

Everything below is available from the terminal UI (and most of it also via the API —
see [section 6](#6-connect-a-client-to-localhost)):

| Command | Feature | Notes |
|---|---|---|
| `/model [name]` | **Switch the LLM provider** | menu when no name given; a context-window guard refuses a switch that would overflow the target model's window |
| `/agent [name]` | **Switch the agent set** | `default` / `web` / `search` / `research` / `word` / `spreadsheet` / `email` / `multi` — different tool sets |
| `/voice [lang]` | **Voice dictation** | dictates from the server microphone into the input (Windows) |
| `/tts [text]` | **Text-to-speech** | speaks the last agent reply (or the given text) with Kokoro TTS; WAV playback |
| `/sip status\|call\|answer\|hangup` | **SIP telephony** | phone-gate the agent: status, outgoing call, auto-answer on/off, hangup (see [section 7](#7-sip-telephony)) |
| `/features [name] [on\|off]` | **Toggle session feature flags** | e.g. `voice`, `tts` — enable/disable per session |
| `/files add <path>` · `/files rm <id>` · `/files` | **File upload/management** | upload+attach a file, delete one, list uploads |
| `/attach [id]` | **Attach a file to the chat** | menu when no id |
| `/new` · `/reset` | **Start a fresh conversation** | new session |
| `/clear` | **Reset the session history** | keeps the session |
| `/status` | **Session state + capabilities** | what is available here and now |
| `/health` | **Server health + latency** | ping the server |
| `/retry` | **Resend the last prompt** | also `Ctrl+Y` |
| `/docs` | **Open the online docs** | in the browser |
| `/web` | **Launch the web GUI (Giraffe AI)** | installs the client on first run and auto-connects it to this server (see [section 6](#6-connect-a-client-to-localhost)) |
| `/modelsetup` | **Configure models & providers** | add/edit/remove providers (including the per-provider API key), active model, email (SMTP), mail reading (IMAP), logging, documents path |
| `/exit` · `/quit` | **Exit** | also `Ctrl+C` twice, or `Ctrl+D` |

> Platform-dependent features are honest: if the platform or the assets are missing, the
> server reports them unavailable (the UI shows it, the API returns 501 and
> `GET /v1/control` lists exactly what is available).

---

## 6. Connect a client to localhost

AgentBridge speaks **OpenAI Chat Completions** plus small documented extensions. Any
OpenAI-compatible client — script, SDK, bot, app — can drive the agents by pointing its
base URL at the server.

**Base URL:** `http://localhost:5290/v1` (change the port via `Urls` in
`appsettings.json` or `ASPNETCORE_URLS`).

### The OpenAI-standard way

```bash
curl -N http://localhost:5290/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"web-agent","messages":[{"role":"user","content":"What is the weather today?"}]}'
```

- `stream: true` returns Server-Sent Events (SSE), exactly like OpenAI.
- `model` selects which agent set runs the conversation (`GET /v1/models` lists them).
- `file_ids` carries uploaded attachments on the request (see below).

**In an OpenAI SDK:** set `base_url` / `BaseAddress` to `http://localhost:5290/v1` and use
the SDK's normal chat methods.

### The built-in web client (Giraffe AI)

The quickest client is the one bundled with the TUI: **`/web`** (menu **Web → GUI**)
downloads the [Giraffe AI](https://github.com/Graphene-Lab/GiraffeAI) web client on first
use, extracts it into a `GiraffeAIWebClient` folder next to the working directory and opens
it in the browser at `http://localhost:8000`. The launch passes `--provider` with this
server's endpoint, so the client comes up with the **AgentBridge provider already registered
and selected** — just start typing. A client installed before this auto-connect feature
existed is re-downloaded automatically; the first download needs internet access.

### Endpoint summary

| Endpoint | Purpose |
|---|---|
| `POST /v1/chat/completions` | Chat with the agents (streaming, sessions, LLM switching) |
| `POST /v1/files` · `GET /v1/files{/id}` · `DELETE /v1/files/{id}` | Upload, list, retrieve, delete files (Markdown-converted) |
| `GET /v1/models` · `GET /v1/models/{id}` | Agent sets **and** LLM providers with their characteristics |
| `POST /v1/audio/speech` | Text-to-speech → WAV bytes (Kokoro neural TTS) |
| `POST /v1/control` | Switch the LLM in use, toggle features, reset history, create sessions |
| `GET /v1/control` | Session state + platform capabilities |
| `POST /v1/voice/listen` | One-shot speech recognition from the server microphone (Windows) |
| `GET /v1/audio/voices` | TTS voices available on this platform |
| `GET /v1/sip/status` · `POST /v1/sip/call` · `POST /v1/sip/hangup` · `POST /v1/sip/answer` | SIP telephony control (see [section 7](#7-sip-telephony)) |
| `GET /health` | Liveness probe |

The full request/response details are in [docs/API.md](API.md).

> **Same conversation:** messages sent from the terminal UI go through the exact same
> endpoint any client uses — you can chat in the TUI while a script drives the agents on
> the same port, simultaneously.

---

## 7. SIP telephony

The server can act as a **phone endpoint**: a caller dials in, proves their identity with
a DTMF PIN (or a trusted caller list), and talks to the agents by voice — the speech is
recognized (whisper), sent through the same `AgentHarness` path as the HTTP API, and
the replies are spoken back with the in-process Kokoro TTS over the RTP audio.

Full reference (architecture, security, NAT/firewall, deployment): **[docs/sip.md](sip.md)**.

### Quick configuration

```json
"Sip": {
  "Enabled": true,
  "ListenPort": 5060,
  "Pin": "12345",
  "MaxPinAttempts": 3,
  "LockoutHours": 24,
  "Lang": "it"
}
```

- **Incoming calls** are auto-answered; the caller is asked for the 5-digit PIN. After 3
  wrong attempts the server hangs up and refuses further calls for 24 hours (persisted
  across restarts).
- **Outgoing calls**: `/sip call sip:user@host` (or a bare number when a `Registrar` is
  configured). `/sip status` shows the live call state; `/sip answer off` rejects new calls.
- **Speech → agent**: needs the `AIOffice.VoiceAgent` executable (whisper) in the
  `voiceagent-stt/` folder next to the server (on Windows the build copies it when the
  sibling repo is present; on Linux/macOS copy it manually — the whisper model downloads
  on first use). `POST /v1/sip/status` reports `stt_available` / `tts_available`.

---

## 8. Where everything lives

| File/folder | Contents |
|---|---|
| `agent` / `agent.exe` | The server (self-contained single file) |
| `appsettings.json` | Server configuration (port, default LLM, voice path) |
| `providers.json` | LLM provider definitions + the per-provider API keys (excluded from updates) |
| `kokoro.onnx` + `voices/` | Kokoro TTS model and voices |
| `AIOffice.VoiceAgent.Win.exe` (Windows) | Voice dictation backend |
| `voiceagent-stt/` | `AIOffice.VoiceAgent` executable (whisper) — SIP call speech-to-text |

---

*Related docs: [Terminal UI reference](TUI.md) · [HTTP API reference](API.md) ·
[Architecture](ARCHITECTURE.md) · [Releases pipeline](RELEASING.md) (developers).*
