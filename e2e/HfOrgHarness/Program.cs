// HfOrgHarness — drives a headless AgentBridge server over its OpenAI-compatible API to run a
// guided web task (e.g. creating a Hugging Face organization) while recording everything the
// agent says and does (SSE transcript) plus the server log, for diagnosis.
//
// Usage:
//   dotnet run --project e2e/HfOrgHarness [--exe <agent.exe>] [--urls http://localhost:5291]
//       [--prompt <file>] [--session <id>] [--provider Gemini] [--no-launch] [--interactive]
//
// The agent's browser (WebTool) opens visible on the user's screen; when the task needs a
// login the user completes it in that browser and the harness continues with follow-up turns.

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
var urls = Arg("--urls") ?? "http://localhost:5291";
var provider = Arg("--provider") ?? "Gemini";
var sessionArg = Arg("--session");
var promptArg = Arg("--prompt");
var noLaunch = argsList.Contains("--no-launch");
var interactive = argsList.Contains("--interactive");

var runsDir = Path.Combine(AppContext.BaseDirectory, "runs");
Directory.CreateDirectory(runsDir);
var runDir = Path.Combine(runsDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(runDir);
var transcriptPath = Path.Combine(runDir, "transcript.txt");
var serverOutPath = Path.Combine(runDir, "server-out.txt");
using var transcript = new StreamWriter(transcriptPath, append: false, Encoding.UTF8) { AutoFlush = true };

// ── prompt ────────────────────────────────────────────────────────────
string prompt;
if (promptArg != null)
{
    if (!File.Exists(promptArg)) { Console.Error.WriteLine($"prompt file not found: {promptArg}"); return 2; }
    prompt = File.ReadAllText(promptArg);
}
else
{
    var inline = argsList.FirstOrDefault(a => !a.StartsWith("--"));
    if (inline == null) { Console.Error.WriteLine("no prompt given (--prompt <file> or inline text)"); return 2; }
    prompt = inline;
}

// ── server lifecycle ──────────────────────────────────────────────────
Process? server = null;
try
{
    if (!noLaunch)
    {
        server = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--headless --no-update --enable-log --SkipIndexingOnStartup true --LLM:Provider {provider} --Urls {urls}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        server!.OutputDataReceived += (_, e) => { if (e.Data != null) File.AppendAllText(serverOutPath, e.Data + "\n"); };
        server.ErrorDataReceived += (_, e) => { if (e.Data != null) File.AppendAllText(serverOutPath, e.Data + "\n"); };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        Console.WriteLine($"[harness] agent launched (pid {server.Id}) — {exe}");

        if (!await WaitForAsync(() => GetHealthAsync(urls), 60_000))
        {
            Console.WriteLine("[harness] FAILED: server never became ready; server output:");
            Console.WriteLine(File.Exists(serverOutPath) ? File.ReadAllText(serverOutPath) : "(none)");
            return 1;
        }
        Console.WriteLine("[harness] server ready — /health OK");
    }

    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

    // session
    var sessionId = sessionArg;
    if (sessionId == null)
    {
        var create = await PostJsonAsync(client, $"{urls}/v1/control", """{"create": true}""");
        sessionId = create?.RootElement.GetProperty("session_id").GetString();
        Console.WriteLine($"[harness] session created: {sessionId}");
    }
    if (sessionId == null) { Console.Error.WriteLine("failed to create session"); return 1; }

    // main turn
    var turn = 1;
    await RunTurnAsync(client, urls, sessionId, prompt, turn++, transcript);

    // follow-up loop
    if (interactive)
    {
        Console.WriteLine("\n[harness] interactive mode — type a follow-up (blank line = end):");
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) break;
            await RunTurnAsync(client, urls, sessionId, line, turn++, transcript);
        }
    }

    Console.WriteLine("\n[harness] done. Transcript: " + transcriptPath);
    DumpLogTail(Path.GetDirectoryName(exe));
    return 0;
}
finally
{
    transcript.Dispose();
    if (server != null)
    {
        try { server.Kill(entireProcessTree: true); } catch { }
        server.Dispose();
    }
}

// ── helpers ───────────────────────────────────────────────────────────

string FindAgentExe()
{
    var here = Path.GetDirectoryName(Environment.ProcessPath);
    for (var d = here; d != null; d = Path.GetDirectoryName(d))
    {
        var candidate = Path.Combine(d, "publish", "agent.exe");
        if (File.Exists(candidate)) return candidate;
        candidate = Path.Combine(d, "agent.exe");
        if (File.Exists(candidate)) return candidate;
    }
    return "agent.exe";
}

async Task<bool> GetHealthAsync(string urls)
{
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var resp = await client.GetAsync($"{urls}/health");
        return resp.IsSuccessStatusCode;
    }
    catch { return false; }
}

async Task<bool> WaitForAsync(Func<Task<bool>> probe, int timeoutMs)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        if (await probe()) return true;
        await Task.Delay(500);
    }
    return false;
}

async Task<JsonDocument?> PostJsonAsync(HttpClient client, string url, string body)
{
    using var resp = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
    if (!resp.IsSuccessStatusCode) return null;
    var text = await resp.Content.ReadAsStringAsync();
    return JsonDocument.Parse(text);
}

async Task RunTurnAsync(HttpClient client, string urls, string sessionId, string userMessage, int turn, StreamWriter transcript)
{
    Console.WriteLine($"\n===== TURN {turn} =====");
    var payload = JsonSerializer.Serialize(new
    {
        model = "web-agent",
        messages = new[] { new { role = "user", content = userMessage } },
        session_id = sessionId,
        llm_provider = "Gemini",
        stream = true,
    });
    using var req = new HttpRequestMessage(HttpMethod.Post, $"{urls}/v1/chat/completions")
    { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    if (!resp.IsSuccessStatusCode)
    {
        Console.WriteLine($"[harness] HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        return;
    }

    using var stream = await resp.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);
    while (true)
    {
        var line = await reader.ReadLineAsync();
        if (line == null) break;
        transcript.WriteLine(line);
        if (!line.StartsWith("data:")) continue;
        var payloadText = line["data:".Length..].Trim();
        if (payloadText.Length == 0 || payloadText == "[DONE]") continue;
        RenderChunk(payloadText);
    }
}

void RenderChunk(string payload)
{
    try
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
        {
            Console.WriteLine($"[error] {err.GetRawText()}");
            return;
        }
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in choices.EnumerateArray())
            {
                var delta = c.TryGetProperty("delta", out var d) ? d : c.TryGetProperty("message", out var m) ? m : default;
                if (delta.ValueKind != JsonValueKind.Object) continue;
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text)) Console.Write(text);
                }
                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        if (!tc.TryGetProperty("function", out var fn)) continue;
                        var name = fn.TryGetProperty("name", out var n) ? n.GetString() : "(unnamed)";
                        var args = fn.TryGetProperty("arguments", out var a) ? a.GetString() : "";
                        Console.WriteLine($"\n[tool] {name} {args}");
                    }
                }
                if (c.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String && fr.GetString() != null)
                    Console.WriteLine($"\n[finish] {fr.GetString()}");
            }
            return;
        }
        Console.WriteLine($"[data] {payload}");
    }
    catch (JsonException)
    {
        Console.WriteLine($"[data] {payload}");
    }
}

void DumpLogTail(string? exeDir)
{
    if (exeDir == null) return;
    var logDir = Path.Combine(exeDir, "logs");
    var log = Directory.Exists(logDir)
        ? Directory.GetFiles(logDir, "*.txt").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
        : null;
    if (log == null) return;
    Console.WriteLine($"\n--- server log tail ({log}) ---");
    var lines = File.ReadAllLines(log);
    foreach (var l in lines.TakeLast(60)) Console.WriteLine(l);
}
