using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using AIOrchestrator;

// OpenAI-compatible HTTP request DTOs and the /v1/files in-memory cache. Kept in their own
// file (global namespace, like the previous placement at the end of Program.cs) so that host
// tests can link this single file instead of referencing the whole Web project
// (Graphene-Lab/AgentHarness AgentHarness.Tests — the Web SDK project is not project-reference
// friendly in every NuGet restore environment).

/// <summary>OpenAI-compatible Chat Completions request body accepted by POST /v1/chat/completions.</summary>
public record ChatCompletionRequest
{
    /// <summary>Agent set to use (see AgentTools.AllIds): default-agent, web-agent, search-agent,
    /// research-agent, document-files, spreadsheet-files, email-agent, office-files, multi-files, all-files.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }
    /// <summary>Chat messages; the last user message becomes the agent prompt.</summary>
    [JsonPropertyName("messages")]
    public List<RequestMessage>? Messages { get; init; }
    /// <summary>Sampling temperature (accepted for OpenAI compatibility, not forwarded to the agent).</summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }
    /// <summary>Roughly maps to agent loop iterations (max_tokens / 100, clamped 1–50).</summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }
    /// <summary>Top-p sampling (accepted for OpenAI compatibility, not forwarded to the agent).</summary>
    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }
    /// <summary>When true the response is streamed as Server-Sent Events.</summary>
    [JsonPropertyName("stream")]
    public bool? Stream { get; init; }
    /// <summary>
    /// Optional ids of previously uploaded files (see POST /v1/files) attached as context.
    /// Additive extension: `file_ids` is not part of the stable OpenAI Chat Completions spec
    /// (it was a short-lived beta parameter); it is our convention, following the OpenAI
    /// Files API "upload once, reference later" model. The server resolves each id against
    /// FileCache and injects the cached Markdown into the agent prompt
    /// (AgentHarness.BuildAttachmentsContext), never sending the raw bytes to the model.
    /// </summary>
    [JsonPropertyName("file_ids")]
    public List<string>? FileIds { get; init; }
    /// <summary>
    /// Optional explicit agent-tool names for this request (extension): overrides the
    /// agent set resolved from <see cref="Model"/> with an arbitrary combination (e.g.
    /// ["WebTool", "SpreadsheetTool"]). Unknown names are skipped by the tool registry.
    /// </summary>
    [JsonPropertyName("tools")]
    public List<string>? Tools { get; init; }
    /// <summary>
    /// Multi-turn session id (extension): keeps the conversation history across requests.
    /// Omit to keep the historical stateless per-request behaviour. Created sessions are
    /// returned in the response and can be managed via /v1/control.
    /// </summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
    /// <summary>
    /// LLM provider to use for this request (extension): e.g. "DeepSeekBridge", "Zai",
    /// "Gemini", "Ollama". Defaults to the appsettings LLM:Provider. On a session
    /// request this switches the LLM currently in use (refused with 409 when the conversation
    /// overflows the target provider's context window). See /v1/models and /v1/control.
    /// </summary>
    [JsonPropertyName("llm_provider")]
    public string? LlmProvider { get; init; }
}

/// <summary>A single chat message of the OpenAI request.</summary>
public record RequestMessage
{
    /// <summary>Message role: "user", "assistant", "system", ...</summary>
    public string Role { get; init; } = "user";
    /// <summary>Message content: a plain string, or the structured content array.</summary>
    public object? Content { get; init; }
}

/// <summary>OpenAI-compatible text-to-speech request body for POST /v1/audio/speech.</summary>
public record SpeechRequest
{
    /// <summary>Model name (accepted for OpenAI compatibility; the server always uses Kokoro).</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }
    /// <summary>The text to synthesize (required).</summary>
    [JsonPropertyName("input")]
    public string? Input { get; init; }
    /// <summary>OpenAI voice name ("alloy", "echo", ...) or raw Kokoro id ("if_sara", "af_heart", ...).</summary>
    [JsonPropertyName("voice")]
    public string? Voice { get; init; }
    /// <summary>
    /// Two-letter ISO language (extension, additive): picks a Kokoro voice of that language
    /// (if_* = Italian, af_*/am_* = English, ef_* = Spanish, ff_* = French, jf_* = Japanese, ...).
    /// Omit to use the server's system language — e.g. an Italian machine speaks Italian.
    /// </summary>
    [JsonPropertyName("lang")]
    public string? Lang { get; init; }
    /// <summary>Audio format: "wav" (default). mp3/opus/aac/flac/pcm are not synthesized.</summary>
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; init; }
    /// <summary>Speaking speed, 0.25–4.0 (default 1.0).</summary>
    [JsonPropertyName("speed")]
    public double? Speed { get; init; }
}

/// <summary>Request body for POST /v1/voice/listen (proprietary one-shot speech recognition).</summary>
public record VoiceListenRequest
{
    /// <summary>Two-letter ISO language code; omitted → the machine's system language.</summary>
    [JsonPropertyName("lang")]
    public string? Lang { get; init; }
    /// <summary>Seconds to wait for speech (1–60, default 15).</summary>
    [JsonPropertyName("timeout_seconds")]
    public int? TimeoutSeconds { get; init; }
}

/// <summary>Request body for POST /v1/sip/call (proprietary).</summary>
public record SipCallRequest
{
    /// <summary>SIP destination: a full URI ("sip:user@host") or a bare number routed via the configured registrar.</summary>
    [JsonPropertyName("uri")]
    public string? Uri { get; init; }
}

/// <summary>Request body for POST /v1/sip/answer (proprietary).</summary>
public record SipAnswerRequest
{
    /// <summary>When true the SIP server auto-answers incoming calls (PIN/allow-list gate).</summary>
    [JsonPropertyName("on")]
    public bool? On { get; init; }
}

/// <summary>Request body for POST /v1/sip/config (proprietary) — one config key at a time.</summary>
public record SipConfigRequest
{
    /// <summary>Sip config key, case-insensitive (e.g. "Enabled", "Pin", "AnswerMode", "AllowedCallers").</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }
    /// <summary>New value as a string; booleans accept true/false/1/on, AllowedCallers is comma-separated.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

/// <summary>Request body for POST /v1/control — the pilot/steering endpoint.</summary>
public record ControlRequest
{
    /// <summary>When true, create a new session and return its state (no session_id needed).</summary>
    [JsonPropertyName("create")]
    public bool? Create { get; init; }
    /// <summary>Target session for the mutations below.</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
    /// <summary>Switch the LLM currently in use for the session (context-window checked).</summary>
    [JsonPropertyName("llm_provider")]
    public string? LlmProvider { get; init; }
    /// <summary>Feature flags to set/clear on the session (voice, tts, ... — extensible).</summary>
    [JsonPropertyName("features")]
    public Dictionary<string, bool>? Features { get; init; }
    /// <summary>When true, clear the session's conversation history.</summary>
    [JsonPropertyName("reset_history")]
    public bool? ResetHistory { get; init; }
    /// <summary>Make the named provider the process-wide default for new chat sessions
    /// (persisted as the explicit default marker in providers.json). No session_id needed.</summary>
    [JsonPropertyName("set_default_provider")]
    public string? SetDefaultProvider { get; init; }
}

/// <summary>
/// In-memory file store (uploaded original + converted Markdown). Volatile by design:
/// files are kept for the lifetime of the process and lost on restart — the /v1/files
/// flow is meant for short-lived chat sessions, not durable storage.
/// </summary>
public static class FileCache
{
    private static readonly ConcurrentDictionary<string, CachedFile> _cache = new();

    /// <summary>Stores an uploaded file under its id (overwrites on duplicate id).</summary>
    public static void Store(CachedFile file) => _cache[file.Id] = file;
    /// <summary>Returns the cached file for an id, or null when not found.</summary>
    public static CachedFile? Get(string id) => _cache.TryGetValue(id, out var f) ? f : null;
    /// <summary>Returns all cached files (unordered).</summary>
    public static IEnumerable<CachedFile> GetAll() => _cache.Values;
    /// <summary>Removes a cached file by id (returns false when absent).</summary>
    public static bool Remove(string id) => _cache.TryRemove(id, out _);
}

/// <summary>
/// A single uploaded file as stored by <see cref="FileCache"/>: the original binary
/// (<see cref="Content"/>) plus the Markdown produced server-side at upload time
/// (<see cref="ExtractedText"/>, see <see cref="AgentHarness.ConvertAttachmentToMarkdown"/>).
/// </summary>
public class CachedFile
{
    /// <summary>Unique file id returned to the client (e.g. "file-&lt;guid&gt;").</summary>
    public string Id { get; init; } = "";
    /// <summary>Original file name, with extension.</summary>
    public string FileName { get; init; } = "";
    /// <summary>Upload Content-Type reported by the client.</summary>
    public string MimeType { get; init; } = "";
    /// <summary>Original binary content of the uploaded file.</summary>
    public byte[] Content { get; init; } = Array.Empty<byte>();
    /// <summary>Markdown produced server-side at upload time (empty when unsupported).</summary>
    public string ExtractedText { get; init; } = "";
    /// <summary>Upload size in bytes.</summary>
    public long SizeBytes { get; init; }
    /// <summary>Upload timestamp (UTC).</summary>
    public DateTime StoredAt { get; init; }
}
