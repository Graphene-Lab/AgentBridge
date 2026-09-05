// ═══════════════════════════════════════════════════════════════════════
//  PuppetSheet — real-user end-to-end test: AgentBridge (DEBUG, puppet mode)
//  + SpreadsheetTool plugin.
//
//  Scenario: a company employee uses the AgentBridge TUI — driven through the
//  puppet TCP surface on localhost:5292, injecting exactly the keys/text a
//  human would type — to ask the agent to create an Excel workbook: ONE
//  worksheet with a SMALL dataset + ONE chart, everything in ENGLISH, fitting
//  a single A4 page (the file is meant to be converted to PNG and shown in
//  the AgentBridge README as a demonstration).
//
//  Steps:
//    1. (self-provision) SpreadsheetTool plugin into the agent Tools\ folder
//       if missing (builds the sibling repo once).
//    2. Redirect the sandbox (Setup.DocumentsPath) to a fresh %TEMP% workspace
//       via PersistentData\rag_settings.json (backed up/restored afterwards).
//    3. Launch agent.exe (Debug) in its own console window: --enable-log
//       --SkipIndexingOnStartup true --no-update --tui.
//    4. Wait for the puppet listener (5292) and the TUI session ("ctx 0/").
//    5. /agent → enable the SpreadsheetTool tool in the checklist (real UI,
//       idempotent: toggled only when it is not already checked).
//    6. Send the prompt (ENGLISH, explicitly asking for an ENGLISH worksheet,
//       a small dataset + one chart on a single A4 page) → Enter → wait for
//       "TUI chat finished" in logs/<pid>.txt.
//    7. Locate the .xlsx: the last "SpreadsheetTool.(Save|SaveAs|Dispose):
//       saved to '<path>'" in the log (fallback: newest *.xlsx in the
//       workspace) and VERIFY it structurally: zip + XML parts well-formed,
//       data cells present, chart parts with series, page setup A4 reported.
//    8. Print the created .xlsx path, write %TEMP%\puppetsheet_results.txt.
//
//  Usage:
//    dotnet run --project e2e\PuppetSheet [--agent-exe <path>] [--keep] [--smoke]
//  Exit code 0 = the workbook was created and passed the structural checks.
//  Requires the DeepSeekBridge provider (127.0.0.1:8787) or another configured
//  LLM provider to be reachable — the agent needs an LLM to drive the tool.
// ═══════════════════════════════════════════════════════════════════════
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

internal static class Program
{
    private const int PuppetPort = 5292;
    private const string ToolToEnable = "SpreadsheetTool";

    // The single scenario: a normal user asks the agent (in English) for a
    // compact, single-A4-page English worksheet with data + one chart.
    private const string Prompt =
        "Create a new Excel workbook (.xlsx file) with ONE worksheet. The worksheet must contain a SMALL " +
        "professional dataset that fits on a single A4 page when printed, plus ONE chart that visualizes the " +
        "data, placed on the same worksheet. EVERYTHING must be written in ENGLISH: the worksheet name, all " +
        "column headers, all data values and the chart title — nothing in any other language. Use realistic " +
        "sample data, for example the monthly Revenue and Costs of a small coffee shop for the last 6 months " +
        "(about 7 data rows total including a Total row). Make it look professional: a bold header row, " +
        "reasonable column widths, number formats for money values. Add the chart (a column or bar chart " +
        "comparing the two series) on the same worksheet, next to or below the data table. Then set the page " +
        "setup so the sheet prints on exactly ONE A4 page: paper size A4, landscape orientation, fit to 1 page " +
        "wide and 1 page tall, and a print area covering the data and the chart. " +
        "Save the workbook as 'coffee-shop-monthly-A4-demo.xlsx' in the workspace and confirm the file path when done.";

    private static readonly Regex AnsiRe = new(
        "\x1b\\[[0-9;?]*[ -/]*[@-~]|\x1b\\][^\\x07\\x1b]*(\\x07|\x1b\\\\)|\x1b[()][A-Za-z0-9]|\x1b[=>]",
        RegexOptions.Compiled);

    private static int _failures;
    private static readonly string ResultsFile = Path.Combine(Path.GetTempPath(), "puppetsheet_results.txt");

    private static async Task<int> Main(string[] args)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")); // → AgentBridge/
        var agentExe = Path.GetFullPath(args.Length > 0 && !args[0].StartsWith('-') ? args[0]
            : Path.Combine(repoRoot, "bin", "Debug", "net10.0", "agent.exe"));
        var agentBin = Path.GetDirectoryName(agentExe)!;
        var keep = args.Contains("--keep");
        var workspace = Path.Combine(Path.GetTempPath(), "AgentBridgePuppetSheet", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        // The sandbox must EXIST before launch: Setup reads the persisted DocumentsPath and
        // falls back to MyDocuments when the folder is missing (it only creates folders via
        // TrySetDocumentsPath, which the read path never calls).
        Directory.CreateDirectory(workspace);

        File.WriteAllText(ResultsFile, $"RUN {DateTime.Now:HH:mm:ss} — PuppetSheet (AgentBridge + SpreadsheetTool)\n");
        WriteResult("STARTED");
        Console.WriteLine("══════════ PuppetSheet — real-user AgentBridge + SpreadsheetTool test ══════════");
        Console.WriteLine($"agent exe : {agentExe}");
        Console.WriteLine($"workspace: {workspace}");
        if (!File.Exists(agentExe)) { Fail("agent-exe", $"agent.exe not found at {agentExe} — build AgentBridge Debug first"); return 1; }

        // Pre-flight: the puppet listener needs a free port and the LLM must be reachable.
        if (IsPortBusy(PuppetPort)) { Fail("preflight", $"port {PuppetPort} busy — another agent instance is running"); return 1; }
        if (!await HttpOkAsync("http://127.0.0.1:8787/v1/models", 3))
            Console.WriteLine("  [warn] DeepSeekBridge (127.0.0.1:8787) unreachable — the LLM calls will fail; start the bridge first");

        // 1) Provision the SpreadsheetTool plugin (idempotent).
        var toolDir = Path.Combine(agentBin, "Tools", "SpreadsheetTool");
        if (!File.Exists(Path.Combine(toolDir, "SpreadsheetTool.dll")))
        {
            Console.WriteLine("  provisioning SpreadsheetTool plugin...");
            if (!await ProvisionPluginAsync(repoRoot, agentBin)) return 1;
        }
        Console.WriteLine($"  plugin  : {Path.Combine(toolDir, "SpreadsheetTool.dll")} ({(File.Exists(Path.Combine(toolDir, "SpreadsheetTool.dll")) ? "ok" : "MISSING")})");

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
            { Fail("launch", "puppet listener (5292) never came up — DEBUG build? another instance?"); Dump(agentExe); return 1; }
            var logFile = Path.Combine(agentBin, "logs", $"{proc.Id}.txt");
            if (!await WaitForAsync(() => File.Exists(logFile), TimeSpan.FromSeconds(30)))
            { Fail("launch", $"log file not found: {logFile}"); Dump(agentExe); return 1; }
            Console.WriteLine($"log file : {logFile}");
            if (!await WaitForAsync(() => Capture().Contains("ctx 0/"), TimeSpan.FromSeconds(90)))
            { Fail("launch", "TUI session not ready (no 'ctx 0/' in the status bar)"); Dump(agentExe); return 1; }

            // 5) Enable SpreadsheetTool via the /agent tools checklist.
            if (!await EnableToolAsync(logFile)) return 1;
            if (args.Contains("--smoke"))
            {
                Console.WriteLine("\n  SMOKE OK — tool enabled, agent running. Skipping the LLM chat.");
                WriteResult("DONE PASS (smoke)");
                return 0;
            }

            // 6) Send the scenario prompt (no attachments needed — it is self-contained).
            Console.WriteLine("\n=== SPREADSHEET SCENARIO ===");
            var mark = LogMark(logFile);
            Text(Prompt);
            await Task.Delay(600);
            Key("enter");
            Console.WriteLine("  prompt submitted, waiting for the agent (this takes minutes: LLM + tool calls)...");

            if (!await WaitForAsync(() => LogSince(logFile, mark).Contains("TUI chat finished"), TimeSpan.FromMinutes(20), 3000))
            { Fail("scenario", "timeout waiting for 'TUI chat finished'"); Dump(agentExe); return 1; }

            // 7) Locate the .xlsx: the LAST save the tool logged (Save/SaveAs/Dispose auto-save),
            //    falling back to the newest .xlsx under the workspace.
            var since = LogSince(logFile, mark);
            ReportToolCalls(since);
            var xlsx = FindCreatedWorkbook(since, workspace);
            if (xlsx == null || !File.Exists(xlsx))
            {
                Fail("scenario", "no .xlsx created by the agent (check the log tail below)");
                Dump(agentExe);
                return 1;
            }

            // 8) Structural verification (parse XML well-formedness, not regex counts).
            var checks = VerifyWorkbook(xlsx);
            foreach (var (label, ok, detail) in checks)
            {
                Console.WriteLine($"  {(ok ? "✓" : "△")} {label}: {detail}");
                if (!ok) { _failures++; WriteResult($"verify {label}: {detail}"); }
            }
            if (_failures > 0) { Console.WriteLine($"  ✗ {_failures} verification failure(s) — the file exists but is not usable as a README demo"); WriteResult("DONE FAIL (verify)"); return 1; }

            Console.WriteLine();
            Console.WriteLine("  WORKBOOK CREATED AND VERIFIED");
            WriteResult("DONE PASS");
            Console.WriteLine("\nXLSX saved to:");
            Console.WriteLine($"  {Path.GetFullPath(xlsx)}");
            WriteResult("XLSX: " + Path.GetFullPath(xlsx));
            return 0;
        }
        catch (Exception ex)
        {
            Fail("main", $"CRASH {ex.GetType().Name}: {ex.Message}");
            WriteResult($"DONE FAIL (crash {ex.GetType().Name})");
            return 1;
        }
        finally
        {
            // 9) Cleanup: graceful /exit, restore the sandbox setting.
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

    // ── Puppet TCP protocol (localhost:5292, DEBUG builds only) ─────────

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

    // ── /agent tools checklist: enable SpreadsheetTool (real UI) ────────

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
            var diag = Path.Combine(Path.GetTempPath(), "puppetsheet_diag");
            Directory.CreateDirectory(diag);
            var file = Path.Combine(diag, "tools-dialog-" + DateTime.Now.ToString("HHmmss") + ".txt");
            File.WriteAllText(file, capture);
            Console.WriteLine($"  [diag] tools dialog dump → {file}");
            foreach (var line in capture.Split('\n').Where(l => l.Contains(ToolToEnable, StringComparison.OrdinalIgnoreCase)).Take(5))
                Console.WriteLine("    row: " + line);
            Fail("enable-tool", $"'{ToolToEnable}' not found in the tool checklist ({string.Join(", ", names)})");
            return false;
        }
        Console.WriteLine($"  tool checklist: {string.Join(", ", names)} (row {idx})");

        // Navigate to the row (Up×N clamps at the top → deterministic), check the current
        // mark, Space to toggle ONLY if it is not already checked (idempotent), then verify ☑.
        var toggled = false;
        for (var attempt = 0; attempt < 3 && !toggled; attempt++)
        {
            for (var i = 0; i < 12; i++) { Key("cursorup"); await Task.Delay(100); }
            for (var i = 0; i < idx; i++) { Key("cursordown"); await Task.Delay(150); }
            await Task.Delay(400);
            var row = Capture().Split('\n').FirstOrDefault(l => l.Contains(ToolToEnable, StringComparison.OrdinalIgnoreCase));
            if (row != null && row.Contains('☑'))
            {
                toggled = true;   // already enabled — nothing to do
                break;
            }
            Key("space");
            await Task.Delay(600);
            row = Capture().Split('\n').FirstOrDefault(l => l.Contains(ToolToEnable, StringComparison.OrdinalIgnoreCase));
            toggled = row != null && row.Contains('☑');
            if (!toggled) Console.WriteLine($"  [retry] SpreadsheetTool row not checked after Space (attempt {attempt + 1})");
        }
        if (!toggled)
        {
            Fail("enable-tool", $"'{ToolToEnable}' could not be checked in the checklist");
            return false;
        }
        Key("escape");                      // close: the checklist state is saved on ANY close path

        // The applied line logs SHORT names ("File, Web, Git, Spreadsheet").
        var mark = LogMark(logFile);
        if (!await WaitForAsync(() => LogSince(logFile, mark).Contains("TUI /agent tools applied"), TimeSpan.FromSeconds(20)))
        {
            Fail("enable-tool", "the /agent dialog did not confirm the tool (log: 'TUI /agent tools applied: ...')");
            return false;
        }
        var appliedLine = Regex.Match(LogSince(logFile, mark), @"TUI /agent tools applied: ([^\r\n]*)");
        if (!appliedLine.Success || !appliedLine.Groups[1].Value.Contains("Spreadsheet", StringComparison.OrdinalIgnoreCase))
        {
            Fail("enable-tool", $"applied tool list does not include Spreadsheet: '{(appliedLine.Success ? appliedLine.Groups[1].Value : "?")}'");
            return false;
        }
        Console.WriteLine($"  ✓ SpreadsheetTool enabled — applied: {appliedLine.Groups[1].Value}");
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

    // ── Agent-behaviour report: the tool call sequence the LLM drove ────

    private static void ReportToolCalls(string logSince)
    {
        var lines = logSince.Split('\n')
            .Where(l => l.Contains("SpreadsheetTool.", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Trim())
            .ToList();
        Console.WriteLine($"  agent tool calls (log): {lines.Count} SpreadsheetTool line(s)");
        foreach (var line in lines.Take(120)) Console.WriteLine("    " + line);
        var finished = Regex.Match(logSince, @"TUI chat finished: ([^\r\n]*)");
        if (finished.Success) Console.WriteLine("  " + finished.Value);
    }

    // ── Workbook location ───────────────────────────────────────────────

    /// <summary>Last path the tool reported as saved (Save/SaveAs/Dispose auto-save or Create),
    /// else the newest .xlsx under the workspace.</summary>
    private static string? FindCreatedWorkbook(string logSince, string workspace)
    {
        var saves = Regex.Matches(logSince,
            @"SpreadsheetTool\.(?:Save|SaveAs|Dispose): (?:auto-)?saved(?: as)? to '([^']+)'");
        if (saves.Count > 0) return saves[^1].Groups[1].Value;
        var created = Regex.Match(logSince, @"SpreadsheetTool\.Create: created '([^']+)'");
        if (created.Success) return created.Groups[1].Value;
        return Directory.Exists(workspace)
            ? Directory.GetFiles(workspace, "*.xlsx", SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
            : null;
    }

    // ── Workbook structural verification ────────────────────────────────

    private static List<(string Label, bool Ok, string Detail)> VerifyWorkbook(string path)
    {
        var results = new List<(string, bool, string)>();
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.Select(e => e.FullName).ToList();

            var workbook = entries.FirstOrDefault(e => e.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase));
            var sheetXml = entries.Where(e => e.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                                              && e.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e).ToList();
            var chartXml = entries.Where(e => e.StartsWith("xl/charts/", StringComparison.OrdinalIgnoreCase)
                                              && e.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e).ToList();

            if (workbook == null || sheetXml.Count == 0)
            {
                results.Add(("archive", false, $"not a workbook: missing xl/workbook.xml or worksheets ({string.Join(", ", entries.Take(10))})"));
                return results;
            }

            // Well-formedness of every XML part (structural check, per the campaign lesson).
            var malformed = new List<string>();
            foreach (var e in entries.Where(e => e.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                try { using var s = zip.GetEntry(e)!.Open(); var d = new XmlDocument(); d.Load(s); }
                catch (Exception ex) { malformed.Add($"{e} ({ex.Message})"); }
            }
            results.Add(("xml well-formed", malformed.Count == 0,
                malformed.Count == 0 ? $"{entries.Count(e => e.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))} parts OK"
                                     : string.Join("; ", malformed)));

            // Data cells across ALL worksheets (the agent may write to a sheet other than
            // the first one — e.g. after add_worksheet/rename). Page setup is checked on the
            // DATA sheet (the one the agent configured), not necessarily the first.
            var maxCells = 0; var maxValues = 0; var dataSheet = ""; XmlDocument? dataDoc = null;
            foreach (var ws in sheetXml)
            {
                using var s = zip.GetEntry(ws)!.Open();
                var d = new XmlDocument(); d.Load(s);
                var c = d.GetElementsByTagName("c").Count;
                var v = d.GetElementsByTagName("v").Count;
                if (c > maxCells) { maxCells = c; maxValues = v; dataSheet = ws; dataDoc = d; }
            }
            results.Add(("data cells", maxCells >= 10,
                $"{sheetXml.Count} worksheet(s), data in '{dataSheet}': {maxCells} cells, {maxValues} values"));
            if (dataDoc != null)
            {
                var ps = dataDoc.GetElementsByTagName("pageSetup");
                var a4 = ps.Count > 0 && ps[0]!.Attributes?["paperSize"]?.Value == "9";
                var orient = ps.Count > 0 ? ps[0]!.Attributes?["orientation"]?.Value ?? "(default)" : "(none)";
                var fitW = ps.Count > 0 ? ps[0]!.Attributes?["fitToWidth"]?.Value ?? "?" : "?";
                var fitH = ps.Count > 0 ? ps[0]!.Attributes?["fitToHeight"]?.Value ?? "?" : "?";
                results.Add(("page setup A4", a4,
                    a4 ? $"A4 paperSize=9, orient={orient}, fitToWidth={fitW}, fitToHeight={fitH}"
                       : $"not A4 (orient={orient}, fitToWidth={fitW}, fitToHeight={fitH})"));
            }

            // Charts present with series (namespace-aware: chart XML uses the "c:" prefix,
            // GetElementsByTagName("ser") matches nothing there).
            var series = 0;
            foreach (var c in chartXml)
            {
                using var s = zip.GetEntry(c)!.Open();
                var d = new XmlDocument(); d.Load(s);
                series += d.SelectNodes("//*[local-name()='ser']")?.Count ?? 0;
            }
            results.Add(("chart", chartXml.Count > 0 && series > 0,
                chartXml.Count == 0 ? "no chart parts (xl/charts/)" : $"{chartXml.Count} chart part(s), {series} series total"));

            // Sheet name + sample strings (transparency for the ENGLISH requirement).
            using (var s = zip.GetEntry(workbook)!.Open())
            {
                var d = new XmlDocument(); d.Load(s);
                var names = new List<string>();
                foreach (XmlNode n in d.GetElementsByTagName("sheet"))
                    names.Add(n.Attributes?["name"]?.Value ?? "?");
                results.Add(("sheet name(s)", names.Count > 0 && names.All(n => !n.Contains("Foglio", StringComparison.OrdinalIgnoreCase)),
                    string.Join(", ", names)));
            }
            var sample = SampleStrings(zip, sheetXml[0]);
            results.Add(("sample strings", true, sample));
        }
        catch (Exception ex)
        {
            results.Add(("open", false, $"cannot open '{path}': {ex.Message}"));
        }
        return results;
    }

    /// <summary>Up to 40 string values (shared strings + inline) so the user can eyeball the language.</summary>
    private static string SampleStrings(ZipArchive zip, string sheetPath)
    {
        var all = new List<string>();
        var ssEntry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase));
        if (ssEntry != null)
        {
            try
            {
                using var s = ssEntry.Open();
                var d = new XmlDocument(); d.Load(s);
                foreach (XmlNode n in d.GetElementsByTagName("t"))
                    all.Add(n.InnerText);
            }
            catch { }
        }
        try
        {
            using var s = zip.GetEntry(sheetPath)!.Open();
            var d = new XmlDocument(); d.Load(s);
            foreach (XmlNode n in d.GetElementsByTagName("is"))
            {
                var ts = n.SelectNodes(".//*[local-name()='t']");
                if (ts == null) continue;
                foreach (XmlNode t in ts) all.Add(t.InnerText);
            }
        }
        catch { }
        var shown = all.Where(x => x.Trim().Length > 0).Distinct().Take(40).ToList();
        return shown.Count == 0 ? "(no string values)" : "values: " + string.Join(" | ", shown);
    }

    // ── Diagnostics / reporting ─────────────────────────────────────────

    private static void Dump(string agentExe)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "puppetsheet_diag");
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

    // ── Plugin provisioning (self-contained; idempotent) ────────────────

    private static async Task<bool> ProvisionPluginAsync(string repoRoot, string agentBin)
    {
        var pluginRepo = Path.GetFullPath(Path.Combine(repoRoot, "..", "SpreadsheetTool"));
        var pluginCsproj = Path.Combine(pluginRepo, "SpreadsheetTool.csproj");
        if (!File.Exists(pluginCsproj)) { Fail("provision", $"SpreadsheetTool repo not found at {pluginCsproj}"); return false; }

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = pluginRepo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(pluginCsproj);
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
            Fail("provision", $"SpreadsheetTool build failed (exit {proc.ExitCode}):\n{stderr.Result}");
            return false;
        }
        var pluginBin = Path.Combine(pluginRepo, "bin", "Debug", "net10.0");
        var toolDir = Path.Combine(agentBin, "Tools", "SpreadsheetTool");
        Directory.CreateDirectory(toolDir);
        foreach (var f in Directory.GetFiles(pluginBin))
            File.Copy(f, Path.Combine(toolDir, Path.GetFileName(f)), true);
        Console.WriteLine("  provisioned SpreadsheetTool plugin (closure copied from the repo build)");
        return true;
    }
}
