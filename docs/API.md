# AgentBridge HTTP API — reference

OpenAI-compatible endpoints plus a small set of **documented proprietary extensions**
for the features that have no OpenAI equivalent (voice speech, LLM switching, platform
capabilities). Any OpenAI SDK, script or standalone client can drive the AI agents
without modification.

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
| `session_id` | **Extension** — multi-turn session id (see [Sessions](#sessions-multi-turn-memory)). |
| `llm_provider` | **Extension** — LLM provider for this request (see [LLM switching](#llm-switching-the-pilot-endpoint)). |

Responses carry an additive `session_id` field when a session was used.

> **Streaming caveat**: LLM-native streaming (`SendQueryStream`) does not support
> anonymization and throws for Gemini — the `/v1/chat/completions` SSE endpoint here is
> response-side only (the agent result is computed with non-streaming `SendQuery`).

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
- **501 `tts_unavailable`** when the model assets are missing (see
  [Build / assets](ARCHITECTURE.md#build--assets)).

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
  `context_window`, `base_address`, `interaction_mode` (`API` or `CLI` — the effective agent
  interaction mode; see below). This is the "read the LLM characteristics" surface: a
  client can pick a provider whose context window fits the task.

**Agent interaction mode.** Each provider drives the agent tools either through the JSON
tool-calling API (`interaction_mode: "API"` — one tool per method) or through the
application CLI (`interaction_mode: "CLI"` — the agent issues `ClassName subcommand args`
commands against the terminal). It is configured per provider in the Models & Providers UI
or in `providers.json` (`AgentInteractionMode`, options `API`/`CLI`/`Default`); `Default`
delegates to the model size — CLI for small models (context window < 128 000 tokens), API
for large ones. `interaction_mode` always reports the **effective** value (the explicit
setting or the size default). The same field appears on `GET /v1/control` session state.

`GET /v1/models/{id}` returns a single entry (`404` for unknown ids).

## `GET /v1/control` — capabilities

Without a session id it returns what this platform can do right now:

```json
{
  "capabilities": {
    "platform": "windows",
    "default_provider": "DeepSeekBridge",
    "providers": [ { "name": "Zai", "model_name": "glm-4.7-flash", "protocol": "OpenAI", "context_window": 128000, "base_address": "https://api.z.ai/", "interaction_mode": "API" }, ... ],
    "tts":   { "available": true, "engine": "kokoro", "voices": [ ... ], "detail": "" },
    "voice": { "available": true, "engine": "voiceagent-win", "detail": "" },
    "sessions": 3
  }
}
```

---

See also: [README](../README.md) · [Terminal UI](TUI.md) · [Architecture](ARCHITECTURE.md)
