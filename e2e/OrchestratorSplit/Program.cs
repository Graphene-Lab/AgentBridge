// OrchestratorSplit — A/B harness for the lean-orchestrator split (see AgentTools.cs and
// AIOrchestrator/docs-dev/ARCHITECTURE.md, "Lean orchestrator").
//
// Launches a headless AgentBridge server TWICE against the SAME agent set — once with the split
// ON (the default) and once with AGENTBRIDGE_ORCHESTRATOR_SPLIT=0 — runs the same chat turn on
// both, and compares the tool-catalog size the server logs on every turn
// ("ExecuteAction: first LLM call, toolCatalog=N chars"). That number is the per-turn prompt
// prefix the agent carries: the exact bytes a provider's prompt cache keys on, so a smaller
// number for the same visible capability is the whole point of the split. With --delegate it
// also probes the other half of the contract: that plugin work still happens, through a
// subagent (log: "launching subagent with types: ..." + "ContinueSubagentSession: session
// 'sub_N' completed").
//
// Usage:
//   dotnet run --project e2e/OrchestratorSplit [--exe <agent.exe>] [--urls http://localhost:5293]
//       [--provider DeepSeekBridge] [--model spreadsheet-files] [--delegate] [--no-launch]
//       [--prompt "..."]
//
// Exit code 1 when the split did not shrink the catalog for a set that contains plugin tools.

using System.Diagnostics;
using System.Text;
using System.Text.Json;

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
var prompt = Arg("--prompt") ?? "Answer with the single word: READY. Do not call any tool.";
var delegatePrompt = "Call launch_subagent once with prompt set to: Reply with the single word OK. "
    + "Then report the subagent's answer.";

Console.WriteLine($"OrchestratorSplit — A/B of the lean-orchestrator split");
Console.WriteLine($"  exe      {exe}");
Console.WriteLine($"  provider {provider}   model {model}");
Console.WriteLine();

if (!File.Exists(exe))
{
    Console.Error.WriteLine($"agent executable not found: {exe}");
    return 2;
}

var passes = new[]
{
    (Name: "split ON", SplitEnv: (string?)null),
    (Name: "split OFF", SplitEnv: "0"),
};
var measured = new Dictionary<string, int>();
var delegated = new Dictionary<string, bool>();
var replies = new Dictionary<string, string>();

foreach (var (passName, splitEnv) in passes)
{
    // One port per pass: the previous server is gone, but a lingering socket would collide.
    var urls = baseUrl.Replace($":{basePort}", $":{basePort + (splitEnv == null ? 0 : 1)}");
    Console.WriteLine($"===== pass: {passName} ({(splitEnv == null ? "AGENTBRIDGE_ORCHESTRATOR_SPLIT unset ⇒ on" : $"AGENTBRIDGE_ORCHESTRATOR_SPLIT={splitEnv}")}) — {urls}");

    Process? server = null;
    var pid = -1;
    try
    {
        if (!noLaunch)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--headless --no-update --enable-log --SkipIndexingOnStartup true --LLM:Provider {provider} --Urls {urls}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (splitEnv != null) psi.Environment["AGENTBRIDGE_ORCHESTRATOR_SPLIT"] = splitEnv;
            server = Process.Start(psi);
            pid = server!.Id;
            server.OutputDataReceived += (_, _) => { };
            server.ErrorDataReceived += (_, _) => { };
            server.BeginOutputReadLine();
            server.BeginErrorReadLine();

            if (!await WaitAsync(() => GetHealthAsync(urls), 90_000))
            {
                Console.Error.WriteLine("  FAILED: server never became ready");
                return 1;
            }
            Console.WriteLine($"  server ready (pid {pid})");
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var sessionId = (await PostJsonAsync(client, $"{urls}/v1/control", """{"create": true}"""))
            ?.RootElement.GetProperty("session_id").GetString();
        if (sessionId == null) { Console.Error.WriteLine("  FAILED: no session"); return 1; }

        replies[passName] = await RunTurnAsync(client, urls, sessionId, model, provider, prompt, "turn 1");
        if (probeDelegation)
            await RunTurnAsync(client, urls, sessionId, model, provider, delegatePrompt, "turn 2 (delegation probe)");
    }
    finally
    {
        if (server != null)
        {
            try { server.Kill(entireProcessTree: true); server.WaitForExit(15_000); } catch { }
            server.Dispose();
        }
    }

    if (pid < 0) continue;

    var logLines = ReadLogLines(Path.GetDirectoryName(exe), pid);
    if (logLines.Count == 0) { Console.Error.WriteLine($"  FAILED: no log for pid {pid}"); return 1; }

    var catalogs = logLines
        .Select(l => CatalogChars(l))
        .Where(n => n > 0)
        .ToArray();
    if (catalogs.Length == 0) { Console.Error.WriteLine("  FAILED: no toolCatalog line in the log"); return 1; }
    measured[passName] = catalogs[0];
    delegated[passName] = logLines.Any(l => l.Contains("launching subagent with types"));

    foreach (var line in logLines.Where(l => l.Contains("first LLM call, toolCatalog=")
        || l.Contains("launching subagent with types")
        || l.Contains("ContinueSubagentSession: session")
        || l.Contains("ContinueSubagentSession: paused")
        || l.Contains("unknown tool class")))
        Console.WriteLine("  log | " + line.Trim());

    Console.WriteLine($"  → orchestrator tool catalog: {measured[passName]} chars"
        + $"{string.Join("", catalogs.Skip(1).Select(c => $" (+{c} on a later turn)"))}");
    Console.WriteLine();
}

// ── verdict ──
Console.WriteLine("===== verdict");
if (!measured.TryGetValue("split ON", out var on) || !measured.TryGetValue("split OFF", out var off))
{
    Console.Error.WriteLine("incomplete measurement");
    return 1;
}
var delta = off - on;
Console.WriteLine($"  catalog: split ON {on} chars | split OFF {off} chars | delta {delta} chars "
    + $"(~{delta * 100.0 / Math.Max(off, 1):F0}% of the per-turn prefix)");
Console.WriteLine($"  delegation fired: split ON {delegated.GetValueOrDefault("split ON")}, "
    + $"split OFF {delegated.GetValueOrDefault("split OFF")}");
foreach (var (k, v) in replies) Console.WriteLine($"  reply [{k}]: {Truncate(v, 200)}");

if (delta <= 0)
{
    Console.WriteLine("  ✗ FAIL: the split did not shrink the orchestrator's tool catalog for a set with plugin tools");
    if (!delegated.GetValueOrDefault("split ON"))
        Console.WriteLine("    (and no subagent was launched — plugin tools would be unreachable)");
    return 1;
}

Console.WriteLine("  ✓ the orchestrator's per-turn prefix shrank while the plugin tools stayed reachable through the subagent");
return 0;

// ── helpers ───────────────────────────────────────────────────────────

static int CatalogChars(string line)
{
    const string marker = "first LLM call, toolCatalog=";
    var i = line.IndexOf(marker, StringComparison.Ordinal);
    if (i < 0) return 0;
    var rest = line[(i + marker.Length)..];
    var end = rest.IndexOf(' ');
    return int.TryParse(end < 0 ? rest : rest[..end], out var n) ? n : 0;
}

static string Truncate(string s, int max) =>
    s.Length <= max ? s.ReplaceLineEndings(" ") : s[..max].ReplaceLineEndings(" ") + "…";

static List<string> ReadLogLines(string? exeDir, int pid)
{
    if (exeDir == null) return new();
    var path = Path.Combine(exeDir, "logs", $"{pid}.txt");
    return File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
}

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
        Console.WriteLine($"  HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
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
