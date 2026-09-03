using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIOrchestrator;

// ═══════════════════════════════════════════════════════════════════════
//  SipVoiceAgent — persistent subprocess bridge to AIOffice.VoiceAgent (--pipe-audio).
//
//  Architectural rule (see docs-dev/ARCHITECTURE.md "Media as I/O"): the media never re-implements
//  speech engines. VAD, whisper STT and Kokoro/SAPI TTS all live in the subprocess; this
//  class is the SIP medium's transport to it:
//    stdin:  {"cmd":"start","lang":…} | {"cmd":"audio","b64":…} | {"cmd":"speak","text":…,"render":true}
//    stdout: {"type":"transcript","text":…} | {"type":"audio","b64":…,"rate":…} | {"type":"done"} | {"type":"error",…}
//  The process stays alive between calls → the whisper model stays loaded (persistent STT).
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Persistent JSON-Lines client to the AIOffice.VoiceAgent --pipe-audio subprocess.</summary>
public static class SipVoiceAgent
{
    private static readonly object Sync = new();
    private static Process? _proc;
    private static StreamWriter? _stdin;
    private static Task? _readerTask;
    private static bool _started;

    /// <summary>Raised on {"type":"transcript"} — recognized user speech (VAD done in the subprocess).</summary>
    public static event Action<string>? Transcript;

    /// <summary>Raised on {"type":"vad"} — the subprocess VAD state: "speech" (utterance opened)
    /// or "end" (utterance closed, transcription started). The media bridge uses it to arm the
    /// processing indicator at the right moment (speech end = processing start).</summary>
    public static event Action<string>? VadState;

    /// <summary>Raised on {"type":"audio"} while a render-speak is in flight — PCM chunk (24 kHz).</summary>
    public static event Action<byte[]>? AudioChunk;

    /// <summary>Raised on {"type":"error"}.</summary>
    public static event Action<string>? Error;

    private static TaskCompletionSource? _speakDone;
    private static readonly object SpeakLock = new();
    private static volatile bool _ttsReady;   // {"type":"tts-ready"} — the subprocess TTS warm-up finished

    /// <summary>True when the subprocess is running and has received "start".</summary>
    public static bool IsReady => _proc is { HasExited: false } && _started;

    /// <summary>True once the subprocess signals its TTS engine is fully warmed up
    /// ({"type":"tts-ready"}). The bridge must not answer calls before it: the Kokoro warm-up
    /// (~7 s) completes AFTER the "ready" line, and answering earlier plays a silent welcome.</summary>
    public static bool IsTtsReady => _ttsReady;

    /// <summary>Waits (bounded) for the subprocess TTS readiness — used before answering a call.</summary>
    public static async Task WaitTtsReadyAsync(TimeSpan timeout)
    {
        var t0 = DateTime.UtcNow;
        while (!_ttsReady && (DateTime.UtcNow - t0) < timeout)
            await Task.Delay(100);
    }

    /// <summary>Path to the cross-platform AIOffice.VoiceAgent executable (voiceagent-stt/).</summary>
    public static string? ExePath { get; set; }

    /// <summary>Whisper model passed to the subprocess via AIOFFICE_WHISPER_MODEL (e.g. "small").</summary>
    public static string? SttModel { get; set; }

    /// <summary>Whisper quantization passed via AIOFFICE_WHISPER_QUANT (e.g. "q8_0", empty = FP16).</summary>
    public static string? SttQuant { get; set; }

    /// <summary>STT accelerator passed via AIOFFICE_WHISPER_DEVICE ("auto"/"cuda"/"vulkan"/"cpu").
    /// The whisper.net loader probes the GPU runtimes and falls back to CPU automatically; "cpu"
    /// skips the probe. Empty = the subprocess default ("auto").</summary>
    public static string? SttDevice { get; set; }

    /// <summary>
    /// Starts the persistent subprocess (idempotent) and sends "start" with the language.
    /// The whisper model loads once and stays resident for the whole server lifetime.
    /// </summary>
    public static async Task StartAsync(string lang, CancellationToken ct = default)
    {
        lock (Sync)
        {
            if (_proc is { HasExited: false } && _started) return;
            if (string.IsNullOrWhiteSpace(ExePath) || !File.Exists(ExePath))
            {
                Log.LogStep("SIP voice subprocess unavailable: AIOffice.VoiceAgent not found (voiceagent-stt/)");
                return;
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ExePath,
                    // Anchor the child to its own folder: the host may have been launched from
                    // any directory, and the child must not inherit an arbitrary CWD.
                    WorkingDirectory = Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardInputEncoding = new UTF8Encoding(false),
                };
                psi.ArgumentList.Add("--pipe-audio");
                // Model + quantization must be set BEFORE the process starts (read at startup).
                if (!string.IsNullOrWhiteSpace(SttModel)) psi.Environment["AIOFFICE_WHISPER_MODEL"] = SttModel;
                if (!string.IsNullOrWhiteSpace(SttQuant)) psi.Environment["AIOFFICE_WHISPER_QUANT"] = SttQuant;
                if (!string.IsNullOrWhiteSpace(SttDevice)) psi.Environment["AIOFFICE_WHISPER_DEVICE"] = SttDevice;
                _proc = new Process { StartInfo = psi };
                if (!_proc.Start())
                {
                    Log.LogStep("SIP voice subprocess failed to start");
                    return;
                }
                _stdin = _proc.StandardInput;
                _readerTask = Task.Run(ReadLoop);
                _started = true;
                _ttsReady = false;   // re-armed by {"type":"tts-ready"} after the Kokoro warm-up
                Log.LogStep("SIP voice subprocess started (--pipe-audio)");
            }
            catch (Exception ex)
            {
                Log.LogStep($"SIP voice subprocess start failed: {ex.Message}");
                _started = false;
            }
        }
        // Wait for the model load before returning: first transcribe is then instant.
        await SendAsync(JsonSerializer.Serialize(new { cmd = "start", lang }), ct);
        _started = true;
    }

    /// <summary>Feeds one decoded 16 kHz mono PCM chunk into the subprocess (VAD → whisper).</summary>
    public static async Task SendAudioAsync(byte[] pcm16k, CancellationToken ct = default)
    {
        if (!IsReady) return;
        var b64 = Convert.ToBase64String(pcm16k);
        await SendAsync(JsonSerializer.Serialize(new { cmd = "audio", b64 }), ct);
    }

    /// <summary>
    /// Renders text to speech in the subprocess and streams the resulting 24 kHz PCM chunks to
    /// <paramref name="onChunk"/> until the reply is complete (the subprocess emits "done").
    /// The subprocess renders ONE speak at a time: a new speak can start while a previous one is
    /// still rendering (e.g. the PIN accepted while the welcome is still streaming, or an
    /// agent-initiative turn racing the reply loop). The newcomer WAITS for the in-flight render
    /// instead of throwing — an exception here faults the fire-and-forget caller task and
    /// silently kills the conversation (phase stays Conversation, StartConversation never runs,
    /// transcripts are dropped: the call goes dead with no reply and no processing indicator).
    /// </summary>
    public static async Task SpeakAsync(string text, string lang, Action<byte[]> onChunk, CancellationToken ct = default)
    {
        TaskCompletionSource done;
        while (true)
        {
            TaskCompletionSource? inFlight;
            lock (SpeakLock) inFlight = _speakDone is { Task.IsCompleted: false } ? _speakDone : null;
            if (inFlight == null)
            {
                lock (SpeakLock)
                {
                    if (_speakDone is { Task.IsCompleted: false }) continue;   // lost the race — retry
                    done = _speakDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                break;
            }
            await inFlight.Task.WaitAsync(ct);
        }
        AudioChunk += Handler;
        try
        {
            await SendAsync(JsonSerializer.Serialize(new { cmd = "speak", text, lang, render = true }), ct);
            await done.Task.WaitAsync(ct);
        }
        finally
        {
            AudioChunk -= Handler;
            // Clear the slot ONLY if it still holds THIS speak's TCS. A newer speak may have
            // acquired the slot between our completion and this finally (the acquirer treats a
            // completed leftover as free and overwrites it) — nulling then would kill the newer
            // speak's "done" resolution and hang it forever (seen in SipSmoke test 19: the
            // locked-notice speak never completed, so the server never hung up).
            lock (SpeakLock) if (ReferenceEquals(_speakDone, done)) _speakDone = null;
        }

        void Handler(byte[] pcm) => onChunk(pcm);
    }

    /// <summary>Stops and disposes the subprocess.</summary>
    public static void Stop()
    {
        lock (Sync)
        {
            try { _stdin?.Dispose(); } catch { }
            try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
            _stdin = null;
            _started = false;
            _speakDone = null;
        }
    }

    private static async Task SendAsync(string json, CancellationToken ct)
    {
        lock (Sync)
        {
            if (_stdin == null || !_started) return;
            try
            {
                _stdin.WriteLine(json);
                _stdin.Flush();
            }
            catch (Exception ex)
            {
                Log.LogStep($"SIP voice subprocess write failed: {ex.Message}");
            }
        }
        await Task.CompletedTask;
    }

    private static async Task ReadLoop()
    {
        var reader = _proc?.StandardOutput;
        if (reader == null) return;
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    switch (type)
                    {
                        case "transcript":
                            if (root.TryGetProperty("text", out var txt) && txt.GetString() is { Length: > 0 } text)
                                Transcript?.Invoke(text);
                            break;
                        case "vad":
                            if (root.TryGetProperty("state", out var st) && st.GetString() is { Length: > 0 } state)
                                VadState?.Invoke(state);
                            break;
                        case "tts-ready":
                            _ttsReady = true;   // the TTS warm-up finished — calls can be answered
                            break;
                        case "audio":
                            if (root.TryGetProperty("b64", out var b64) && b64.ValueKind == JsonValueKind.String)
                                AudioChunk?.Invoke(Convert.FromBase64String(b64.GetString()!));
                            break;
                        case "done":
                            TaskCompletionSource? d;
                            lock (SpeakLock) d = _speakDone;
                            d?.TrySetResult();
                            break;
                        case "error":
                            if (root.TryGetProperty("text", out var err))
                                Error?.Invoke(err.GetString() ?? "voice subprocess error");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.LogStep($"SIP voice subprocess parse failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogStep($"SIP voice subprocess read failed: {ex.Message}");
        }
        finally
        {
            Log.LogStep("SIP voice subprocess exited");
        }
    }
}
