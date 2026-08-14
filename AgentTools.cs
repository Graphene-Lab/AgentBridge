// ═══════════════════════════════════════════════════════════════════════
//  AgentTools — agent-set id → AIOrchestrator tool NAMES
//
//  Single table for the "model" ids exposed by /v1/models: the HTTP chat
//  endpoint AND the SIP telephony loop (SipBridge) both resolve the id to the
//  tool names with this helper, so the two paths can never drift apart.
//  The names are resolved to concrete BaseAgentTool types at runtime by
//  AgentOrchestrator.ToolRegistry (plugin names are unique by definition), so
//  no project needs a compile-time dependency on a plugin. Core tools live in
//  the AIOrchestrator assembly; plugin tools are loaded dynamically from the
//  Tools/ folder (see ToolPlugins). See AIOrchestrator/ARCHITECTURE.md —
//  "Agent Architecture".
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Maps an agent-set id ("default-agent", "web-agent", ...) to the AIOrchestrator
/// tool names used by <see cref="AgentOrchestrator.ExecuteAction"/>.</summary>
public static class AgentTools
{
    /// <summary>Resolves the agent-set id to the tool names for <see cref="AgentOrchestrator.ExecuteAction"/>.</summary>
    public static string[] Resolve(string? model) => model?.ToLower() switch
    {
        "web-agent" => new[] { "FileTool", "WebTool" },
        "document-agent" => new[] { "DocumentTool" },
        "spreadsheet-agent" => new[] { "SpreadsheetTool" },
        "search-agent" or "research-agent" => new[] { "FileTool" },
        "email-agent" => new[] { "EMailTool" },
        "office-agent" => new[] { "FileTool", "OfficeTool" },
        "multi-agent" => new[]
        {
            "FileTool",
            "WebTool",
            "DocumentTool",
            "SpreadsheetTool",
            "EMailTool"
        },
        _ => new[] { "FileTool", "WebTool" }
    };
}
