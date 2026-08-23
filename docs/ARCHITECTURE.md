# AgentBridge — architecture & operations

## What it is

AgentBridge is a .NET 10 web application that hosts the **AIOrchestrator** library
(which is a library, not directly executable) and exposes its chat pipeline through standard
**OpenAI-compatible REST endpoints**, plus a small set of **documented proprietary
extensions** for the features that have no OpenAI equivalent (voice speech, LLM switching,
platform capabilities). Any OpenAI SDK, script or standalone client — such as
[Giraffe AI](../GiraffeAI/index.html) — can drive the AI agents without modification.

## Why it exists

- **AIOffice** uses the agents in-process (Blazor Server UI + Voice panel) — no HTTP layer needed.
- **Standalone clients** (a plain HTML file, a Python/curl script, an OpenAI SDK) have no
  access to that in-process pipeline. AgentBridge gives them the same agents through
  `POST /v1/chat/completions`, plus a file-upload flow for attachments, text-to-speech,
  speech recognition (Windows) and on-the-fly LLM switching.

```
Standalone client ──HTTP──▶ AgentBridge (this project)
                                 │  hosts (references)
                                 ▼
                          AIOrchestrator (AgentHarness)
                                 │
                                 ▼
              LLM (DeepSeekBridge / DeepSeek / Z.ai / Gemini / Ollama / ExLlamaV2)
              + agent tools (WebTool, FileTool, DocumentTool, SpreadsheetTool, EMailTool)
              + Kokoro neural TTS (in-process) + VoiceAgent.Win (Windows STT)
```

## Launch modes

| Mode | How |
|---|---|
| Terminal UI (default) | `agent` / `dotnet run` — the UI opens when the console is interactive |
| Server only | `agent --headless` (or `--no-gui`) — plain server console for scripts/CI |
| Force UI | `agent --tui` — falls back to server-only when the console is not interactive |

When the UI starts while another instance already owns the port, it connects to
that instance instead of failing (useful to attach a UI to a running service).

## Debugging — separate ports from the running instance

Running a debug build on the same machine as an installed AgentBridge **conflicts on the
HTTP port** (`Urls`, default `5290`) and, when SIP is enabled, on **`Sip:ListenPort`**
(default `5060`/`6070`). In TUI mode the second instance silently attaches to the running
one (your breakpoints never fire); in `--headless` mode it fails with a bind exception.

The repo ships debug profiles that use **separate ports** — `5291` for HTTP, `6071` for
SIP — so the installed agent can keep running untouched:

| IDE | File | How it launches |
|---|---|---|
| Visual Studio | `Properties/launchSettings.json` | F5 → `applicationUrl: http://localhost:5291` + `--Sip:ListenPort 6071` |
| VS Code | `.vscode/launch.json` + `.vscode/tasks.json` | Run and Debug → "AgentBridge (Debug ports)" → `--Urls http://localhost:5291 --Sip:ListenPort 6071` |

Manual equivalent:

```
agent.exe --Urls http://localhost:5291 --Sip:ListenPort 6071
```

Note: `%LocalAppData%\agent\` runtime files (`sipstate.json`, `setup.json`) are shared
between all instances on the machine — the SIP PIN lockout state is machine-wide by design.

## Configuration

`appsettings.json`:

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
| `LLM:Provider` | `Ollama_Granite3b`, `DeepSeek`, `DeepSeekBridge`, `Zai`, `Gemini`, `ExllamaV2_Llama3b`, ... | **Default** LLM provider for the `AgentHarness`. The provider in use can be switched per-request / per-session — see [LLM switching](API.md#llm-switching-the-pilot-endpoint). |
| `LLM:Anonymize` | `true` / `false` | NameOrKey anonymization (see AIOrchestrator docs). |
| `SkipIndexingOnStartup` | `true` / `false` | Skip the DocumentsPath index build/refresh + file watcher at startup (debug/dev). |
| `AutoUpdate:Enabled` | `true` / `false` | Automatic update check at startup (default `true`). Overridden by the CLI `--no-update` flag and the TUI **File → Auto-Update** menu — see [autoupdate.md](autoupdate.md). |
| `Voice:ExePath` | path | Path to `AIOffice.VoiceAgent.Win.exe` for `POST /v1/voice/listen`. Empty (default) = look next to the server executable. |
| `Sip:*` | see [sip.md](sip.md) | SIP telephony: auto-answer + PIN gate, outgoing calls, voice conversation over RTP (whisper STT + Kokoro TTS). |
| `Urls` | e.g. `http://localhost:5290` | Kestrel listening address. |

Every key is overridable from the command line (`--LLM:Provider Zai`, `--SkipIndexingOnStartup true`, `--Voice:ExePath ...`, `--Sip:Enabled true`); run `--help` for the list.

### Telegram chat medium (`telegram.json`)

Telegram is a **text-chat client medium** — like the TUI and the HTML client, not like SIP.
A private message (text and/or file attachments) is handed to the agent through a per-user
chat session and the reply (text + attachments) is sent back to the same chat. There is no
audio: the Telegram Client API has no audio-call support, so Telegram adds **no
`IAudioMedia` and no `VoiceConversation`** — the media list stays **SIP (phone calls)** and
**Voice (desktop microphone)**. The transport is
[WTelegramClient](https://github.com/wiz0u/WTelegramClient) **4.4.8** (userbot, MTProto).

Configuration lives in its own file, **`telegram.json` next to the executable** — separate
from `appsettings.json`, and **excluded from updates** (same whitelist as
`appsettings.json` and `providers.json`, see
[RELEASING.md](RELEASING.md#what-an-update-must-never-touch--the-file-storage-tiers)).
Keys: `Enabled`, `ApiId`, `ApiHash`, `PhoneNumber`, `SessionPath`, `AllowedUsers`,
`Agent` (see [docs/telegram.md](telegram.md)). When `Enabled=true` the bridge **starts in
the background at boot** — a pending first login (verification code / 2FA password) never
blocks the boot; the TUI `/telegram` command drives status, config, the pending-login code
and the allow-list **in-process** (the bridge exposes **no HTTP endpoints** — Telegram is a
chat client, not a web client, so nothing about it travels over HTTP).

### LLM providers & API keys

LLM providers are defined in **`providers.json`** next to the executable (the same file the
AIOrchestrator library ships — embedded factory default as fallback). Each entry carries its
own **`ApiKey`** field: the **single source of truth** for keys. `Setup.ApiKey` resolves the
active provider's key from here; the legacy per-provider `Setup` properties are honored only
as a fallback when a provider config has no key.

- **Cloud providers** (DeepSeek, Z.ai, Gemini, Anthropic, …) require `ApiKey` — sent as
  `Authorization: Bearer` (OpenAI/Anthropic) or in the query string (Gemini).
- **Local providers are keyless by design**: any provider whose `BaseAddress` is on loopback
  (`localhost` / `127.0.0.1`) is treated as keyless regardless of name — a dynamically added
  Ollama/ExLlamaV2/DeepSeekBridge entry works out of the box.
- Keys are edited via the TUI **provider dialog** (`/modelsetup` → Edit, masked field), the
  AIOffice Settings panel, or directly in the file. `providers.json` is **excluded from
  updates** (whitelisted — see [autoupdate.md](autoupdate.md)), so keys survive every update.
- The Z.ai key also enables the image OCR pipeline (`ZaiOcrConverter` reads the `Zai` entry's
  key).

Full field reference: the AIOrchestrator `docs/providers-config.md`.

> **⚠️ Startup indexing**: the server indexes `DocumentsPath` at startup (minutes on large
> folders). When the feature under test does **not** need document searches, start with
> `--SkipIndexingOnStartup true`.

## Where files live (storage tiers)

Persisted files are split into **three tiers**; every update mechanism (release archives,
future auto-updater) may only replace the distribution tier. The full rule table is in
[RELEASING.md](RELEASING.md#what-an-update-must-never-touch--the-file-storage-tiers);
summary:

- **User-editable configuration** — `<app folder>\PersistentData\` (currently
  `rag_settings.json`, the persisted DocumentsPath). Never overwritten by updates;
  legacy `rag_settings.json` next to the executable is migrated there on first run.
- **Application data & secrets** — the OS app-data folder in a subfolder named after the
  running executable: `%LocalAppData%\agent\setup.json` on Windows (`~/.local/share/agent`
  on Linux, `~/Library/Application Support/agent` on macOS). SMTP/IMAP credentials,
  DPAPI-encrypted on Windows. Outside the app folder, so updates never touch it. (LLM API
  keys are **not** here: they live per-provider in `providers.json` — see the AIOrchestrator
  `docs/providers-config.md`; the legacy key fields of `setup.json` remain only as a
  fallback.)
- **Distribution content** — everything else next to the executable (what the archive
  ships): `agent(.exe)`, `agent.xml`, `voices/`, `kokoro.onnx`, `assets/`, `.playwright/`,
  `agent.staticwebassets.endpoints.json`, the default `appsettings.json`. Replaced on
  every update, **except `appsettings.json`, `providers.json` and `telegram.json`**
  (user-editable server config, LLM providers and the Telegram chat medium — preserved by
  whitelist). All other `.json` files in the archive are distribution content and must be
  overwritten. The automatic updater implements these rules — see [autoupdate.md](autoupdate.md).

Ephemeral runtime files (TTS WAVs, the Giraffe web GUI download) go to the OS temp folder
(`%TEMP%`) and are never part of an update.

## Build / assets

The project references `KokoroSharp` (ships `voices/` + `voices-zh/` to the output; the
phonemizer is the pure-managed MisakiSharp since 0.8.4 — no espeak-ng binaries) and needs
`kokoro.onnx` (~325 MB, not tracked in git). The build target `DownloadKokoroModel` provides
it automatically: copy from the sibling `AIOffice.VoiceAgent.Win` build output if present,
else `curl` from GitHub. TTS returns 501 until the model is present. The native ONNX Runtime
engine (per-RID) comes from the explicit `Microsoft.ML.OnnxRuntime` reference — KokoroSharp
only pulls the managed wrapper.

On Windows, if the sibling `AIOffice.VoiceAgent.Win` build output exists, the target
`CopyVoiceAgentOutput` copies the whole VoiceAgent (exe + voices + espeak + model + runtime
deps) next to the server, enabling `POST /v1/voice/listen` out of the box. Without it the
voice endpoint self-reports as unavailable — copy the exe manually or set `Voice:ExePath`.

## Build & publish

> The **automated** release pipeline (NuGet dependency packages, version scheme,
> `IsPrerelease` gate, sync-all/pre-push hook, wait-for-packages, release.yml) is
> documented in **[RELEASING.md](RELEASING.md)**. The commands below are the manual,
> development-oriented way to build and publish locally.

The executable is named **`agent`** (`agent.exe` on Windows, `agent` on Linux/macOS) — like
`qwen` in Qwen Code. Development:

```bash
dotnet build                      # → bin/Debug/net10.0/agent(.exe)
dotnet run --project AgentBridge.csproj            # terminal UI + server
dotnet run --project AgentBridge.csproj -- --headless   # server only
```

Production single-file (self-contained, no .NET install needed on the target):

```bash
# Windows
dotnet publish AgentBridge.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# Linux server
dotnet publish AgentBridge.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

The produced `agent.exe` / `agent` starts the **terminal UI by default and keeps
answering API calls** — run it on a server with `--headless` (optionally under
systemd) so only the HTTP API is exposed.

> **Assets stay next to the executable.** Single-file bundles the managed code; the
> Kokoro TTS voices (`voices/`, `kokoro.onnx` ~325 MB) and, on Windows, the
> `AIOffice.VoiceAgent.Win.exe` voice bridge are **published alongside** (never embedded —
> that would bloat the exe by hundreds of MB). Keep the whole publish folder together and
> run `agent` from it. On Windows the publish copies the sibling VoiceAgent output
> automatically (`CopyVoiceAgentOutput` also runs on Publish).

## Project layout

| File | Purpose |
|---|---|
| `Program.cs` | All endpoints + helpers (top-level statements) + DTOs + launch-mode selection |
| `Tui.cs` | The Terminal.Gui terminal UI: menu bar, AGENT logo panel, streaming chat panel + input line, status bar, `/` command palette, `@` file palette, shortcuts, mouse |
| `SessionStore.cs` | Multi-turn sessions (orchestrator + history + feature flags) |
| `TtsEngine.cs` | In-process Kokoro TTS (lazy init, WAV synthesis) |
| `VoiceBridge.cs` | VoiceAgent.Win subprocess bridge (one-shot recognition) |
| `TelegramBridge.cs` | Telegram text-chat medium: WTelegramClient 4.4.8 userbot (private-chat → agent session → reply) |
| `SystemLang.cs` | Machine language resolution (two-letter ISO code) |
| `AgentBridge.csproj` | Web SDK, `AssemblyName=agent`, references AIOrchestrator + Terminal.Gui + KokoroSharp, asset targets |
| `AGRNT_ascii_art.txt` | Source of the colored `AGENT` wordmark shown in the TUI |
| `appsettings.json` | Port, LLM provider, voice path |
| `telegram.json` | Telegram chat medium config (enabled, api credentials, allow-list, agent) — excluded from updates |
| `e2e/` | PowerShell regression harness (33+ tests, requires DeepSeekBridge) |
| `e2e/TuiSmoke/` | ConPTY harness that launches the real TUI, injects keystrokes and asserts the UI (logo, `/model` picker + Esc, chat) |

## Testing

```bash
dotnet build AgentBridge.csproj
```

Smoke: `health` → `models` → upload a CSV → `files/{id}/content` → `DELETE` → chat with
`file_ids` → SSE → create session → multi-turn chat → `control` switch → `audio/speech`.
The offline unit tests for the conversion pipeline live in
`AIOrchestrator/AgentHarness.Tests`.

**Terminal UI smoke (Windows):** `e2e/TuiSmoke` launches the real `agent.exe` in a
pseudoconsole (ConPTY), types `/model`, presses Esc, sends a chat message and asserts the
rendered UI. Requires port 5290 free and the Debug build:

```bash
dotnet run --project e2e\TuiSmoke        # 8 checks; exit 0 = all pass
```

---

See also: [README](../README.md) · [Terminal UI](TUI.md) · [API reference](API.md)
