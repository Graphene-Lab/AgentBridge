# 🦒 Giraffe AI

> **The lightest, most powerful AI chat client. Zero installation, zero dependencies — start and chat.**

Giraffe AI is a featherweight AI chat client that runs directly in your browser. Nothing to install, nothing to configure, no runtime to download: double-click the launcher and you are chatting with your AI models in seconds. It is the fastest, simplest and most resource-efficient way to talk to local or cloud AI models — an immediate, professional chat experience that barely touches your system.

**No installation · No dependencies · No setup · Start and go.**

---

## Why Giraffe AI?

| | |
|---|---|
| ⚡ **Instant start** | No installer, no package manager, no build step. Launch and chat immediately. |
| 🪶 **Ultra-lightweight** | A single HTML file with near-zero RAM and CPU usage — it does not occupy resources. |
| 🔌 **Any AI model** | OpenAI, DeepSeek, Groq, OpenRouter, Anthropic (via OpenRouter), local models via Ollama, or any OpenAI-compatible endpoint. |
| 🚀 **Zero install** | Ships with ready-made launchers for Windows, Linux and macOS. |
| 🔒 **Private by design** | Conversations and settings live in your browser's local storage; your API keys never leave your machine. |
| 💬 **Professional chat** | Chat history, Markdown rendering, code blocks with one-click copy, reasoning/thinking display, regenerate answers, fine-grained settings (temperature, max tokens, top-p). |
| 🌍 **Multi-language UI** | English, Italian, French, German, Spanish, Russian. |

## Quick Start

**Windows** — double-click `start.bat`

**Linux / macOS** — run `./start.sh`

The launcher starts a tiny local server, opens the app in app-mode, and proxies `/v1/*` requests to a local OpenAI-compatible model server (e.g. llama.cpp, Ollama, LM Studio) on `127.0.0.1:8787` — so local models work with no CORS workarounds and no API key.

Cloud providers (OpenAI, DeepSeek, Groq, OpenRouter, …) are configured in seconds from the built-in presets: pick a preset, paste your API key, done.

Alternatively, open `index.html` directly in any modern browser. Cloud APIs can be used immediately; the launcher is only needed for local model servers.

## Supported Providers

| Provider | Type | Preset | Native format |
|---|---|---|---|
| OpenAI (GPT-4o, o3-mini) | Cloud API | ✅ | OpenAI-compatible |
| DeepSeek (chat, R1 reasoner) | Cloud API | ✅ | OpenAI-compatible |
| Ollama (local) | Local model | ✅ | Ollama `/api/chat` |
| Groq (Llama 3.3) | Cloud API | ✅ | OpenAI-compatible |
| OpenRouter | Cloud API | ✅ | OpenAI-compatible |
| Anthropic Claude (via OpenRouter) | Cloud API | ✅ | OpenAI-compatible |
| Anthropic Claude (native) | Cloud API | ✅ | Anthropic Messages API |
| Gemini 2.0 Flash (native) | Cloud API | ✅ | Gemini API (`inlineData`) |
| Any OpenAI-compatible endpoint | Cloud / Local | Custom | OpenAI-compatible |

## Features

- **Multiple providers & models** per workspace, switchable from the sidebar
- **Chat history** per provider, persisted locally
- **Markdown rendering** with syntax-highlighted code blocks and copy button
- **Reasoning / thinking display** for reasoning models (e.g. DeepSeek R1, o3-mini)
- **Regenerate & copy** for any assistant message
- **Chat settings** — system prompt, temperature, max tokens, top-p (global or per chat)
- **Light / dark theme** and **6 UI languages**
- **Installable as a PWA** (app-mode / standalone window)
- **Streaming responses** with stop-generation control
- **Multi-file attachments** — attach several files at once (or one at a time) to any message; the original bytes are uploaded and converted to Markdown **server-side** by the connected backend (AgentBridge `/v1/files`, or any OpenAI-compatible file endpoint)

## Giraffe AI vs. the Big Five

Compared with the five most popular AI chat platforms — **ChatGPT**, **Claude.ai**, **Gemini**, **Open WebUI** and **LM Studio** — Giraffe AI stands out as the simplest and lightest way to use LLMs:

| | 🦒 Giraffe AI | ChatGPT | Claude.ai | Gemini | Open WebUI | LM Studio |
|---|---|---|---|---|---|---|
| **Installation** | None — a single HTML file | App + account | App + account | App + account | Docker / Python environment | Desktop installer |
| **Dependencies** | Zero | Vendor app | Vendor app | Vendor app | Python + many packages | Bundled runtime |
| **Time to first chat** | Seconds — start and go | Sign-up + sign-in | Sign-up + sign-in | Sign-up + sign-in | Long setup | Download + install |
| **Resource usage** | Minimal (one browser tab) | Heavy browser app | Heavy browser app | Heavy browser app | Server + database always running | Multi-GB install |
| **Models supported** | Any — OpenAI, DeepSeek, Groq, OpenRouter, Anthropic, Ollama, custom | OpenAI only | Anthropic only | Google only | Any | Local only |
| **Local models** | ✅ built-in proxy, no CORS hacks | ❌ | ❌ | ❌ | ✅ (adds complexity) | ✅ |
| **Privacy** | Data stays in your browser (localStorage) | Stored on servers | Stored on servers | Stored on servers | Self-hosted | Local |
| **Offline** | ✅ (with local models) | ❌ | ❌ | ❌ | ✅ | ✅ |
| **Cost** | Free | Subscription | Subscription | Subscription | Free (self-hosted) | Free |
| **App-like experience** | ✅ optional PWA install | ✅ | ✅ | ✅ | ❌ | ✅ |

### What makes Giraffe AI different

- **Zero-install innovation** — the entire client is one self-contained HTML file: no framework, no Node.js, no Python required to use it. Every other client above needs an installer, a package manager, or a full runtime environment.
- **Instant by design** — no account, no onboarding, no build step. Double-click and you are talking to an LLM.
- **Provider-agnostic** — one lightweight UI for cloud APIs *and* local models. No vendor lock-in, no model ceiling.
- **Near-zero footprint** — nothing runs in the background, no server daemon, no database. It occupies essentially no resources.
- **Private by design** — API keys and chat history never leave your machine; the only network calls go to the model providers you explicitly configure.
- **Smart same-origin proxy** — the launcher embeds a tiny reverse proxy (`/v1/*` → `127.0.0.1:8787`) so local models work without CORS workarounds, browser flags, or extensions.

## Giraffe AI vs. other API clients

There is a whole family of apps that, like Giraffe AI, are *pure clients*: you bring your own API key and connect to whichever provider you want. Compared with the most popular of them — **Chatbox**, **Jan**, **AnythingLLM**, **NextChat** and **LibreChat** — Giraffe AI is the only one that delivers the same power with no application chassis at all:

| | 🦒 Giraffe AI | Chatbox | Jan | AnythingLLM | NextChat | LibreChat |
|---|---|---|---|---|---|---|
| **Type** | Web client — single HTML file | Desktop app (Electron) | Desktop app (Electron) | Desktop app / Docker | Self-hosted web app (Next.js) | Self-hosted web app (Next.js) |
| **Installation** | None — open the file, start and go | Installer | Installer | Installer or Docker | npm + Node.js | Docker or npm + MongoDB |
| **Dependencies** | Zero | Bundled Electron runtime | Bundled Electron runtime | Node.js + vector DB | Node.js + npm packages | Node.js + MongoDB |
| **Resource usage** | Minimal — one browser tab | Hundreds of MB (Electron) | Hundreds of MB (Electron) | Server + database | Node.js server | Node.js + database servers |
| **Time to first chat** | Seconds | Install + setup | Install + setup | Setup + configuration | Build + deploy | Full-stack setup |
| **Portable single file** | ✅ — copy it anywhere, it runs | ❌ | ❌ | ❌ | ❌ | ❌ |
| **Models** | Any OpenAI-compatible + local | Many (by key) | Local + cloud (by key) | Many + RAG | Many (by key) | Many (by key) |
| **Offline** | ✅ (with local models) | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Cost** | Free | Freemium | Free, open source | Free, open source | Free, open source | Free, open source |

These apps share Giraffe AI's philosophy — one UI, any provider — but they carry a heavy chassis: an Electron runtime, a Node.js stack, or a Docker + database setup that must be installed, updated and maintained before you can chat. Giraffe AI delivers the same capability as a **single portable HTML file** that runs anywhere a browser runs: nothing to install, nothing to update, nothing running in the background.

## How It Works

Giraffe AI is intentionally simple:

- `index.html` — the entire chat client, self-contained (HTML + CSS + JS), no external libraries.
- `start.bat` — Windows launcher: a built-in PowerShell `HttpListener` on port `8000` that serves the app and proxies `/v1/*` to `127.0.0.1:8787` (same-origin, so no CORS).
- `start.sh` — Linux/macOS launcher: uses `python3` (with the macOS built-in Ruby as fallback) for the same role.
- All data is stored in the browser's `localStorage` — nothing is sent anywhere except the model APIs you configure.

## Auto-configuration (`--provider`)

Start the launcher with a provider configuration and the client registers it (if not already
present, same `name` + `endpoint` + `format`) and selects it as active — so AgentBridge and
similar hosts can open a ready-to-chat window with no manual setup:

```bash
# Linux / macOS — raw JSON (URL-encoded automatically by the launcher):
./start.sh --provider '{"name":"AgentBridge","format":"openai","model":"default-agent","endpoint":"http://localhost:5290/v1/chat/completions"}'

# Windows — the JSON is passed base64url-encoded (no padding; safe on the cmd.exe command line):
start.bat --provider eyJuYW1lIjoiQWdlbnRCcmlkZ2UiLCJmb3JtYXQiOiJvcGVuYWkiLCJtb2RlbCI6ImRlZmF1bHQtYWdlbnQiLCJlbmRwb2ludCI6Imh0dHA6Ly9sb2NhbGhvc3Q6NTI5MC92MS9jaGF0L2NvbXBsZXRpb25zIn0
```

The launcher appends `?provider=<url-encoded JSON>` to the opened URL; on load the client
decodes it, registers/selects the provider and clears the query string (a later refresh does
not re-apply it). Supported fields: `name`, `format` (`openai` / `ollama` / `anthropic` /
`gemini`), `model`, `endpoint`, `apiKey`, `temperature`, `maxTokens`, `topP`,
`reasoningEnabled`, `systemPrompt`, `attachments`, `textLimit`, `id`.

> ⚠️ **Security note:** when an `apiKey` is included it is visible in the URL and stored in
> the browser's `localStorage`. Keep `--provider` auto-config for local/trusted hosts and
> avoid passing secrets on shared machines.

## File Attachments

Attach files to any message via the paperclip button: several files can be selected at once
(Ctrl/Shift-click) or added one at a time; the selected files are listed under the input and can
be removed individually. Attachment support is **per-provider configurable**:

- **Text files are always available.** A large list of textual extensions (`.txt`, `.csv`, `.md`,
  `.yaml`, `.py`, `.js`, `.ts`, `.json`, `.xml`, config/code files, `Dockerfile`, `Makefile`,
  `README`, …) is always attachable: the client reads them in the browser (`FileReader`) and
  embeds their content in the message (truncated to the provider's **Text Char Limit / File**,
  default 30 000 chars). No backend needed.
- **Images & documents need the provider flag.** The provider form has a toggle
  **"Allow image/document attachments"** (on by default). When off, only text files can be
  attached. When on:
  - *Images* are sent inline (base64) using the provider's native format — `image_url` for
    OpenAI-compatible, `source.base64` for Anthropic, `inlineData` for Gemini, `images[]` for
    Ollama.
  - *Documents* (PDF/DOCX/XLSX/…) are uploaded to the backend `POST /v1/files`
    (e.g. AgentBridge → AllToMarkdown → `file_id`); the chat request includes the `file_ids`
    in the request body (`file_ids`) as context. If the upload fails, the send is **blocked with a
    clear error** instead of silently losing the content. Anthropic native also supports documents
    inline (base64) and falls back to it when the provider has no `/files` endpoint.
- The text content of each textual file is embedded **once**: it is shown in the message bubble
  and sent in the API payload a single time (no duplication).

Provider presets set sensible defaults; the toggle and the text limit are editable per provider
in the provider modal. All of this is implemented inside `index.html` — consistent with the
single-file philosophy.

### Tests

`tests/test_attachments.js` runs the attachment logic headlessly (no browser needed):
requires Node.js and executes the pure functions extracted from `index.html`
(`isTextualFile`, `uploadFiles`, `buildMessageWithAttachments`,
`buildAPIMessagesWithFiles` — including the per-format payloads for OpenAI, Ollama,
Anthropic and Gemini, and the clear-error path when a binary upload is not supported):

```bash
node tests/test_attachments.js
```

## Contributing

The goal of Giraffe AI is to be a **complete, functional and powerful chat client** built on a **radically minimalist foundation**. Every contribution must preserve these principles:

- **No installation for the user** — the app must keep running as a single, self-contained `index.html` opened directly in any modern browser. No build steps, package managers, or runtime requirements for end users.
- **Zero dependencies** — no external libraries, frameworks, or CDN imports. Everything stays hand-rolled vanilla HTML/CSS/JS, exactly as it is today.
- **Minimalist under the hood** — prefer simple, readable code over abstractions. Add a feature only if it does not require installing complex components or moving the project to a full toolchain.
- **Powerful by value, not by weight** — streaming, reasoning display, multi-provider support and chat history belong in the client; heavy infrastructure does not.
- **The launcher stays optional** — `start.bat` / `start.sh` only serve the page and proxy local models for convenience; they must never become a requirement to run the app.

If you want to contribute, open an issue first to discuss the change — especially anything that would introduce a dependency or an installation step. Pull requests that keep the project dependency-free and single-file are always welcome.

## Search Keywords

If you are looking for any of the following, this project is for you: lightweight AI chat client, no-install AI chat, zero-dependency LLM client, portable AI chatbot, minimal resource usage chat, browser-based AI chat, local LLM web client, OpenAI-compatible chat UI, lightweight ChatGPT alternative, offline-friendly AI assistant.

---

**Giraffe AI** — simple, powerful, immediate. No installation, no dependencies. Start and go.
