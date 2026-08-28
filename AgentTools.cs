// ═══════════════════════════════════════════════════════════════════════
//  AgentTools — agent-set id → AIOrchestrator tool NAMES
//
//  Single table for the "model" ids exposed by /v1/models: the HTTP chat
//  endpoint AND the SIP telephony loop (SipBridge) both resolve the id to the
//  tool names with this helper, so the two paths can never drift apart.
//  The names are resolved to concrete BaseAgentTool types at runtime by
//  AgentHarness.McpToolRegistry (plugin names are unique by definition), so
//  no project needs a compile-time dependency on a plugin. Core tools live in
//  the AIOrchestrator assembly; plugin tools are loaded dynamically from the
//  Tools/ folder (see ToolPlugins). See AIOrchestrator/docs-dev/ARCHITECTURE.md —
//  "Agent Architecture".
// ═══════════════════════════════════════════════════════════════════════
using System.Text.Json;
using System.Text.Json.Nodes;
using AIOrchestrator;

/// <summary>Maps an agent-set id ("default-agent", "web-agent", ...) to the AIOrchestrator
/// tool names used by <see cref="AgentHarness.ExecuteAction"/>, and enumerates the DYNAMIC
/// tool catalog for the TUI (multi-tool checklist). The preset table is the single source
/// of truth for agent sets: <see cref="Resolve"/> derives from it, so the presets and the
/// HTTP resolution can never drift apart. The catalog itself is not static — most tools are
/// plugins loaded at runtime into <see cref="McpToolRegistry"/> (see ToolPlugins).</summary>
public static class AgentTools
{
    /// <summary>Core tools — architectural primitives the other tools depend on (FileTool:
    /// sandbox search/read surface; GitTool: versioning/rollback; TaskSchedulerTool: scheduled
    /// automated task chats). Always ON by default and locked in the TUI picker; changeable only
    /// via tools.json (see docs-dev/ARCHITECTURE.md, "Agent sets &amp; tool policy").</summary>
    public static readonly string[] CoreTools = { "FileTool", "GitTool", "TaskSchedulerTool" };

    /// <summary>Class-B tools — vendored engines wrapped by our adapters. Default OFF unless
    /// explicitly enabled in tools.json (domain overlap + trust/control, see the policy doc).
    /// Currently the only one: OfficeTool (vendored officecli engine).</summary>
    public static readonly string[] ClassBTools = { "OfficeTool" };

    /// <summary>Agent-set presets (id → tool names) in TUI display order. Tool names are the
    /// API contract; a preset only activates the ones that are actually loaded at runtime.</summary>
    public static readonly (string Id, string[] Tools)[] Presets =
    {
        ("default-agent", new[] { "FileTool", "WebTool", "GitTool" }),
        ("web-agent", new[] { "FileTool", "WebTool" }),
        ("search-agent", new[] { "FileTool" }),
        ("research-agent", new[] { "FileTool" }),
        ("document-files", new[] { "FileTool", "DocumentTool", "GitTool" }),
        ("spreadsheet-files", new[] { "FileTool", "SpreadsheetTool", "GitTool" }),
        ("email-agent", new[] { "EMailTool" }),
        ("office-files", new[] { "FileTool", "OfficeTool", "GitTool" }),
        ("multi-files", new[] { "FileTool", "WebTool", "DocumentTool", "SpreadsheetTool", "EMailTool", "GitTool" }),
    };

    /// <summary>Agent-set ids exposed as models: the static presets plus the dynamic
    /// "all-files" preset (every loaded tool the per-tool config leaves enabled).</summary>
    public static string[] AllIds { get; } = Presets.Select(p => p.Id).Append("all-files").ToArray();

    /// <summary>All tools actually available at runtime — core tools plus dynamically loaded
    /// plugins — with their one-line description (class-level XML summary, English, falling
    /// back to the class name). Used by the TUI tool checklist. Cached: the registry is
    /// populated once at startup (ToolPlugins.Host), so the per-open assembly scan of the
    /// first draft was pure waste.</summary>
    public static (string Name, string Description)[] Catalog() => _catalog.Value;

    private static readonly Lazy<(string Name, string Description)[]> _catalog = new(() =>
        McpToolRegistry.All()
            .Select(t => (t.Name, Describe(t.Type)))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    private static string Describe(Type type)
    {
        var desc = UISupportGeneric.Terminal.GetClassDescriptionFirstLine(type);
        return string.IsNullOrWhiteSpace(desc) ? type.Name : desc;
    }

    /// <summary>Resolves the agent-set id to the tool names for <see cref="AgentHarness.ExecuteAction"/>.
    /// The enabled core tools are always appended to a preset (they are primitives, not optional
    /// tools — see docs-dev/ARCHITECTURE.md, "Agent sets &amp; tool policy"). "all-files" resolves
    /// to every loaded tool the per-tool config leaves enabled.</summary>
    public static string[] Resolve(string? model)
    {
        var m = model?.Trim().ToLowerInvariant();
        if (string.Equals(m, "all-files", StringComparison.OrdinalIgnoreCase))
            return AllFilesTools();
        foreach (var p in Presets)
            if (string.Equals(p.Id, m, StringComparison.OrdinalIgnoreCase))
                return WithCore(p.Tools);
        return WithCore(Presets[0].Tools);   // default-agent
    }

    /// <summary>Dynamic "all-files" set: every tool currently loaded that the per-tool config
    /// leaves enabled (core tools included — they default ON like everything else).</summary>
    public static string[] AllFilesTools() => Catalog()
        .Select(c => c.Name)
        .Where(IsEnabled)
        .ToArray();

    /// <summary>Effective per-tool status: an explicit tools.json value wins; otherwise class-B
    /// tools default OFF and everything else ON (the "unspecified ⇒ ON" rule).</summary>
    public static bool IsEnabled(string toolName) =>
        Config.TryGetValue(toolName, out var on) ? on : !ClassBTools.Contains(toolName);

    private static string[] WithCore(string[] tools)
    {
        // A core tool the config disabled is removed from the preset's own list AND not
        // re-added, so the "disabled in tools.json" state is uniform across every preset.
        var set = tools.Where(t => !CoreTools.Contains(t) || IsEnabled(t)).ToList();
        foreach (var core in CoreTools)
            if (IsEnabled(core) && !set.Contains(core))
                set.Add(core);
        return set.ToArray();
    }

    /// <summary>Per-tool config (tools.json next to the executable, protected from updates —
    /// same pattern as telegram.json). Records only deviations; an absent file means "all
    /// unspecified ⇒ defaults". Format: {"tools": { "OfficeTool": true, "FileTool": false }}.</summary>
    private static readonly Dictionary<string, bool> Config = LoadConfig();

    private static Dictionary<string, bool> LoadConfig()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools.json");
            if (!File.Exists(path)) return result;
            if (JsonSerializer.Deserialize<JsonObject>(File.ReadAllText(path))?["tools"] is not JsonObject tools)
                return result;
            foreach (var kv in tools)
                if (bool.TryParse(kv.Value?.ToString(), out var on))
                    result[kv.Key] = on;
        }
        catch (Exception ex)
        {
            Log.LogStep($"AgentTools: failed to read tools.json ({ex.Message}) — using defaults");
        }
        return result;
    }
}
