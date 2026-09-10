// ═══════════════════════════════════════════════════════════════════════
//  CliFlags — regression test for the command-line configuration overrides.
//
//  The bug this guards against (found and fixed 2026-09-10): the app's own bare switches
//  (--enable-log, --tui, --headless, --no-gui, --no-update) carry no value, but the .NET
//  command-line configuration provider pairs EVERY "--key" with the NEXT token as its value.
//  A bare switch therefore swallowed the key that followed it:
//
//      agent --headless --LLM:Anonymize true
//        → configuration:  headless = "--LLM:Anonymize"   (the override was silently ignored)
//
//  so every documented --Key:Sub override placed after a bare switch was lost (--Urls,
//  --Sip:ListenPort, --LLM:Anonymize, ...), while the same values via environment variables
//  worked — the symptom that made it look like the app read the file config only.
//
//  This harness runs the DEBUG build in PUPPET MODE (TUI + TCP control surface on 5292, see
//  docs-dev/PUPPET-MODE-GUIDE.md) with each override placed IMMEDIATELY AFTER a bare switch —
//  the exact trigger order — and verifies the effect through the real UI and the app's own API:
//
//    --tui --Urls http://localhost:5391     → status bar shows localhost:5391 + /health answers
//    --no-update --Sip:ListenPort 6073      → GET /v1/sip/config reports 6073
//
//  Usage: dotnet run --project e2e\CliFlags [path-to-agent.exe]
//  Exit code 0 = both overrides applied. Requires a DEBUG build (puppet listener exists only there).
// ═══════════════════════════════════════════════════════════════════════
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

internal static class Program
{
    private const int PuppetPort = 5292;
    private const int ServerPort = 5391;      // --Urls override (the debug default would be 5291)
    private const int SipPort = 6073;         // --Sip:ListenPort override (the default is 5060)

    private static int _pass, _fail;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Console.WriteLine($"  OK   {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL {name}{(detail == null ? "" : "  — " + detail)}"); }
    }

    private static bool PortBusy(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Any(e => e.Port == port);

    /// <summary>One puppet command: JSON on the socket, EOF, read the answer to EOF.</summary>
    private static string Puppet(string json)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", PuppetPort);
        using var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(json);
        stream.Write(bytes, 0, bytes.Length);
        client.Client.Shutdown(SocketShutdown.Send);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition()) return true;
            await Task.Delay(500);
        }
        return false;
    }

    private static async Task<string?> GetAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            return await http.GetStringAsync(url);
        }
        catch { return null; }
    }

    private static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")); // → AgentBridge/
        var bin = Path.Combine(root, "bin", "Debug", "net10.0");
        var exe = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(bin, "agent.exe");
        Console.WriteLine($"agent exe : {exe}");
        if (!File.Exists(exe)) { Console.WriteLine("FAIL: agent.exe not found (build the DEBUG configuration)"); return 1; }

        foreach (var (port, what) in new[] { (PuppetPort, "puppet control surface"), (ServerPort, "server (--Urls override)") })
            if (PortBusy(port)) { Console.WriteLine($"FAIL: port {port} is busy — stop the other agent instance ({what})"); return 1; }

        // Every override sits IMMEDIATELY AFTER a bare switch: the order that used to swallow
        // the override key itself.
        var cmdArgs = $"--tui --Urls http://localhost:{ServerPort} --no-update --Sip:ListenPort {SipPort} " +
                      "--enable-log --SkipIndexingOnStartup true";
        Console.WriteLine($"cmdline   : {cmdArgs}\n");

        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = bin,
                Arguments = cmdArgs,
                UseShellExecute = true,   // own console window: Terminal.Gui renders (puppet needs a TUI)
            });
            Console.WriteLine($"agent pid : {proc!.Id}");
            var baseUrl = $"http://localhost:{ServerPort}";

            // 1) The override reaches the HTTP server (the debug default 5291 would be used if lost).
            var health = await WaitAsync(async () => await GetAsync($"{baseUrl}/health") != null,
                                         TimeSpan.FromSeconds(90));
            Check($"--Urls honored after a bare switch (/health answers on {ServerPort})", health,
                "nothing listening on the override port — the debug default 5291 was used instead");

            // 2) The override reaches the SIP configuration snapshot.
            var sipJson = health ? await GetAsync($"{baseUrl}/v1/sip/config") : null;
            Check($"--Sip:ListenPort honored after a bare switch (config reports {SipPort})",
                sipJson != null && sipJson.Contains($"\"listen_port\":{SipPort}"),
                sipJson == null ? "no /v1/sip/config response" : $"reports: {sipJson[..Math.Min(160, sipJson.Length)]}");

            // 3) The override reaches the TUI (puppet capture of the status bar). The puppet
            //    socket answers only once the TUI is up, so a refused connection is "not ready
            //    yet", not a test failure: the lambda swallows it and the wait retries.
            var ready = await WaitAsync(async () =>
            {
                try { return Puppet("{\"type\":\"capture\"}").Contains("ctx 0/"); }
                catch { return false; }
            }, TimeSpan.FromSeconds(90));
            var screen = "";
            if (ready)
            {
                try { screen = Puppet("{\"type\":\"capture\"}"); } catch { }
            }
            Check($"--Urls honored in the TUI status bar (localhost:{ServerPort})",
                screen.Contains($"localhost:{ServerPort}"),
                ready ? "status bar does not show the override port" : "TUI session not ready");
        }
        finally
        {
            // Clean shutdown through the TUI (empty input first: a stray char would go to chat).
            try
            {
                Puppet("{\"type\":\"key\",\"key\":\"escape\"}");
                await Task.Delay(400);
                Puppet("{\"type\":\"text\",\"text\":\"/exit\"}");
                await Task.Delay(600);
                Puppet("{\"type\":\"key\",\"key\":\"enter\"}");
                await Task.Delay(1000);
            }
            catch { }
            try { if (proc is { HasExited: false }) await WaitAsync(async () => proc.HasExited, TimeSpan.FromSeconds(10)); } catch { }
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"CliFlags: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
