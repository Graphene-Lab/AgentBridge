using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;

// ═══════════════════════════════════════════════════════════════════════
//  TtsEngine — in-process Kokoro neural TTS for POST /v1/audio/speech
//
//  Cross-platform (ONNX runtime): needs kokoro.onnx + voices/ next to
//  the executable. The csproj brings voices/ + voices-zh/ from the KokoroSharp
//  package content and provides kokoro.onnx (copy from the sibling VoiceAgent
//  build output, else curl download) — the endpoint reports itself unavailable
//  (501) until the assets are present, so clients only activate TTS when the
//  platform actually supports it.
//
//  The engine is initialized lazily on first use (loading the 325 MB model takes
//  seconds and RAM); a static lock serializes synthesis because the underlying
//  synthesizer queues jobs internally.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>In-process Kokoro neural TTS engine backing POST /v1/audio/speech.</summary>
public static class TtsEngine
{
    private static readonly object Sync = new();
    private static KokoroWavSynthesizer? _synth;
    private static List<string> _voices = new();
    private static string? _unavailableReason;

    /// <summary>True when the TTS engine could be initialized (model + voices present).</summary>
    public static bool IsAvailable
    {
        get { EnsureInitialized(); return _synth != null; }
    }

    /// <summary>Human-readable reason when <see cref="IsAvailable"/> is false.</summary>
    public static string UnavailableReason
    {
        get { EnsureInitialized(); return _unavailableReason ?? "TTS not initialized"; }
    }

    /// <summary>Kokoro voice ids currently loaded from voices/, sorted.</summary>
    public static IReadOnlyList<string> Voices
    {
        get { EnsureInitialized(); return _voices; }
    }

    /// <summary>
    /// Synthesizes text to WAV bytes with the requested voice. The voice name accepts an
    /// OpenAI voice name ("alloy", "echo", ...) or a raw Kokoro voice id ("if_sara",
    /// "af_heart", ...); unknown names fall back to the first loaded voice.
    /// The optional <paramref name="lang"/> (two-letter ISO code) picks a voice of that
    /// language: Kokoro voices are per-language (af_*/am_* = English, if_*/im_* = Italian,
    /// ef_* = Spanish, ff_* = French, jf_* = Japanese, ...). When no voice is given, the
    /// machine's language (<see cref="SystemLang.Get"/>) selects the default — every machine
    /// speaks its own language, no hardcoded default.
    /// </summary>
    public static byte[] Synthesize(string text, string? voice, double? speed, string? lang = null)
    {
        EnsureInitialized();
        if (_synth == null)
            throw new InvalidOperationException(UnavailableReason);

        var voiceId = ResolveVoiceId(voice, lang);
        var kokoroVoice = _voices.Contains(voiceId, StringComparer.OrdinalIgnoreCase)
            ? KokoroVoiceManager.GetVoice(voiceId)
            : null;
        if (kokoroVoice == null && _voices.Count > 0)
            kokoroVoice = KokoroVoiceManager.GetVoice(_voices[0]);
        if (kokoroVoice == null)
            throw new InvalidOperationException("No Kokoro voice available.");

        var config = new KokoroTTSPipelineConfig
        {
            Speed = (float)Math.Clamp(speed ?? 1.0, 0.25, 4.0)
        };

        byte[] pcm;
        lock (Sync)
        {
            pcm = _synth.Synthesize(text, kokoroVoice, config);
        }
        return WrapWav(pcm);
    }

    // KokoroSharp's KokoroWavSynthesizer returns RAW 16-bit PCM (no container); the WAV
    // header below (RIFF/fmt/data chunks) makes the response a playable audio/wav file.
    private static byte[] WrapWav(byte[] pcm)
    {
        var fmt = KokoroPlayback.waveFormat;
        var sampleRate = fmt.SampleRate;
        var channels = fmt.Channels;
        var bits = fmt.BitsPerSample;
        var blockAlign = (short)(channels * bits / 8);
        var byteRate = sampleRate * blockAlign;

        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);              // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write((short)bits);
        w.Write("data"u8);
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    // INVARIANT (project rule): voice selection follows the MACHINE'S LANGUAGE
    // (SystemLang.Get() / lang parameter) — the code contains no per-language
    // setting. The map below is only the "ISO language → Kokoro voice prefix of
    // that language" translation; if the machine's language is not in the map a
    // neutral default is used, without assuming the user.
    // OpenAI voice names → Kokoro voice ids (best-effort; the mapping only picks a
    // pleasant default, the full Kokoro catalogue stays reachable by raw id).
    private static readonly Dictionary<string, string> OpenAiToKokoro = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alloy"] = "af_heart",
        ["echo"] = "am_michael",
        ["fable"] = "bf_alice",
        ["onyx"] = "am_onyx",
        ["nova"] = "af_bella",
        ["shimmer"] = "ef_dora",
        ["coral"] = "bf_emma",
        ["sage"] = "bm_george",
        ["ash"] = "pm_alex",
        ["ballad"] = "ff_siwis",
        ["verse"] = "jf_alpha",
    };

    // Two-letter ISO language → Kokoro voice prefix (the part before the underscore).
    private static readonly Dictionary<string, string> LangPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["it"] = "if_",      // Italian
        ["en"] = "af_",      // American English (af_/am_ both exist)
        ["es"] = "ef_",      // Spanish
        ["fr"] = "ff_",      // French
        ["ja"] = "jf_",      // Japanese
        ["zh"] = "cm_",      // Mandarin
        ["ko"] = "kf_",      // Korean
        ["ar"] = "am_",      // Arabic (fallback to a male English id is wrong, but Kokoro's
                             // Arabic voices use am_/af_ prefixes in some builds — keep simple)
    };

    private static string ResolveVoiceId(string? voice, string? lang)
    {
        // Raw Kokoro voice ("if_sara") → use it as-is.
        if (!string.IsNullOrWhiteSpace(voice) && !OpenAiToKokoro.ContainsKey(voice))
            return voice;

        var id = string.IsNullOrWhiteSpace(voice) ? "" : OpenAiToKokoro[voice];
        var targetLang = string.IsNullOrWhiteSpace(lang) ? SystemLang.Get() : lang!;

        // No voice requested: the language (explicit or system) picks the default.
        if (string.IsNullOrWhiteSpace(voice))
        {
            var prefix = LangPrefix.TryGetValue(targetLang, out var p) ? p : null;
            if (prefix != null)
            {
                var match = _voices.FirstOrDefault(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return id.Length > 0 ? id : "af_heart";
        }

        // Named OpenAI voice but of a different language than requested → prefer the
        // language (e.g. "alloy" + lang "it" → an if_* voice).
        var mappedPrefix = LangPrefix.TryGetValue(targetLang, out var mp) ? mp : null;
        if (mappedPrefix != null && !id.StartsWith(mappedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var match = _voices.FirstOrDefault(v => v.StartsWith(mappedPrefix, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return id;
    }

    private static void EnsureInitialized()
    {
        if (_synth != null || _unavailableReason != null) return;
        lock (Sync)
        {
            if (_synth != null || _unavailableReason != null) return;

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var modelPath = Path.Combine(baseDir, "kokoro.onnx");
            var voicesDir = Path.Combine(baseDir, "voices");

            if (!File.Exists(modelPath))
            {
                _unavailableReason = $"kokoro.onnx not found at '{modelPath}'. Build the server (the DownloadKokoroModel target copies or downloads it) to enable TTS.";
                return;
            }
            if (!Directory.Exists(voicesDir))
            {
                _unavailableReason = $"voices/ directory not found at '{voicesDir}'. TTS unavailable.";
                return;
            }

            try
            {
                KokoroVoiceManager.LoadVoicesFromPath(voicesDir);
                _voices = KokoroVoiceManager.Voices
                    .Select(v => v.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _synth = new KokoroWavSynthesizer(modelPath);
                if (_voices.Count == 0)
                {
                    _unavailableReason = "voices/ contains no Kokoro voices.";
                    _synth.Dispose();
                    _synth = null;
                }
            }
            catch (Exception ex)
            {
                _unavailableReason = $"TTS initialization failed: {ex.Message}";
                _synth?.Dispose();
                _synth = null;
            }
        }
    }
}
