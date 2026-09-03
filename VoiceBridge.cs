using System.Diagnostics;
using System.Text;
using System.Text.Json;

// ═══════════════════════════════════════════════════════════════════════
//  VoiceBridge — one-shot speech recognition via the AIOffice.VoiceAgent.Win.exe
//  subprocess (Windows only; same JSON-Lines stdin/stdout protocol used by the
//  AIOffice Voice panel: {"cmd":"start","lang":...} → {"type":"transcript","text":...}).
//
//  The endpoint POST /v1/voice/listen reports itself unavailable (501) when the
//  platform or the executable is missing — the client activates voice speech only
//  where it really runs. The microphone is exclusive, so recognition is serialized
//  across requests with a static gate.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>One-shot speech recognition bridge to the Windows VoiceAgent subprocess.</summary>
public static class VoiceBridge
{
    /// <summary>Serializes microphone access (one listener at a time).</summary>
    private static readonly SemaphoreSlim ListenGate = new(1, 1);

    /// <summary>
    /// Path to AIOffice.VoiceAgent.Win.exe. Defaults to the server base directory;
    /// overridable with the "Voice:ExePath" appsettings key / --Voice:ExePath CLI override.
    /// </summary>
    public static string? ExePath { get; set; }

    /// <summary>True when the platform is Windows and the VoiceAgent executable is present.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            var exe = ResolveExe();
            return exe != null && File.Exists(exe);
        }
    }

    /// <summary>Human-readable reason when <see cref="IsAvailable"/> is false.</summary>
    public static string UnavailableReason
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return "Voice speech recognition runs only on Windows (AIOffice.VoiceAgent.Win).";
            var exe = ResolveExe();
            if (exe == null || !File.Exists(exe))
                return $"AIOffice.VoiceAgent.Win.exe not found in '{Path.GetDirectoryName(exe) ?? AppDomain.CurrentDomain.BaseDirectory}'. Copy the VoiceAgent output into the voiceagent/ subfolder next to the server (the csproj does it automatically when the sibling VoiceAgent build exists) or set Voice:ExePath.";
            return "";
        }
    }

    /// <summary>
    /// Listens once with the server microphone and returns the recognized phrase.
    /// </summary>
    /// <param name="lang">Two-letter ISO language code; default = the machine's UI/system language.</param>
    /// <param name="timeoutSeconds">Seconds to wait for speech (clamped 1–60; default 15).</param>
    /// <param name="ct">Cancellation token (client disconnect).</param>
    /// <exception cref="TimeoutException">No speech recognized within the timeout.</exception>
    public static async Task<string> ListenOnceAsync(string? lang, int timeoutSeconds, CancellationToken ct)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException(UnavailableReason);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds <= 0 ? 15 : timeoutSeconds, 1, 60));
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // Speech recognition speaks the machine's language (never a hardcoded default).
        lang = string.IsNullOrWhiteSpace(lang) ? SystemLang.Get() : lang.Trim();

        await ListenGate.WaitAsync(ct);
        Process? process = null;
        try
        {
            var exe = ResolveExe()!;
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    // Anchor the child to its own folder: the host may have been launched from
                    // any directory, and the child must not inherit an arbitrary CWD.
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardInputEncoding = new UTF8Encoding(false)
                }
            };
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start {ResolveExe()}.");

            var input = process.StandardInput;
            var output = process.StandardOutput;

            while (await output.ReadLineAsync(timeoutCts.Token) is { } line)
            {
                if (!TryParseLine(line, out var type, out var text)) continue;
                switch (type)
                {
                    case "ready":
                        await WriteCommandAsync(input, new { cmd = "start", lang });
                        break;
                    case "transcript" when !string.IsNullOrWhiteSpace(text):
                        return text;
                    case "error":
                        throw new InvalidOperationException($"VoiceAgent error: {text}");
                }
            }
            throw new InvalidOperationException("VoiceAgent exited before recognizing speech.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No speech recognized within {timeout.TotalSeconds:0}s.");
        }
        finally
        {
            try { if (process != null && !process.HasExited) { process.StandardInput.WriteLine("{\"cmd\":\"stop\"}"); process.StandardInput.Flush(); } } catch { }
            try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process?.Dispose();
            ListenGate.Release();
        }
    }

    private static string? ResolveExe()
    {
        if (!string.IsNullOrWhiteSpace(ExePath))
            return Path.IsPathRooted(ExePath) ? ExePath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ExePath);
        // The csproj copies the VoiceAgent output into the voiceagent/ subfolder — NOT
        // flat next to the server: VoiceAgent.Win pins an older KokoroSharp (0.6.7) that
        // would shadow this server's 0.8.4 one and break the in-process TTS.
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voiceagent", "AIOffice.VoiceAgent.Win.exe");
    }

    private static async Task WriteCommandAsync(StreamWriter input, object command)
    {
        var json = JsonSerializer.Serialize(command, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await input.WriteLineAsync(json);
        await input.FlushAsync();
    }

    private static bool TryParseLine(string line, out string type, out string text)
    {
        type = "";
        text = "";
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            text = root.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
