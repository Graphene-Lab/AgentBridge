// SetupPuppet — E2E test for AgentBridge issue #8 through the REAL TUI (/setup dialog),
// driven by the puppet TCP surface (localhost:5292). Verifies that editing a provider's
// API key from the console dialog does NOT silently drop the fields the dialog does not
// expose (ForceTextToolDefinitions, PauseBetweenRequests, CacheType).
//
// Scenario:
//   1. Seed providers.json with DeepSeekBridge (default) + TestGemini carrying
//      ForceTextToolDefinitions=true and PauseBetweenRequests=7s.
//   2. Launch agent.exe (Debug) --tui; wait for the puppet listener + session.
//   3. /setup -> LLM tab -> select TestGemini in the list -> Edit -> change ApiKey -> OK.
//   4. Read providers.json: TestGemini must keep ForceTextToolDefinitions=true and
//      PauseBetweenRequests=00:00:07 (pre-fix these were reset to defaults).
//   5. Cleanup: restore the backed-up providers.json, close the agent.
//
// Usage: dotnet run --project e2e\SetupPuppet [--agent-exe <path>] [--keep]
// Exit 0 = pass.
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    private const int PuppetPort = 5292;
    private static int _failures;
    private static readonly string AgentDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")); // AgentBridge/
    private static readonly Regex AnsiRe = new(
        "\x1b\\[[0-9;?]*[ -/]*[@-~]|\x1b\\][^\\x07\\x1b]*(\\x07|\x1b\\\\)|\x1b[()][A-Za-z0-9]|\x1b[=>]",
        RegexOptions.Compiled);
    private static string StripAnsi(string s) => AnsiRe.Replace(s, "");

    private static void Check(string name, bool cond)
    {
        Console.WriteLine($"  {(cond ? "✓" : "✗ FAIL")} {name}");
        if (!cond) _failures++;
    }

    private static string Puppet(string json)
    {
        using var c = new TcpClient("127.0.0.1", PuppetPort);
        var s = c.GetStream();
        var b = Encoding.UTF8.GetBytes(json);
        s.Write(b, 0, b.Length);
        s.Flush();
        c.Client.Shutdown(SocketShutdown.Send);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
    private static string Capture() => StripAnsi(Puppet("{\"type\":\"capture\"}"));
    private static void Key(string k) => Puppet($"{{\"type\":\"key\",\"key\":\"{k}\"}}");
    private static void Text(string t) => Puppet($"{{\"type\":\"text\",\"text\":\"{JsonEnc(t)}\"}}");
    private static void Mouse(int x, int y) => Puppet($"{{\"type\":\"mouse\",\"x\":{x},\"y\":{y},\"flags\":\"LeftButtonClicked\"}}");
    private static void MouseFlags(string flags, int x, int y) => Puppet($"{{\"type\":\"mouse\",\"x\":{x},\"y\":{y},\"flags\":\"{flags}\"}}");
    private static async Task MouseClickAsync(int x, int y)
    {
        // A real terminal click is press → (some ms later) release. TG v2.4.17 synthesizes
        // the Button click from a press+release pair; the puppet pump ticks every 250 ms, so
        // both events must land in SEPARATE ticks or the button never sees a real click.
        Puppet($"{{\"type\":\"mouse\",\"x\":{x},\"y\":{y},\"flags\":\"LeftButtonPressed\"}}");
        await Task.Delay(500);   // > one pump tick
        Puppet($"{{\"type\":\"mouse\",\"x\":{x},\"y\":{y},\"flags\":\"LeftButtonReleased\"}}");
        await Task.Delay(500);
    }
    private static string HitTest(int x, int y) => Puppet($"{{\"type\":\"hit\",\"x\":{x},\"y\":{y}}}");
    private static string JsonEnc(string s) => JsonSerializer.Serialize(s).Trim('"');

    // Scans a screen row for a button by exact title (hit-test based) and returns the
    // center x of the widest run of hits. Hit-testing uses real screen cells, so this is
    // immune to wide/combining glyphs shifting character indices.
    private static int? FindButtonCenter(int y, string title)
    {
        int? bestStart = null, bestEnd = null;
        int? curStart = null, curEnd = null;
        for (int x = 0; x < 160; x++)
        {
            var hit = HitTest(x, y);
            var onButton = hit.Contains($"Button \"{title}\"");
            if (onButton)
            {
                curStart ??= x;
                curEnd = x;
            }
            else if (curStart != null)
            {
                if (bestStart == null || curEnd - curStart > bestEnd - bestStart)
                { bestStart = curStart; bestEnd = curEnd; }
                curStart = null;
            }
        }
        if (curStart != null && (bestStart == null || curEnd - curStart > bestEnd - bestStart))
        { bestStart = curStart; bestEnd = curEnd; }
        if (bestStart == null) return null;
        Console.WriteLine($"[diag] button '{title}' row {y} cells {bestStart}..{bestEnd} center {(bestStart + bestEnd) / 2}");
        return (bestStart + bestEnd) / 2;
    }

    // The grid capture adds two header rows plus a "NNNN " row-number prefix per line.
    // Strip both so that grid coordinates map 1:1 to puppet mouse x/y (screen cells).
    private static string[] ParseGrid(string gridText)
    {
        var lines = gridText.TrimEnd('\n').Split('\n');
        if (lines.Length <= 2) return Array.Empty<string>();
        return lines.Skip(2).Select(l => l.Length > 5 ? l[5..] : "").ToArray();
    }

    private static bool CanConnect(int port)
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", port); return true; }
        catch { return false; }
    }
    private static bool PortBusy(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    private static async Task<bool> WaitAsync(Func<bool> f, TimeSpan timeout, int ms = 300)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (f()) return true;
            await Task.Delay(ms);
        }
        return false;
    }

    private static async Task<int> Main(string[] args)
    {
        var exe = args.Length > 0 && !args[0].StartsWith('-') ? Path.GetFullPath(args[0])
            : Path.Combine(AgentDir, "bin", "Debug", "net10.0", "agent.exe");
        var agentBin = Path.GetDirectoryName(exe)!;
        var keep = args.Contains("--keep");
        var providersFile = Path.Combine(agentBin, "PersistentData", "providers.json");
        var backup = providersFile + ".testbak";

        Console.WriteLine($"agent exe : {exe}");
        if (!File.Exists(exe)) { Console.WriteLine("FAIL: agent.exe not found — build AgentBridge Debug first"); return 1; }
        if (PortBusy(PuppetPort)) { Console.WriteLine("FAIL: port 5292 busy — stop other agent instances"); return 1; }

        // Seed test providers (only if a backup exists or none is present: never clobber real config silently).
        if (!File.Exists(backup) && File.Exists(providersFile)) File.Copy(providersFile, backup, true);
        File.WriteAllText(providersFile, """
[
  {
    "ProviderName": "DeepSeekBridge",
    "Protocol": "OpenAI",
    "CacheType": "PrefixCache",
    "ModelName": "deepseek-web/deepseek-chat",
    "BaseAddress": "http://127.0.0.1:8787/",
    "EndPoint": "v1/chat/completions",
    "Timeout": "00:05:00",
    "PauseBetweenRequests": "00:00:05",
    "ContextWindow": 1000000,
    "ForceTextToolDefinitions": true,
    "ApiKey": "old-key",
    "IsDefault": true
  },
  {
    "ProviderName": "TestGemini",
    "Protocol": "Gemini",
    "ModelName": "gemini-2.5-flash",
    "BaseAddress": "https://generativelanguage.googleapis.com/",
    "EndPoint": "v1beta/models",
    "ContextWindow": 1000000,
    "ForceTextToolDefinitions": true,
    "PauseBetweenRequests": "00:00:07",
    "ApiKey": ""
  }
]
""");
        Console.WriteLine("seeded test providers.json (DeepSeekBridge + TestGemini)");

        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = agentBin,
                Arguments = "--enable-log --SkipIndexingOnStartup true --no-update --tui",
                UseShellExecute = true,   // own console window: Terminal.Gui renders
            });
            Console.WriteLine($"agent pid : {proc!.Id}");
            if (!await WaitAsync(() => CanConnect(PuppetPort), TimeSpan.FromSeconds(90)))
            { Console.WriteLine("FAIL: puppet listener never came up"); return 1; }
            if (!await WaitAsync(() => Capture().Contains("ctx 0/"), TimeSpan.FromSeconds(90)))
            { Console.WriteLine("FAIL: TUI session not ready"); return 1; }
            Console.WriteLine("agent + TUI session ready\n");

            // Open the setup dialog through the palette. The input field must be EMPTY
            // before a slash command (a stray char concatenates and the text goes to chat):
            // Esc clears it, then "/setup" opens the palette filtered to the setup command.
            Key("escape");
            await Task.Delay(400);
            Text("/setup");
            await Task.Delay(600);
            Key("enter");
            // Marker of the model-setup dialog (localized, so match layout markers that are
            // stable): the dialog title bar and the provider row markers / "Modello attivo".
            if (!await WaitAsync(() =>
                    Capture().Contains("Modello attivo") || Capture().Contains("Active model")
                    || Capture().Contains("Provider attivo") || Capture().Contains("Active provider"),
                    TimeSpan.FromSeconds(15)))
            { Check("setup dialog opened", false); Dump(); return 1; }
            Check("setup dialog opened", true);
            await Task.Delay(1000);
            var dlg = Capture();
            Console.WriteLine("--- /setup dialog (first capture) ---");
            Console.WriteLine(dlg.Length > 3000 ? dlg[^3000..] : dlg);
            Console.WriteLine("--------------------------------------\n");

            // The LLM tab shows the providers ListView (with marks). We must select
            // TestGemini, press Edit, change the API key field, press OK.
            // KEYBOARD-ONLY (reliable in Terminal.Gui v2.4.17): content buttons added with
            // dlg.Add do not reliably fire from synthetic mouse clicks (see the puppet
            // guide), so focus traversal + Enter is used: dropdown → list (Tab), pick the
            // row (arrows), list → Add → Edit (Tab ×2), activate Edit (Enter).
            var grid = StripAnsi(Puppet("{\"type\":\"capture\",\"grid\":true}"));
            var lines = grid.Split('\n');
            int geminiRow = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("TestGemini")) { geminiRow = i; break; }
            }
            Console.WriteLine($"[diag] geminiRow={geminiRow}");
            Check("dialog lists TestGemini", geminiRow >= 0);
            if (geminiRow < 0) { Dump(); return 1; }

            // Focus starts on the "active provider" dropdown (SetFocus in Initialized).
            // Tab → providers ListView; Down highlights the row AFTER the current one, so
            // move Up/Down until TestGemini is selected (the list opens on DeepSeekBridge).
            Key("tab");
            await Task.Delay(400);
            // Ensure DeepSeekBridge is the selected row, then Down once → TestGemini.
            Key("home");            // first row = DeepSeekBridge
            await Task.Delay(250);
            Key("down");            // second row = TestGemini
            await Task.Delay(400);
            // Tab → Add, Tab → Edit, Enter → edit dialog for the selected provider.
            Key("tab"); await Task.Delay(250);
            Key("tab"); await Task.Delay(250);
            Key("enter");
            // 3) The edit dialog opens: wait for its API-key field label.
            if (!await WaitAsync(() => Capture().Contains("Chiave") || Capture().Contains("ApiKey") || Capture().Contains("API"), TimeSpan.FromSeconds(10)))
            { Check("edit dialog opened", false); Dump(); return 1; }
            Check("edit dialog opened", true);
            await Task.Delay(800);
            var editDlg = Capture();
            Console.WriteLine("--- edit dialog ---");
            Console.WriteLine(editDlg.Length > 2500 ? editDlg[^2500..] : editDlg);
            Console.WriteLine("--------------------\n");

            // Field geometry: AddField lays the label out at X=1 and the field at
            // X = labelWidth+2 = 20 (dialog-local), so clicking right after the label text
            // lands in the gap between label and field and focus never moves. Derive the
            // real field column from a row whose field has visible text (the model value),
            // then click the API-key row at that column. ParseGrid strips the grid headers
            // and "NNNN " row prefix so indices are real screen x/y.
            var egrid = ParseGrid(StripAnsi(Puppet("{\"type\":\"capture\",\"grid\":true}")));
            int keyRow = -1; string? keyLine = null;
            int fieldX = -1;
            for (int i = 0; i < egrid.Length; i++)
            {
                if (fieldX < 0 && egrid[i].Contains("gemini-2.5-flash"))
                    fieldX = egrid[i].IndexOf("gemini-2.5-flash");
                if ((egrid[i].Contains("Chiave API") || egrid[i].Contains("API key"))
                    && keyRow < 0) { keyRow = i; keyLine = egrid[i]; }
            }
            Check("edit dialog locates field column", fieldX >= 0);
            Check("edit dialog shows API-key field", keyRow >= 0 && keyLine != null);
            if (fieldX < 0 || keyRow < 0 || keyLine == null) { Dump(); return 1; }
            var keyFieldX = fieldX + 5;
            Mouse(keyFieldX, keyRow);
            await Task.Delay(600);
            Text("new-test-key-123");
            await Task.Delay(600);
            // The API-key field is masked (Secret=true), so verify the key actually landed:
            // the row must now show a run of masked characters (not blank).
            var typedGrid = ParseGrid(StripAnsi(Puppet("{\"type\":\"capture\",\"grid\":true}")));
            int masked = 0;
            if (keyRow < typedGrid.Length && fieldX >= 0)
            {
                var seg = typedGrid[keyRow];
                for (int i = fieldX; i < seg.Length && i < fieldX + 30 && masked < 16; i++)
                    if (!char.IsWhiteSpace(seg[i])) masked++;
            }
            Check("API-key field received the typed key", masked >= 10);
            Console.WriteLine($"[diag] keyRow={keyRow} fieldX={fieldX} typedRow='{(keyRow < typedGrid.Length ? typedGrid[keyRow] : "<n/a>")}'");
            if (masked < 10) { Dump(); return 1; }
            // Confirm the edit. With the issue-#8 fix the OK button is the LAST AddButton,
            // so it is the dialog default ►◄ and Enter confirms (previously Enter triggered
            // Annulla — the edits appeared "not saved"). Prefer Enter; fall back to clicking
            // the OK token only if Enter does not close the dialog.
            Key("enter");
            var closed = await WaitAsync(() =>
                !(Capture().Contains("Modifica provider") || Capture().Contains("Edit provider")),
                TimeSpan.FromSeconds(6));
            if (!closed)
            {
                // Fallback: locate the OK button by hit-testing its real screen cells.
                var fg2 = ParseGrid(StripAnsi(Puppet("{\"type\":\"capture\",\"grid\":true}")));
                int frow = -1;
                for (int i = 0; i < fg2.Length; i++)
                    if (fg2[i].Contains("OK") && fg2[i].Contains("Annulla")) { frow = i; break; }
                if (frow < 0) { Dump(); return 1; }
                var cx = FindButtonCenter(frow, "OK");
                if (cx == null) { Dump(); return 1; }
                Mouse(cx.Value, frow);
                closed = await WaitAsync(() =>
                    !(Capture().Contains("Modifica provider") || Capture().Contains("Edit provider")),
                    TimeSpan.FromSeconds(6));
            }
            Check("edit dialog closed after OK", closed);
            if (!closed) { Dump(); return 1; }
            var afterClose = Capture();
            Console.WriteLine($"[diag] after-close note visible: {afterClose.Contains("aggiornat") || afterClose.Contains("updated")}");
            // Close the main setup dialog too.
            Key("escape");
            await Task.Delay(800);

            // Verify: TestGemini kept the non-exposed fields after the console edit.
            var after = File.ReadAllText(providersFile);
            using var doc = JsonDocument.Parse(after);
            var gemini = doc.RootElement.EnumerateArray()
                .First(x => x.GetProperty("ProviderName").GetString() == "TestGemini");
            bool keptForce = gemini.TryGetProperty("ForceTextToolDefinitions", out var f) && f.GetBoolean();
            bool keptPause = gemini.TryGetProperty("PauseBetweenRequests", out var p) && p.GetString() == "00:00:07";
            bool keySaved = gemini.TryGetProperty("ApiKey", out var k) && k.GetString() == "new-test-key-123";
            Console.WriteLine("--- providers.json after edit ---");
            Console.WriteLine(gemini.ToString());
            Console.WriteLine("---------------------------------");
            Check("edit saved the new API key", keySaved);
            Check("issue #8: ForceTextToolDefinitions preserved by console edit", keptForce);
            Check("issue #8: PauseBetweenRequests preserved by console edit", keptPause);
        }
        finally
        {
            if (proc != null && !proc.HasExited)
            {
                try { Text("/exit"); await Task.Delay(2500); } catch { }
                if (!proc.HasExited) proc.Kill(true);
            }
            if (!keep && File.Exists(backup))
            {
                File.Copy(backup, providersFile, true);
                File.Delete(backup);
                Console.WriteLine("restored original providers.json");
            }
        }
        Console.WriteLine(_failures == 0 ? "\nALL OK" : $"\n{_failures} FAILURES");
        return _failures == 0 ? 0 : 1;
    }

    private static void Dump()
    {
        try
        {
            var dump = Path.Combine(Path.GetTempPath(), "setup_puppet_dump.txt");
            File.WriteAllText(dump, Capture());
            Console.WriteLine($"[diag] screen dumped to {dump}");
        }
        catch { }
    }
}
