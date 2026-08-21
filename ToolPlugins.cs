using AIOrchestrator;

namespace AgentBridge
{
    /// <summary>Agent-tool plugin host: loads <see cref="AIOrchestrator.API.BaseAgentTool"/>
    /// plugins from the Tools/ folder next to the executable — scanned at startup, hot-added
    /// via a filesystem watcher (30 s debounce). No AgentBridge project references a plugin:
    /// the agent sets in <see cref="AgentTools"/> pass tool NAMES, and
    /// <see cref="McpToolRegistry"/> resolves them to the loaded types at runtime.</summary>
    public static class ToolPlugins
    {
        /// <summary>The one plugin host shared by the whole server (startup scan + watcher).
        /// Touched during startup so plugins are loaded (and registered in
        /// <see cref="McpToolRegistry"/>) before the first ExecuteAction call.</summary>
        public static McpToolHost Host { get; } =
            new(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools"));
    }
}
