# How streaming calls work

In this ecosystem the streaming calls ("SIO" calls) are the streaming calls that connect a client
to the agent engine: the client sends a `/v1/chat/completions` request with `stream=true`,
AgentBridge breaks the response up into **Server-Side (SSE)** events, and streams the model's
tokens back token by token as they are generated, without waiting for generation to finish. The
flow crosses three layers: HTTP (client ↔ AgentBridge), agent loop
(AIOrchestrator/AgentHarness) and model (LLM provider).

> Note: "SIO" is not a token in the code; here it refers to the **S**erver-**S**ide-**I**/O
> streaming mechanism that bridges the client and the model. If "SIO" means something else to you
> (e.g. a specific endpoint), tell me and I will adjust this document.

## End-to-end flow

```mermaid
sequenceDiagram
    participant C["Client<br/>(TUI / Giraffe / OpenAI SDK / curl)"]
    participant A["AgentBridge<br/>POST /v1/chat/completions"]
    participant H["AgentHarness<br/>(AIOrchestrator)"]
    participant L["LLM provider<br/>(DeepSeek/Z.ai/Gemini/Ollama/ExLlamaV2 ...)"]

    C->>A: POST /v1/chat/completions {"model","messages","stream":true,"session_id",...}
    A->>A: parse body (ApiModels); resolve session_id / ProviderConfigs
    A->>H: ExecuteAction / ExecuteActionStream (streaming, isVoiceChat)
    H->>H: resolve tool set (core in front, plugin via subagent)
    H->>L: chat completion streaming request (stream_options.include_usage)
    L-->>H: yield token stream (chunks)
    H-->>A: yield text chunks (ExecuteChatStream / ExecuteActionStream)
    A->>A: Content-Type: text/event-stream
    loop for each chunk
        A-->>C: data: {"type":"message","delta":"..."}<br/>(SSE, keepalive every 15 s)
    end
    alt stream_options.include_usage == true
        A-->>C: data: {...,"usage":{...}} (empty choices, right before [DONE])
    end
    A-->>C: data: [DONE]
    A->>A: (post-handler) attachment delivery, sandbox persistence, history
```

## The `/v1/chat/completions` handler (`AgentBridge/Program.cs`)

```mermaid
flowchart TD
  req["POST /v1/chat/completions<br/>parse body (ApiModels):<br/>model · messages · stream<br/>session_id · stream_options"] --> stream{"request.Stream == true?"}
  stream --| "no" |json["Single JSON response<br/>(one response, no streaming)"]
  stream --| "yes" |sse["Content-Type: text/event-stream<br/>body written MANUALLY in the handler,<br/>NOT via Results.Stream"]
  sse --> write["For each chunk yielded by AgentHarness:<br/>`data: {json}<br/>(SSE)<br/>+ flush"]
  write --> ka["Keepalive: `: keepalive` comment<br/>every 15 s (SSE spec allows comments)"]
  ka --> usage{"StreamOptions.IncludeUsage == true?"}
  usage --| "yes" |uch["Usage chunk:<br/>empty choices, right before [DONE]"]
  usage --| "no" |done["Only [DONE]"]
  done --> endr["Stream end"]
  uch --> endr

  note["Why manual body: Results.Stream runs its delegate ONLY AFTER the handler<br/>returns; the streaming body must be written INSIDE the handler.<br/>The HTTP 200 was already written, so the SSE starts immediately."]
```

## SSE payload detail

Every event is a `data:` line with a serialized JSON (the project's `JsonSerializer` options),
followed by an empty line. The standard OpenAI client parses the stream:

```jsonc
// content chunk (one or more deltas per event)
data: {"type":"message","role":"assistant","content":"Hi"}

// keepalive (only keeps the connection alive while the agent works)
: keepalive

// attachment chunk (only if the run produced resource/attachments)
data: {"type":"attachments","items":[...]}

// usage chunk (only if stream_options.include_usage == true)
data: {"type":"usage","prompt_tokens":120,"completion_tokens":34}

// termination
data: [DONE]
```

- **Sessions**: a request may carry `session_id` to keep the history across requests; the session
  is owned by `SessionStore` (`AgentHarness` + history). Expired/non-existent sessions are
  regenerated.
- **In-flight LLM switch**: the `model` field (or `POST /v1/control`) changes the provider for the
  session without restarting — see `AgentBridge/docs/API.md → "LLM switching"`.
- **Attachments**: client uploads arrive in their original binary format and are converted to
  Markdown **server-side** (`AgentHarness.ConvertAttachmentToMarkdown`, `AllToMarkdown` for
  documents, Z.ai GLM-OCR for images) and injected into the prompt.
- **Cancellation**: if the client aborts the stream, the orchestrator is disposed under a live
  connection (clean-up of the document watcher suspended for the whole turn).

## Correlated paths

| Path | Streaming? | Notes |
|---|---|---|
| `/v1/chat/completions` (HTTP) | Yes (SSE) or no (JSON) | Via `AgentHarness.ExecuteAction` / `ExecuteChatStream` / `ExecuteActionStream` |
| AIOffice panels (Web/Voice) | Yes (streaming) | The panels use the streaming wrapper with `isVoiceChat: true` for the Voice panel |
| SIP voice session | Yes (VoiceConversation) | The streaming loop is owned by `VoiceConversation` |

> The conversion of attachments and the rendering of results happen **all server-side**: never on
> the client. This holds for the TUI, for Giraffe AI and for the HTTP streaming.
