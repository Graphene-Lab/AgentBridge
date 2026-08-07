# MinimalChatApi

**OpenAI-compatible HTTP server for `AgentOrchestrator` (AIOrchestrator)**

MinimalChatApi is a small .NET 10 web application that hosts the **AIOrchestrator** library
(which is a library, not directly executable) and exposes its chat pipeline through standard
**OpenAI-compatible REST endpoints**. Any OpenAI SDK, script or standalone client — such as
[Giraffe AI](../GiraffeAI/index.html) — can drive the AI agents without modification.

## Why it exists

- **AIOffice** uses the agents in-process (Blazor Server UI + Voice panel) — no HTTP layer needed.
- **Standalone clients** (a plain HTML file, a Python/curl script, an OpenAI SDK) have no
  access to that in-process pipeline. MinimalChatApi gives them the same agents through
  `POST /v1/chat/completions`, plus a file-upload flow for attachments.

```
Standalone client ──HTTP──▶ MinimalChatApi (this project)
                                 │  hosts (references)
                                 ▼
                          AIOrchestrator (AgentOrchestrator)
                                 │
                                 ▼
                    LLM (Ollama / DeepSeek / Z.ai / Gemini / DeepSeekBridge)
                    + agent tools (WebTool, FileTool, WordTool, SpreadsheetTool)
```

## Quick start

**Windows** — double-click `start.bat`

**Linux / macOS** — run `./start.sh`

Or manually:

```bash
cd MinimalChatApi
dotnet run --project MinimalChatApi.csproj
```

The server listens on `http://localhost:5290` (configurable via the `Urls` key in
`appsettings.json` or the `ASPNETCORE_URLS` environment variable).

Verify it is up:

```bash
curl http://localhost:5290/health
# {"status":"healthy","timestamp":"..."}
```

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
  }
}
```

| Key | Values | Description |
|---|---|---|
| `LLM:Provider` | `Ollama_Granite3b`, `DeepSeek`, `DeepSeekBridge`, `Zai`, `Gemini`, ... | Which `LLMUtility.LLMProvider` the `AgentOrchestrator` uses (one instance per HTTP request). Default `DeepSeekBridge` (see the codex-deepseek-bridge proxy on `127.0.0.1:8787`). |
| `LLM:Anonymize` | `true` / `false` | When `true`, NameOrKey elements (names, keys) found in prompts, support documents and tool results are replaced with placeholders before they reach the LLM and translated back in the response. Applies to the main agent **and** subagent sessions. See `LLMUtility` for details. |
| `SkipIndexingOnStartup` | `true` / `false` | When `true`, the DocumentsPath index is neither built nor refreshed and the file watcher is not started. Use during debug/dev when **no document searches are needed** — skips the multi-minute full index on large folders. File searches then return empty results. |
| `Urls` | e.g. `http://localhost:5290` | Kestrel listening address. |

### Command-line overrides & `--help`

Every key in `appsettings.json` can be overridden from the command line with `--Key:SubKey <value>`
(the ASP.NET config chain gives CLI precedence over `appsettings.json`), so a single
`builder.Configuration` read covers both sources:

```bash
dotnet run --project MinimalChatApi.csproj -- --LLM:Provider Zai
dotnet run --project MinimalChatApi.csproj -- --LLM:Anonymize true
dotnet run --project MinimalChatApi.csproj -- --SkipIndexingOnStartup true
```

Run `--help` (or `-h`) to print the supported options:

```bash
dotnet run --project MinimalChatApi.csproj -- --help
```

> **⚠️ For coding agents & debug runs**: this server indexes the `DocumentsPath` folder at
> startup (full build if no index exists, incremental refresh in Release). On a large folder
> (e.g. the developer's Documents, 33k files) the initial index takes **minutes**, burns
> CPU/RAM and logs nothing until it finishes — it looks hung but is working. When the feature
> under test does **not** need document searches (chat-only, streaming, file-upload flows,
> agent-loop fixes), start the server with `--SkipIndexingOnStartup true` for a fast, quiet
> startup. Only drop it when the test actually queries the Documents index (FileTool searches,
> auto-search context).
>
> **Note**: the `LLM:Anonymize` flag requires an explicit boolean value (`--LLM:Anonymize true`).
> A bare flag without a value reads as `false`.
>
> **Streaming caveat**: LLM-native streaming (`SendQueryStream`) does not support
> anonymization and throws `NotSupportedException` — the `/v1/chat/completions` SSE endpoint
> here is response-side only (the agent result is computed with non-streaming `SendQuery`),
> so it is unaffected.

## Endpoints

### `POST /v1/chat/completions` — chat with the agents

OpenAI Chat Completions compatible. `model` selects which agents are used
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
| `model` | Agent set to use: `default-agent`, `web-agent`, `search-agent`, `word-agent`, `spreadsheet-agent`, `multi-agent`. |
| `messages` | OpenAI messages; the last `user` message is the prompt. |
| `file_ids` | Optional ids from `POST /v1/files` — attached as context (converted to Markdown server-side). |
| `max_tokens` | Roughly maps to agent loop iterations (`max_tokens / 100`, clamped 1–50). |
| `stream` | `true` → SSE chunks; `false` (default) → single JSON response with `usage`. |

### `POST /v1/files` — upload a file (multipart)

Uploads the file in its **original binary** format and converts it to Markdown **server-side**
(AllToMarkdown for documents, Z.ai GLM-OCR for images when `Setup.ZaiApiKey` is configured).
Same rule as the Blazor UI: never client-side.

```bash
curl http://localhost:5290/v1/files \
  -F "file=@report.csv" -F "purpose=assistants"
```

Response: `{"id": "file-...", "status": "processed"|"unsupported", "extracted_content": "..."}`.

The response also carries the OpenAI metadata (`object`, `bytes`, `created_at`, `filename`,
`purpose`): the **filename is metadata** here — OpenAI convention — never embedded as YAML
frontmatter in the converted content. The name is preserved on the cached file and surfaces
again in the LLM prompt as a `[File: {FileName}]` header (see "File attachment flow").

`status` is `processed` only when the server-side conversion produced **non-empty** Markdown;
a file that parses but yields no text (e.g. a PDF with no extractable content) is reported as
`unsupported` — consistent with `GET /v1/files/{id}`. The upload response and the cached GET
share the same rule, so clients never see `processed` with empty content.

Limits: 25 MB per upload (hard-coded); files are kept in an **in-memory** cache, lost on restart.

> **Concurrency**: the server is multi-request. Each HTTP request gets its **own**
> `AgentOrchestrator` (DI scoped), so the conversation history and the subagent sessions are
> never shared across requests; the remaining shared statics (`Log.LogStep`, `GUIChatHistory`,
> auto-search dedup) are lock-protected. Parallel chat requests are supported — verified with
> 2 and 4 simultaneous chats in the e2e campaign.

> **E2E regression harness**: `e2e/run_e2e.ps1` (+ `e2e/make_corpus.ps1`) runs a 33-test
> battery against a live server (chat, streaming, agent sets, auto-search, uploads of 8 formats,
> `file_ids`, error paths, concurrency). Requires the DeepSeekBridge on `127.0.0.1:8787` and a
> server started with `DocumentsPath` pointing to the generated corpus
> (via `rag_settings.json` next to the executable).

### `GET /v1/files/{id}` — retrieve a converted file

Returns the file metadata plus its Markdown `extracted_content`.

### `GET /v1/files` — list uploaded files

Returns the file list (metadata only, no content).

### `GET /v1/models` — available agent sets

```json
{ "object": "list", "data": [ { "id": "web-agent", ... }, ... ] }
```

### `GET /health` — liveness probe

Returns `{"status": "healthy"}`.

## File attachment flow

1. Client uploads the original file → `POST /v1/files`.
2. Server stores the bytes in `FileCache` and converts them to Markdown via
   `AgentOrchestrator.ConvertAttachmentToMarkdown`
   (documents → AllToMarkdown; images → Z.ai GLM-OCR, only when a Z.ai key is set).
3. Client passes the returned `file_id` in `file_ids` on the next chat request.
4. `ResolveAttachments` rebuilds `FileAttachment` instances (original binary + cached Markdown)
   and `AgentOrchestrator.ExecuteAction` injects the content into the LLM prompt
   (respecting the `MaxAttachmentContextChars` budget).

This is the OpenAI Files API "**upload once, reference later**" model: the chat request
carries only lightweight `file_ids`, never the document bytes; the server resolves them
against `FileCache` and injects each Markdown block prefixed by `[File: {FileName}]` (the
same marker convention the Giraffe AI client uses in its messages). Full architectural
rationale — including why YAML frontmatter is used only for on-disk support documents and
not for endpoint attachments — is in `AIOrchestrator/ARCHITECTURE.md`.

### OpenAI compatibility notes

- **Spec-following**: multipart upload shape, upload response schema, SSE streaming
  (`data: ...` + `[DONE]`), filename-as-metadata.
- **Additive extensions** (ignored by strict OpenAI clients): `extracted_content`,
  `content_format`, and the `file_ids` parameter on `/v1/chat/completions` (a deprecated
  OpenAI beta parameter; the stable pattern is the Assistants API `attachments` +
  file_search — here content is injected straight into the prompt because the local agents
  have no retrieval tool).
- **Known gaps** (optional, only if strict OpenAI compatibility or durability is needed):
  no `GET /v1/files/{id}/content` (raw bytes), no `DELETE /v1/files/{id}`, and the cache is
  in-memory so `file_ids` die with the server process.

## Project layout

| File | Purpose |
|---|---|
| `Program.cs` | All endpoints + helpers (top-level statements, single file). |
| `MinimalChatApi.csproj` | Web SDK, references `AIOrchestrator`. |
| `appsettings.json` | Port + LLM provider configuration. |
| `start.bat` / `start.sh` | Launchers (Windows / Linux-macOS). |

## Testing

```bash
dotnet build MinimalChatApi.csproj
```

Manual smoke test (see the endpoint examples above): health → models → upload a CSV → list →
chat with `file_ids` → streaming SSE. The offline unit tests for the conversion pipeline live
in `AIOrchestrator/AgentOrchestrator.Tests` (run with `dotnet run --project AgentOrchestrator.Tests`).
