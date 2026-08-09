# AgentBridge — AI agents from your terminal and over the API

**One server, two faces: a full-screen terminal chat and an OpenAI-compatible HTTP API —
the same agents, the same conversation, the same process.**

AgentBridge is a .NET 10 server that hosts the [AIOffice](https://github.com/Graphene-Lab/AIOffice)
agent orchestrator (`AIOrchestrator`) and makes the AI agents available to *everything*:

- **You, in the terminal** — a modern chat UI (built with [Terminal.Gui](https://github.com/tui-cs/Terminal.Gui),
  in the style of [Qwen Code](https://github.com/QwenLM/qwen-code)) with streaming replies,
  a `/` command palette, file attachments, voice dictation and neural text-to-speech.
- **Any OpenAI-compatible client** — scripts, SDKs, bots and apps talk plain
  `POST /v1/chat/completions` to the same agents, no modification needed.

> **CLI and API are the same conversation.** Messages you send from the terminal go through
> the exact same endpoint any other client uses, so you can chat in the TUI while a script
> keeps driving the agents on the same port — simultaneously.

## Why it's interesting

- **Terminal + API in one process** — no separate bridge, no duplicated state, no sync.
- **Switch the LLM on the fly** (`/model` or the API) — with a *context-window guard*: the
  server refuses a switch that would overflow the target model's window and explains why.
- **Voice in the terminal**: `/voice` dictates from the server microphone (Windows),
  `/tts` speaks the replies with in-process Kokoro neural TTS — in the UI *and* over the API.
- **Upload-and-attach files** with server-side Markdown conversion (documents and images);
  attachments ride along on chat requests as `file_ids`.
- **Agents with tools** — web, search, research, Word, spreadsheet, email and multi-agent
  sets: pick the right tools with `/agent` or the `model` field.

## How it works

`AIOrchestrator` is a .NET library (it cannot run on its own). AgentBridge hosts it and
exposes its chat pipeline as a standard web API:

```
Terminal UI  ──┐
               ├──▶ AgentBridge (this server, one process)
OpenAI client ─┘            │
                            ▼
                     AIOrchestrator (agents + tools)
                            │
                            ▼
     LLMs (DeepSeek · Z.ai · Gemini · Ollama · ExLlamaV2 · ...)
     + agent tools (web, search, Word, spreadsheet, email)
     + Kokoro neural TTS (in-process) + VoiceAgent.Win (Windows speech)
```

Any OpenAI SDK, script or standalone client — such as
[Giraffe AI](../GiraffeAI/index.html) — can drive the AI agents without modification.

## Quick start

**Prebuilt executables** — download the archive for your platform from the
[Releases page](https://github.com/Graphene-Lab/AgentBridge/releases) (Windows `win-x64`,
Linux `linux-x64`, macOS `osx-x64` / `osx-arm64`): extract and run `agent.exe` / `agent`,
no .NET installation required. Each archive includes the Kokoro TTS voices and model, so
text-to-speech works out of the box.

Run the executable directly — `agent.exe` on Windows, `agent` on Linux/macOS — or, in development:

```bash
cd AgentBridge
dotnet run --project AgentBridge.csproj
```

The console opens the **terminal UI** and the server keeps answering API calls in the same
process on `http://localhost:5290` (change the port via the `Urls` key in
`appsettings.json` or the `ASPNETCORE_URLS` environment variable). Add `--headless` for
the plain server console (scripts, CI, services).

```bash
curl http://localhost:5290/health   # {"status":"healthy","timestamp":"..."}
```

## Terminal UI

A full-screen chat in your console: menu bar, AGENT logo, a streaming chat panel with an
input line at the bottom and a status bar showing server, provider, model, session and
context usage.

```text
Scrivi un messaggio e premi Invio  /  apre i comandi  @ i file  ? le scorciatoie
```

| You type | What happens |
|---|---|
| a plain message + `Enter` | the agents reply, streaming into the conversation |
| `/` | command palette — filters as you type, `Tab` completes, `Enter` runs |
| `/model` | switch the LLM provider (with context-window guard) |
| `@` | attach/detach uploaded files to the chat |
| `/voice` · `/tts` | dictate from the microphone · hear the reply spoken |
| `?` · `F1` | shortcuts overlay · full help |

Everything is discoverable in the UI itself: hints in every dialog, `?` for shortcuts,
`/help` for the full guide. The complete reference — every command, shortcut and mouse
action — is in **[docs/TUI.md](docs/TUI.md)**.

## The API

Standard OpenAI Chat Completions plus small, documented extensions for the features that
have no OpenAI equivalent (sessions, LLM switching, voice, capabilities):

```bash
curl -N http://localhost:5290/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"web-agent","messages":[{"role":"user","content":"What is the weather today?"}]}'
```

The full reference — endpoints, request/response details, sessions, LLM switching,
TTS, voice and files — is in **[docs/API.md](docs/API.md)**.

## Documentation

This README is the friendly introduction. The technical documentation lives in
separate files:

| File | Contents |
|---|---|
| [docs/TUI.md](docs/TUI.md) | Terminal UI guide: every command, keyboard shortcut and mouse action |
| [docs/API.md](docs/API.md) | HTTP API reference: endpoints, sessions, LLM switching, TTS, voice, files |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Architecture, configuration, launch modes, build & publish, project layout, testing |
| [docs/RELEASING.md](docs/RELEASING.md) | *(developers)* how releases, NuGet packages and the automatic update system work |

---

Built on the [AIOffice](https://github.com/Graphene-Lab/AIOffice) ecosystem ·
[AgentBridge](https://github.com/Graphene-Lab/AgentBridge) repository.
