using AIOrchestrator;

// ═══════════════════════════════════════════════════════════════════════
//  RuntimeAssets — readiness of the runtime data payload next to the exe
//
//  Kokoro TTS (voices/, kokoro.onnx) and the SearchPioneer.Lingua language
//  models (Lingua/LanguageModels/) are DISTRIBUTION CONTENT: they ship inside
//  the release archive (see the CopyTtsAssetsToPublish / CopyLinguaModels*
//  csproj targets) and are read from the app base directory at runtime.
//  A freshly installed app therefore always has them. An OLD installation
//  that predates a payload (e.g. Lingua was never shipped before 2026-09)
//  runs with the assets missing until its next auto-update: nothing must
//  crash or show a technical error in that window — the app stays fully
//  functional and surfaces a clear, human "setup incomplete" state instead.
//  This class is the single check consumed by /v1/control capabilities,
//  the TUI status and the startup console note.
// ═══════════════════════════════════════════════════════════════════════
public static class RuntimeAssets
{
    /// <summary>The runtime payload tokens in user-facing order (also used as capability
    /// keys). Unknown future tokens simply show as themselves.</summary>
    public static readonly string[] Tokens = { "tts", "lingua" };

    /// <summary>True when the Kokoro TTS payload (voices/ + kokoro.onnx) is reachable from
    /// the app base directory — the same resolution the TTS engine itself uses.</summary>
    public static bool TtsReady =>
        KokoroTts.FindModel() != null && KokoroTts.FindVoicesDir() != null;

    /// <summary>True when the lingua language models (Lingua/LanguageModels/<lang>/*.json.br)
    /// are present next to the executable.</summary>
    public static bool LinguaReady =>
        Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Lingua", "LanguageModels"));

    /// <summary>True when every runtime payload of this install is present.</summary>
    public static bool IsComplete => Missing.Length == 0;

    /// <summary>The payload tokens that are missing on this install (empty = complete).</summary>
    public static string[] Missing =>
        new[]
        {
            TtsReady ? null : "tts",
            LinguaReady ? null : "lingua",
        }.Where(t => t != null).Select(t => t!).ToArray();
}
