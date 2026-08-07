using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using AIOrchestrator;
using UISupportGeneric;

// ═══════════════════════════════════════════════════════════════════════
//  MinimalChatApi — OpenAI-compatible HTTP server for AgentOrchestrator
//
//  Architecture (see AIOrchestrator/ARCHITECTURE.md):
//    Standalone clients (e.g. Giraffe AI) → HTTP endpoints → AgentOrchestrator
//    → LLM + agent tools. The server hosts the AIOrchestrator library (which is
//    not directly executable) and exposes its chat pipeline as standard
//    OpenAI-compatible REST endpoints, so any OpenAI SDK works unchanged:
//
//    POST /v1/chat/completions   (OpenAI Chat Completions, streaming SSE)
//    POST /v1/files              (multipart upload + server-side Markdown conversion)
//    GET  /v1/files/{id}         (retrieve converted content)
//    GET  /v1/files              (list uploaded files)
//    GET  /v1/models             (available agent "models")
//    GET  /health
//
//  File attachments follow the same server-side conversion rule as the Blazor
//  UI (never client-side): uploaded bytes are stored as FileAttachment and
//  converted to Markdown via AgentOrchestrator.ConvertAttachmentToMarkdown
//  (AllToMarkdown for documents, Z.ai GLM-OCR for images).
// ═══════════════════════════════════════════════════════════════════════

// ─────────────────────────────────────────────────────────────────────
// Command-line help — print usage and exit before the server starts.
// Any appsettings.json key is already overridable from the command line
// (WebApplication.CreateBuilder wires the command-line config provider
// with precedence over appsettings.json), so the help documents the
// app-specific keys plus that general mechanism.
// ─────────────────────────────────────────────────────────────────────
if (args.Contains("-h") || args.Contains("--help") || args.Contains("/?"))
{
    Console.WriteLine("""
        MinimalChatApi — OpenAI-compatible HTTP server for AgentOrchestrator

        Usage:
          dotnet run --project MinimalChatApi.csproj [-- <options>]

        Options (command line overrides appsettings.json; any key is overridable
        with --Key:SubKey <value>):
          --LLM:Provider <name>   LLM provider: DeepSeekBridge (default), DeepSeek, Zai,
                                  Gemini, Ollama_Granite3b, ExllamaV2_Llama3b
          --LLM:Anonymize <bool>  Anonymize NameOrKey elements before sending to the LLM
                                  (true|false)
          --SkipIndexingOnStartup <bool>
                                  Skip the DocumentsPath index build/refresh + file watcher
                                  at startup (true|false) — use during debug/dev when no
                                  document searches are needed (large folders index for minutes)
          --Urls <address>        Kestrel listening address, e.g. http://localhost:5290
          --environment <name>    ASP.NET environment: Development | Production

        Examples:
          dotnet run --project MinimalChatApi.csproj -- --LLM:Provider Zai
          dotnet run --project MinimalChatApi.csproj -- --LLM:Anonymize true
          dotnet run --project MinimalChatApi.csproj -- --SkipIndexingOnStartup true
        """);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Configure the LLM provider via appsettings "LLM:Provider" (e.g. DeepSeekBridge).
var providerName = builder.Configuration["LLM:Provider"] ?? nameof(LLMUtility.LLMProvider.DeepSeekBridge);
Enum.TryParse<LLMUtility.LLMProvider>(providerName, ignoreCase: true, out var provider);

// Anonymization flag from appsettings "LLM:Anonymize" (default false). Overridable from the
// command line with --LLM:Anonymize true — the ASP.NET config chain already gives CLI
// precedence over appsettings.json, so a single Configuration read covers both sources.
var anonymize = builder.Configuration.GetValue<bool>("LLM:Anonymize");

// Startup indexing toggle from appsettings "SkipIndexingOnStartup" (default false, CLI
// --SkipIndexingOnStartup true). When true, the DocumentsPath index is neither built nor
// refreshed and the file watcher is not started: use during debug/dev when no document
// searches are needed, to skip the multi-minute full index on large folders. MUST be set
// before Setup.Load() below — Setup.RagDocumentProcessor is created lazily on first use,
// so this early assignment is what the processor sees (see Setup.SkipIndexingOnStartup).
Setup.SkipIndexingOnStartup = builder.Configuration.GetValue<bool>("SkipIndexingOnStartup");

// This host has no settings UI of its own: load credentials persisted by the previous
// run (Setup.Save) from %LocalAppData%\{app}\setup.json — SMTP/IMAP for EMailTool,
// API keys. Provider selection above stays appsettings-driven (Setup.Load only restores
// Setup.ProviderConfig if the file contains ProviderName). See Setup.Load XML docs.
Setup.Load();

// Scoped (one orchestrator per HTTP request), NOT a singleton: AgentOrchestrator keeps the
// conversation history and the subagent sessions in instance state (LLMUtility._messageHistory,
// _subagentSessions, _pendingSubagentResult). A singleton shared across concurrent requests
// would interleave those lists — two overlapping chats would corrupt each other's context.
builder.Services.AddScoped<AgentOrchestrator>(_ => new AgentOrchestrator(provider, anonymize));

var app = builder.Build();
app.UseCors();

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// ─────────────────────────────────────────────────────────────────────
// POST /v1/chat/completions — OpenAI Chat Completions API
// ─────────────────────────────────────────────────────────────────────
app.MapPost("/v1/chat/completions", (
    [FromBody] ChatCompletionRequest request,
    AgentOrchestrator orchestrator,
    CancellationToken ct) =>
{
    try
    {
        var lastUserMessage = request.Messages?
            .LastOrDefault(m => m.Role == "user");
        if (lastUserMessage == null)
            return Results.BadRequest(new { error = "No user message found" });

        var prompt = ExtractTextContent(lastUserMessage.Content);
        if (string.IsNullOrWhiteSpace(prompt))
            return Results.BadRequest(new { error = "User message is empty" });

        var agentTypes = ResolveAgentTypes(request.Model);
        var attachments = ResolveAttachments(request.FileIds);
        var maxIterations = request.MaxTokens > 0
            ? Math.Clamp(request.MaxTokens.Value / 100, 1, 50)
            : 50;

        var result = orchestrator.ExecuteAction(prompt, agentTypes, maxIterations: maxIterations, attachments: attachments);

        var content = result.Message ?? result.Error ?? "No output generated";
        var finishReason = result.Success ? "stop" : "error";

        if (request.Stream == true)
        {
            return Results.Stream(async stream =>
            {
                var model = request.Model ?? "default-agent";
                foreach (var word in content.Split(' '))
                {
                    if (ct.IsCancellationRequested) break;
                    var chunk = new
                    {
                        id = $"chatcmpl-{Guid.NewGuid():N}",
                        @object = "chat.completion.chunk",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        model,
                        choices = new[]
                        {
                            new { index = 0, delta = new { content = word + " " }, finish_reason = (string?)null }
                        }
                    };
                    await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: {JsonSerializer.Serialize(chunk, jsonOptions)}\n\n"), ct);
                    await stream.FlushAsync(ct);
                    await Task.Delay(30, ct);
                }
                var finalChunk = new
                {
                    id = $"chatcmpl-{Guid.NewGuid():N}",
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model,
                    choices = new[]
                    {
                        new { index = 0, delta = new { content = "" }, finish_reason = finishReason }
                    }
                };
                await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: {JsonSerializer.Serialize(finalChunk, jsonOptions)}\n\n"), ct);
                await stream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct);
                await stream.FlushAsync(ct);
            }, "text/event-stream");
        }

        var response = new
        {
            id = $"chatcmpl-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = request.Model ?? "default-agent",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content },
                    finish_reason = finishReason
                }
            },
            usage = new
            {
                prompt_tokens = EstimateTokens(prompt),
                completion_tokens = EstimateTokens(content),
                total_tokens = EstimateTokens(prompt + content)
            }
        };
        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500, title: "Agent execution failed");
    }
});

// ─────────────────────────────────────────────────────────────────────
// POST /v1/files — multipart upload + server-side Markdown conversion
// ─────────────────────────────────────────────────────────────────────
// Architectural model (see AIOrchestrator/ARCHITECTURE.md — "MinimalChatApi"):
// this endpoint mirrors the OpenAI Files API "upload once, reference later" pattern.
//  - The multipart shape (form field `file` + `purpose`) matches the OpenAI upload call.
//  - The response carries the OpenAI metadata schema (id, object, bytes, created_at,
//    filename, purpose, status) plus two additive extensions used by Giraffe AI:
//    `extracted_content` (the server-side Markdown) and `content_format`.
//  - Conversion is always server-side (never in the browser) — same rule as the
//    Blazor UI; documents go through AllToMarkdown, images through Z.ai GLM-OCR.
//  - The bytes + Markdown are cached in memory so a later chat request can reference
//    them by a lightweight `file_id` instead of re-sending the content.
//  - The original `filename` travels as response metadata (OpenAI convention), NOT as
//    YAML frontmatter in the converted content: the name is first-class state in
//    FileCache/FileAttachment, and injecting it into the Markdown would pollute the
//    document text. See LLMUtility.SendQuery's `supportDocuments` path for the one
//    place where YAML frontmatter is appropriate (files persisted on disk).
// ─────────────────────────────────────────────────────────────────────
app.MapPost("/v1/files", async (IFormFile file, [FromQuery] string purpose = "assistants") =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "No file provided" });
    if (file.Length > 25_000_000)
        return Results.BadRequest(new { error = "File too large (max 25MB)" });

    await using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var content = ms.ToArray();

    var fileId = $"file-{Guid.NewGuid():N}";
    var attachment = new FileAttachment(file.FileName, content);
    var markdown = AgentOrchestrator.ConvertAttachmentToMarkdown(attachment);

    FileCache.Store(new CachedFile
    {
        Id = fileId,
        FileName = file.FileName,
        MimeType = file.ContentType,
        Content = content,
        ExtractedText = markdown ?? "",
        SizeBytes = file.Length,
        StoredAt = DateTime.UtcNow
    });

    return Results.Ok(new
    {
        id = fileId,
        @object = "file",
        bytes = file.Length,
        created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        filename = file.FileName,
        purpose,
        status = !string.IsNullOrEmpty(markdown) ? "processed" : "unsupported",
        extracted_content = markdown,
        content_format = "markdown"
    });
}).DisableAntiforgery();

// ─────────────────────────────────────────────────────────────────────
// GET /v1/files/{fileId}
// ─────────────────────────────────────────────────────────────────────
app.MapGet("/v1/files/{fileId}", (string fileId) =>
{
    var file = FileCache.Get(fileId);
    if (file == null)
        return Results.NotFound(new { error = $"File '{fileId}' not found" });

    return Results.Ok(new
    {
        id = file.Id,
        @object = "file",
        bytes = file.SizeBytes,
        created_at = new DateTimeOffset(file.StoredAt).ToUnixTimeSeconds(),
        filename = file.FileName,
        status = file.ExtractedText.Length > 0 ? "processed" : "unsupported",
        extracted_content = file.ExtractedText,
        content_format = "markdown"
    });
});

// ─────────────────────────────────────────────────────────────────────
// GET /v1/files
// ─────────────────────────────────────────────────────────────────────
app.MapGet("/v1/files", () =>
{
    var files = FileCache.GetAll();
    return Results.Ok(new
    {
        @object = "list",
        data = files.Select(f => new
        {
            id = f.Id,
            @object = "file",
            bytes = f.SizeBytes,
            created_at = new DateTimeOffset(f.StoredAt).ToUnixTimeSeconds(),
            filename = f.FileName,
            status = f.ExtractedText.Length > 0 ? "processed" : "unsupported"
        })
    });
});

// ─────────────────────────────────────────────────────────────────────
// GET /v1/models — the available agent "models"
// ─────────────────────────────────────────────────────────────────────
app.MapGet("/v1/models", () =>
{
    var models = new[]
    {
        new { id = "default-agent", @object = "model", owned_by = "ai-orchestrator" },
        new { id = "web-agent", @object = "model", owned_by = "ai-orchestrator" },
        new { id = "word-agent", @object = "model", owned_by = "ai-orchestrator" },
        new { id = "spreadsheet-agent", @object = "model", owned_by = "ai-orchestrator" },
        new { id = "search-agent", @object = "model", owned_by = "ai-orchestrator" },
        new { id = "multi-agent", @object = "model", owned_by = "ai-orchestrator" }
    };
    return Results.Ok(new { @object = "list", data = models });
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();

// ─────────────────────────────────────────────────────────────────────
// HELPERS
// ─────────────────────────────────────────────────────────────────────

// Extracts plain text from an OpenAI message content field: a plain string, or the
// structured content array (text / image_url parts) some clients send. image_url parts
// are rendered as "[Image attached]" — the actual image bytes are handled through the
// file_ids / /v1/files flow instead.
static string ExtractTextContent(object? content)
{
    if (content is string text)
        return text;
    if (content is JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.String)
            return json.GetString() ?? "";
        if (json.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in json.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type))
                {
                    if (type.GetString() == "text" && item.TryGetProperty("text", out var txt))
                        parts.Add(txt.GetString() ?? "");
                    else if (type.GetString() == "image_url")
                        parts.Add("[Image attached]");
                }
            }
            return string.Join("\n", parts);
        }
    }
    return content?.ToString() ?? "";
}

// Maps the OpenAI "model" name to the real agent tool types in AIOrchestrator
// (see AIOrchestrator/ARCHITECTURE.md — "Agent Architecture"): each "model" id exposed
// by /v1/models corresponds to a concrete set of IAgentTool implementations that
// AgentOrchestrator.ExecuteAction will instantiate as tools.
static Type[] ResolveAgentTypes(string? model)
{
    return model?.ToLower() switch
    {
        "web-agent" => new[] { typeof(AIOrchestrator.API.FileTool), typeof(AIOrchestrator.API.WebTool) },
        "word-agent" => new[] { typeof(AIOrchestrator.API.WordTool) },
        "spreadsheet-agent" => new[] { typeof(AIOrchestrator.API.SpreadsheetTool) },
        "search-agent" or "research-agent" => new[] { typeof(AIOrchestrator.API.FileTool) },
        "email-agent" => new[] { typeof(AIOrchestrator.API.EMailTool) },
        "multi-agent" => new[]
        {
            typeof(AIOrchestrator.API.FileTool),
            typeof(AIOrchestrator.API.WebTool),
            typeof(AIOrchestrator.API.WordTool),
            typeof(AIOrchestrator.API.SpreadsheetTool),
            typeof(AIOrchestrator.API.EMailTool)
        },
        _ => new[] { typeof(AIOrchestrator.API.FileTool), typeof(AIOrchestrator.API.WebTool) }
    };
}

// Resolves uploaded file ids to FileAttachment instances (original binary + converted
// Markdown, see AgentOrchestrator.ConvertAttachmentToMarkdown), reusing the server-side
// conversion already performed at upload time. Unknown ids are skipped; returns null
// when no usable attachment remains.
//
// This is the "reference later" half of the Files API pattern: the chat request carries
// only the lightweight `file_ids` (OpenAI convention), never the document bytes. The
// original filename survives the round trip because it was stored on CachedFile at upload
// time — the LLM sees it later via the "[File: {FileName}]" header that
// AgentOrchestrator.BuildAttachmentsContext prepends to each Markdown block.
static IEnumerable<FileAttachment>? ResolveAttachments(List<string>? fileIds)
{
    if (fileIds == null || fileIds.Count == 0)
        return null;
    var attachments = new List<FileAttachment>();
    foreach (var fileId in fileIds)
    {
        var cached = FileCache.Get(fileId);
        if (cached == null)
            continue;
        var attachment = new FileAttachment(cached.FileName, cached.Content)
        {
            MarkdownContent = cached.ExtractedText.Length > 0 ? cached.ExtractedText : null
        };
        attachments.Add(attachment);
    }
    return attachments.Count > 0 ? attachments : null;
}

// Rough token estimate (~4 chars per token for latin scripts).
static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 4.0);

// ─────────────────────────────────────────────────────────────────────
// DTOs
// ─────────────────────────────────────────────────────────────────────
/// <summary>OpenAI-compatible Chat Completions request body accepted by POST /v1/chat/completions.</summary>
public record ChatCompletionRequest
{
    /// <summary>Agent set to use: default-agent, web-agent, search-agent, word-agent, spreadsheet-agent, multi-agent.</summary>
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
    /// (AgentOrchestrator.BuildAttachmentsContext), never sending the raw bytes to the model.
    /// </summary>
    [JsonPropertyName("file_ids")]
    public List<string>? FileIds { get; init; }
}

/// <summary>A single chat message of the OpenAI request.</summary>
public record RequestMessage
{
    /// <summary>Message role: "user", "assistant", "system", ...</summary>
    public string Role { get; init; } = "user";
    /// <summary>Message content: a plain string, or the structured content array.</summary>
    public object? Content { get; init; }
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
    /// <summary>Removes a cached file by id (no-op when absent).</summary>
    public static void Remove(string id) => _cache.TryRemove(id, out _);
}

/// <summary>
/// A single uploaded file as stored by <see cref="FileCache"/>: the original binary
/// (<see cref="Content"/>) plus the Markdown produced server-side at upload time
/// (<see cref="ExtractedText"/>, see <see cref="AgentOrchestrator.ConvertAttachmentToMarkdown"/>).
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
