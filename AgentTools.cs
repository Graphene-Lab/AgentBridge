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
using AIOrchestrator;

/// <summary>Maps an agent-set id ("default-agent", "web-agent", ...) to the AIOrchestrator
/// tool names used by <see cref="AgentHarness.ExecuteAction"/>, and enumerates the DYNAMIC
/// tool catalog for the TUI (multi-tool checklist). The preset table is the single source
/// of truth for agent sets: <see cref="Resolve"/> derives from it, so the presets and the
/// HTTP resolution can never drift apart. The catalog itself is not static — most tools are
/// plugins loaded at runtime into <see cref="McpToolRegistry"/> (see ToolPlugins).</summary>
public static class AgentTools
{
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

    /// <summary>Resolves the agent-set id to the tool names for <see cref="AgentHarness.ExecuteAction"/>.</summary>
    public static string[] Resolve(string? model)
    {
        var m = model?.Trim().ToLowerInvariant();
        foreach (var p in Presets)
            if (string.Equals(p.Id, m, StringComparison.OrdinalIgnoreCase))
                return p.Tools;
        return Presets[0].Tools;   // default-agent
    }
}
