// SetupProviderSwitch — E2E test for the TUI "active provider" choice, driven through the
// REAL /setup dialog by the puppet TCP surface (localhost:5292).
//
// User report: changing the active LLM provider from the TUI settings (e.g. DeepSeekBridge ->
// TestGemini), then coming back to the settings, shows a DIFFERENT active provider.
//
// Scenario:
//   1. Seed providers.json: DeepSeekBridge (IsDefault, the process default) + TestGemini.
//   2. Launch agent.exe (Debug) --tui; wait for the puppet listener + session.
//   3. Initial state: status bar (session truth) + /setup dropdown both DeepSeekBridge.
//   4. /setup -> change the "Active provider" dropdown to TestGemini -> Save (Salva).
//   5. Propagation: status bar, "provider now" note, setup-save log line.
//   6. Reopen /setup: the dropdown must still show TestGemini (check 3, close/reopen).
//   7. RESTART the app (leave and come back): the new session must also come up on
//      TestGemini — the provider chosen in the settings is the program's provider.
//   8. Restore: choose DeepSeekBridge again from the settings, verify, restore providers.json.
//
// Usage: dotnet run --project e2e\SetupProviderSwitch [--agent-exe <path>] [--keep] [--trace]
// Exit 0 = pass.
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    private const int PuppetPort = 5292;
    private const string ProviderA = "DeepSeekBridge";   // seeded default, initial provider
    private const string ProviderB = "TestGemini";       // the provider the user picks
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
    private static string JsonEnc(string s) => JsonSerializer.Serialize(s).Trim('"');

    // Grid capture: two header rows plus a "NNNN " row-number prefix per line. Strip both so
    // row indices map to puppet screen coordinates.
    private static string[] ParseGrid(string gridText)
    {
        var lines = gridText.TrimEnd('\n').Split('\n');
        if (lines.Length <= 2) return Array.Empty<string>();
        return lines.Skip(2).Select(l => l.Length > 5 ? l[5..] : "").ToArray();
    }
    private static string[] Grid() => ParseGrid(StripAnsi(Puppet("{\"type\":\"capture\",\"grid\":true}")));

    // Numbered screen dump for step-by-step diagnosis (--trace).
    private static void Trace(string tag)
    {
        Console.WriteLine($"--- [{tag}] ---");
        var rows = Grid();
        for (int i = 0; i < rows.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(rows[i])) continue;
            Console.WriteLine($"{i,3}|{rows[i]}");
        }
        Console.WriteLine("---");
    }

    private static int? FindRow(string[] grid, params string[] needles)
    {
        for (int i = 0; i < grid.Length; i++)
            if (needles.Any(n => grid[i].Contains(n, StringComparison.OrdinalIgnoreCase))) return i;
        return null;
    }

    // Value drawn in the dialog's "active provider" row: the label sits at X=1 and the
    // dropdown at X=17, so the value follows the label text on the same row.
    private static string? DropdownValue(string[] grid)
    {
        var row = FindRow(grid, "Provider attivo", "Active provider");
        if (row == null) return null;
        var line = grid[row.Value];
        var labelIdx = line.IndexOf("Provider attivo", StringComparison.OrdinalIgnoreCase);
        if (labelIdx < 0) labelIdx = line.IndexOf("Active provider", StringComparison.OrdinalIgnoreCase);
        if (labelIdx < 0) return null;
        var rest = line[(labelIdx + 15)..].Trim();
        var stop = rest.IndexOfAny(new[] { '▼', '│', '┃' });
        if (stop > 0) rest = rest[..stop];
        return rest.Trim();
    }

    // The main-window status bar line carries the provider currently in use (session truth).
    private static string? StatusLine(string[] grid)
    {
        var row = FindRow(grid, "ctx ");
        return row == null ? null : grid[row.Value];
    }

    // Is the model setup dialog on screen? The tab hint is dialog chrome and is never clipped
    // (unlike the LLM tab's last row), so it is the reliable marker.
    private static bool SetupOpen()
    {
        var c = Capture();
        return c.Contains("Ctrl+PageDown") || c.Contains("Provider attivo") || c.Contains("Active provider");
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

    private static Process StartAgent(string exe, string agentBin) =>
        Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = agentBin,
            Arguments = "--enable-log --SkipIndexingOnStartup true --no-update --tui",
            UseShellExecute = true,   // own console window: Terminal.Gui renders
        })!;

    private static async Task<bool> WaitAgentReady()
    {
        if (!await WaitAsync(() => CanConnect(PuppetPort), TimeSpan.FromSeconds(90))) return false;
        return await WaitAsync(() => Capture().Contains("ctx "), TimeSpan.FromSeconds(90));
    }

    // Opens the model setup dialog from the palette. The input field must be EMPTY before a
    // slash command (a stray char concatenates and the text goes to chat).
    private static async Task OpenSetup()
    {
        Key("escape");
        await Task.Delay(400);
        Text("/setup");
        await Task.Delay(700);
        Key("enter");
        var opened = await WaitAsync(() =>
        {
            var c = Capture();
            return c.Contains("Provider attivo") || c.Contains("Active provider");
        }, TimeSpan.FromSeconds(15));
        if (!opened) Console.WriteLine("FAIL: /setup dialog did not open");
        await Task.Delay(900);
    }

    // Picks a provider in the focused dropdown. A ReadOnly DropDownList in Terminal.Gui v2
    // opens its popover on ACTIVATE (Space): Space → popover, arrows → highlight, Enter →
    // accept (writes the item back into the field).
    private static async Task PickProvider(string direction)
    {
        Key("space");
        await Task.Delay(700);
        Key(direction);
        await Task.Delay(500);
        Key("enter");
        await Task.Delay(800);
    }

    // Saves the dialog: Enter on the focused control bubbles to the default button (Salva).
    // If the popover swallowed it, walk the TabStops and retry.
    private static async Task SaveByEnter()
    {
        for (int i = 0; i < 12; i++)
        {
            Key("enter");
            await Task.Delay(600);
            if (!SetupOpen()) return;
            Key("tab");
            await Task.Delay(250);
        }
        Console.WriteLine("[diag] WARNING: Save never activated — screen dump follows");
        Dump();
    }

    private static async Task<int> Main(string[] args)
    {
        var exe = args.Length > 0 && !args[0].StartsWith('-') ? Path.GetFullPath(args[0])
            : Path.Combine(AgentDir, "bin", "Debug", "net10.0", "agent.exe");
        var agentBin = Path.GetDirectoryName(exe)!;
        var keep = args.Contains("--keep");
        var trace = args.Contains("--trace");
        var providersFile = Path.Combine(agentBin, "PersistentData", "providers.json");
        var backup = providersFile + ".testbak";

        Console.WriteLine($"agent exe : {exe}");
        if (!File.Exists(exe)) { Console.WriteLine("FAIL: agent.exe not found — build AgentBridge Debug first"); return 1; }
        if (PortBusy(PuppetPort)) { Console.WriteLine("FAIL: port 5292 busy — stop other agent instances"); return 1; }

        if (!File.Exists(backup) && File.Exists(providersFile)) File.Copy(providersFile, backup, true);
        File.WriteAllText(providersFile, $$"""
[
  {
    "ProviderName": "{{ProviderA}}",
    "Protocol": "OpenAI",
    "ModelName": "deepseek-web/deepseek-chat",
    "BaseAddress": "http://127.0.0.1:8787/",
    "EndPoint": "v1/chat/completions",
    "ContextWindow": 1000000,
    "ApiKey": "test-key",
    "IsDefault": true
  },
  {
    "ProviderName": "{{ProviderB}}",
    "Protocol": "Gemini",
    "ModelName": "gemini-2.5-flash",
    "BaseAddress": "https://generativelanguage.googleapis.com/",
    "EndPoint": "v1beta/models",
    "ContextWindow": 1000000,
    "ApiKey": "test-key"
  }
]
""");
        Console.WriteLine($"seeded providers.json ({ProviderA}=default, {ProviderB})\n");

        Process? proc = null;
        string logFile = "";
        try
        {
            proc = StartAgent(exe, agentBin);
            logFile = Path.Combine(agentBin, "logs", $"{proc.Id}.txt");
            Console.WriteLine($"agent pid : {proc.Id}");
            if (!await WaitAgentReady()) { Console.WriteLine("FAIL: TUI session never became ready"); return 1; }
            Console.WriteLine("agent + TUI session ready\n");

            // ── 0. initial state ─────────────────────────────────────────────────────────
            var status0 = StatusLine(Grid());
            Console.WriteLine($"[diag] status bar: {status0}");
            Check($"initial session provider is {ProviderA} (status bar)", status0 != null && status0.Contains(ProviderA));

            await OpenSetup();
            var dd0 = DropdownValue(Grid());
            Console.WriteLine($"[diag] /setup dropdown at open: '{dd0}'");
            Check($"check 0 (state at open): dropdown shows the active provider {ProviderA}", dd0 == ProviderA);
            if (dd0 == null) { Dump(); return 1; }

            // ── 1. choose TestGemini in the dropdown ─────────────────────────────────────
            if (trace) Trace("dropdown focused (initial)");
            await PickProvider("down");
            if (trace) Trace("after pick");
            var dd1 = DropdownValue(Grid());
            Console.WriteLine($"[diag] dropdown after picking {ProviderB}: '{dd1}' (setup open: {SetupOpen()})");
            Check($"check 2 (interaction): dropdown now shows {ProviderB}", dd1 == ProviderB);

            // ── 2. Save (Salva) ──────────────────────────────────────────────────────────
            if (SetupOpen()) await SaveByEnter();
            var dlgGone = await WaitAsync(() => !SetupOpen(), TimeSpan.FromSeconds(8));
            Check("setup dialog closed after Save", dlgGone);
            await Task.Delay(2000);   // let the fire-and-forget switch land

            // ── 3. propagation: the choice must reach the session and the config ─────────
            var status1 = StatusLine(Grid());
            var chatLog = Capture();
            Console.WriteLine($"[diag] status bar after save: {status1}");
            Check("check 4 (propagation): status bar shows the chosen provider", status1 != null && status1.Contains(ProviderB));
            Check("check 4 (propagation): chat log shows the switch note",
                chatLog.Contains("provider ora") || chatLog.Contains("provider now"));
            var log = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
            Check("check 4 (propagation): log records the setup save",
                log.Contains($"TUI ModelSetup saved (provider: {ProviderB})"));
            Check("check 4 (propagation): providers.json marks the chosen provider as default",
                File.ReadAllText(providersFile).Contains($"\"ProviderName\": \"{ProviderB}\"") &&
                IsDefaultProvider(providersFile, ProviderB));

            // ── 4. close/reopen the settings: the reported symptom ───────────────────────
            await OpenSetup();
            var dd2 = DropdownValue(Grid());
            Console.WriteLine($"[diag] /setup dropdown after reopen: '{dd2}'");
            Check("check 3 (persistence): dropdown still shows the chosen provider on reopen", dd2 == ProviderB);

            // ── 5. leave the app and come back: the provider must survive the restart ────
            Text("/exit");
            await Task.Delay(3000);
            if (!proc.HasExited) proc.Kill(true);
            await WaitAsync(() => !CanConnect(PuppetPort), TimeSpan.FromSeconds(20));

            proc = StartAgent(exe, agentBin);
            logFile = Path.Combine(agentBin, "logs", $"{proc.Id}.txt");
            Console.WriteLine($"[diag] restarted agent pid: {proc.Id}");
            if (!await WaitAgentReady()) { Console.WriteLine("FAIL: TUI session never became ready after restart"); return 1; }
            var status2 = StatusLine(Grid());
            Console.WriteLine($"[diag] status bar after restart: {status2}");
            Check("restart: the new session starts from the provider chosen in the settings",
                status2 != null && status2.Contains(ProviderB));
            await OpenSetup();
            var dd3 = DropdownValue(Grid());
            Console.WriteLine($"[diag] /setup dropdown after restart: '{dd3}'");
            Check("restart: the settings show the provider chosen in the settings", dd3 == ProviderB);

            // ── 6. restore: choose the original provider again from the settings ─────────
            await PickProvider("up");
            if (SetupOpen()) await SaveByEnter();
            await Task.Delay(2000);
            var status3 = StatusLine(Grid());
            Console.WriteLine($"[diag] status bar after restore: {status3}");
            Check($"restore: session back on {ProviderA}", status3 != null && status3.Contains(ProviderA));
            Check("restore: providers.json default back to the original provider",
                IsDefaultProvider(providersFile, ProviderA));
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

    private static bool IsDefaultProvider(string providersFile, string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(providersFile));
        var marked = doc.RootElement.EnumerateArray()
            .Where(p => p.TryGetProperty("IsDefault", out var d) && d.ValueKind == JsonValueKind.True)
            .Select(p => p.GetProperty("ProviderName").GetString())
            .ToList();
        return marked.Count == 1 && marked[0] == name;
    }

    private static void Dump()
    {
        try
        {
            var dump = Path.Combine(Path.GetTempPath(), "setup_provider_switch_dump.txt");
            File.WriteAllText(dump, Capture());
            Console.WriteLine($"[diag] screen dumped to {dump}");
        }
        catch { }
    }
}
