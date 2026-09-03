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

if (withToolsJson) File.Delete(toolsJsonPath);
Console.WriteLine(failures == 0 ? "\nALL OK" : $"\n{failures} FAILURES");
Environment.Exit(failures == 0 ? 0 : 1);
