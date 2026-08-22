using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIOrchestrator;

// ═══════════════════════════════════════════════════════════════════════
//  SipVoiceAgent — persistent subprocess bridge to AIOffice.VoiceAgent (--pipe-audio).
//
//  Architectural rule (see ARCHITECTURE.md "Media as I/O"): the media never re-implements
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

    /// <summary>Raised on {"type":"audio"} while a render-speak is in flight — PCM chunk (24 kHz).</summary>
    public static event Action<byte[]>? AudioChunk;

    /// <summary>Raised on {"type":"error"}.</summary>
    public static event Action<string>? Error;

    private static TaskCompletionSource? _speakDone;
    private static readonly object SpeakLock = new();

    /// <summary>True when the subprocess is running and has received "start".</summary>
    public static bool IsReady => _proc is { HasExited: false } && _started;

    /// <summary>Path to the cross-platform AIOffice.VoiceAgent executable (voiceagent-stt/).</summary>
    public static string? ExePath { get; set; }

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
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardInputEncoding = new UTF8Encoding(false),
                };
                psi.ArgumentList.Add("--pipe-audio");
                _proc = new Process { StartInfo = psi };
                if (!_proc.Start())
                {
                    Log.LogStep("SIP voice subprocess failed to start");
                    return;
                }
                _stdin = _proc.StandardInput;
                _readerTask = Task.Run(ReadLoop);
                _started = true;
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
    /// </summary>
    public static async Task SpeakAsync(string text, string lang, Action<byte[]> onChunk, CancellationToken ct = default)
    {
        TaskCompletionSource done;
        lock (SpeakLock)
        {
            if (_speakDone is { Task.IsCompleted: false })
                throw new InvalidOperationException("SIP voice subprocess: overlapping speak not supported");
            done = _speakDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            lock (SpeakLock) _speakDone = null;
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
