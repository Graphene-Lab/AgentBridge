// ═══════════════════════════════════════════════════════════════════════
//  PuppetDocs — real-user end-to-end test: AgentBridge (DEBUG, puppet mode)
//  + OfficeSupportTool plugin.
//
//  Scenario: a company employee uses the AgentBridge TUI — driven through the
//  puppet TCP surface on localhost:5291, injecting exactly the keys/text a
//  human would type — to create TWO office documents from the OfficeSupportTool
//  templates (invoice, employment contract). Each chat message carries the
//  document material as an attached file (/files add), so the agent can fill
//  the template; if the material is insufficient, OfficeSupportTool rejects the
//  request with the deterministic "Document fields:" list and the test reports
//  it (the run can then be repeated with a richer context file).
//
//  Steps:
//    1. (self-provision) OfficeSupportTool plugin into the agent Tools\ folder
//       and assets\templates if missing (builds the sibling repo once).
//    2. Redirect the sandbox (Setup.DocumentsPath) to a fresh %TEMP% workspace
//       via PersistentData\rag_settings.json (backed up/restored afterwards).
//    3. Launch agent.exe (Debug) in its own console window: --enable-log
//       --SkipIndexingOnStartup true --no-update --tui.
//    4. Wait for the puppet listener (5291) and the TUI session ("ctx 0/").
//    5. /agent → enable the OfficeSupportTool tool in the checklist (real UI).
//    6. Per document: /files add <context file> → prompt → Enter → wait for
//       "TUI chat finished" in logs/<pid>.txt → verify the .docx (path from
//       "OfficeSupportTool.CreateDocument: wrote '<path>'" + content check).
//    7. Print the created .docx paths, write %TEMP%\puppetdocs_results.txt.
//
//  Usage:
//    dotnet run --project e2e\PuppetDocs [--agent-exe <path>] [--keep]
//  Exit code 0 = both documents created and verified.
//  Requires the DeepSeekBridge provider (127.0.0.1:8787) or another configured
//  LLM provider to be reachable — the agent needs an LLM for the material gate
//  and the HTML generation.
// ═══════════════════════════════════════════════════════════════════════
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    private const int PuppetPort = 5291;
    private const string ToolToEnable = "OfficeSupportTool";

    // Scenario: the two documents the "company" creates (edit freely between runs).
    private static readonly (string Label, string CtxFile, string Prompt, string ExpectInDocx)[] Documents =
    {
        (
            "1 - INVOICE",
            "context-invoice.md",
            "Create an invoice for our company ACME S.p.A. using the data in the attached file 'context-invoice.md' " +
            "(the file contains all the data: seller, customer, line items, payment details). " +
            "Write the ENTIRE document in ENGLISH — all headings, labels and legal text must be in English " +
            "(company and person names stay as-is). Save the document as 'invoice-INV-2026-0417.docx'.",
            "INV-2026-0417"
        ),
        (
            "2 - EMPLOYMENT CONTRACT",
            "context-contract.md",
            "Create an employment contract for the new employee Giulia Verdi using the data in the attached " +
            "file 'context-contract.md' (the file contains all the data: employer, employee, position, " +
            "remuneration, leave, clauses). Write the ENTIRE document in ENGLISH — all headings, labels and " +
            "legal text must be in English (company and person names stay as-is). " +
            "Save the document as 'employment-contract-Giulia-Verdi.docx'.",
            "Giulia Verdi"
        ),
    };

    private static readonly Regex AnsiRe = new(
        "\x1b\\[[0-9;?]*[ -/]*[@-~]|\x1b\\][^\\x07\\x1b]*(\\x07|\x1b\\\\)|\x1b[()][A-Za-z0-9]|\x1b[=>]",
        RegexOptions.Compiled);

    private static int _failures;
    private static readonly string ResultsFile = Path.Combine(Path.GetTempPath(), "puppetdocs_results.txt");

    private static async Task<int> Main(string[] args)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")); // → AgentBridge/
        var agentExe = Path.GetFullPath(args.Length > 0 && !args[0].StartsWith('-') ? args[0]
            : Path.Combine(repoRoot, "bin", "Debug", "net10.0", "agent.exe"));
        var agentBin = Path.GetDirectoryName(agentExe)!;
        var keep = args.Contains("--keep");
        var workspace = Path.Combine(Path.GetTempPath(), "AgentBridgePuppetDocs", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        // The sandbox must EXIST before launch: Setup reads the persisted DocumentsPath and
        // falls back to MyDocuments when the folder is missing (it only creates folders via
        // TrySetDocumentsPath, which the read path never calls).
        Directory.CreateDirectory(workspace);

        File.WriteAllText(ResultsFile, $"RUN {DateTime.Now:HH:mm:ss} — PuppetDocs (AgentBridge + OfficeSupportTool)\n");
        WriteResult("STARTED");
        Console.WriteLine("══════════ PuppetDocs — real-user AgentBridge + OfficeSupportTool test ══════════");
        Console.WriteLine($"agent exe : {agentExe}");
        Console.WriteLine($"workspace: {workspace}");
        if (!File.Exists(agentExe)) { Fail("agent-exe", $"agent.exe not found at {agentExe} — build AgentBridge Debug first"); return 1; }

        // Pre-flight: the puppet listener needs a free port and the LLM must be reachable.
        if (IsPortBusy(PuppetPort)) { Fail("preflight", $"port {PuppetPort} busy — another agent instance is running"); return 1; }
        if (!await HttpOkAsync("http://127.0.0.1:8787/v1/models", 3))
            Console.WriteLine("  [warn] DeepSeekBridge (127.0.0.1:8787) unreachable — the LLM calls will fail; start the bridge first");

        // 1) Provision the OfficeSupportTool plugin (idempotent).
        var toolDir = Path.Combine(agentBin, "Tools", "OfficeSupportTool");
        if (!File.Exists(Path.Combine(toolDir, "OfficeSupportTool.dll")))
        {
            Console.WriteLine("  provisioning OfficeSupportTool plugin...");
            if (!await ProvisionPluginAsync(repoRoot, agentBin)) return 1;
        }
        var templatesDir = Path.Combine(agentBin, "assets", "templates");
        if (!File.Exists(Path.Combine(templatesDir, "invoice.html")))
            Console.WriteLine("  [warn] assets\\templates missing — OfficeSupportTool will auto-generate templates (slower)");
        Console.WriteLine($"  plugin  : {Path.Combine(toolDir, "OfficeSupportTool.dll")} ({(File.Exists(Path.Combine(toolDir, "OfficeSupportTool.dll")) ? "ok" : "MISSING")})");

        // 2) Redirect the sandbox to a fresh %TEMP% workspace (restored on exit).
        var persistentDir = Path.Combine(agentBin, "PersistentData");
        var settingsFile = Path.Combine(persistentDir, "rag_settings.json");
        var hadSettings = File.Exists(settingsFile);
        if (hadSettings) File.Copy(settingsFile, settingsFile + ".bak", true);
        Directory.CreateDirectory(persistentDir);
        File.WriteAllText(settingsFile, JsonSerializer.Serialize(new { DocumentsPath = workspace }));

        // 3) Launch the agent in its own console window (the TUI renders like a real user).
        Process? proc = null;
        try
        {
            proc = LaunchAgent(agentExe, agentBin);
            Console.WriteLine($"agent pid : {proc.Id}");

            // 4) Wait for the puppet listener + the HTTP server + the session state.
            if (!await WaitForAsync(() => CanConnect(PuppetPort), TimeSpan.FromSeconds(90)))
            { Fail("launch", "puppet listener (5291) never came up — DEBUG build? another instance?"); Dump(agentExe); return 1; }
            var logFile = Path.Combine(agentBin, "logs", $"{proc.Id}.txt");
            if (!await WaitForAsync(() => File.Exists(logFile), TimeSpan.FromSeconds(30)))
            { Fail("launch", $"log file not found: {logFile}"); Dump(agentExe); return 1; }
            Console.WriteLine($"log file : {logFile}");
            if (!await WaitForAsync(() => Capture().Contains("ctx 0/"), TimeSpan.FromSeconds(90)))
            { Fail("launch", "TUI session not ready (no 'ctx 0/' in the status bar)"); Dump(agentExe); return 1; }

            // 5) Enable OfficeSupportTool via the /agent tools checklist.
            if (!await EnableToolAsync(logFile)) return 1;
            if (args.Contains("--smoke"))
            {
                Console.WriteLine("\n  SMOKE OK — tool enabled, agent running. Skipping the LLM documents.");
                WriteResult("DONE PASS (smoke)");
                return 0;
            }

            // 6) Create the two documents.
            var created = new List<string>();
            for (var i = 0; i < Documents.Length; i++)
            {
                var doc = Documents[i];
                Console.WriteLine($"\n=== DOCUMENT {doc.Label} ===");
                var ctxPath = Path.Combine(AppContext.BaseDirectory, "context", doc.CtxFile);
                if (!File.Exists(ctxPath)) { Fail(doc.Label, $"context file not found: {ctxPath}"); return 1; }

                // Detach the previous document's attachment (real user behaviour), then upload+attach the new one.
                if (i > 0)
                {
                    Text($"/attach {Documents[i - 1].CtxFile}");
                    await Task.Delay(600);
                    Key("enter");
                    await Task.Delay(1500);
                }
                Text($"/files add \"{ctxPath}\"");
                await Task.Delay(600);
                Key("enter");
                if (!await WaitForAsync(async () => await FilesListContainsAsync(doc.CtxFile), TimeSpan.FromSeconds(30)))
                { Fail(doc.Label, $"upload of {doc.CtxFile} not confirmed via /v1/files"); Dump(agentExe); return 1; }
                Console.WriteLine($"  uploaded + attached: {doc.CtxFile}");

                var mark = LogMark(logFile);
                Text(doc.Prompt);
                await Task.Delay(600);
                Key("enter");
                Console.WriteLine($"  prompt submitted, waiting for the agent (this takes minutes: material gate + HTML + DOCX)...");

                if (!await WaitForAsync(() => LogSince(logFile, mark).Contains("TUI chat finished"), TimeSpan.FromMinutes(20), 3000))
                { Fail(doc.Label, "timeout waiting for 'TUI chat finished'"); Dump(agentExe); return 1; }

                var since = LogSince(logFile, mark);
                var wrote = Regex.Match(since, @"OfficeSupportTool\.CreateDocument: wrote '([^']+)'");
                string? docx = wrote.Success ? wrote.Groups[1].Value : null;
                if (docx == null)   // fallback: newest .docx in the workspace documents folder
                {
                    var docsDir = Path.Combine(workspace, "documents");
                    if (Directory.Exists(docsDir))
                        docx = Directory.GetFiles(docsDir, "*.docx").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
                }
                if (docx == null || !File.Exists(docx))
                {
                    Fail(doc.Label, "no .docx created by the agent (check the log tail below)");
                    ReportToolOutcome(since);
                    Dump(agentExe);
                    return 1;
                }
                var text = DocxText(docx);
                if (text == null || !text.Contains(doc.ExpectInDocx, StringComparison.OrdinalIgnoreCase))
                {
                    Fail(doc.Label, $"docx created but the expected text '{doc.ExpectInDocx}' is missing");
                    Dump(agentExe);
                    return 1;
                }
                ReportToolOutcome(since);
                created.Add(Path.GetFullPath(docx));
                Console.WriteLine($"  ✓ {doc.Label}: {Path.GetFullPath(docx)}");
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "  ALL DOCUMENTS CREATED AND VERIFIED" : $"  {_failures} FAILURES");
            WriteResult(_failures == 0 ? "DONE PASS" : $"DONE FAIL ({_failures})");
            if (_failures == 0)
            {
                Console.WriteLine("\nDOCX saved to:");
                foreach (var p in created)
                {
                    Console.WriteLine($"  {p}");
                    WriteResult("DOCX: " + p);
                }
            }
            return _failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Fail("main", $"CRASH {ex.GetType().Name}: {ex.Message}");
            WriteResult($"DONE FAIL (crash {ex.GetType().Name})");
            return 1;
        }
        finally
        {
            // 7) Cleanup: graceful /exit, restore the sandbox setting.
            if (!keep && proc is { HasExited: false })
            {
                try { Text("/exit"); await Task.Delay(500); Key("enter"); await WaitForAsync(() => proc.HasExited, TimeSpan.FromSeconds(8)); }
                catch { }
                if (!proc.HasExited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
            }
            try
            {
                if (hadSettings && File.Exists(settingsFile + ".bak")) File.Copy(settingsFile + ".bak", settingsFile, true);
                else if (!hadSettings) File.Delete(settingsFile);
            }
            catch { }
        }
    }

    // ── Agent launch ───────────────────────────────────────────────────

    private static Process LaunchAgent(string exe, string workDir)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            UseShellExecute = true,          // own console window: Terminal.Gui renders (like a real user)
        };
        psi.ArgumentList.Add("--enable-log");
        psi.ArgumentList.Add("--SkipIndexingOnStartup");
        psi.ArgumentList.Add("true");
        psi.ArgumentList.Add("--no-update");
        psi.ArgumentList.Add("--tui");
        return Process.Start(psi)!;
    }

    // ── Puppet TCP protocol (localhost:5291, DEBUG builds only) ─────────

    private static string Puppet(string json)
    {
        using var client = new TcpClient("127.0.0.1", PuppetPort);
        client.ReceiveTimeout = 30000;
        using var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(json);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
        client.Client.Shutdown(SocketShutdown.Send);   // EOF: the server executes and responds
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Capture() => AnsiRe.Replace(Puppet("{\"type\":\"capture\"}"), "");

    private static void Key(string name) => Puppet($"{{\"type\":\"key\",\"key\":\"{name}\"}}");

    private static void Text(string text) => Puppet(JsonSerializer.Serialize(new { type = "text", text }));

    // ── /agent tools checklist: enable OfficeSupportTool (real UI) ──────

    private static async Task<bool> EnableToolAsync(string logFile)
    {
        Text("/agent");
        await Task.Delay(700);
        Key("enter");
        if (!await WaitForAsync(() => Capture().Contains(ToolToEnable), TimeSpan.FromSeconds(20)))
        {
            Fail("enable-tool", $"/agent dialog did not list '{ToolToEnable}' — plugin not loaded?");
            return false;
        }
        await Task.Delay(800);
        var capture = Capture();
        var names = ToolRows(capture);
        var idx = names.FindIndex(n => n.Equals(ToolToEnable, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            var diag = Path.Combine(Path.GetTempPath(), "puppetdocs_diag");
            Directory.CreateDirectory(diag);
            var file = Path.Combine(diag, "tools-dialog-" + DateTime.Now.ToString("HHmmss") + ".txt");
            File.WriteAllText(file, capture);
            Console.WriteLine($"  [diag] tools dialog dump → {file}");
            foreach (var line in capture.Split('\n').Where(l => l.Contains(ToolToEnable, StringComparison.OrdinalIgnoreCase)).Take(5))
                Console.WriteLine("    row: " + line);
            Fail("enable-tool", $"'{ToolToEnable}' not found in the tool checklist ({string.Join(", ", names)})");
            return false;
        }
        Console.WriteLine($"  tool checklist: {string.Join(", ", names)} (enabling '{ToolToEnable}' at row {idx})");

        // Navigate to the row (Up×N clamps at the top → deterministic), Space to toggle,
        // and VERIFY the ☑ glyph in the capture before closing (the checklist keeps the
        // marks of the current preset: File/Web/Git are already checked).
        var toggled = false;
        for (var attempt = 0; attempt < 3 && !toggled; attempt++)
        {
            for (var i = 0; i < 12; i++) { Key("cursorup"); await Task.Delay(100); }
            for (var i = 0; i < idx; i++) { Key("cursordown"); await Task.Delay(150); }
            Key("space");
            await Task.Delay(600);
            var row = Capture().Split('\n').FirstOrDefault(l => l.Contains(ToolToEnable, StringComparison.OrdinalIgnoreCase));
            toggled = row != null && row.Contains('☑');
            if (!toggled) Console.WriteLine($"  [retry] OfficeSupportTool row not checked after Space (attempt {attempt + 1})");
        }
        if (!toggled)
        {
            Fail("enable-tool", $"'{ToolToEnable}' could not be checked in the checklist");
            return false;
        }
        Key("escape");                      // close: the checklist state is saved on ANY close path

        // The applied line logs SHORT names ("File, Web, Git, OfficeSupport").
        var mark = LogMark(logFile);
        if (!await WaitForAsync(() => LogSince(logFile, mark).Contains("TUI /agent tools applied"), TimeSpan.FromSeconds(20)))
        {
            Fail("enable-tool", "the /agent dialog did not confirm the tool (log: 'TUI /agent tools applied: ...')");
            return false;
        }
        var appliedLine = Regex.Match(LogSince(logFile, mark), @"TUI /agent tools applied: ([^\r\n]*)");
        if (!appliedLine.Success || !appliedLine.Groups[1].Value.Contains("OfficeSupport", StringComparison.OrdinalIgnoreCase))
        {
            Fail("enable-tool", $"applied tool list does not include OfficeSupport: '{(appliedLine.Success ? appliedLine.Groups[1].Value : "?")}'");
            return false;
        }
        Console.WriteLine($"  ✓ OfficeSupportTool enabled — applied: {appliedLine.Groups[1].Value}");
        return true;
    }

    /// <summary>Ordered tool names from the /agent checklist capture: each row is
    /// "┃ ☐ &lt;Name&gt; — &lt;description&gt;" (alphabetical, from AgentTools.Catalog), so the
    /// first "CamelCase — " token per line is the tool name.</summary>
    private static List<string> ToolRows(string capture) =>
        capture.Split('\n')
            .Select(line => Regex.Match(line, @"([A-Za-z][A-Za-z0-9_]*)\s*—"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Where(n => char.IsUpper(n[0]))
            .ToList();

    // ── Log tail (logs/<pid>.txt) ───────────────────────────────────────

    private static long LogMark(string logFile) => File.Exists(logFile) ? new FileInfo(logFile).Length : 0;

    private static string LogSince(string logFile, long mark)
    {
        if (!File.Exists(logFile)) return "";
        using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(Math.Min(mark, fs.Length), SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    // ── DOCX verification ───────────────────────────────────────────────

    /// <summary>Concatenated text of a DOCX (all &lt;w:t&gt; runs of word/document.xml).</summary>
    private static string? DocxText(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml");
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var xml = reader.ReadToEnd();
            return string.Concat(Regex.Matches(xml, "<w:t[^>]*>(.*?)</w:t>", RegexOptions.Singleline)
                .Select(m => m.Groups[1].Value));
        }
        catch { return null; }
    }

    // ── Diagnostics / reporting ─────────────────────────────────────────

    private static void ReportToolOutcome(string logSince)
    {
        var m = Regex.Match(logSince, @"OfficeSupportTool\.CreateDocument: type='([^']*)'");
        if (m.Success) Console.WriteLine($"  OfficeSupportTool.CreateDocument: type='{m.Groups[1].Value}'");
        var err = Regex.Match(logSince, @"Error: the context does not provide all the information needed[^\n]*");
        if (err.Success) Console.WriteLine($"  [material gate] {err.Value}");
    }

    private static void Dump(string agentExe)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "puppetdocs_diag");
            Directory.CreateDirectory(dir);
            var cap = Path.Combine(dir, "screen-" + DateTime.Now.ToString("HHmmss") + ".txt");
            File.WriteAllText(cap, Capture());
            Console.WriteLine($"  [diag] last screen dumped to {cap}");
            var logDir = Path.Combine(Path.GetDirectoryName(agentExe)!, "logs");
            var log = Directory.Exists(logDir)
                ? Directory.GetFiles(logDir, "*.txt").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                : null;
            if (log != null)
            {
                var tail = File.ReadLines(log).TakeLast(80);
                Console.WriteLine($"  [diag] log tail ({log}):");
                foreach (var line in tail) Console.WriteLine("    " + line);
            }
        }
        catch (Exception ex) { Console.WriteLine($"  [diag] dump failed: {ex.Message}"); }
    }

    private static void Fail(string id, string problem) { _failures++; Console.WriteLine($"  ✗ {id} FAIL: {problem}"); WriteResult($"{id} FAIL: {problem}"); }
    private static void WriteResult(string line) => File.AppendAllText(ResultsFile, line + Environment.NewLine);

    // ── Small helpers ───────────────────────────────────────────────────

    private static bool IsPortBusy(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Any(e => e.Port == port);

    private static bool CanConnect(int port)
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", port); return true; }
        catch { return false; }
    }

    private static async Task<bool> HttpOkAsync(string url, int timeoutSeconds)
    {
        try
        {
            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            using var resp = await hc.GetAsync(url);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<bool> FilesListContainsAsync(string fileName)
    {
        try
        {
            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var resp = await hc.GetAsync("http://localhost:5290/v1/files");
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("data", out var data)) return false;
            foreach (var f in data.EnumerateArray())
                if (f.TryGetProperty("filename", out var n) && n.GetString() == fileName) return true;
            return false;
        }
        catch { return false; }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, int pollMs = 500)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try { if (condition()) return true; } catch { }
            await Task.Delay(pollMs);
        }
        return false;
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, int pollMs = 500)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try { if (await condition()) return true; } catch { }
            await Task.Delay(pollMs);
        }
        return false;
    }

    // ── Plugin provisioning (self-contained; idempotent) ────────────────

    private static async Task<bool> ProvisionPluginAsync(string repoRoot, string agentBin)
    {
        var ostRepo = Path.GetFullPath(Path.Combine(repoRoot, "..", "OfficeSupportTool"));
        var ostCsproj = Path.Combine(ostRepo, "OfficeSupportTool.csproj");
        if (!File.Exists(ostCsproj)) { Fail("provision", $"OfficeSupportTool repo not found at {ostCsproj}"); return false; }

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = ostRepo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(ostCsproj);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("minimal");
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(proc.WaitForExitAsync(), stdout, stderr);
        if (proc.ExitCode != 0)
        {
            Fail("provision", $"OfficeSupportTool build failed (exit {proc.ExitCode}):\n{stderr.Result}");
            return false;
        }
        var ostBin = Path.Combine(ostRepo, "bin", "Debug", "net10.0");
        var toolDir = Path.Combine(agentBin, "Tools", "OfficeSupportTool");
        Directory.CreateDirectory(toolDir);
        foreach (var f in new[] { "OfficeSupportTool.dll", "OfficeSupportTool.pdb", "OfficeSupportTool.xml" })
            File.Copy(Path.Combine(ostBin, f), Path.Combine(toolDir, f), true);
        var nugetHtml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "htmltoopenxml.dll", "3.5.0", "lib", "net8.0", "HtmlToOpenXml.dll");
        if (File.Exists(nugetHtml)) File.Copy(nugetHtml, Path.Combine(toolDir, "HtmlToOpenXml.dll"), true);
        // HtmlToOpenXml's transitive deps (not shipped by the AgentBridge build): AngleSharp
        // and Microsoft.Extensions.Logging.Abstractions. Look them up in the NuGet cache.
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        foreach (var (name, version) in new[] { ("anglesharp", "1.5.0"), ("microsoft.extensions.logging.abstractions", "10.0.0") })
        {
            var lib = Path.Combine(packages, name, version, "lib");
            var dll = Directory.Exists(lib)
                ? Directory.GetFiles(lib, (name == "anglesharp" ? "AngleSharp" : "Microsoft.Extensions.Logging.Abstractions") + ".dll", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.Contains("net10.0", StringComparison.OrdinalIgnoreCase) ? 2
                        : f.Contains("netstandard2.0", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .FirstOrDefault()
                : null;
            if (dll != null) File.Copy(dll, Path.Combine(toolDir, Path.GetFileName(dll)), true);
        }
        var templatesDir = Path.Combine(agentBin, "assets", "templates");
        Directory.CreateDirectory(templatesDir);
        foreach (var f in Directory.GetFiles(Path.Combine(ostBin, "assets", "templates"), "*.html"))
            File.Copy(f, Path.Combine(templatesDir, Path.GetFileName(f)), true);
        foreach (var f in new[] { "design-guidelines.md", "essential-guidelines.md" })
        {
            var src = Path.Combine(ostBin, "assets", f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(agentBin, "assets", f), true);
        }
        Console.WriteLine("  provisioned OfficeSupportTool plugin + templates");
        return true;
    }
}
