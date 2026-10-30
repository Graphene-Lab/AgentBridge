# AgentBridge, AIOrchestrator and AIOffice — developer architecture (Mermaid diagrams)

Developer-facing notes that sketch how the AIOffice ecosystem is wired together. Every file
is a Markdown document with **Mermaid** diagrams (rendered by
[MermaidRendering](../MermaidRendering/, v11 — the same engine as the official Mermaid Live
Editor). Open the `.md` files in a Markdown viewer that supports Mermaid, or paste the
`graph` blocks into <https://mermaid.live>.

## Contents

| File | What it shows |
|---|---|
| [01-AgentBridge-and-clients.md](01-AgentBridge-and-clients.md) | AgentBridge as an OpenAI-compatible HTTP server: which clients reach it and how. |
| [02-AgentBridge-AIOrchestrator-Tools.md](02-AgentBridge-AIOrchestrator-Tools.md) | The AgentBridge → AIOrchestrator (AgentHarness) → system tools → plugin tools relation, with the lean orchestrator. |
| [03-Streaming-calls.md](03-Streaming-calls.md) | How the streaming (SSE) calls work from `/v1/chat/completions` up to the LLM and back. |
| [04-Telegram.md](04-Telegram.md) | The Telegram bridge implementation (WTelegramClient userbot, per-user sessions, access control). |
| [05-Software-parts.md](05-Software-parts.md) | The software parts (AIOrchestrator library, plugins, AIOffice/AgentBridge hosts) and their interactions. |

> These diagrams are a *navigational aid*, not a full contract. The authoritative behavior
> lives in the code (`AgentBridge/*.cs`, `AIOrchestrator/*.cs`, `AIOffice/*.cs`) and in
> `AgentBridge/docs/API.md`, `AgentBridge/docs-dev/ARCHITECTURE.md` and the AIOrchestrator
> `ARCHITECTURE.md`.
