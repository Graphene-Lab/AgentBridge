using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using AIOrchestrator;
using UISupportGeneric;

// ═══════════════════════════════════════════════════════════════════════
//  AgentBridge — OpenAI-compatible HTTP server for AgentOrchestrator
//
//  Architecture (see AIOrchestrator/ARCHITECTURE.md):
//    Standalone clients (e.g. Giraffe AI) → HTTP endpoints → AgentOrchestrator
//    → LLM + agent tools. The server hosts the AIOrchestrator library (which is
//    not directly executable) and exposes its chat pipeline as standard
//    OpenAI-compatible REST endpoints, so any OpenAI SDK works unchanged.
//
//  Standard endpoints (OpenAI-compatible):
//    POST /v1/chat/completions   (Chat Completions, streaming SSE, sessions)
//    POST /v1/files              (multipart upload + server-side Markdown conversion)
//    GET  /v1/files              (list uploaded files)
//    GET  /v1/files/{id}         (retrieve converted content)
//    GET  /v1/files/{id}/content (retrieve raw bytes — OpenAI Files API)
//    DELETE /v1/files/{id}       (delete an uploaded file — OpenAI Files API)
//    GET  /v1/models             (agent sets + LLM providers with characteristics)
//    GET  /v1/models/{id}        (single model details)
//    POST /v1/audio/speech       (text → speech, Kokoro neural TTS, returns WAV)
//    GET  /health
//
//  Proprietary extensions (documented, additive, ignored by strict OpenAI clients):
//    POST /v1/control            (pilot: switch the LLM in use, features, reset)
//    GET  /v1/control            (session state + platform capabilities)
//    POST /v1/voice/listen       (one-shot server-mic speech recognition, Windows)
//    GET  /v1/audio/voices       (TTS voices available on this platform)
//
//  Sessions: chat requests may carry session_id (extension) to keep the
//  conversation history across requests; the pilot endpoint switches the LLM
//  provider on the fly with a context-window check (see README.md).
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
        AgentBridge — OpenAI-compatible HTTP server for AgentOrchestrator

        Usage:
          dotnet run --project AgentBridge.csproj [-- <options>]

        Options (command line overrides appsettings.json; any key is overridable
        with --Key:SubKey <value>):
          --LLM:Provider <name>   Default LLM provider: DeepSeekBridge (default), DeepSeek,
                                  Zai, Gemini, Ollama_Granite3b, ExllamaV2_Llama3b. Per-request
                                  overrides via the llm_provider field on /v1/chat/completions
                                  or POST /v1/control.
          --LLM:Anonymize <bool>  Anonymize NameOrKey elements before sending to the LLM
                                  (true|false)
          --SkipIndexingOnStartup <bool>
                                  Skip the DocumentsPath index build/refresh + file watcher
                                  at startup (true|false) — use during debug/dev when no
                                  document searches are needed (large folders index for minutes)
          --Voice:ExePath <path>  Path to AIOffice.VoiceAgent.Win.exe for POST /v1/voice/listen
                                  (default: <server dir>\AIOffice.VoiceAgent.Win.exe)
          --Urls <address>        Kestrel listening address, e.g. http://localhost:5290
          --environment <name>    ASP.NET environment: Development | Production

        Terminal UI (default when the console is interactive):
          Without flags the console opens the Qwen-Code-style terminal UI (chat,
          slash commands, model/agent/voice/TTS/files/help) while the server keeps
          answering API calls in the same process.
          --headless               Server only (no terminal UI) — for scripts/CI.
          --tui                    Force the terminal UI (falls back to server-only
                                   when the console is not interactive).

        Endpoints: /v1/chat/completions, /v1/files[/{id}[/content]], /v1/models[/{id}],
                   /v1/audio/speech, /v1/audio/voices, /v1/voice/listen, /v1/control, /health

        Examples:
          dotnet run --project AgentBridge.csproj -- --LLM:Provider Zai
          dotnet run --project AgentBridge.csproj -- --LLM:Anonymize true
          dotnet run --project AgentBridge.csproj -- --SkipIndexingOnStartup true
          agent --headless         (server only, e.g. as a systemd service)
        """);
    return;
}

// Content root = the executable's folder (not the CWD): the standalone exe must find
// appsettings.json even when launched from another directory (double click, services, tests).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Terminal UI mode: by default the console opens the Qwen-Code-style TUI (chat +
// slash commands + voice/model/files/help) while the server keeps answering API
// calls in the same process — "CLI + API simultaneously". --headless restores the
// plain server console for scripts/CI; --tui forces the UI even when the console
// looks redirected. When the console is redirected (no interactive terminal) the
// UI is skipped automatically, so existing launchers keep working unchanged.
var forceTui = args.Contains("--tui");
var forceHeadless = args.Contains("--headless") || args.Contains("--no-gui");
var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected && !Console.IsErrorRedirected;
var useTui = forceTui || (!forceHeadless && interactive);

// The TUI needs a real console (cursor control, key input): forcing it on a
// redirected console would crash on the console APIs, so fall back to server-only.
if (forceTui && !interactive)
{
    Console.WriteLine("Console is not interactive — --tui ignored, starting server-only (--headless).");
    useTui = false;
}

// The terminal UI owns the console: suppress ASP.NET's console logging so it
// cannot garble the TUI (HTTP errors surface inside the UI itself).
if (useTui)
    builder.Logging.ClearProviders();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Configure the LLM provider via appsettings "LLM:Provider" (e.g. DeepSeekBridge).
// This is the DEFAULT provider; per-request and per-session overrides are handled by
// the llm_provider field (chat) / POST /v1/control — see README.md "LLM switching".
var startupProvider = builder.Configuration["LLM:Provider"] ?? "DeepSeekBridge";
if (!ProviderConfigs.TryGet(startupProvider, out _))
{
    Console.WriteLine($"Unknown LLM provider '{startupProvider}' — using '{ProviderConfigs.DefaultProviderName}'.");
    startupProvider = ProviderConfigs.DefaultProviderName;
}

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
// The assembly was renamed AgentBridge → agent: migrate the credentials file so the
// old %LocalAppData%\AgentBridge\setup.json still applies (Setup.SetupFilePath uses
// the entry-assembly name).
try
{
    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var oldSetup = Path.Combine(local, "AgentBridge", "setup.json");
    var newSetup = Path.Combine(local, "agent", "setup.json");
    if (File.Exists(oldSetup) && !File.Exists(newSetup))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(newSetup)!);
        File.Copy(oldSetup, newSetup);
        Console.WriteLine($"Migrated setup.json: {oldSetup} → {newSetup}");
    }
}
catch { /* best-effort migration */ }

Setup.Load();

// Optional path to the VoiceAgent executable for POST /v1/voice/listen
// (appsettings "Voice:ExePath", CLI --Voice:ExePath). Null → server base directory.
VoiceBridge.ExePath = builder.Configuration["Voice:ExePath"];

var app = builder.Build();
app.UseCors();

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// Agent-set ids exposed as "models" (each maps to a concrete tool set in
// ResolveAgentTypes). Keep in sync with the switch in ResolveAgentTypes.
var agentModelIds = new[]
{
    "default-agent", "web-agent", "search-agent", "research-agent",
    "word-agent", "spreadsheet-agent", "email-agent", "multi-agent"
};

// ─────────────────────────────────────────────────────────────────────
// POST /v1/chat/completions — OpenAI Chat Completions API
//
// Extensions over the OpenAI contract (all additive):
//   session_id    — keep the conversation history across requests (multi-turn).
//   llm_provider  — use a specific LLM provider for this request (default: appsettings).
// On session requests the response carries the session_id; switching the provider on
// a session is refused (409) when the accumulated history overflows the new provider's
// context window — the client resets the conversation via POST /v1/control first.
// ─────────────────────────────────────────────────────────────────────
app.MapPost("/v1/chat/completions", async (
    [FromBody] ChatCompletionRequest request,
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

        var resolvedProvider = ResolveProvider(request.LlmProvider, startupProvider, out var providerError);
        if (providerError != null)
            return Results.BadRequest(new { error = providerError });
        var provider = resolvedProvider!;

        var agentTypes = ResolveAgentTypes(request.Model);
        var attachments = ResolveAttachments(request.FileIds);
        var maxIterations = request.MaxTokens > 0
            ? Math.Clamp(request.MaxTokens.Value / 100, 1, 50)
            : 50;

        ActiveSession? session = null;
        AgentOrchestrator? owned = null;
        try
        {
            if (!string.IsNullOrEmpty(request.SessionId))
            {
                session = SessionStore.Get(request.SessionId);
                if (session == null)
                    return Results.NotFound(new { error = $"Session '{request.SessionId}' not found. Omit session_id to start a new session, or create one via POST /v1/control." });

                await session.Gate.WaitAsync(ct);

                // Switch the LLM in use on the fly (history preserved), but refuse when the
                // conversation overflows the target provider's context window.
                var target = session.Orchestrator.Provider;
                if (!string.Equals(target, provider, StringComparison.OrdinalIgnoreCase))
                    target = provider;
                var fitError = ContextFitError(session, target, prompt);
                if (fitError != null)
                    return Results.Json(fitError, statusCode: 409);
                if (!string.Equals(session.Orchestrator.Provider, provider, StringComparison.OrdinalIgnoreCase))
                    session.Orchestrator.SwitchProvider(provider);
            }
            else
            {
                // No session → the historical stateless behaviour: one orchestrator per
                // request (fresh history), disposed when the request completes.
                owned = new AgentOrchestrator(provider, anonymize);
            }

            var orchestrator = session?.Orchestrator ?? owned!;
            var result = orchestrator.ExecuteAction(prompt, agentTypes, maxIterations: maxIterations, attachments: attachments);

            var content = result.Message ?? result.Error ?? "No output generated";
            var finishReason = result.Success ? "stop" : "error";
            var sessionId = session?.Id;

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
                session_id = sessionId,
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
        finally
        {
            session?.Gate.Release();
            owned?.Dispose();
        }
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500, title: "Agent execution failed");
    }
});

// ─────────────────────────────────────────────────────────────────────
// POST /v1/files — multipart upload + server-side Markdown conversion
// ─────────────────────────────────────────────────────────────────────
// Architectural model (see AIOrchestrator/ARCHITECTURE.md — "AgentBridge"):
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
// GET /v1/files/{fileId}/content — the original uploaded bytes (OpenAI Files API).
// Closes a documented gap (see ARCHITECTURE.md): raw retrieval complements the
// Markdown in /v1/files/{id}.
// ─────────────────────────────────────────────────────────────────────
app.MapGet("/v1/files/{fileId}/content", (string fileId) =>
{
    var file = FileCache.Get(fileId);
    if (file == null)
        return Results.NotFound(new { error = $"File '{fileId}' not found" });

    return Results.File(file.Content,
        string.IsNullOrEmpty(file.MimeType) ? "application/octet-stream" : file.MimeType,
        file.FileName);
});

// ─────────────────────────────────────────────────────────────────────
// DELETE /v1/files/{fileId} — file lifecycle (OpenAI Files API).
// ─────────────────────────────────────────────────────────────────────
app.MapDelete("/v1/files/{fileId}", (string fileId) =>
{
    if (!FileCache.Remove(fileId))
        return Results.NotFound(new { error = $"File '{fileId}' not found" });

    return Results.Ok(new { id = fileId, @object = "file", deleted = true });
});

// ─────────────────────────────────────────────────────────────────────
// GET /v1/files — list uploaded files
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
// GET /v1/models — agent sets AND LLM providers with their characteristics
// ─────────────────────────────────────────────────────────────────────
// Two kinds of "models":
//  - agent sets (default-agent, web-agent, ...): select which agent tools ExecuteAction
//    instantiates via the `model` field of /v1/chat/completions;
//  - LLM providers (DeepSeekBridge, Zai, ...): the actual LLMs behind the agents. The
//    provider in use is switched via the llm_provider field / POST /v1/control; each
//    entry carries the LLM characteristics (model_name, protocol, context_window,
//    base_address) so clients can pick a provider that fits their task and context size.
// ─────────────────────────────────────────────────────────────────────
app.MapGet("/v1/models", () =>
{
    var agents = agentModelIds.Select(id => new
    {
        id,
        @object = "model",
        owned_by = "ai-orchestrator",
        created = 0
    });

    var providers = ProviderConfigs.All.Select(p => new
    {
        id = p.ProviderName,
        @object = "model",
        owned_by = "llm-provider",
        created = 0,
        // additive LLM characteristics (ignored by strict OpenAI clients)
        provider = p.ProviderName,
        model_name = p.ModelName,
        protocol = p.Protocol.ToString(),
        context_window = p.ContextWindow,
        base_address = p.BaseAddress.ToString()
    });

    return Results.Ok(new { @object = "list", data = agents.Cast<object>().Concat(providers.Cast<object>()) });
});

// ─────────────────────────────────────────────────────────────────────
// GET /v1/models/{model} — single model details (agent set or LLM provider)
// ─────────────────────────────────────────────────────────────────────
app.MapGet("/v1/models/{model}", (string model) =>
{
    var id = model.ToLowerInvariant();
    if (agentModelIds.Contains(id))
    {
        return Results.Ok(new
        {
            id,
            @object = "model",
            owned_by = "ai-orchestrator",
            created = 0
        });
    }

    if (ProviderConfigs.TryGet(model, out var provider))
    {
        return Results.Ok(new
        {
            id = provider!.ProviderName,
            @object = "model",
            owned_by = "llm-provider",
            created = 0,
            provider = provider.ProviderName,
            model_name = provider.ModelName,
            protocol = provider.Protocol.ToString(),
            context_window = provider.ContextWindow,
            base_address = provider.BaseAddress.ToString()
        });
    }

    return Results.NotFound(new { error = $"Model '{model}' not found" });
});

// ─────────────────────────────────────────────────────────────────────
// POST /v1/audio/speech — text-to-speech (standard OpenAI endpoint)
//
// In-process Kokoro neural TTS (same engine/voices as the Windows VoiceAgent, but
// cross-platform). Returns WAV bytes. Voice names accept OpenAI names ("alloy",
// "echo", ...) or raw Kokoro ids ("if_sara", "af_heart", ...) — see /v1/audio/voices.
// Returns 501 when the model assets are not present on this platform.
// ─────────────────────────────────────────────────────────────────────
app.MapPost("/v1/audio/speech", (SpeechRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Input))
        return Results.BadRequest(new { error = "input is required" });

    if (!TtsEngine.IsAvailable)
        return Results.Json(new { error = "tts_unavailable", detail = TtsEngine.UnavailableReason }, statusCode: 501);

    var format = (request.ResponseFormat ?? "wav").ToLowerInvariant();
    if (format != "wav")
        return Results.BadRequest(new { error = $"Unsupported response_format '{format}'. Supported: wav." });

    try
    {
        var audio = TtsEngine.Synthesize(request.Input, request.Voice, request.Speed, request.Lang);
        return Results.Bytes(audio, "audio/wav", $"speech-{DateTime.UtcNow:yyyyMMddHHmmss}.wav");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500, title: "TTS synthesis failed");
    }
});

// ─────────────────────────────────────────────────────────────────────
// GET /v1/audio/voices — TTS voices available on this platform (proprietary)
// ─────────────────────────────────────────────────────────────────────
app.MapGet("/v1/audio/voices", () =>
{
    return Results.Ok(new
    {
        @object = "list",
        available = TtsEngine.IsAvailable,
        engine = "kokoro",
        detail = TtsEngine.IsAvailable ? "" : TtsEngine.UnavailableReason,
        data = TtsEngine.Voices.Select(v => new { id = v, @object = "voice" })
    });
});

// ─────────────────────────────────────────────────────────────────────
// POST /v1/voice/listen — one-shot speech recognition (proprietary, Windows only)
//
// Uses the server microphone through the AIOffice.VoiceAgent.Win.exe subprocess
// (the same chain as the AIOffice Voice panel). Reported unavailable (501) when the
// platform or the executable is missing. The client then falls back to text input.
// ─────────────────────────────────────────────────────────────────────
app.MapPost("/v1/voice/listen", async (VoiceListenRequest request, CancellationToken ct) =>
{
    if (!VoiceBridge.IsAvailable)
        return Results.Json(new { error = "voice_unavailable", detail = VoiceBridge.UnavailableReason }, statusCode: 501);

    try
    {
        var text = await VoiceBridge.ListenOnceAsync(request.Lang, request.TimeoutSeconds ?? 15, ct);
        // Speech recognition speaks the machine's language: reflect the language actually
        // used (never a hardcoded default) in the response.
        var lang = request.Lang ?? SystemLang.Get();
        return Results.Ok(new { text, lang, provider = "voiceagent-win" });
    }
    catch (TimeoutException ex)
    {
        return Results.Json(new { error = "timeout", detail = ex.Message }, statusCode: 408);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500, title: "Speech recognition failed");
    }
});

// ─────────────────────────────────────────────────────────────────────
// GET /v1/control — read session state and/or platform capabilities (proprietary)
//
// ?session_id=... → that session's state (LLM in use, history estimate, features).
// without session  → platform capabilities (TTS/voice availability, providers).
// ─────────────────────────────────────────────────────────────────────
app.MapGet("/v1/control", (string? session_id) =>
{
    if (!string.IsNullOrEmpty(session_id))
    {
        var session = SessionStore.Get(session_id);
        if (session == null)
            return Results.NotFound(new { error = $"Session '{session_id}' not found" });
        return Results.Ok(SessionState(session));
    }
    return Results.Ok(new { capabilities = BuildCapabilities() });
});

// ─────────────────────────────────────────────────────────────────────
// POST /v1/control — pilot endpoint (proprietary, extensible)
//
// The "control plane" of the server: switch the LLM currently in use for a session,
// toggle feature flags (voice, tts, ...), reset the conversation, or create a session.
// Body (all fields optional):
//   { "create": true }                         → create a new session (returns its id)
//   { "session_id": "...", "llm_provider": "Zai" }      → switch the LLM in use
//   { "session_id": "...", "features": { "voice": true, "tts": false } }
//   { "session_id": "...", "reset_history": true }
//
// A provider switch is refused (409) when the accumulated conversation overflows the
// target provider's context window (the exact case "switch on the fly conflicts with
// the context window of the model in use"); reset the conversation and retry.
// ─────────────────────────────────────────────────────────────────────
app.MapPost("/v1/control", (ControlRequest request) =>
{
    if (request.Create == true)
    {
        var created = SessionStore.Create(startupProvider, anonymize);
        return Results.Ok(SessionState(created));
    }

    if (string.IsNullOrEmpty(request.SessionId))
        return Results.BadRequest(new { error = "session_id is required for mutations (or use {\"create\": true} to create a new session)" });

    var session = SessionStore.Get(request.SessionId);
    if (session == null)
        return Results.NotFound(new { error = $"Session '{request.SessionId}' not found" });

    session.Gate.Wait();
    try
    {
        if (request.ResetHistory == true)
            session.Orchestrator.ResetConversation();

        if (!string.IsNullOrEmpty(request.LlmProvider))
        {
            var target = ResolveProvider(request.LlmProvider, startupProvider, out var error)!;
            if (error != null)
                return Results.BadRequest(new { error });

            if (!string.Equals(session.Orchestrator.Provider, target, StringComparison.OrdinalIgnoreCase))
            {
                var fitError = ContextFitError(session, target, "");
                if (fitError != null)
                    return Results.Json(fitError, statusCode: 409);
                session.Orchestrator.SwitchProvider(target);
            }
        }

        if (request.Features != null)
            foreach (var kv in request.Features)
                session.Features[kv.Key] = kv.Value;

        return Results.Ok(SessionState(session));
    }
    finally
    {
        session.Gate.Release();
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// ─────────────────────────────────────────────────────────────────────
// Launch mode: terminal UI (default, interactive console) or plain server.
// The TUI runs the HTTP server in the same process: the API keeps answering
// while you chat — that is the "CLI + API simultaneously" requirement.
// ─────────────────────────────────────────────────────────────────────
if (useTui)
{
    string? hostError = null;
    // The actual URL comes from config ("urls" ← appsettings/ASPNETCORE_URLS). app.Urls
    // before StartAsync only contains Kestrel's default (e.g. localhost:5000),
    // so DON'T use it: the TUI must talk to the same port Kestrel binds to.
    // (LESSON: this was a real bug — the TUI connected to :5000 and reported the
    // server unreachable while the server was fine on :5290.)
    var tuiUrl = app.Configuration["urls"] ?? "http://localhost:5290";
    try
    {
        await app.StartAsync();
        if (string.IsNullOrEmpty(tuiUrl) && app.Urls.Count > 0)
            tuiUrl = app.Urls.First();
    }
    catch (Exception ex)
    {
        // Address already in use: another agent instance is running — the UI
        // connects to it instead of hosting a second (conflicting) server.
        hostError = ex.Message;
    }
    try
    {
        await ConsoleTui.RunAsync(tuiUrl, hostError);
    }
    finally
    {
        try { await app.StopAsync(); } catch { }
    }
    return;
}

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

// Resolves the effective LLM provider for a request: the explicit llm_provider field
// (extension) or the appsettings default. Returns null + error message for unknown names.
static string? ResolveProvider(string? requested, string startupProvider, out string? error)
{
    var provider = string.IsNullOrWhiteSpace(requested) ? startupProvider : requested;
    if (!ProviderConfigs.TryGet(provider, out _))
    {
        error = $"Unknown LLM provider '{provider}'. Configured: {string.Join(", ", ProviderConfigs.All.Select(p => p.ProviderName))}.";
        return null;
    }
    error = null;
    return provider;
}

// Context-window guard for the "switch the LLM in use on the fly" operation: estimates
// the conversation tokens (accumulated history + the new prompt) and refuses when they
// exceed the (target) provider's context window. Returns an error payload, or null when
// the conversation fits.
static object? ContextFitError(ActiveSession session, string targetProvider, string newPrompt)
{
    var window = ProviderConfigs.Get(targetProvider).ContextWindow;
    var history = session.Orchestrator.GetHistory();
    var estimate = EstimateTokens(string.Join("\n", history.Select(h => h.Content)) + "\n" + newPrompt);
    if (estimate <= window) return null;

    return new
    {
        error = "context_window_exceeded",
        detail = $"The conversation needs ≈{estimate} tokens but provider '{targetProvider}' has a context window of {window} tokens. Reset the conversation (POST /v1/control with reset_history: true) or switch to a provider with a larger context window.",
        estimated_tokens = estimate,
        context_window = window,
        provider = targetProvider
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

// Platform capabilities (TTS/voice availability, LLM providers) — GET /v1/control
// without a session and the capabilities block of session states.
object BuildCapabilities()
{
    var ttsAvailable = TtsEngine.IsAvailable;
    return new
    {
        platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other",
        default_provider = startupProvider,
        providers = ProviderConfigs.All.Select(p => new
        {
            name = p.ProviderName,
            model_name = p.ModelName,
            protocol = p.Protocol.ToString(),
            context_window = p.ContextWindow,
            base_address = p.BaseAddress.ToString()
        }),
        tts = new
        {
            available = ttsAvailable,
            engine = "kokoro",
            voices = TtsEngine.Voices,
            detail = ttsAvailable ? "" : TtsEngine.UnavailableReason
        },
        voice = new
        {
            available = VoiceBridge.IsAvailable,
            engine = "voiceagent-win",
            detail = VoiceBridge.IsAvailable ? "" : VoiceBridge.UnavailableReason
        },
        sessions = SessionStore.Count
    };
}

// Full session state for /v1/control responses: the LLM currently in use with its
// characteristics and history estimate, the feature flags, and platform capabilities.
object SessionState(ActiveSession session)
{
    var history = session.Orchestrator.GetHistory();
    return new
    {
        session_id = session.Id,
        llm = new
        {
            provider = session.Orchestrator.Provider,
            model_name = session.Orchestrator.ModelName,
            context_window = session.Orchestrator.ContextWindow,
            history_messages = history.Count,
            history_tokens_estimate = EstimateTokens(string.Join("\n", history.Select(h => h.Content)))
        },
        features = session.Features,
        capabilities = BuildCapabilities()
    };
}

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
    /// <summary>
    /// Multi-turn session id (extension): keeps the conversation history across requests.
    /// Omit to keep the historical stateless per-request behaviour. Created sessions are
    /// returned in the response and can be managed via /v1/control.
    /// </summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
    /// <summary>
    /// LLM provider to use for this request (extension): e.g. "DeepSeekBridge", "Zai",
    /// "Gemini", "Ollama_Granite3b". Defaults to the appsettings LLM:Provider. On a session
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
