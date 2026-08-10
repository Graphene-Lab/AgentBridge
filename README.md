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

## Agent Bridge: Your AI Assistant for Office Work

Agent Bridge is a tool that allows you to connect to your preferred AI, transforming it into your personal assistant: a tireless worker capable of handling office tasks such as drafting complex documents, working with spreadsheets, interacting with email, and performing internet-based activities—all while having full awareness of your company's knowledge base: clients, documents, products, and everything stored in your archive.

Agent Bridge positions itself as a cloud platform for businesses or private individuals seeking AI solutions. Everything uploaded to the cloud area becomes part of the AI's knowledge, enabling it, with full understanding of your documents and data, to work as a tireless employee and carry out office work. The product fits within the enterprise segment, capable of storing and managing even several terabytes of data, and can generate PDF documents with a level of detail and precision that is unmatched.

## Your documents area: the company's brain and memory

Think of the documents area as your company's brain. It is a simple folder on disk — nothing special, just a place where your files already live. Whatever you put in it, the AI can read, remember and use: contracts, invoices, client files, product sheets, emails, spreadsheets, technical drawings... everything. It does not matter how much data you have or how big the files are: the more you store, the smarter your AI becomes, because it answers using *your* real documents, not guesses. You never upload anything into the chat — you just keep working with your normal folders, and the AI reaches into them directly.

### How to configure the documents area

1. Open AgentBridge and type `/modelsetup` (menu **File → Models & Providers**).
2. In the **General** tab, set **Documents path** to the folder that holds your documents. The default is your personal Documents folder — you can change it at any time.
3. Press **Save**. AgentBridge starts reading that area in the background: the first indexing of a large archive takes a few minutes, but you can keep working while it runs.
4. From now on, ask the AI anything about your documents — it searches the whole area and answers from your data. If you move to a different folder, just change the path again: AgentBridge automatically re-indexes the new area.

## Comparison with Main Alternative Products

| Product | Target Audience | Key Strength | Main Integrations |
| :--- | :--- | :--- | :--- |
| **Agent Bridge** | Businesses and individuals | Cloud platform for "all-in-one" AI assistant | Preferred AI, company archives |
| **Claude for Small Business** | Small businesses | Ready-to-use workflows for operational tasks | QuickBooks, PayPal, HubSpot, Canva, DocuSign |
| **Microsoft Scout** | Microsoft 365 companies | Autonomous, proactive AI agent always active | Outlook, Teams, SharePoint, OneDrive |
| **Nono CoWork** | Power users and developers | Proactive agent always active on VPS | Email, synced folders, Telegram |
| **171305 Cowork** | Power users and developers | Local AI workspace, privacy-first | Gmail, Calendar, Drive, Sheets, Ollama |
| **Templafy** | Large enterprises | Platform for compliant, branded documents | Office, CRM, Claude, Copilot, ChatGPT |

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
