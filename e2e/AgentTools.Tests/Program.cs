// AgentTools policy selftest — run standalone: dotnet run --project e2e/AgentTools.Tests
//
// Verifies the three-level tool policy (docs-dev/ARCHITECTURE.md, "Agent sets & tool policy"):
//   - core tools (FileTool, GitTool) default ON and are appended to every preset;
//   - class-B tools (OfficeTool) default OFF;
//   - the dynamic "all-files" preset resolves to every loaded, enabled tool;
//   - tools.json overrides (explicit value wins, "unspecified ⇒ ON" otherwise).
//
// Phase 1 (no arg): asserts the defaults with NO tools.json present.
// Phase 2 (--with-tools-json): writes {"tools": {"OfficeTool": true, "FileTool": false}} under
//   PersistentData\ in its own output directory (AgentTools reads tools.json from there — the
//   single persistent-config directory rule) BEFORE the first AgentTools access
//   (AgentTools.Config is a static snapshot loaded at first access), asserts the overrides,
//   then deletes the file.
using AIOrchestrator;

var withToolsJson = args.Contains("--with-tools-json");
var toolsJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PersistentData", "tools.json");

if (withToolsJson)
{
    if (File.Exists(toolsJsonPath))
        throw new InvalidOperationException("tools.json already present — delete it first");
    Directory.CreateDirectory(Path.GetDirectoryName(toolsJsonPath)!);
    File.WriteAllText(toolsJsonPath, """{ "tools": { "OfficeTool": true, "FileTool": false } }""");
}
else if (File.Exists(toolsJsonPath))
{
    Console.WriteLine("tools.json present in the output — delete it and re-run for phase 1 (defaults)");
    Environment.Exit(2);
}

var failures = 0;
void Check(string what, bool cond)
{
    Console.WriteLine($"  {(cond ? "✓" : "✗ FAIL")} {what}");
    if (!cond) failures++;
}

Console.WriteLine($"AgentTools policy test — {(withToolsJson ? "phase 2 (tools.json overrides)" : "phase 1 (defaults)")}");

// Model surface.
Check("all-files is exposed as a model id", AgentTools.AllIds.Contains("all-files", StringComparer.OrdinalIgnoreCase));
foreach (var p in AgentTools.Presets)
    Check($"static preset '{p.Id}' exposed", AgentTools.AllIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase));

// Per-tool default status ("unspecified ⇒ ON", class B OFF) — defaults only (phase 1).
if (!withToolsJson)
{
    Check("core FileTool enabled by default", AgentTools.IsEnabled("FileTool"));
    Check("core GitTool enabled by default", AgentTools.IsEnabled("GitTool"));
    Check("class-A DocumentTool enabled by default", AgentTools.IsEnabled("DocumentTool"));
    Check("class-B OfficeTool disabled by default", !AgentTools.IsEnabled("OfficeTool"));
}

// Core tools are appended to every preset; a config-disabled core is removed from every
// preset uniformly (phase 2 asserts the drop, phase 1 the inclusion).
var email = AgentTools.Resolve("email-agent");
Check("email-agent resolves", email.Contains("EMailTool"));
if (withToolsJson)
{
    Check("email-agent drops FileTool when disabled in tools.json", !email.Contains("FileTool"));
    Check("email-agent keeps GitTool", email.Contains("GitTool"));
    var fallback = AgentTools.Resolve("no-such-agent");
    Check("default-agent also drops FileTool when disabled", !fallback.Contains("FileTool"));
    Check("default-agent keeps GitTool", fallback.Contains("GitTool"));
}
else
{
    Check("email-agent gains core FileTool", email.Contains("FileTool"));
    Check("email-agent gains core GitTool", email.Contains("GitTool"));
}

// Dynamic all-files: every loaded, enabled tool (plugins are not loaded in this harness).
var allFiles = AgentTools.Resolve("all-files");
if (!withToolsJson)
    Check("all-files includes core FileTool", allFiles.Contains("FileTool"));
Check("all-files includes core GitTool", allFiles.Contains("GitTool"));
Check("all-files includes WebTool", allFiles.Contains("WebTool"));
Check("all-files includes EMailTool", allFiles.Contains("EMailTool"));

if (withToolsJson)
{
    Check("tools.json enables class-B OfficeTool", AgentTools.IsEnabled("OfficeTool"));
    Check("tools.json disables core FileTool", !AgentTools.IsEnabled("FileTool"));
    Check("all-files drops FileTool when tools.json disables it", !allFiles.Contains("FileTool"));
}

// Unknown ids fall back to default-agent (with core).
var unknown = AgentTools.Resolve("no-such-agent");
Check("unknown id falls back to default-agent", unknown.Contains("WebTool"));
if (!withToolsJson)
    Check("fallback still includes core FileTool", unknown.Contains("FileTool"));

// ── Lean-orchestrator split (docs-dev/ARCHITECTURE.md, "Lean orchestrator") ──
// The split is about tools that are LOADED: a name that resolves to nothing is no tool at all,
// so it must not trigger a delegation. This harness loads no plugin assembly, so it registers a
// probe tool of its own — a type outside the AIOrchestrator assembly, exactly like a real
// Tools/ plugin — to exercise the rules.
Console.WriteLine("\nLean-orchestrator split:");

// Pin the switch for the whole block, then restore it: the assertions describe the default
// (split on), and a shell that exported the variable must not change this suite's result.
var savedSplit = Environment.GetEnvironmentVariable(AgentTools.SplitEnvVar);
Environment.SetEnvironmentVariable(AgentTools.SplitEnvVar, null);
Check("split is ON by default", AgentTools.SplitEnabled);

McpToolRegistry.Register(typeof(ProbePluginTool));

var allNative = AgentTools.SplitForOrchestration(new[] { "FileTool", "WebTool", "GitTool" });
Check("all-native set: orchestrator keeps every tool",
    allNative.OrchestratorTools.SequenceEqual(new[] { "FileTool", "WebTool", "GitTool" }));
Check("all-native set: no subagent (nothing to delegate)", allNative.SubagentTools.Length == 0);

var unresolvedOnly = AgentTools.SplitForOrchestration(new[] { "FileTool", "NoSuchTool" });
Check("an unresolved name is not a plugin: no split", unresolvedOnly.SubagentTools.Length == 0);
Check("an unresolved name stays with the orchestrator", unresolvedOnly.OrchestratorTools.Contains("NoSuchTool"));

var mixed = AgentTools.SplitForOrchestration(new[] { "FileTool", "ProbePluginTool", "WebTool", "GitTool" });
Check("mixed set: orchestrator keeps only system tools",
    mixed.OrchestratorTools.SequenceEqual(new[] { "FileTool", "WebTool", "GitTool" }));
Check("mixed set: the loaded plugin leaves the orchestrator", !mixed.OrchestratorTools.Contains("ProbePluginTool"));
Check("mixed set: subagent gets the WHOLE set (self-sufficient)", mixed.SubagentTools.Length == 4);
Check("mixed set: subagent keeps the system tools too", mixed.SubagentTools.Contains("FileTool"));

var pluginOnly = AgentTools.SplitForOrchestration(new[] { "ProbePluginTool" });
Check("plugin-only set: not split (the client's list is the client's list)", pluginOnly.SubagentTools.Length == 0);
Check("plugin-only set: orchestrator keeps the requested tool",
    pluginOnly.OrchestratorTools.SequenceEqual(new[] { "ProbePluginTool" }));

// DocumentTool is NOT loaded in this harness, so the preset must stay flat here; with a loaded
// plugin present the same shape splits, which the probe assertions above pin.
var presets = AgentTools.Resolve("document-files");
var presetSplit = AgentTools.SplitForOrchestration(presets);
Check("preset naming an unloaded plugin: stays flat", presetSplit.SubagentTools.Length == 0);
Check("preset naming an unloaded plugin: orchestrator keeps the resolved set",
    presetSplit.OrchestratorTools.SequenceEqual(presets));

// Kill-switch: an off-looking value in any spelling = flat single agent, no change at all.
foreach (var off in new[] { "0", "false", "OFF", " no " })
{
    Environment.SetEnvironmentVariable(AgentTools.SplitEnvVar, off);
    Check($"kill-switch '{off}': split reports disabled", !AgentTools.SplitEnabled);
    var flat = AgentTools.SplitForOrchestration(new[] { "FileTool", "ProbePluginTool" });
    Check($"kill-switch '{off}': orchestrator keeps the full set", flat.OrchestratorTools.Length == 2);
    Check($"kill-switch '{off}': no subagent", flat.SubagentTools.Length == 0);
}
Environment.SetEnvironmentVariable(AgentTools.SplitEnvVar, savedSplit);
Check("the switch is restored to what this run inherited", AgentTools.SplitEnabled == DefaultSplitState(savedSplit));

static bool DefaultSplitState(string? value) =>
    string.IsNullOrWhiteSpace(value) || value.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

if (withToolsJson) File.Delete(toolsJsonPath);
Console.WriteLine(failures == 0 ? "\nALL OK" : $"\n{failures} FAILURES");
Environment.Exit(failures == 0 ? 0 : 1);

/// <summary>A tool type that lives in THIS assembly, so it is not a native system tool. It plays
/// the part of a plugin loaded from the Tools/ folder for the split assertions above, which is
/// what lets them run without loading a real plugin assembly.</summary>
public sealed class ProbePluginTool : AIOrchestrator.API.BaseAgentTool
{
    /// <summary>Returns "pong" — the probe only needs to exist and be callable.</summary>
    /// <returns>pong</returns>
    public static string Ping() => "pong";
}
