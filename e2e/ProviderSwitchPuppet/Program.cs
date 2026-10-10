// ProviderSwitchPuppet — E2E diagnostic for the CURRENT /providers panel (single dropdown),
// driven through the puppet TCP surface (localhost:5292).
//
// Verifies, end-to-end through the real TUI:
//   1. baseline chat works on the seeded default provider (DeepSeekBridge, local, no key);
//   2. selecting a provider in the dropdown + typing its API key + Salva persists BOTH the
//      default marker AND the key against the SELECTED provider (providers.json);
//   3. the running chat switches to the newly selected provider and answers;
//   4. a WRONG API key produces a pertinent auth error (not a bare "no response");
//   5. switching to a different provider (Gemini) with its key works too.
//
// The dropdown is driven by CLICKING it (to guarantee focus) then sending bare arrow keys;
// the API key field and the Salva button are driven by mouse clicks at coordinates computed
// from the grid capture (no reliance on the popover, which the current panel does not use).
//
// Usage: dotnet run --project e2e\ProviderSwitchPuppet [--agent-exe <path>] [--keep]
// Exit 0 = all checks pass.
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    private const int PuppetPort = 5292;
    private const string Bridge = "DeepSeekBridge";
    private const string DeepSeek = "DeepSeek";
    private const string Gemini = "Gemini";
    // Real API keys come from the environment — never hardcode them in this public repo.
    // Set DEEPSEEK_API_KEY and GEMINI_API_KEY before running. DeepSeekWrong is a fake key
    // used only to exercise the wrong-key error path.
    private static readonly string DeepSeekKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "";
    private const string DeepSeekWrong = "sk-WRONG00000000000000000000000000";
    private static readonly string GeminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";

    private static int _failures;
    private static readonly string AgentDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly Regex AnsiRe = new(
        "\x1b\\[[0-9;?]*[ -/]*[@-~]|\x1b\\][^\\x07\\x1b]*(\\x07|\x1b\\\\)|\x1b[()][A-Za-z0-9]|\x1b[=>]",
        RegexOptions.Compiled);
    private static string StripAnsi(string s) => AnsiRe.Replace(s, "");

    private static void Check(string name, bool cond)
    {
        Console.WriteLine($"  {(cond ? "PASS" : "FAIL")} {name}");
        if (!cond) _failures++;
    }

    private static string Puppet(string json)
    {
        using var c = new TcpClient("127.0.0.1", PuppetPort);
        var s = c.GetStream();
        var b = Encoding.UTF8.GetBytes(json);
        s.Write(b, 0, b.Length);
        c.Client.Shutdown(SocketShutdown.Send);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
    private static string Capture() => StripAnsi(Puppet("{\"type\":\"capture\"}"));
    private static void Key(string k) => Puppet($"{{\"type\":\"key\",\"key\":\"{k}\"}}");
    private static void Text(string t) => Puppet($"{{\"type\":\"text\",\"text\":\"{JsonSerializer.Serialize(t).Trim('"')}\"}}");
    private static void Click(int x, int y) => Puppet($"{{\"type\":\"mouse\",\"x\":{x},\"y\":{y},\"flags\":\"LeftButtonClicked\"}}");

    // Grid content rows (2 ruler rows skipped); index == screen row.
    private static string[] Grid()
    {
        var lines = StripAnsi(Puppet("{\"type\":\"capture\",\"grid\":true}")).TrimEnd('\n').Split('\n');
        return lines.Length <= 2 ? Array.Empty<string>() : lines.Skip(2).Select(l => l.Length > 5 ? l[5..] : "").ToArray();
    }

    // Screen (x,y) of the center of the first occurrence of `text` in the grid, or null.
    private static (int x, int y)? Find(string text, int fromCol = 0)
    {
        var g = Grid();
        for (int y = 0; y < g.Length; y++)
        {
            int idx = g[y].IndexOf(text, fromCol, StringComparison.Ordinal);
            if (idx >= 0) return (idx + text.Length / 2, y);
        }
        return null;
    }

    // The dropdown value: the "Provider attivo" row that is NOT the chat save-note.
    private static string? DropdownValue()
    {
        foreach (var line in Grid())
        {
            int li = line.IndexOf("Provider attivo", StringComparison.OrdinalIgnoreCase);
            if (li < 0 || line.Contains("salvato")) continue;
            var rest = line[(li + 15)..].Trim();
            int stop = rest.IndexOfAny(new[] { '▼', '│', '┃' });
            if (stop > 0) rest = rest[..stop];
            return rest.Trim();
        }
        return null;
    }

    // The provider token in the status bar: the "·"-separated token right after "localhost:PORT".
    private static string? StatusProvider()
    {
        foreach (var line in Grid())
        {
            int li = line.IndexOf("localhost:", StringComparison.OrdinalIgnoreCase);
            if (li < 0) continue;
            var after = line[(li)..];
            var parts = after.Split('·');
            if (parts.Length >= 2) return parts[1].Trim();
        }
        return null;
    }

    private static bool PanelOpen()
    {
        var c = Capture();
        return c.Contains("Chiave API") || c.Contains("API key") || c.Contains("LLM e provider");
    }

    private static string LastAgentReply()
    {
        var lines = Capture().Split('\n');
        int last = -1;
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains("◆ agente") || lines[i].Contains("◆ agent")) last = i;
        if (last < 0) return "";
        var sb = new StringBuilder();
        for (int i = last + 1; i < lines.Length; i++)
        {
            var t = lines[i].Trim('│', ' ', '·');
            if (t.Contains("◆") || t.Contains("❯") || t.Contains("╰")) break;
            if (!string.IsNullOrWhiteSpace(t)) sb.Append(t).Append(' ');
        }
        return sb.ToString().Trim();
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
        while (sw.Elapsed < timeout) { if (f()) return true; await Task.Delay(ms); }
        return false;
    }

    private static Process StartAgent(string exe, string bin) =>
        Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = bin,
            Arguments = "--enable-log --SkipIndexingOnStartup true --no-update --tui",
            UseShellExecute = true,
        })!;

    private static async Task<bool> WaitReady()
    {
        if (!await WaitAsync(() => CanConnect(PuppetPort), TimeSpan.FromSeconds(90))) return false;
        return await WaitAsync(() => Capture().Contains("localhost:") && Capture().Contains("strumenti"), TimeSpan.FromSeconds(90));
    }

    private static async Task OpenProviders()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (PanelOpen()) { Key("escape"); await Task.Delay(400); }
            Text("/"); await Task.Delay(700);
            Text("providers"); await Task.Delay(700);
            Key("enter"); await Task.Delay(1500);
            if (PanelOpen()) return;
        }
    }

    // Click the dropdown to focus it, then send `ups` bare Up arrows. Returns the new value.
    private static async Task<string> SelectProviderUp(int ups)
    {
        var dd = Find("Provider attivo");
        if (dd != null) { Click(dd.Value.x + 12, dd.Value.y); await Task.Delay(500); }
        for (int i = 0; i < ups; i++) { Key("up"); await Task.Delay(450); }
        await Task.Delay(300);
        return DropdownValue() ?? "";
    }

    // Move focus from the (focused) dropdown to the API key field via Tab, clear it, type the key.
    // (Clicking the Secret field is unreliable; Tab from the dropdown reaches it, as the
    // savetest confirmed.)
    private static async Task TypeKey(string key)
    {
        Key("tab"); await Task.Delay(400);
        Key("ctrl-a"); await Task.Delay(150);
        Key("delete"); await Task.Delay(150);
        Text(key); await Task.Delay(400);
    }

    // Trigger the default Salva button with Enter (it bubbles up from the focused field).
    // Clicking the button via the puppet mouse is unreliable in this setup.
    private static async Task ClickSalva()
    {
        Key("enter"); await Task.Delay(2500);
    }

    private static async Task<string> Chat(string msg, int waitSec)
    {
        Text(msg); await Task.Delay(400);
        Key("enter");
        await Task.Delay(waitSec * 1000);
        return LastAgentReply();
    }

    private static async Task<int> Main(string[] args)
    {
        var exe = args.Length > 0 && !args[0].StartsWith('-') ? Path.GetFullPath(args[0])
            : Path.Combine(AgentDir, "bin", "Debug", "net10.0", "agent.exe");
        var bin = Path.GetDirectoryName(exe)!;
        var keep = args.Contains("--keep");
        var providersFile = Path.Combine(bin, "PersistentData", "providers.json");
        var backup = providersFile + ".pswitchbak";

        Console.WriteLine($"agent exe : {exe}");
        if (!File.Exists(exe)) { Console.WriteLine("FAIL: agent.exe not found"); return 1; }
        if (PortBusy(PuppetPort)) { Console.WriteLine("FAIL: port 5292 busy"); return 1; }
        if (string.IsNullOrEmpty(DeepSeekKey) || string.IsNullOrEmpty(GeminiKey))
        {
            Console.WriteLine("FAIL: set the DEEPSEEK_API_KEY and GEMINI_API_KEY environment variables before running this test.");
            return 1;
        }

        if (!File.Exists(backup) && File.Exists(providersFile)) File.Copy(providersFile, backup, true);
        File.WriteAllText(providersFile, $$"""
[
  { "ProviderName": "{{Bridge}}", "IsDefault": true, "Protocol": "OpenAI", "ModelName": "deepseek-web/deepseek-chat",
    "BaseAddress": "http://127.0.0.1:8787/", "EndPoint": "v1/chat/completions", "ContextWindow": 1000000, "ForceTextToolDefinitions": true },
  { "ProviderName": "{{DeepSeek}}", "Protocol": "OpenAI", "ModelName": "deepseek-v4-flash",
    "BaseAddress": "https://api.deepseek.com/", "EndPoint": "v1/chat/completions", "ContextWindow": 1000000, "ApiKey": "" },
  { "ProviderName": "{{Gemini}}", "Protocol": "Gemini", "ModelName": "gemini-3.1-flash-lite",
    "BaseAddress": "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.1-flash-lite:generateContent",
    "EndPoint": "", "ContextWindow": 1000000, "ApiKey": "" }
]
""");
        Console.WriteLine($"seeded providers.json ({Bridge}=default, {DeepSeek}/{Gemini} empty keys)\n");

        Process? proc = null;
        try
        {
            proc = StartAgent(exe, bin);
            Console.WriteLine($"agent pid : {proc.Id}");
            if (!await WaitReady()) { Console.WriteLine("FAIL: TUI session never ready"); return 1; }
            Console.WriteLine("agent + TUI ready\n");

            // ── 1. baseline chat on the default provider (Bridge) ──
            Console.WriteLine("[1] baseline chat on " + Bridge);
            var r0 = await Chat("Hello", 25);
            Console.WriteLine($"    reply: {r0}");
            Check("baseline: agent replied (non-empty)", !string.IsNullOrWhiteSpace(r0));
            Check("baseline: status provider == " + Bridge, StatusProvider() == Bridge);

            // ── 2. select DeepSeek + type correct key + Salva ──
            Console.WriteLine("\n[2] /providers -> pick " + DeepSeek + " + correct key + Salva");
            await OpenProviders();
            Check("panel opened", PanelOpen());
            Check("dropdown opens on the active provider " + Bridge, DropdownValue() == Bridge);
            // The dropdown opened on Bridge (index 0); Down 1 -> DeepSeek (index 1).
            var after = await SelectProviderDown(1);
            Console.WriteLine($"    dropdown now: {after}");
            Check("dropdown shows " + DeepSeek, after == DeepSeek);
            await TypeKey(DeepSeekKey);
            await ClickSalva();
            Check("panel closed after Salva", !PanelOpen());

            var (isDef, key) = ReadProvider(providersFile, DeepSeek);
            Console.WriteLine($"    providers.json {DeepSeek}: IsDefault={isDef} ApiKey={key}");
            Check("providers.json: " + DeepSeek + " is now the default", isDef);
            Check("providers.json: " + DeepSeek + " ApiKey == the typed correct key", key == DeepSeekKey);

            // ── 3. chat switched to DeepSeek and answers ──
            Console.WriteLine("\n[3] chat on " + DeepSeek + " (switched)");
            var r1 = await Chat("Say hi in one short sentence", 30);
            Console.WriteLine($"    reply: {r1}");
            Check("switched chat: agent replied (non-empty)", !string.IsNullOrWhiteSpace(r1));
            Check("switched chat: status provider == " + DeepSeek, StatusProvider() == DeepSeek);

            // ── 4. WRONG key -> pertinent auth error ──
            Console.WriteLine("\n[4] /providers -> " + DeepSeek + " WRONG key -> Salva -> chat");
            await OpenProviders();
            if (DropdownValue() != DeepSeek) await SelectProviderDown(1);
            await TypeKey(DeepSeekWrong);
            await ClickSalva();
            var (_, key2) = ReadProvider(providersFile, DeepSeek);
            Console.WriteLine($"    providers.json {DeepSeek} ApiKey={key2}");
            Check("providers.json: " + DeepSeek + " ApiKey == the wrong key", key2 == DeepSeekWrong);
            var r2 = await Chat("Hello", 30);
            Console.WriteLine($"    reply (expect auth error): {r2}");
            bool authErr = r2.Contains("autentic", StringComparison.OrdinalIgnoreCase)
                      || r2.Contains("chiave", StringComparison.OrdinalIgnoreCase)
                      || r2.Contains("auth", StringComparison.OrdinalIgnoreCase)
                      || r2.Contains("key", StringComparison.OrdinalIgnoreCase)
                      || r2.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                      || r2.Contains("rifiut", StringComparison.OrdinalIgnoreCase)
                      || r2.Contains("401", StringComparison.OrdinalIgnoreCase);
            Check("wrong key: chat shows a pertinent auth/key error", authErr);
            Check("wrong key: NOT a bare 'no response'",
                  !r2.Trim().Equals("LLM returned no response", StringComparison.OrdinalIgnoreCase)
                  && !r2.Trim().Equals("Il LLM non ha restituito alcuna risposta", StringComparison.OrdinalIgnoreCase));

            // ── 5. switch to Gemini with its key ──
            Console.WriteLine("\n[5] /providers -> pick " + Gemini + " + key -> Salva -> chat");
            await OpenProviders();
            if (DropdownValue() != Gemini) await SelectProviderDown(1);   // DeepSeek(1) -> Gemini(2)
            Console.WriteLine($"    dropdown now: {DropdownValue()}");
            Check("dropdown shows " + Gemini, DropdownValue() == Gemini);
            await TypeKey(GeminiKey);
            await ClickSalva();
            var (gDef, gKey) = ReadProvider(providersFile, Gemini);
            Console.WriteLine($"    providers.json {Gemini}: IsDefault={gDef} ApiKey={gKey}");
            Check("providers.json: " + Gemini + " is now the default", gDef);
            Check("providers.json: " + Gemini + " ApiKey == the typed key", gKey == GeminiKey);
            var r3 = await Chat("Say hi in one short sentence", 30);
            Console.WriteLine($"    reply: {r3}");
            Check("Gemini chat: agent replied (non-empty)", !string.IsNullOrWhiteSpace(r3));
            Check("Gemini chat: status provider == " + Gemini, StatusProvider() == Gemini);
        }
        finally
        {
            if (proc != null && !proc.HasExited)
            {
                try { Text("/exit"); await Task.Delay(2500); } catch { }
                if (!proc.HasExited) proc.Kill(true);
            }
            if (!keep && File.Exists(backup)) { File.Copy(backup, providersFile, true); File.Delete(backup); Console.WriteLine("restored providers.json"); }
        }
        Console.WriteLine(_failures == 0 ? "\nALL PASS" : $"\n{_failures} FAILURES");
        return _failures == 0 ? 0 : 1;
    }

    // Click the dropdown to focus it, then send `downs` bare Down arrows.
    private static async Task<string> SelectProviderDown(int downs)
    {
        var dd = Find("Provider attivo");
        if (dd != null) { Click(dd.Value.x + 12, dd.Value.y); await Task.Delay(500); }
        for (int i = 0; i < downs; i++) { Key("down"); await Task.Delay(450); }
        await Task.Delay(300);
        return DropdownValue() ?? "";
    }

    private static (bool isDefault, string apiKey) ReadProvider(string file, string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            if (p.GetProperty("ProviderName").GetString() == name)
            {
                bool isDef = p.TryGetProperty("IsDefault", out var d) && d.ValueKind == JsonValueKind.True;
                string key = p.TryGetProperty("ApiKey", out var k) ? k.GetString() ?? "" : "";
                return (isDef, key);
            }
        }
        return (false, "");
    }
}
