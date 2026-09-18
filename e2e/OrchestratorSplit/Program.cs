// OrchestratorSplit — A/B harness for the lean-orchestrator split (see AgentTools.cs and
// AIOrchestrator/docs-dev/ARCHITECTURE.md, "Lean orchestrator").
//
// Launches a headless AgentBridge server TWICE against the SAME agent set — once with the split
// ON and once with it OFF (both set explicitly, never inherited) — runs the same chat turn on
// both, and reports three independent measurements:
//   • the tool-catalog size the server logs on every turn ("first LLM call, toolCatalog=N chars")
//     — the per-turn prompt prefix, i.e. the bytes a provider's prompt cache keys on;
//   • the provider's own token counts, read back from the log lines the engine writes for every
//     response ("SendQuery usage (<provider>): prompt=… completion=… total=… cached=…") — so the
//     saving is measured in tokens, not only in characters;
//   • the delegation evidence ("launching subagent with types: …" + "ContinueSubagentSession:
//     session 'sub_N' completed"), which is what proves the plugin tools stayed reachable.
//
// Usage:
//   dotnet run --project e2e/OrchestratorSplit [--exe <agent.exe>] [--urls http://localhost:5293]
//       [--provider DeepSeekBridge] [--model spreadsheet-files] [--delegate] [--no-launch]
//       [--pid <pid>] [--prompt "..."] [--runs <dir>]
//
// --no-launch drives an ALREADY RUNNING server instead of starting one: pass --pid so the harness
// can find that server's log, otherwise there is nothing to measure.
//
// Exit codes: 0 = the measurement was produced and the contract held; 1 = a contract violation
// (or a failure, with the child's own output printed); 2 = bad usage.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Must match AgentTools.SplitEnvVar — this project does not reference the AgentBridge assembly
// (it only launches agent.exe), so the name is repeated here in exactly one place.
const string SplitEnvVar = "AGENTBRIDGE_ORCHESTRATOR_SPLIT";

var argsList = Environment.GetCommandLineArgs().Skip(1).ToArray();
string? Arg(string name)
{
    var i = Array.IndexOf(argsList, name);
    return i >= 0 && i + 1 < argsList.Length ? argsList[i + 1] : null;
}

var exe = Arg("--exe") ?? FindAgentExe();
var baseUrl = Arg("--urls") ?? "http://localhost:5293";
var basePort = int.Parse(baseUrl.Split(':').Last().TrimEnd('/'));
var provider = Arg("--provider") ?? "DeepSeekBridge";
var model = Arg("--model") ?? "spreadsheet-files";
var probeDelegation = argsList.Contains("--delegate");
var noLaunch = argsList.Contains("--no-launch");
var runningPid = Arg("--pid") is { } pidText && int.TryParse(pidText, out var p) ? p : (int?)null;
var prompt = Arg("--prompt") ?? "Answer with the single word: READY. Do not call any tool.";
var delegatePrompt = "Call launch_subagent once with prompt set to: Reply with the single word OK. "
    + "Then report the subagent's answer.";
var runsRoot = Arg("--runs") ?? Path.Combine(AppContext.BaseDirectory, "runs");
var runDir = Path.Combine(runsRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(runDir);

Console.WriteLine("OrchestratorSplit — A/B of the lean-orchestrator split");
Console.WriteLine($"  exe      {exe}");
Console.WriteLine($"  provider {provider}   model {model}");
Console.WriteLine($"  run dir  {runDir}");
Console.WriteLine();

if (!noLaunch && !File.Exists(exe))
{
    Console.Error.WriteLine($"agent executable not found: {exe}");
    return 2;
}
if (noLaunch && runningPid == null)
{
    Console.Error.WriteLine("--no-launch needs --pid <pid>: without it there is no server log to measure.");
    return 2;
}
if (noLaunch && Arg("--exe") == null)
{
    Console.Error.WriteLine("--no-launch needs --exe <agent.exe>: the server's log lives next to that executable.");
    return 2;
}

var usageRegex = new Regex(
    @"usage \((?<provider>[^)]*)\): prompt=(?<p>\d+) completion=(?<c>\d+) total=(?<t>\d+)(?: cached=(?<k>\d+))?",
    RegexOptions.Compiled);

var passes = new[]
{
    (Name: "split ON", SplitEnv: "1", Port: basePort),
    (Name: "split OFF", SplitEnv: "0", Port: basePort + 1),
};
var results = new List<PassResult>();

foreach (var (passName, splitEnv, passPort) in passes)
{
    var urls = noLaunch ? baseUrl : baseUrl.Replace($":{basePort}", $":{passPort}");
    Console.WriteLine($"===== pass: {passName} ({SplitEnvVar}={splitEnv}) — {urls}");

    Process? server = null;
    var pid = runningPid ?? -1;
    long logOffset = 0;
    var outPath = Path.Combine(runDir, $"{passName.Replace(' ', '-')}-server.out");
    var errPath = Path.Combine(runDir, $"{passName.Replace(' ', '-')}-server.err");
    try
    {
        if (!noLaunch)
        {
            // A server already answering on this port would be measured as if it were ours.
            if (await GetHealthAsync(urls))
            {
                Console.Error.WriteLine($"  FAILED: something already answers on {urls} — nothing was launched, so no measurement is attributable.");
                return 1;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = Quoted("--headless", "--no-update", "--enable-log", "--SkipIndexingOnStartup", "true",
                    "--LLM:Provider", provider, "--Urls", urls),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Explicit in both passes: an inherited value must not turn the ON pass into an OFF pass.
            psi.Environment[SplitEnvVar] = splitEnv;

            server = Process.Start(psi);
            pid = server!.Id;
            // The launched process's own complaint is the first thing a failure needs: keep it.
            var outWriter = new StreamWriter(outPath, append: false) { AutoFlush = true };
            var errWriter = new StreamWriter(errPath, append: false) { AutoFlush = true };
            server.OutputDataReceived += (_, e) => { if (e.Data != null) outWriter.WriteLine(e.Data); };
            server.ErrorDataReceived += (_, e) => { if (e.Data != null) errWriter.WriteLine(e.Data); };
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();

            if (!await WaitAsync(() => GetHealthAsync(urls), 120_000))
            {
                Console.Error.WriteLine($"  FAILED: server never became ready. Its own output:");
                Console.Error.WriteLine(Tail(errPath, 25));
                Console.Error.WriteLine(Tail(outPath, 25));
                return 1;
            }
            Console.WriteLine($"  server ready (pid {pid})");
        }
        else
        {
            Console.WriteLine($"  driving the server already running as pid {pid}");
        }

        // Only lines appended after this point belong to this pass: a recycled pid leaves an
        // older run's lines at the top of its log file.
        logOffset = LogLineCount(Path.GetDirectoryName(exe), pid);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var sessionId = (await PostJsonAsync(client, $"{urls}/v1/control", """{"create": true}"""))
            ?.RootElement.GetProperty("session_id").GetString();
        if (sessionId == null) { Console.Error.WriteLine("  FAILED: no session"); return 1; }

        var reply = await RunTurnAsync(client, urls, sessionId, model, provider, prompt, "turn 1");
        if (probeDelegation)
            await RunTurnAsync(client, urls, sessionId, model, provider, delegatePrompt, "turn 2 (delegation probe)");

        results.Add(new PassResult(passName, reply, logOffset, Path.GetDirectoryName(exe), pid));
    }
    finally
    {
        if (server != null)
        {
            try { server.Kill(entireProcessTree: true); server.WaitForExit(15_000); } catch { }
            server.Dispose();
        }
    }

    var measured = results[^1];
    var logLines = ReadLogLines(measured.LogDir, measured.Pid, measured.LogOffset);
    if (logLines.Count == 0)
    {
        Console.Error.WriteLine($"  FAILED: no log lines for pid {pid} (looked in {measured.LogDir}\\logs\\{pid}.txt)");
        if (noLaunch) Console.Error.WriteLine("  (with --no-launch the pid must be the SERVER's pid)");
        return 1;
    }

    measured.Catalogs.AddRange(logLines.Select(CatalogChars).Where(n => n > 0));
    measured.OrchestratorPrompts.AddRange(OrchestratorTurnPrompts(logLines));
    measured.Usage.AddRange(logLines.Select(ParseUsage).Where(u => u != null).Select(u => u!));
    measured.Delegated = logLines.Any(l => l.Contains("launching subagent with types"));
    measured.SubagentCompleted = logLines.Any(l => l.Contains("ContinueSubagentSession: session") && l.Contains("completed"));

    foreach (var line in logLines.Where(l => l.Contains("first LLM call, toolCatalog=")
        || l.Contains("launching subagent with types")
        || l.Contains("ContinueSubagentSession: session")
        || l.Contains("usage (")
        || l.Contains("unknown tool class")
        || l.Contains("API key is not set")))
        Console.WriteLine("  log | " + line.Trim());

    if (measured.Catalogs.Count == 0)
    {
        Console.Error.WriteLine("  FAILED: no toolCatalog line in this pass's log");
        Console.Error.WriteLine(Tail(errPath, 20));
        return 1;
    }

    Console.WriteLine($"  → tool catalog per turn: {string.Join(", ", measured.Catalogs)} chars");
    Console.WriteLine($"  → provider tokens: {measured.UsageSummary()}");
    Console.WriteLine();
}

// ── verdict ──
Console.WriteLine("===== verdict");
PassResult on = results[0], off = results[1];
Console.WriteLine($"  catalog   : split ON {on.FirstCatalog} chars | split OFF {off.FirstCatalog} chars | delta {off.FirstCatalog - on.FirstCatalog}");
Console.WriteLine($"  tokens    : orchestrator turn-opening prompts ON [{string.Join(", ", on.OrchestratorPrompts)}]"
    + $" = {on.TotalOrchestratorPrompt()} | OFF [{string.Join(", ", off.OrchestratorPrompts)}] = {off.TotalOrchestratorPrompt()}");
Console.WriteLine($"  all calls : ON {on.TotalPrompt()} prompt ({on.TotalCached()} cached), OFF {off.TotalPrompt()} prompt ({off.TotalCached()} cached)"
    + " — the difference includes the delegated work the flat run never does");
Console.WriteLine($"  delegation: ON {on.Delegated} (subagent completed: {on.SubagentCompleted}) | OFF {off.Delegated}");
Console.WriteLine($"  reply ON  : {Truncate(on.Reply, 160)}");
Console.WriteLine($"  reply OFF : {Truncate(off.Reply, 160)}");

var violated = false;

// The shrink claim only applies to a set that actually carries a loaded plugin tool: if both
// passes measure the same catalog, the split correctly declined to act (a native-only set, or a
// preset naming tools this install does not ship) — that is a pass, not a failure.
if (on.FirstCatalog == off.FirstCatalog)
    Console.WriteLine("  ✔ no loaded plugin tool in this set: the split correctly declined (no delegation offered)");
else if (on.FirstCatalog > off.FirstCatalog)
{
    Console.Error.WriteLine("  ✗ FAIL: the split did NOT shrink the orchestrator's tool catalog");
    violated = true;
}
else
    Console.WriteLine($"  ✔ the orchestrator's per-turn prefix shrank while the plugin tools stayed reachable");

if (on.TotalOrchestratorPrompt() >= off.TotalOrchestratorPrompt() && on.TotalOrchestratorPrompt() > 0)
{
    Console.Error.WriteLine("  ✗ FAIL: the orchestrator's own turn-opening calls spent no fewer prompt tokens"
        + " with the split on (the per-turn prefix is what the split is for)");
    violated = true;
}
if (probeDelegation && !on.Delegated)
{
    Console.Error.WriteLine("  ✗ FAIL: --delegate was requested and the split-on pass never delegated —"
        + " the plugin tools were unreachable, which is the half of the contract that matters");
    violated = true;
}
if (probeDelegation && on.Delegated && !on.SubagentCompleted)
{
    Console.Error.WriteLine("  ✗ FAIL: a subagent was launched but never reported completion");
    violated = true;
}

Console.WriteLine(violated ? "  → the measurement is above; the contract did not hold" : "  → contract held");
return violated ? 1 : 0;

// ── helpers ───────────────────────────────────────────────────────────

/// <summary>Quotes the parts of a command line that need it, so a value carrying whitespace or a
/// leading dash cannot become a second argument or a flag of the launched process.</summary>
static string Quoted(params string[] parts) =>
    string.Join(" ", parts.Select(p => p.Length > 0 && (p.Contains(' ') || p.StartsWith('-'))
        ? "\"" + p.Replace("\"", "\\\"") + "\"" : p));

static int CatalogChars(string line)
{
    const string marker = "first LLM call, toolCatalog=";
    var i = line.IndexOf(marker, StringComparison.Ordinal);
    if (i < 0) return 0;
    var rest = line[(i + marker.Length)..];
    var end = rest.IndexOf(' ');
    return int.TryParse(end < 0 ? rest : rest[..end], out var n) ? n : 0;
}

/// <summary>The token count of each turn's OPENING call — the number the split is meant to shrink.
/// Read deterministically from the log: every turn logs its catalog line ("first LLM call,
/// toolCatalog=…") and the very next usage line reports that call. Every other usage line in the
/// log belongs to a later iteration of the same turn or to a subagent, and counting those would
/// charge the split for the delegated work it exists to enable.</summary>
List<int> OrchestratorTurnPrompts(List<string> logLines)
{
    var result = new List<int>();
    for (var i = 0; i < logLines.Count; i++)
    {
        if (!logLines[i].Contains("first LLM call, toolCatalog=")) continue;
        for (var j = i + 1; j < logLines.Count; j++)
        {
            var usage = ParseUsage(logLines[j]);
            if (usage == null) continue;
            result.Add(usage.Prompt);
            break;
        }
    }
    return result;
}

/// <summary>Reads the token counts the engine logged for one response — the provider's own
/// numbers, which is what makes this A/B a token measurement and not only a byte measurement.</summary>
Usage? ParseUsage(string line)
{
    var m = usageRegex.Match(line);
    if (!m.Success) return null;
    return new Usage(
        m.Groups["provider"].Value,
        int.Parse(m.Groups["p"].Value), int.Parse(m.Groups["c"].Value),
        int.Parse(m.Groups["t"].Value),
        m.Groups["k"].Success ? int.Parse(m.Groups["k"].Value) : 0);
}

static int LogLineCount(string? exeDir, int pid)
{
    var path = LogPath(exeDir, pid);
    return path != null && File.Exists(path) ? File.ReadAllLines(path).Length : 0;
}

static List<string> ReadLogLines(string? exeDir, int pid, long skipLines)
{
    var path = LogPath(exeDir, pid);
    if (path == null || !File.Exists(path)) return new List<string>();
    var lines = File.ReadAllLines(path);
    return lines.Skip((int)Math.Min(skipLines, lines.Length)).ToList();
}

static string? LogPath(string? exeDir, int pid) =>
    exeDir == null ? null : Path.Combine(exeDir, "logs", $"{pid}.txt");

static string Tail(string path, int lines)
{
    if (!File.Exists(path)) return $"    ({path} — not written)";
    var all = File.ReadAllLines(path);
    return all.Length == 0 ? "    (empty)" : string.Join(Environment.NewLine, all.TakeLast(lines).Select(l => "    " + l));
}

static string Truncate(string s, int max) =>
    s.Length <= max ? s.ReplaceLineEndings(" ") : s[..max].ReplaceLineEndings(" ") + "…";

static string FindAgentExe()
{
    for (var d = Path.GetDirectoryName(Environment.ProcessPath); d != null; d = Path.GetDirectoryName(d))
    {
        foreach (var candidate in new[]
        {
            Path.Combine(d, "publish", "agent.exe"),
            Path.Combine(d, "agent.exe"),
        })
            if (File.Exists(candidate)) return candidate;
    }
    return "agent.exe";
}

static async Task<bool> GetHealthAsync(string urls)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var resp = await client.GetAsync($"{urls}/health");
        return resp.IsSuccessStatusCode;
    }
    catch { return false; }
}

static async Task<bool> WaitAsync(Func<Task<bool>> probe, int timeoutMs)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        if (await probe()) return true;
        await Task.Delay(500);
    }
    return false;
}

static async Task<JsonDocument?> PostJsonAsync(HttpClient client, string url, string body)
{
    using var resp = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
    if (!resp.IsSuccessStatusCode) return null;
    return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
}

static async Task<string> RunTurnAsync(HttpClient client, string urls, string sessionId, string model,
    string provider, string userMessage, string label)
{
    Console.WriteLine($"  ── {label}: \"{Truncate(userMessage, 90)}\"");
    var payload = JsonSerializer.Serialize(new
    {
        model,
        messages = new[] { new { role = "user", content = userMessage } },
        session_id = sessionId,
        llm_provider = provider,
        stream = true,
    });
    using var req = new HttpRequestMessage(HttpMethod.Post, $"{urls}/v1/chat/completions")
    { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    if (!resp.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"  HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        return "";
    }

    var text = new StringBuilder();
    using var stream = await resp.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);
    while (await reader.ReadLineAsync() is { } line)
    {
        if (!line.StartsWith("data:")) continue;
        var body = line["data:".Length..].Trim();
        if (body.Length == 0 || body == "[DONE]") continue;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) continue;
            foreach (var c in choices.EnumerateArray())
            {
                if (!c.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object) continue;
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    text.Append(content.GetString());
                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    foreach (var tc in tcs.EnumerateArray())
                        if (tc.TryGetProperty("function", out var fn) && fn.TryGetProperty("name", out var n))
                            Console.WriteLine($"  tool call: {n.GetString()}");
            }
        }
        catch (JsonException) { }
    }

    var reply = text.ToString();
    Console.WriteLine($"  ← {(reply.Length == 0 ? "(empty)" : Truncate(reply, 160))}");
    return reply;
}

/// <summary>One provider response's token counts, as the engine reported them.</summary>
internal sealed record Usage(string Provider, int Prompt, int Completion, int Total, int Cached);

/// <summary>Everything one pass measured.</summary>
internal sealed class PassResult
{
    public PassResult(string name, string reply, long logOffset, string? logDir, int pid)
    {
        Name = name; Reply = reply; LogOffset = logOffset; LogDir = logDir; Pid = pid;
    }

    public string Name { get; }
    public string Reply { get; }
    public long LogOffset { get; }
    public string? LogDir { get; }
    public int Pid { get; }
    public List<int> Catalogs { get; } = new();
    public List<Usage> Usage { get; } = new();

    /// <summary>One entry per turn: the prompt tokens of that turn's opening call (see
    /// OrchestratorTurnPrompts) — the orchestrator's own cost, the one the split targets.</summary>
    public List<int> OrchestratorPrompts { get; } = new();
    public bool Delegated { get; set; }
    public bool SubagentCompleted { get; set; }

    public int FirstCatalog => Catalogs.Count > 0 ? Catalogs[0] : 0;
    public Usage? FirstUsage => Usage.Count > 0 ? Usage[0] : null;
    public int TotalPrompt() => Usage.Sum(u => u.Prompt);
    public int TotalCached() => Usage.Sum(u => u.Cached);
    public int TotalOrchestratorPrompt() => OrchestratorPrompts.Sum();

    public string UsageSummary() => Usage.Count == 0
        ? "none reported by the provider"
        : string.Join(" | ", Usage.Select(u => $"{u.Prompt}+{u.Completion}" + (u.Cached > 0 ? $" ({u.Cached} cached)" : "")));
}
