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

## Release and DEBUG coexistence (resource conflicts)

**Release and DEBUG builds are designed to coexist on the same machine.** The installed
release keeps the documented default ports; a DEBUG build automatically shifts its own
defaults so the two never fight for the same resource. The shift happens in `Program.cs`
at configuration load and applies only when the effective value is still the release
default — an explicit override (CLI `--Urls` / `--Sip:ListenPort`, `ASPNETCORE_URLS`, a
hand-edited `appsettings.json`) always wins:

| Resource | Release default | DEBUG default | Where it lives |
|---|---|---|---|
| HTTP server (`Urls`) | `http://localhost:5290` | `http://localhost:5291` | AgentBridge `Program.cs` (config shift, `#if DEBUG`) |
| SIP (`Sip:ListenPort`) | `5060` | `6071` | AgentBridge `Program.cs` (config shift, `#if DEBUG`) |
| Puppet TCP (TUI automation tests) | — never compiled in | `5292` | AgentBridge `Program.cs` (`#if DEBUG` listener) |
| WebTool CDP browser port | `9222` | `9223` | AIOrchestrator `API/WebTool.cs` (`CdpPort`) |
| WebTool persistent browser profile | `%TEMP%\aioffice_webtool_session` | `%TEMP%\aioffice_webtool_session_9223` | AIOrchestrator `API/WebTool.cs` |

What this means for the user-visible flows:

- A second instance no longer silently attaches to the running one (TUI mode, breakpoints
  never fired) nor fails with a bind exception (`--headless` mode): each instance hosts its
  own server and the launching instance's TUI attaches to itself.
- The clients the TUI launches follow the launching instance automatically, because they
  derive the URL from the same `urls` configuration value that drives the bind (Tui.cs
  `_serverUrl`): `/officemanager` opens `{serverUrl}/OfficeManager` (served by that same
  server) and the Giraffe web client is told that instance's `/v1/chat/completions`
  endpoint.

The IDE debug profiles below are no longer required (the DEBUG default already shifts), but
they stay valid — explicit flags always win — and document the scheme:

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

Configuration files are user-editable and live under **`<app folder>\PersistentData\`**
(`appsettings.json`, `providers.json`, `telegram.json` + `telegram.session`, `tools.json`,
and the TUI's agent tool-set `toolset.json`) —
never next to the executable (single-directory rule, see "Where files live" below and
RELEASE-CHECKLIST.md). `AppConfig.Initialize()` seeds the default `appsettings.json` and
migrates legacy root files on the first run; every key is overridable from the command line.

`appsettings.json` (under `PersistentData\`):

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
| `LLM:Provider` | `Ollama`, `DeepSeek`, `DeepSeekBridge`, `Zai`, `Gemini`, `ExllamaV2`, ... | **Default** LLM provider for the `AgentHarness`. The provider in use can be switched per-request / per-session — see [LLM switching](API.md#llm-switching-the-pilot-endpoint). |
| `LLM:Anonymize` | `true` / `false` | NameOrKey anonymization (see AIOrchestrator docs). |
| `SkipIndexingOnStartup` | `true` / `false` | Skip the DocumentsPath index build/refresh + file watcher at startup (debug/dev). |
| `AutoUpdate:Enabled` | `true` / `false` | Automatic update check at startup (default `true`). Overridden by the CLI `--no-update` flag and the TUI **Help → Auto-Update** menu — see [autoupdate.md](autoupdate.md). |
| `Voice:ExePath` | path | Path to `AIOffice.VoiceAgent.Win.exe` for `POST /v1/voice/listen`. Empty (default) = the `voiceagent\` folder next to the server executable — see [Build / assets](#build--assets) for where it comes from in development vs. releases. |
| `Sip:*` | see [sip.md](../docs/sip.md) | SIP telephony: auto-answer + PIN gate, outgoing calls, voice conversation over RTP (whisper STT + Kokoro TTS). |
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

Configuration lives in its own file, **`telegram.json` under `PersistentData\`** — separate
from `appsettings.json`, and **never touched by updates** (single-directory rule — see
[RELEASING.md](RELEASING.md#what-an-update-must-never-touch--the-file-storage-tiers)). The
`.session` file (Telegram auth) lives in the same folder. Keys: `Enabled`, `ApiId`,
`ApiHash`, `PhoneNumber`, `SessionPath`, `AllowedUsers`,
`Agent` (see [docs/telegram.md](../docs/telegram.md)). When `Enabled=true` the bridge **starts in
the background at boot** — a pending first login (verification code / 2FA password) never
blocks the boot; the TUI `/telegram` command drives status, config, the pending-login code
and the allow-list **in-process** (the bridge exposes **no HTTP endpoints** — Telegram is a
chat client, not a web client, so nothing about it travels over HTTP).

**Access control (closed by default).** Only `AllowedUsers` entries (numeric ids/@usernames)
may talk. An unlisted user who sends the **shared external-client access PIN** (`Sip:Pin`,
set once from the TUI with `/sip config set Pin`) is enrolled into the allow-list and
welcomed with the same "How can I help you?" used after a SIP login, as text
(`SipBridge.SubmitClientPin` → shared `PinAuthGate`: one attempt/lockout budget across SIP
and Telegram, lockout persisted in the app-data `sipstate.json`). No PIN configured → the
allow-list is the only way in.

### LLM providers & API keys

LLM providers are defined in **`providers.json` under `PersistentData\`** (the same file the
AIOrchestrator library ships — embedded factory default as fallback; `ProviderConfigs.ConfigDirectory`
redirects the library to `PersistentData\` for this host, other hosts keep the default next to
the executable). Each entry carries its own **`ApiKey`** field: the **single source of truth**
for keys. `Setup.ApiKey` resolves the active provider's key from here; the legacy per-provider
`Setup` properties are honored only as a fallback when a provider config has no key.

- **Cloud providers** (DeepSeek, Z.ai, Gemini, Anthropic, …) require `ApiKey` — sent as
  `Authorization: Bearer` (OpenAI/Anthropic) or in the query string (Gemini).
- **Local providers are keyless by design**: any provider whose `BaseAddress` is on loopback
  (`localhost` / `127.0.0.1`) is treated as keyless regardless of name — a dynamically added
  Ollama/ExLlamaV2/DeepSeekBridge entry works out of the box.
- Keys are edited via the TUI **provider dialog** (`/setup` → Edit, masked field), the
  AIOffice Settings panel, or directly in the file. `providers.json` is **never touched by
  updates** (single-directory rule — see [autoupdate.md](autoupdate.md)), so keys survive
  every update.
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

- **User-editable configuration — the single-directory rule** — `<app folder>\PersistentData\`
  holds EVERY file the user edits or the app persists that must survive updates:
  `appsettings.json`, `providers.json`, `telegram.json` + `telegram.session`, `tools.json`,
  `rag_settings.json`. Never overwritten by updates; legacy root copies are migrated there by
  `AppConfig.Initialize()` on first run (in DEBUG the app refuses to start while a config json
  still sits next to the executable — `AppConfig.GuardNoStrayRootJson`).
- **Application data & secrets** — the OS app-data folder in a subfolder named after the
  running executable: `%LocalAppData%\agent\setup.json` on Windows (`~/.local/share/agent`
  on Linux, `~/Library/Application Support/agent` on macOS). SMTP/IMAP credentials,
  DPAPI-encrypted on Windows, plus `autoupdate.json`, `crashreport.json`, `sipstate.json`.
  Outside the app folder, so updates never touch it. (LLM API keys are **not** here: they
  live per-provider in `providers.json` — see the AIOrchestrator `docs/providers-config.md`;
  the legacy key fields of `setup.json` remain only as a fallback.)
- **Distribution content** — everything else next to the executable (what the archive
  ships): `agent(.exe)`, `agent.xml`, `voices/`, `kokoro.onnx`, `assets/`, `Lingua/`
  (SearchPioneer.Lingua language models), `.playwright/`,
  `docs/`, `Tools/`, `voiceagent/` (Windows), the SDK-generated `agent.staticwebassets.endpoints.json`. Replaced on
  every update with **no exceptions and no whitelist** — user config is never in the
  archive, so replacing the distribution tier cannot touch it. All `.json` at the archive
  root are generated content and must be overwritten. The automatic updater implements
  these rules — see [autoupdate.md](autoupdate.md).

Ephemeral runtime files (TTS WAVs) go to the OS temp folder (`%TEMP%`) and are never part of
an update. The Giraffe web GUI client is installed next to the executable (a
`GiraffeAIWebClient` folder, not in the source tree) from the client's GitHub release and
kept at the latest version — see [WebClientUpdater.cs](../WebClientUpdater.cs).

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
deps) into the `voiceagent\` subfolder next to the server, enabling `POST /v1/voice/listen`
out of the box. This is the **development** rule: the release pipeline downloads the
VoiceAgent.Win release from the public `Graphene-Lab/AIOffice.VoiceAgent.Win` repository,
so **public Windows releases ship the component inside the archive** (same `voiceagent\`
folder — the TUI message and the `voice_unavailable` 501 both point the user at updating
the app, not at files). Without the component the voice endpoint self-reports as
unavailable — copy the exe manually or set `Voice:ExePath`.

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
> `AIOffice.VoiceAgent.Win.exe` voice bridge (in the `voiceagent\` subfolder) are
> **published alongside** (never embedded — that would bloat the exe by hundreds of MB).
> Keep the whole publish folder together and run `agent` from it. On Windows the publish
> copies the sibling VoiceAgent output automatically into `voiceagent\`
> (`CopyVoiceAgentOutput` also runs on Publish); release archives are produced the same
> way, with the VoiceAgent.Win release downloaded in CI.

## Agent sets & tool policy

> **Design decision (2026-08-27, issue #2), implemented in the following release.** The
> preset table in `AgentTools.cs` is the static form of a three-level tool policy. The
> model is config-driven so new plugins need no code change and users cannot accidentally
> cripple the agent. Implemented: per-tool config (`tools.json` under `PersistentData\`,
> "unspecified ⇒ ON"), the dynamic `all-files` preset, core tools appended to every preset
> and locked in the TUI picker. Not implemented (future hardening): server-side
> enforcement — an explicit API `tools` list can still omit the core tools (documented
> bypass, option (a) in the issue discussion).

### Tool levels

| Level | Tools | Default | Changeable from the TUI? |
|---|---|---|---|
| **Core** (always-on) | `FileTool`, `GitTool`, `TaskSchedulerTool` | Always ON | No — locked (separate read-only line in the picker, not toggleable) |
| **Class A** (our plugins) | `DocumentTool`, `SpreadsheetTool`, `PresentationTool`, `OfficeSupportTool`, ... | ON at first start (rule below) | Yes (per-tool config) |
| **Class B** (vendored engine behind our adapter) | `OfficeTool` | OFF unless enabled | Yes (per-tool config) |

**Core tools are architectural primitives.** `FileTool`, `GitTool` and `TaskSchedulerTool`
live in the AIOrchestrator assembly (not in the `Tools/` plugin folder) and other tools
depend on them: `FileTool` is the sandbox search/read surface (FileSearch, AnswerQuery,
GetDirectoryTree); `GitTool` owns rollback and versioning — plugins snapshot through
`GitSupport` (AGENT_TOOLS_GUIDE: "Rollback lives in the shared GitTool");
`TaskSchedulerTool` owns scheduled automated task chats (timers + JSON persistence in
`/.taskscheduler`, run logs in `/.taskscheduler/logs`). Removing them would cripple the
tools that rely on them, so they are locked ON and changeable only by editing the config
file directly.

**Default-state rule — "unspecified ⇒ ON".** The per-tool config file records only
deviations: a tool present at first start without an explicit status is ON. This makes
hot-added plugins (PluginUpdater, dynamic `Tools/` discovery) work immediately — zero
code, zero user action — and prevents the "the plugin does not activate" support ticket.

**Why OfficeTool (class B) defaults OFF:**
1. **Domain overlap** — OfficeTool covers DOCX/XLSX/PPTX, duplicating `DocumentTool`
   (Word) and `SpreadsheetTool` (Excel). Overlapping tools cost tokens (the agent reads
   both skill/help surfaces) and hurt tool-selection reliability. At first start the user
   finds only the class-A tools active, with no overlapping pairs.
2. **Trust/control** — OfficeTool is our adapter around a vendored engine (`officecli`);
   the engine's internals are opaque and regenerated by `update-vendor.ps1`. The adapter
   is fully sandboxed (every filesystem path entry point resolves via `SandboxPath`
   before the engine sees a path; results render through `ToAgent`), so this is a
   trust/control trade-off, not a sandbox hole. Users who want it enable it explicitly.

> PPTX is still covered with OfficeTool OFF: `PresentationTool` builds HTML decks (no
> PowerPoint needed, browser + F11 fullscreen) and does **not** overlap OfficeTool's real
> `.pptx` editing — they are complementary.

**The "all-files" preset is dynamic, not a fixed list** — it resolves to *core tools +
every class-A plugin present (default ON) − class-B tools (default OFF)*, so new class-A
plugins join it automatically. The token cost of a large catalog (~155 methods in the
full set, re-sent into the system prompt on every agent iteration) is paid only by users
who choose the all-in-one preset; the narrow presets (`web-agent`, `email-agent`, ...)
remain for focused tasks and small models. Any custom combination remains possible via
the additive `tools` field (API) and the `/tools` checklist (TUI).

## Project layout

| File | Purpose |
|---|---|
| `Program.cs` | All endpoints + helpers (top-level statements) + DTOs + launch-mode selection |
| `Tui.cs` | The Terminal.Gui terminal UI: menu bar, AGENT logo panel, streaming chat panel + input line, status bar, `/` command palette, `@` file palette, shortcuts, mouse |
| `SessionStore.cs` | Multi-turn sessions (orchestrator + history + feature flags; session-backed orchestrators record the conversation to the AIOrchestrator Memory at disposal — idle timeout from `AgentHarness.SuggestedConversationTimeout`; lifecycle events `SessionCreated`/`SessionRemoved` feed the OfficeManager hub) |
| `OfficeBridge.cs` | OfficeManager WebSocket hub (`/ws/office`): every agent/subagent instance of this process becomes an employee (spawn/assign/running/method/closed + chat protocol); accepts forwarded events from other processes (`POST /v1/office/events`) |
| `StatelessConversation.cs` | Dynamic transcript-hash correlation for stateless chat requests (no `session_id`): full-transcript SHA-256 → session dictionary, so a third-party client that resends its transcript stays ONE conversation (one employee) instead of a one-shot per message |
| `TtsEngine.cs` | In-process Kokoro TTS (lazy init, WAV synthesis) |
| `VoiceBridge.cs` | VoiceAgent.Win subprocess bridge (one-shot recognition) |
| `TelegramBridge.cs` | Telegram text-chat medium: WTelegramClient 4.4.8 userbot (private-chat → agent session → reply) |
| `SystemLang.cs` | Machine language resolution (two-letter ISO code) |
| `AgentBridge.csproj` | Web SDK, `AssemblyName=agent`, references AIOrchestrator + Terminal.Gui + KokoroSharp, asset targets |
| `AppConfig.cs` | Single persistent-config directory: `PersistentData\` layout, CWD anchoring, legacy-root migration, default-appsettings seed, DEBUG stray-json guard |
| `AGRNT_ascii_art.txt` | Source of the colored `AGENT` wordmark shown in the TUI |
| `appsettings.json` | Repo copy of the DEFAULT user config — embedded and seeded into `PersistentData\` on first run (port, LLM provider, voice path) |
| `telegram.json` | Telegram chat medium config, created under `PersistentData\` at runtime — never part of an update |
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

See also: [README](../README.md) · [Terminal UI](../docs/TUI.md) · [API reference](../docs/API.md)
