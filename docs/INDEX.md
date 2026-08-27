# AgentBridge — user documentation

This folder ships **next to the executable** in every release archive (`docs/`) and is the
reference for people who install and operate AgentBridge. The developer documentation lives
in `docs-dev/` in the repository and is **not** distributed.

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
| [Windows installer](install.ps1) · [Linux/macOS installer](install.sh) | One-line installers for the release archives |

## Rules for contributors

- **User guides live in this folder.** They are copied to the build output and into every
  release archive automatically — if a guide is not here, it never reaches the installed app.
- **Developer guides** (architecture, release pipeline, TUI internals) live in `docs-dev/`
  and are **not** shipped.
- **`media/`** holds README demo assets only — never shipped.
