// ═══════════════════════════════════════════════════════════════════════
//  AgentTools — agent-set id → AIOrchestrator tool types
//
//  Single table for the "model" ids exposed by /v1/models: the HTTP chat
//  endpoint AND the SIP telephony loop (SipBridge) both resolve the id to the
//  concrete IAgentTool implementations with this helper, so the two paths can
//  never drift apart. See AIOrchestrator/ARCHITECTURE.md — "Agent Architecture".
// ═══════════════════════════════════════════════════════════════════════

using AIOrchestrator;

/// <summary>Maps an agent-set id ("default-agent", "web-agent", ...) to the AIOrchestrator
/// IAgentTool implementations used by <see cref="AgentOrchestrator.ExecuteAction"/>.</summary>
public static class AgentTools
{
    /// <summary>Resolves the agent-set id to the tool types for <see cref="AgentOrchestrator.ExecuteAction"/>.</summary>
    public static Type[] Resolve(string? model) => model?.ToLower() switch
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
