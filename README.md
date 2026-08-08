# AgentBridge

**OpenAI-compatible HTTP server for `AgentOrchestrator` (AIOrchestrator) — the professional API entry point of the AIOffice ecosystem.**

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
                          AIOrchestrator (AgentOrchestrator)
                                 │
                                 ▼
              LLM (DeepSeekBridge / DeepSeek / Z.ai / Gemini / Ollama / ExLlamaV2)
              + agent tools (WebTool, FileTool, WordTool, SpreadsheetTool, EMailTool)
              + Kokoro neural TTS (in-process) + VoiceAgent.Win (Windows STT)
```

## Quick start

**Windows** — double-click `start.bat`; **Linux / macOS** — `./start.sh`. Or:

```bash
cd AgentBridge
dotnet run --project AgentBridge.csproj
```

The console opens the **terminal UI** (chat + slash commands — see [Terminal UI](#terminal-ui))
and the server keeps answering API calls in the same process on
`http://localhost:5290` (configurable via the `Urls` key in `appsettings.json` or
the `ASPNETCORE_URLS` environment variable). Add `--headless` for the plain server
console (scripts, CI, services).

```bash
curl http://localhost:5290/health   # {"status":"healthy","timestamp":"..."}
```

The published executable is named `agent` (`agent.exe` on Windows) — see
[Build & publish](#build--publish).

## Terminal UI (Agent, Qwen-Code style)

The console app is an interactive terminal UI modelled on
[Qwen Code](https://github.com/QwenLM/qwen-code)'s TUI: an input line at the
bottom to chat with the agents, `/` slash commands with a **contextual help menu
that appears below the input and filters live while you type**, keyboard
shortcuts, and online help — while the HTTP server keeps serving every other
client on the same port. **CLI and API are the same conversation**: messages you
send from the UI go through the exact same `POST /v1/chat/completions` endpoint
that any OpenAI-compatible client uses, so both can drive the agents at the same
time (the UI holds one session; other clients create their own).

### How Qwen Code's TUI works (the model we followed)

Qwen Code is a terminal-based agentic coding tool: you launch `qwen` in a
project and get a persistent TUI where:

- a **bottom input field** accepts plain-language prompts; a small **status
  line** shows the model/provider and context usage;
- typing **`/`** opens a **slash-command palette** — a contextual help list
  (name + one-line description per command) that **filters as you type**;
  Enter runs the highlighted command, Tab completes it, Esc closes it;
- **`@`** opens file/context completion;
- a rich **keyboard-shortcut layer** covers editing (Ctrl+A/E/U/K/W, word
  jumps), history (Up/Down, Ctrl+R reverse search), Ctrl+C cancel, Ctrl+L
  clear screen, `?` shortcuts overlay, Tab ghost-text completion;
- **`/model`** switches the LLM on the fly, `/help` shows the command docs,
  `/docs` opens the online documentation in the browser;
- agent replies **stream** into the conversation and can be cancelled.

### What AgentBridge does better

| Qwen Code | AgentBridge (this app) |
|---|---|
| TUI is the only front-end | TUI **and** the OpenAI-compatible API simultaneously, same process — any SDK/script can keep driving the agents while you chat |
| Model switch is provider-side | `/model` switches LLM **with a context-window guard** — the server refuses (409) when the conversation overflows the target provider's window and explains why |
| Mouse: scroll + click | native mouse (Windows console API): **wheel** scrolls the conversation and navigates menus, **click** positions the input cursor or selects a menu row, **double-click** runs the selected command; QuickEdit disabled so clicks are never stolen |
| Menus | pickers are drawn inside the TUI layout — **Esc always cancels** (`/model`, `/agent`, `/attach`) with a clean screen, no residue |
| No voice | `/voice` — dictation from the server microphone (Windows), `/tts` — Kokoro neural TTS speaks the replies and plays the WAV |
| File completion via `@` | `@` palette of **uploaded** files (server-side `/v1/files`), `/files add <path>` uploads and attaches; attachments ride along as `file_ids` |
| Status shows context | status bar also shows **history tokens / context window**, agent set, TTS/mic availability, features |
| `/docs` opens docs site | `/docs` opens **this project's** online README; `/help` lists commands, shortcuts, API endpoints and links |
| — | `/agent` switches the agent tool set, `/features` toggles session feature flags, `/health` pings the server, `/retry` (Ctrl+Y) resends the last prompt |

### Commands (type `/` for the live list)

| Command | What it does |
|---|---|
| `/help` · `/?` | Full help: commands, shortcuts, API endpoints, online docs |
| `/docs` | Open the online documentation in the browser |
| `/model [name]` | Switch the LLM provider (menu when no name given; context-window checked) |
| `/agent [name]` | Switch the agent set (default/web/search/research/word/spreadsheet/email/multi) |
| `/voice [lang]` | Dictate from the server microphone into the input |
| `/tts [text]` | Speak the last agent reply (or the given text) — Kokoro TTS, WAV playback |
| `/features [name] [on\|off]` | Show or toggle session feature flags (voice, tts, ...) |
| `/new` · `/reset` | Start a new session (fresh conversation) |
| `/clear` | Reset the current session history (keeps the session) |
| `/status` | Session state + platform capabilities |
| `/files add <path>` · `/files rm <id>` · `/files` | Upload+attach a file, delete one, list uploads |
| `/attach [id]` | Toggle a file attachment for the chat (menu when no id) |
| `/shortcuts` · `/keys` | Keyboard shortcuts overlay (also press `?` on an empty input) |
| `/health` | Ping the server, report latency |
| `/retry` | Resend the last prompt (also Ctrl+Y) |
| `/exit` · `/quit` | Exit (also Ctrl+C twice, or Ctrl+D) |

### Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Enter` | Send the message / run the selected command |
| `/` | Open the slash-command palette (contextual help below the input) |
| `@` | Open the file palette (toggle chat attachments) |
| `?` | Shortcuts overlay (empty input) |
| `Tab` | Complete the selected command / attach the selected file |
| `Esc` | Close palette · clear input · twice: exit |
| `Ctrl+C` | Cancel the reply · clear input · twice: exit |
| `Ctrl+D` | Exit (empty input) |
| `Ctrl+L` | Clear the screen |
| `Ctrl+R` | Reverse-search prompt history |
| `Ctrl+Y` | Retry the last prompt |
| `Up` / `Down` | Prompt history (also Ctrl+P / Ctrl+N) |
| `←` / `→` | Move the cursor (with Ctrl: by word) |
| `Ctrl+A` / `Ctrl+E` | Jump to start / end of the input |
| `Ctrl+U` / `Ctrl+K` | Delete to start / end of the line |
| `Ctrl+W` | Delete the word before the cursor |
| `PgUp` / `PgDn` | Scroll the conversation history |
| `F1` | Full help page |

### Mouse (Windows)

The TUI uses the native Windows console input (like Qwen Code): menus are drawn
inside the layout and **Esc always cancels them** cleanly (`/model`, `/agent`,
`/attach`).

| Action | Effect |
|---|---|
| Mouse wheel (conversation) | Scroll the history |
| Mouse wheel (menu/palette) | Move the selection |
| Click the input line | Position the text cursor |
| Click a menu/palette row | Select it |
| Double-click a menu row | Run it |

QuickEdit is disabled while the TUI runs so clicks are never hijacked by text
selection; the terminal switches to the alternate screen buffer and restores it
on exit. On Linux/macOS the TUI is keyboard-only.

### Launch modes

| Mode | How |
|---|---|
| Terminal UI (default) | `agent` / `dotnet run` — the UI opens when the console is interactive |
| Server only | `agent --headless` (or `--no-gui`) — plain server console for scripts/CI |
| Force UI | `agent --tui` — falls back to server-only when the console is not interactive |

When the UI starts while another instance already owns the port, it connects to
that instance instead of failing (useful to attach a UI to a running service).

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
  }
}
```

| Key | Values | Description |
|---|---|---|
| `LLM:Provider` | `Ollama_Granite3b`, `DeepSeek`, `DeepSeekBridge`, `Zai`, `Gemini`, `ExllamaV2_Llama3b`, ... | **Default** LLM provider for the `AgentOrchestrator`. The provider in use can be switched per-request / per-session — see [LLM switching](#llm-switching) below. |
| `LLM:Anonymize` | `true` / `false` | NameOrKey anonymization (see AIOrchestrator docs). |
| `SkipIndexingOnStartup` | `true` / `false` | Skip the DocumentsPath index build/refresh + file watcher at startup (debug/dev). |
| `Voice:ExePath` | path | Path to `AIOffice.VoiceAgent.Win.exe` for `POST /v1/voice/listen`. Empty (default) = look next to the server executable. |
| `Urls` | e.g. `http://localhost:5290` | Kestrel listening address. |

Every key is overridable from the command line (`--LLM:Provider Zai`, `--SkipIndexingOnStartup true`, `--Voice:ExePath ...`); run `--help` for the list.

> **⚠️ Startup indexing**: the server indexes `DocumentsPath` at startup (minutes on large
> folders). When the feature under test does **not** need document searches, start with
> `--SkipIndexingOnStartup true`.
>
> **Streaming caveat**: LLM-native streaming (`SendQueryStream`) does not support
> anonymization and throws for Gemini — the `/v1/chat/completions` SSE endpoint here is
> response-side only (the agent result is computed with non-streaming `SendQuery`).

## Endpoint summary

### Standard (OpenAI-compatible)

| Endpoint | Purpose |
|---|---|
| `POST /v1/chat/completions` | Chat with the agents (streaming SSE, sessions, LLM switching) |
| `POST /v1/files` | Multipart upload + server-side Markdown conversion |
| `GET /v1/files` · `GET /v1/files/{id}` | List / retrieve converted files |
| `GET /v1/files/{id}/content` | Raw uploaded bytes (OpenAI Files API) |
| `DELETE /v1/files/{id}` | Delete an uploaded file |
| `GET /v1/models` | Agent sets **and** LLM providers with their characteristics |
| `GET /v1/models/{id}` | Single model details |
| `POST /v1/audio/speech` | Text-to-speech → WAV bytes (Kokoro neural TTS) |
| `GET /health` | Liveness probe |

### Proprietary extensions (documented, additive — ignored by strict OpenAI clients)

| Endpoint | Purpose |
|---|---|
| `POST /v1/control` | Pilot/steering: switch the LLM in use, toggle features, reset history, create sessions |
| `GET /v1/control` | Session state + platform capabilities (what is available here and now) |
| `POST /v1/voice/listen` | One-shot speech recognition from the server microphone (Windows only) |
| `GET /v1/audio/voices` | TTS voices available on this platform |

The rule for platform-dependent features: the server reports them **unavailable (501)** when
the platform or the assets are missing, and `GET /v1/control` / `GET /v1/audio/voices` always
tell the client what is actually available — a chat client activates voice/TTS only where they
really run.

---

## `POST /v1/chat/completions` — chat with the agents

OpenAI Chat Completions compatible. `model` selects which agent set is used
(see `GET /v1/models`); `stream: true` returns Server-Sent Events (SSE).

```bash
curl -N http://localhost:5290/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "web-agent",
    "messages": [{"role": "user", "content": "What is the weather today?"}],
    "file_ids": ["file-..."],
    "stream": true
  }'
```

| Field | Meaning |
|---|---|
| `model` | Agent set: `default-agent`, `web-agent`, `search-agent`, `word-agent`, `spreadsheet-agent`, `email-agent`, `multi-agent`. |
| `messages` | OpenAI messages; the last `user` message is the prompt. |
| `file_ids` | Optional ids from `POST /v1/files` — attached as context (Markdown, server-side). |
| `max_tokens` | Roughly maps to agent loop iterations (`max_tokens / 100`, clamped 1–50). |
| `stream` | `true` → SSE chunks; `false` (default) → single JSON response with `usage`. |
| `session_id` | **Extension** — multi-turn session id (see [Sessions](#sessions)). |
| `llm_provider` | **Extension** — LLM provider for this request (see [LLM switching](#llm-switching)). |

Responses carry an additive `session_id` field when a session was used.

## Sessions (multi-turn memory)

By default every request is stateless (fresh orchestrator, fresh history). Passing a
`session_id` keeps the conversation history across requests:

1. Create a session: `POST /v1/control {"create": true}` → returns the `session_id`
   (or omit `session_id` on the first chat request — the response returns the new id;
   for `stream: true`, create the session via `/v1/control` first).
2. Send chat requests with `"session_id": "sess-..."` — the agent remembers previous turns.
3. Inspect/reset: `GET /v1/control?session_id=...` and `POST /v1/control` with
   `reset_history: true`.

Sessions are in-memory, expire after 30 minutes of inactivity, and are serialized (one chat
at a time per session). Unknown `session_id` → `404`.

## LLM switching (the pilot endpoint)

The LLM provider is not a server-wide constant: it can be changed **on the fly**, like
switching models in a code editor — per request, or per session. There is no OpenAI-standard
way to do this, so the server exposes the **`POST /v1/control` pilot endpoint** (proprietary
but stable and extensible):

```json
// switch the LLM currently in use for a session
{ "session_id": "sess-...", "llm_provider": "Zai" }
// toggle feature flags (extensible for future features)
{ "session_id": "sess-...", "features": { "voice": true, "tts": true } }
// start a fresh conversation
{ "session_id": "sess-...", "reset_history": true }
// create a session
{ "create": true }
```

`GET /v1/control?session_id=...` returns the full session state: provider in use, model name,
**context window, history size and estimated history tokens**, feature flags and platform
capabilities.

**Context-window guard.** A switch is refused with **`409 context_window_exceeded`** when the
accumulated conversation overflows the target provider's context window — the exact
"on-the-fly switch conflicts with the context window of the model in use" case:

```json
{
  "error": "context_window_exceeded",
  "detail": "The conversation needs ≈44744 tokens but provider 'ExllamaV2_Llama3b' has a context window of 8192 tokens. Reset the conversation (POST /v1/control with reset_history: true) or switch to a provider with a larger context window.",
  "estimated_tokens": 44744,
  "context_window": 8192,
  "provider": "ExllamaV2_Llama3b"
}
```

The same check applies to a per-request `llm_provider` on a session chat. The switch itself
preserves the conversation (history is moved to the new provider's utility). Note that some
providers block while being activated — e.g. `ExllamaV2_Llama3b` auto-starts the local
ExLlamaV2 server and waits for it to become ready (up to 3 minutes, then it fails).

Per-request switching without a session works too: `"llm_provider": "Zai"` on any
`/v1/chat/completions` body.

## TTS — `POST /v1/audio/speech` (standard OpenAI)

In-process **Kokoro neural TTS** (the same engine/voices as the Windows VoiceAgent, but
cross-platform — it runs on Windows **and** Linux). Request:

```bash
curl http://localhost:5290/v1/audio/speech \
  -H "Content-Type: application/json" \
  -d '{"input":"Ciao! Oggi è una bella giornata.","voice":"alloy","speed":1.0}' \
  -o speech.wav
```

- `input` (required), `voice` (OpenAI names `alloy`, `echo`, `fable`, `onyx`, `nova`,
  `shimmer`, `coral`, `sage`, `ash`, `ballad`, `verse` **or** raw Kokoro ids like `if_sara`,
  `af_heart` — see `GET /v1/audio/voices`), `speed` (0.25–4.0, default 1.0).
- **`lang`** (extension): two-letter ISO language. Kokoro voices are per-language
  (`if_*` Italian, `af_*`/`am_*` English, `ef_*` Spanish, `ff_*` French, `jf_*` Japanese, ...).
  When `lang` is omitted the **server's system language** selects the voice — an Italian
  machine speaks Italian (`if_sara`), not accented English. A named `voice` of a different
  language is overridden by `lang` (e.g. `alloy` + `lang: it` → an `if_*` voice).
- Response: `audio/wav` (24 kHz mono 16-bit PCM).
- `response_format` accepts `wav` (default); others → `400`. `model` is accepted for
  compatibility and ignored.
- **501 `tts_unavailable`** when the model assets are missing (see [Build / assets](#build--assets)).

## Voice speech — `POST /v1/voice/listen` (proprietary, Windows)

One-shot speech recognition from the **server microphone** through the
`AIOffice.VoiceAgent.Win.exe` subprocess — the same chain as the AIOffice Voice panel.

```bash
curl http://localhost:5290/v1/voice/listen \
  -H "Content-Type: application/json" \
  -d '{"lang":"it","timeout_seconds":15}'
# → {"text":"quanto fa sette per otto","lang":"it","provider":"voiceagent-win"}
```

- `lang`: two-letter ISO code (default `it`); `timeout_seconds`: 1–60 (default 15).
- **501 `voice_unavailable`** on non-Windows or when the executable is missing
  (`Voice:ExePath`, default: next to the server). The microphone is exclusive — one listener
  at a time. `408` on timeout.

Typical voice chat flow: `voice/listen` → transcript → `chat/completions` → `audio/speech` →
audio back to the client.

## Files — upload once, reference later (OpenAI Files API)

```bash
curl http://localhost:5290/v1/files -F "file=@report.csv" -F "purpose=assistants"
```

- Upload: original binary + server-side Markdown conversion (AllToMarkdown for documents,
  Z.ai GLM-OCR for images). Response: OpenAI metadata + additive `extracted_content` /
  `content_format`; `status` is `processed`/`unsupported`.
- `GET /v1/files/{id}/content` returns the original bytes; `DELETE /v1/files/{id}` removes the
  file (`{"deleted": true}`, `404` when unknown). Chat references files via `file_ids`.
- Limits: 25 MB per upload; in-memory cache, lost on restart (volatile by design).

## `GET /v1/models` — agents **and** LLM providers

Two kinds of entries:

- **Agent sets** (`owned_by: "ai-orchestrator"`): select the agent tools via the chat `model` field.
- **LLM providers** (`owned_by: "llm-provider"`): the actual LLMs behind the agents, each with
  its characteristics — `provider`, `model_name`, `protocol` (`OpenAI`/`Gemini`),
  `context_window`, `base_address`. This is the "read the LLM characteristics" surface: a
  client can pick a provider whose context window fits the task.

`GET /v1/models/{id}` returns a single entry (`404` for unknown ids).

## `GET /v1/control` — capabilities

Without a session id it returns what this platform can do right now:

```json
{
  "capabilities": {
    "platform": "windows",
    "default_provider": "DeepSeekBridge",
    "providers": [ { "name": "Zai", "model_name": "glm-4.7-flash", "protocol": "OpenAI", "context_window": 128000, "base_address": "https://api.z.ai/" }, ... ],
    "tts":   { "available": true, "engine": "kokoro", "voices": [ ... ], "detail": "" },
    "voice": { "available": true, "engine": "voiceagent-win", "detail": "" },
    "sessions": 3
  }
}
```

## Build / assets

The project references `KokoroSharp` (ships `voices/` + `espeak/` to the output) and needs
`kokoro.onnx` (~325 MB, not tracked in git). The build target `DownloadKokoroModel` provides
it automatically: copy from the sibling `AIOffice.VoiceAgent.Win` build output if present,
else `curl` from GitHub. TTS returns 501 until the model is present.

On Windows, if the sibling `AIOffice.VoiceAgent.Win` build output exists, the target
`CopyVoiceAgentOutput` copies the whole VoiceAgent (exe + voices + espeak + model + runtime
deps) next to the server, enabling `POST /v1/voice/listen` out of the box. Without it the
voice endpoint self-reports as unavailable — copy the exe manually or set `Voice:ExePath`.

## Build & publish

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
> Kokoro TTS voices (`voices/`, `espeak/`, `kokoro.onnx` ~325 MB) and, on Windows, the
> `AIOffice.VoiceAgent.Win.exe` voice bridge are **published alongside** (never embedded —
> that would bloat the exe by hundreds of MB). Keep the whole publish folder together and
> run `agent` from it. On Windows the publish copies the sibling VoiceAgent output
> automatically (`CopyVoiceAgentOutput` also runs on Publish).

## Project layout

| File | Purpose |
|---|---|
| `Program.cs` | All endpoints + helpers (top-level statements) + DTOs + launch-mode selection |
| `Tui.cs` | The Qwen-Code-style terminal UI (Spectre.Console): input line, `/` command palette, `@` file palette, shortcuts, streaming chat |
| `SessionStore.cs` | Multi-turn sessions (orchestrator + history + feature flags) |
| `TtsEngine.cs` | In-process Kokoro TTS (lazy init, WAV synthesis) |
| `VoiceBridge.cs` | VoiceAgent.Win subprocess bridge (one-shot recognition) |
| `AgentBridge.csproj` | Web SDK, `AssemblyName=agent`, references AIOrchestrator + Spectre.Console + KokoroSharp, asset targets |
| `AGRNT_ascii_art.txt` | Source of the colored `AGENT` wordmark shown in the TUI |
| `appsettings.json` | Port, LLM provider, voice path |
| `start.bat` / `start.sh` | Launchers (terminal UI by default) |
| `e2e/` | PowerShell regression harness (33+ tests, requires DeepSeekBridge) |
| `e2e/TuiSmoke/` | ConPTY harness that launches the real TUI, injects keystrokes and asserts the UI (logo, `/model` picker + Esc, chat) |

## Testing

```bash
dotnet build AgentBridge.csproj
```

Smoke: `health` → `models` → upload a CSV → `files/{id}/content` → `DELETE` → chat with
`file_ids` → SSE → create session → multi-turn chat → `control` switch → `audio/speech`.
The offline unit tests for the conversion pipeline live in
`AIOrchestrator/AgentOrchestrator.Tests`.

**Terminal UI smoke (Windows):** `e2e/TuiSmoke` launches the real `agent.exe` in a
pseudoconsole (ConPTY), types `/model`, presses Esc, sends a chat message and asserts the
rendered UI. Requires port 5290 free and the Debug build:

```bash
dotnet run --project e2e\TuiSmoke        # 9 checks; exit 0 = all pass
```
