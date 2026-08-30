# AgentBridge — user documentation

This folder ships **next to the executable** in every release archive (`docs/`) and is the
reference for people who install and operate AgentBridge. The developer documentation lives
in `docs-dev/` in the repository and is **not** distributed.

## Beginner guides

A series of short guides written in plain language for anyone who uses AgentBridge day to day. No technical knowledge is needed: each guide covers one aspect, in the order you are most likely to meet it.

| Guide | What it covers |
|---|---|
| [Getting started](guide/01-Getting-Started.md) | Download, first start, the first indexing |
| [Choosing your AI](guide/02-Choosing-Your-AI.md) | Cloud and local providers, API keys, switching |
| [Chatting with your agent](guide/03-Chatting-with-Your-Agent.md) | The chat window, files, sessions, the web version |
| [Your documents area](guide/04-Your-Documents-Area.md) | The folder the agent reads, indexing, asking questions |
| [Creating documents](guide/05-Creating-Documents.md) | Word, Excel, PowerPoint and PDF from a request |
| [Email](guide/06-Email.md) | Reading and sending mail with your account |
| [Web research](guide/07-Web-Research.md) | Searching the internet, sources and reports |
| [Voice](guide/08-Voice.md) | Dictating and hearing the assistant speak |
| [Phone access](guide/09-Phone-Access.md) | Calling the assistant, the PIN, voice calls |
| [Telegram](guide/10-Telegram.md) | Chatting with the assistant in Telegram |
| [Scheduled tasks](guide/11-Scheduled-Tasks.md) | Deadlines and recurring work the agent does alone |
| [Podcasts](guide/12-Podcasts.md) | Complete podcast episodes from one request |
| [Privacy and security](guide/13-Privacy-and-Security.md) | The sandbox, anonymisation, what leaves your machine |
| [The agent's memory](guide/14-The-Agents-Memory.md) | How the assistant remembers your work |
| [Updates](guide/15-Updates.md) | How updates work and what they never touch |

## Guides

| Guide | What it covers |
|---|---|
| [Manual](MANUAL.md) | **Start here** — install, configure the JSON files, use the terminal UI, connect OpenAI or MCP clients |
| [Terminal UI reference](TUI.md) | Every command, shortcut and mouse action |
| [HTTP API reference](API.md) | All endpoints: chat, sessions, LLM switching, TTS, voice, files, MCP connector |
| [Telegram chat](telegram.md) | Connect the agents to Telegram: config, first login, allow-list, attachments |
| [SIP telephony](sip.md) | Phone-gate the agents: config, PIN/allow-list security, NAT/trunk, STT deployment |
| [SIP entry point](sip-entry/README.md) | Unattended Kamailio + rtpengine entry-point installation |
| [Auto-update](autoupdate.md) | How the automatic update works and what it never touches |
| [AIOrchestrator white paper](AIORCHESTRATOR-WHITEPAPER.md) | Why the AIOrchestrator library model outperforms MCP deployments |
| [Disclaimer](DISCLAIMER.md) | Warranty and liability terms, including AI-generated content and autonomous agent actions |
| [Windows installer](install.ps1) · [Linux/macOS installer](install.sh) | One-line installers for the release archives |

## Rules for contributors

- **User guides live in this folder.** They are copied to the build output and into every
  release archive automatically — if a guide is not here, it never reaches the installed app.
- **Developer guides** (architecture, release pipeline, TUI internals) live in `docs-dev/`
  and are **not** shipped.
- **`media/`** holds README demo assets only — never shipped.
