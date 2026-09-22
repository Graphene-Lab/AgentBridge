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
using System.Reflection;
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

    /// <summary>Agent reporting tools — the agent's own voice toward the maintainers
    /// (MalfunctionReporterTool: malfunction reports and feature requests as GitHub issues).
    /// Appended to every agent set when active, like the core tools; the user controls them
    /// with their own persisted gate (TUI Help → Malfunction reports). Active = the tool's
    /// own Enabled AND the tools.json policy both allow it.</summary>
    public static readonly string[] ReportingTools = { "MalfunctionReporterTool" };

    /// <summary>Whether a reporting tool is active: its own persisted gate wins over the
    /// default, and the tools.json policy can veto it too. Non-reporting names are always true.
    /// Name comparison is case-insensitive, like every other tool-name lookup in this class.</summary>
    public static bool IsReportingToolActive(string name) =>
        !ReportingTools.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        (AIOrchestrator.API.MalfunctionReporterTool.Enabled && IsEnabled(name));

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
        ("multi-files", new[] { "FileTool", "WebTool", "DocumentTool", "SpreadsheetTool", "PresentationTool", "EMailTool", "GitTool" }),
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

    // ── Lean-orchestrator split (see docs-dev/ARCHITECTURE.md, "Lean orchestrator") ──
    // System tools are compiled into AIOrchestrator (FileTool, GitTool, TaskSchedulerTool,
    // WebTool, EMailTool); plugin tools load from the Tools/ folder. The orchestrator keeps
    // the lean system surface for immediate, simple work; plugin tools run behind an
    // isolated subagent so their large definitions stay OUT of the orchestrator's per-turn
    // cached prompt prefix. Controlled by AGENTBRIDGE_ORCHESTRATOR_SPLIT (default on; "0"
    // disables and restores the flat single-agent behavior).
    private static readonly Assembly NativeAssembly = typeof(AIOrchestrator.API.BaseAgentTool).Assembly;

    /// <summary>Environment variable that switches the split on or off (see
    /// <see cref="SplitEnabled"/>). Exposed so a host or a test never has to retype it.</summary>
    public const string SplitEnvVar = "AGENTBRIDGE_ORCHESTRATOR_SPLIT";

    /// <summary>Kill-switch for the split: setting <see cref="SplitEnvVar"/> to 0/false/off/no
    /// (any case, surrounding spaces ignored) restores the flat single-agent behavior — every
    /// tool on the orchestrator — without a rebuild. Read per call, once per user turn, so the
    /// environment lookup costs nothing and the switch stays testable.</summary>
    public static bool SplitEnabled
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(SplitEnvVar)?.Trim();
            if (string.IsNullOrEmpty(raw)) return true;
            return raw.ToLowerInvariant() is not ("0" or "false" or "off" or "no");
        }
    }

    /// <summary>True when the tool is a native system tool (compiled into the AIOrchestrator
    /// assembly), false when it is a plugin loaded dynamically from the Tools/ folder — and
    /// false for a name that nothing resolves, which is no tool at all.</summary>
    public static bool IsSystemTool(string name) => McpToolRegistry.Resolve(name)?.Assembly == NativeAssembly;

    /// <summary>Splits an already-resolved tool-name array into the orchestrator's set (the system
    /// tools — a lean, cache-stable prefix) and the heavy-work subagent's set (the WHOLE set, so a
    /// delegated task never pauses mid-flight just to read a file or search the web). The split
    /// happens only when it pays and stays lawful:
    ///   • a name that resolves to nothing stays on the orchestrator — it is not a tool the
    ///     subagent could use either, so it must not be what triggers a delegation;
    ///   • a set with no LOADED plugin tool is not split at all: flat behavior, no subagent;
    ///   • a set that would leave the orchestrator with no tool of its own is not split either:
    ///     that is the client's explicit choice (the `tools` extension overrides the preset —
    ///     docs/API.md), and a pure dispatcher would contradict the documented contract.
    /// In every unsplit case the subagent set is empty and the caller runs exactly as before.</summary>
    public static (string[] OrchestratorTools, string[] SubagentTools) SplitForOrchestration(string[] resolvedNames)
    {
        if (!SplitEnabled) return (resolvedNames, Array.Empty<string>());

        var orchestrator = new List<string>(resolvedNames.Length);
        var hasLoadedPlugin = false;
        foreach (var name in resolvedNames)
        {
            var type = McpToolRegistry.Resolve(name);
            if (type == null || type.Assembly == NativeAssembly)
            {
                orchestrator.Add(name);
                continue;
            }
            hasLoadedPlugin = true;
        }

        if (!hasLoadedPlugin || orchestrator.Count == 0)
            return (resolvedNames, Array.Empty<string>());
        return (orchestrator.ToArray(), resolvedNames);
    }

    /// <summary>Runs one agent turn with the lean-orchestrator split applied — the single entry
    /// point every textual chat path in this host uses, so a new path cannot forget the split.
    /// The voice path passes the two sets to VoiceConversation explicitly instead: it owns the
    /// streaming loop and needs both arrays.</summary>
    public static AgentResult ExecuteSplit(AgentHarness harness, string prompt, string[] resolvedToolNames,
        int maxIterations = 200, IEnumerable<UISupportGeneric.FileAttachment>? attachments = null, bool isLocalUser = false)
    {
        var (orchestratorTools, subagentTools) = SplitForOrchestration(resolvedToolNames);
        return harness.ExecuteAction(prompt, orchestratorTools, subagentNames: subagentTools,
            maxIterations: maxIterations, attachments: attachments, isLocalUser: isLocalUser);
    }

    /// <summary>Dynamic "all-files" set: every tool currently loaded that the per-tool config
    /// leaves enabled (core tools included — they default ON like everything else). Reporting
    /// tools follow their own gate too: disabled means absent from every set.</summary>
    public static string[] AllFilesTools() => Catalog()
        .Select(c => c.Name)
        .Where(IsEnabled)
        .Where(IsReportingToolActive)
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
        // Reporting tools join every set when active (see ReportingTools) — the agent can
        // report a blocker or request a feature from any agent set, not just all-files.
        return WithReporting(set.ToArray());
    }

    /// <summary>Normalizes a tool list against the reporting gate: active reporting tools are
    /// present, inactive ones removed. Applied to preset resolution and to the TUI's custom
    /// selection (save, send and display) so an explicit combination can never contradict
    /// the user's Help-menu toggle for the reporting tool. Name comparison is
    /// case-insensitive; the canonical spelling from <see cref="ReportingTools"/> is what gets
    /// added.</summary>
    public static string[] WithReporting(string[] tools)
    {
        var set = tools
            .Where(t => !ReportingTools.Contains(t, StringComparer.OrdinalIgnoreCase) || IsReportingToolActive(t))
            .ToList();
        foreach (var rep in ReportingTools)
            if (IsReportingToolActive(rep) && !set.Contains(rep, StringComparer.OrdinalIgnoreCase))
                set.Add(rep);
        return set.ToArray();
    }

    /// <summary>Per-tool config (tools.json under <see cref="AppConfig.PersistentDir"/>,
    /// protected from updates — same pattern as telegram.json). Records only deviations; an
    /// absent file means "all unspecified ⇒ defaults". Format:
    /// {"tools": { "OfficeTool": true, "FileTool": false }}.</summary>
    private static readonly Dictionary<string, bool> Config = LoadConfig();

    private static Dictionary<string, bool> LoadConfig()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = AppConfig.ToolsFile;
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
