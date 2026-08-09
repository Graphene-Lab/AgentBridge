# AgentBridge — AI agents in your terminal and via an OpenAI-compatible API

**AgentBridge is a self-hosted .NET server that runs AI agents with two faces in one
process: a full-screen terminal chat (TUI) and a standard OpenAI-compatible HTTP API.**
Chat in the terminal while scripts, bots and apps drive the same agents on the same port —
same process, same conversations, no bridge, no sync.

Built on the [AIOffice](https://github.com/Graphene-Lab/AIOffice) agent orchestrator
(`AIOrchestrator`), AgentBridge brings the agents to **everything**:

- **You, in the terminal** — a modern chat UI (Terminal.Gui) with streaming replies, a `/`
  command palette, file attachments, voice dictation and in-process neural text-to-speech.
- **Any OpenAI-compatible client** — SDKs, bots and scripts talk plain
  `POST /v1/chat/completions` to the same agents. No plugin, no custom SDK, no lock-in.

## What you can do with it

- **Chat with AI agents from the terminal** — streaming replies, `@` file attachments,
  prompt history, sessions, all in a full-screen UI with a command palette.
- **Open the web GUI in one keystroke** — `/web` (menu **Web → GUI**) downloads on first run
  and launches the Giraffe AI web client in the browser, already connected to this server.
- **Expose the agents as a local OpenAI server** — point any OpenAI client at
  `http://localhost:5290/v1` and it drives the agents without modification (see the
  [manual](docs/MANUAL.md#connecting-a-client-to-localhost)).
- **Switch the LLM on the fly** — DeepSeek, Z.ai, Gemini, Ollama, ExLlamaV2 and more, with
  a context-window guard that refuses an overflow and explains why.
- **Configure models & providers from the UI** — `/modelsetup` (menu **File → Models &
  Providers**) adds, edits or removes providers, picks the active model, sets the API keys,
  SMTP/IMAP and documents path — no JSON editing required.
- **Voice in the terminal** — dictate from the server microphone (Windows) and hear the
  replies spoken by Kokoro neural TTS, in the UI and over the API.
- **Upload-and-attach files** — documents and images converted to Markdown server-side,
  attached to chat requests as `file_ids`.
- **Agents with tools** — web, search, research, Word, spreadsheet, email and multi-agent
  sets; pick the right tools per chat with `/agent` or the API `model` field.
- **One conversation everywhere** — messages from the terminal go through the exact same
  endpoint any client uses, so you can chat in the TUI while a script keeps driving the
  agents on the same port, simultaneously.

## Quick start

Download the archive for your platform from the
[Releases page](https://github.com/Graphene-Lab/AgentBridge/releases) (Windows `win-x64`,
Linux `linux-x64`, macOS `osx-x64` / `osx-arm64`), extract and run `agent.exe` / `agent`.
No .NET installation needed; each archive already includes the Kokoro TTS voices and model.

```bash
curl http://localhost:5290/health   # {"status":"healthy","timestamp":"..."}
```

The full step-by-step guide — installation, JSON configuration, the terminal UI and how a
client connects to localhost — is in the **[user manual](docs/MANUAL.md)**.

## Documentation

| Document | Audience / contents |
|---|---|
| [User manual](docs/MANUAL.md) | **Start here.** Install, configure the JSON files, use the terminal UI, connect a client |
| [Terminal UI reference](docs/TUI.md) | Every command, shortcut and mouse action |
| [HTTP API reference](docs/API.md) | All endpoints: chat, sessions, LLM switching, TTS, voice, files |
| [Architecture & operations](docs/ARCHITECTURE.md) | Launch modes, configuration keys, build & publish, project layout |
| [Releases & NuGet pipeline](docs/RELEASING.md) | *(developers)* how updates and releases work |

## How it works

`AIOrchestrator` is a .NET library — it cannot run alone. AgentBridge hosts it and exposes
its chat pipeline as a standard web API:

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

---

Built on the [AIOffice](https://github.com/Graphene-Lab/AIOffice) ecosystem ·
[AgentBridge](https://github.com/Graphene-Lab/AgentBridge) repository.
