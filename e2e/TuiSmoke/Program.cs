// ═══════════════════════════════════════════════════════════════════════
//  TuiSmoke — end-to-end smoke test of the AgentBridge terminal UI.
//
//  Launches the real `agent.exe --tui` inside a Windows pseudoconsole
//  (ConPTY), injects real keystrokes, captures the rendered ANSI output and
//  asserts the Qwen-Code-style UI behaves: logo + input line render, the
//  /model picker opens and Esc closes it cleanly, a chat message is sent.
//
//  Usage:
//    dotnet run --project e2e\TuiSmoke [path-to-agent.exe] [base-url]
//  Exit code 0 = all checks passed. Requires port 5290 to be free.
//
//  LESSONS LEARNED (read before touching ConPTY code — each one cost a bug):
//  1. CreateProcess needs EXTENDED_STARTUPINFO_PRESENT = 0x00080000, NOT
//     0x01000000 (CREATE_BREAKAWAY_FROM_JOB): without it the attribute list is
//     silently ignored and the child never attaches to the pseudoconsole
//     (it inherits the parent's std handles and runs "headless").
//  2. UpdateProcThreadAttribute's lpValue is the HPCON handle VALUE, not a
//     pointer to it (the kernel reads it as the handle itself).
//  3. Close the ConPTY-owned pipe ends (inRead/outWrite) only AFTER
//     CreateProcess, not before — closing early breaks the session.
//  4. The child's ConPTY attach can silently fail when this harness runs inside
//     a pipe (CI / captured output): the UI never renders. The launch loop
//     below retries once — do not remove it. When debugging, check whether the
//     agent went headless (ASP.NET logs appear on the harness stdout).
//  5. NEVER pipe `dotnet build ... | Select-Object ...` and keep going: a
//     failed build's exit code is masked by the pipe, leaving a STALE binary.
//     Always surface build errors (no pipe, or check $LASTEXITCODE) before
//     running the test.
// ═══════════════════════════════════════════════════════════════════════
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private static int _pass, _fail;

    // Strips ANSI sequences (colors, cursor, OSC) so text matches are reliable.
    // The OSC alternative must handle BOTH terminations: BEL (\x07) and ST (ESC \)
    // — Terminal.Gui's window-title OSC ends with ST, and a stray ST byte after a
    // command name corrupted marker matching (e.g. "/clear" + ESC instead of "/clear ").
    private static readonly System.Text.RegularExpressions.Regex AnsiRe = new(
        "\x1b\\[[0-9;?]*[ -/]*[@-~]|\x1b\\][^\\x07\\x1b]*(\\x07|\x1b\\\\)|\x1b[()][A-Za-z0-9]|\x1b[=>]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string StripAnsi(string s) => AnsiRe.Replace(s, "");

    private static void Check(string name, bool cond)
    {
        if (cond) { _pass++; Console.WriteLine($"  OK   {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL {name}"); }
    }

    private static bool IsPortBusy(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Any(e => e.Port == port);

    // Dumps the full clean screen to %TEMP%\tui_dump.txt — used to inspect a crash
    // or a failed interaction (the console output is truncated otherwise).
    private static void DumpScreen(ConPty conpty, string label)
    {
        try
        {
            var dump = Path.Combine(Path.GetTempPath(), "tui_dump.txt");
            File.WriteAllText(dump, label + "\n\n" + conpty.Screen);
            Console.WriteLine($"[diag] full screen dumped to {dump}");
        }
        catch { }
    }

    // Checks a liveness assertion and reports the exit code when the process died.
    private static void CheckAlive(ConPty conpty, string name)
    {
        var alive = !conpty.Exited;
        if (alive) { Check(name, true); }
        else
        {
            Check(name, false);
            try { Console.WriteLine($"[diag] {name} — process EXITED with code {conpty.ExitCode}"); } catch { }
        }
    }

    private static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")); // → AgentBridge/
        var exe = args.Length > 0 ? Path.GetFullPath(args[0])
            : Path.Combine(root, "bin", "Debug", "net10.0", "agent.exe");
        // Arguments after the exe are the command line, unless the first one is a URL.
        var rest = args.Skip(1).ToArray();
        var baseUrl = rest.Length > 0 && rest[0].StartsWith("http") ? rest[0] : "http://localhost:5290";
        var cmdArgs = rest.Length > 0 && rest[0].StartsWith("http") ? string.Join(' ', rest.Skip(1)) : string.Join(' ', rest);
        if (string.IsNullOrWhiteSpace(cmdArgs) && exe.Contains("agent.exe")) cmdArgs = "--SkipIndexingOnStartup true";

        Console.WriteLine($"agent exe : {exe}");
        Console.WriteLine($"base url  : {baseUrl}");
        if (!File.Exists(exe)) { Console.WriteLine($"FAIL: agent.exe not found at {exe}"); return 1; }

        // Port free? The previous run may leave the agent shutting down: wait
        // up to 20s for the port to free, then fail with a clear message.
        var port = new Uri(baseUrl).Port;
        var sw = Stopwatch.StartNew();
        while (IsPortBusy(port) && sw.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(250);
        if (IsPortBusy(port))
        {
            Console.WriteLine($"FAIL: port {port} is still busy after 20s — stop the server first");
            return 1;
        }

        // In some environments (process launched inside a pipe) the child's attach to the
        // ConPTY fails: the UI does not render. Relaunch once before declaring the test a
        // failure (the app code is not involved: it is a test-infrastructure
        // quirk that runs fine in a real terminal).
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            _pass = _fail = 0;
            if (attempt > 1)
            {
                Console.WriteLine("  (UI did not render — relaunching once)");
                await Task.Delay(2000);   // let ConDrv breathe
            }
            var uiRendered = await RunOnceAsync(exe, cmdArgs, baseUrl);
            // Retry ONLY when the UI did not appear (ConPTY attach flaky in a pipe):
            // if the UI was there and a check failed, it's a real test failure.
            if (_fail == 0 || attempt == 2 || uiRendered) break;
        }

        Console.WriteLine();
        Console.WriteLine($"TuiSmoke: {_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>A full round of tests against the agent launched in ConPTY.
    /// Returns true if the UI is rendered (i.e. the child attached).</summary>
    private static async Task<bool> RunOnceAsync(string exe, string cmdArgs, string baseUrl)
    {
        using var conpty = new ConPty(exe, cmdArgs);
        Console.WriteLine($"cmdline   : {cmdArgs}");

        // 1) The UI renders: window title, status bar and chat panel. The status bar
        //    shows the server host:port once the in-process server answers /health,
        //    which also proves the Terminal.Gui screen is rendering. The chat panel
        //    starts with the welcome history (its system entries render with "·").
        await conpty.WaitForText(baseUrl.Replace("http://", ""), TimeSpan.FromSeconds(20));
        var out0 = conpty.Screen;
        var uiRendered = out0.Contains("·");
        Check("chat panel rendered (system entries)", out0.Contains("·"));
        Check("ascii art banner rendered (gradient block chars)", out0.Contains("█"));
        Check("window title rendered", out0.Contains("AGENT - AI Chat Console") || out0.Contains("Console chat IA"));
        Check("status bar shows server host:port", out0.Contains(baseUrl.Replace("http://", "")));

        // If the child did not attach (headless), no point continuing: the retry relaunches.
        if (!uiRendered) return false;

        // Locale-independent marker of the provider picker: it lists the providers
        // fetched from /v1/models as "id — name · ctx N". The dialog title (and every
        // other UI string) is localized, so the tests never assert localized text.
        // "· ctx " (with the bullet) disambiguates the picker rows from the status
        // bar's context segment ("ctx 0/32k"), which must not leak into these checks.
        const string pickerMarker = "· ctx ";
        // The slash palette lists command names ("/agent ...") which are NOT translated.
        const string paletteMarker = "/agent ";

        // 2) "/model" opens the provider picker (the slash-command palette opens live
        //    on "/", the rest of the line goes to its filter field).
        conpty.Send("/model\r");
        await conpty.WaitForNewTextAsync(pickerMarker, TimeSpan.FromSeconds(20));
        Check("/model via palette opens picker", conpty.Screen.Contains(pickerMarker));
        CheckAlive(conpty, "process alive after /model");

        // 2b) USER REQUEST: the picker must also accept typed text — type a provider
        //     name into its filter field and press Enter (nobody remembers the ids).
        //     The switch must succeed (no "refused"/"rifiutato" note). The FIRST
        //     /v1/control call lazily loads the TTS engine (kokoro.onnx, seconds),
        //     so the startup session creation completes only when the status bar shows the
        //     context segment ("ctx N/M") — wait for that before typing, else the switch
        //     POST races the create and 400s with "session_id is required". The context
        //     segment "ctx 0/" appears in the status bar only after the session state
        //     refreshed — WaitForText is timing-independent (accumulated stream).
        await conpty.WaitForText("ctx 0/", TimeSpan.FromSeconds(25));
        conpty.Mark();
        conpty.Send("Zai\r");
        await Task.Delay(1800);
        var afterTyped = conpty.ScreenSinceMark();
        Check("typed provider name selects and switches (Zai)", afterTyped.Contains("Zai") && !afterTyped.Contains("refused") && !afterTyped.Contains("rifiutato") && !afterTyped.Contains("required"));
        CheckAlive(conpty, "process alive after typed provider name");

        // 2c) Esc closes the picker with no residue. Wait on " · ctx " — the provider
        //     rows ("id — name · ctx N") — which the palette rows never carry. After
        //     Esc the picker's row signature " — <name> · ctx " must be gone: history
        //     lines with a bare " — " or the status bar's " · ctx 0/128k" do not match.
        conpty.Send("/model\r");
        await conpty.WaitForNewTextAsync(" · ctx ", TimeSpan.FromSeconds(20));
        await Task.Delay(400);   // let the dialog finish opening before Esc
        conpty.Mark();
        conpty.Send("\x1b");
        await Task.Delay(800);
        var afterEsc = conpty.ScreenSinceMark();
        var residue = System.Text.RegularExpressions.Regex.Match(afterEsc, " — [^ ]+ · ctx ");
        if (residue.Success)
            Console.WriteLine($"[diag] picker residue [{residue.Value}]:\n" + (afterEsc.Length > 1200 ? afterEsc[^1200..] : afterEsc));
        Check("Esc closes picker (no residue)", !residue.Success);

        // 2d) List selection: Enter on the list picks the highlighted provider. The
        //     first llm-provider on this machine is ExllamaV2 — after the
        //     picker closes the switch note/status shows that name (2b switched to
        //     Zai first, so this is a real switch, not "already on").
        conpty.Send("/model\r");
        await conpty.WaitForNewTextAsync(pickerMarker, TimeSpan.FromSeconds(20));
        conpty.Send("\x1b[B\r");   // Down (already at first item) then Enter
        await conpty.WaitForNewTextAsync("ExllamaV2", TimeSpan.FromSeconds(20));
        var afterPick = conpty.ScreenSinceMark();
        Check("Enter on provider list selects and switches", afterPick.Contains("ExllamaV2") && !afterPick.Contains("refused") && !afterPick.Contains("rifiutato"));
        CheckAlive(conpty, "process alive after list selection");

        // 3) USER BUG REPORT (crash): typing "/m", selecting /model with the cursor
        //    arrows and pressing Enter (or Tab to complete first) must open the
        //    picker — it used to kill the whole TUI with "Cannot change document
        //    within another document change" (unhandled CLR exception).
        conpty.Send("/m\x1b[B\r");
        await conpty.WaitForNewTextAsync(pickerMarker, TimeSpan.FromSeconds(20));
        Check("/m + arrows + Enter opens picker (no crash)", conpty.Screen.Contains(pickerMarker));
        CheckAlive(conpty, "process alive after /m + arrows + Enter");
        conpty.Send("\x1b");
        await Task.Delay(500);

        // 3b) Tab-complete then Enter: the palette filter is filled with "/model "
        //     (leading slash). The completed command must STAY visible in the LIST
        //     ("/model [name]" — a list row, not just the filter text) and Enter
        //     must run it without closing the app.
        conpty.Send("/m\x1b[B\t");
        await Task.Delay(600);
        var afterTab = conpty.Screen;
        Check("Tab completion keeps the command visible", afterTab.Contains("/model [name]"));
        conpty.Send("\r");
        await conpty.WaitForNewTextAsync(pickerMarker, TimeSpan.FromSeconds(20));
        Check("/m + arrows + Tab + Enter opens picker (no crash)", conpty.Screen.Contains(pickerMarker));
        CheckAlive(conpty, "process alive after Tab completion");
        conpty.Send("\x1b");
        await Task.Delay(500);

        // 3c) "@" and "?" opened the same DocumentChanged code path — they must not
        //     crash either (no files uploaded → a note appears; Esc then closes the
        //     page the "?" opens).
        conpty.Send("@");
        await Task.Delay(1200);
        CheckAlive(conpty, "process alive after @ (files dialog/note)");
        conpty.Send("\x1b");
        await Task.Delay(500);
        conpty.Send("?");
        await Task.Delay(1200);
        CheckAlive(conpty, "process alive after ? (shortcuts page)");
        conpty.Send("\x1b");
        await Task.Delay(500);

        // 4) The palette lists the commands in ALPHABETICAL order (user request).
        //    Only the fresh frames after "/" are inspected (the accumulated stream
        //    also carries earlier palette/dialog frames that would break the order).
        //    Wait for a mid-list row so the ListView finished rendering before capture.
        conpty.Mark();
        conpty.Send("/");
        await conpty.WaitForNewTextAsync("/clear ", TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        var palette = conpty.ScreenSinceMark();
        Check("palette opens and lists commands", palette.Contains(paletteMarker));
        var idx = new List<int>
        {
            palette.IndexOf("/agent "),
            palette.IndexOf("/attach "),
            palette.IndexOf("/clear "),
            palette.IndexOf("/docs "),
            palette.IndexOf("/exit "),
            palette.IndexOf("/features "),
        };
        if (idx.Any(i => i < 0) || !idx.SequenceEqual(idx.OrderBy(i => i)))
        {
            Console.WriteLine($"[diag] palette ({palette.Length} chars):\n" + palette);
            var raw = conpty.OutputSinceMark();
            var ci = raw.IndexOf("/clear");
            Console.WriteLine($"[diag] raw /clear at {ci}: " + (ci >= 0 ? string.Join(",", raw.Skip(Math.Max(0, ci - 6)).Take(20).Select(c => ((int)c).ToString())) : "raw NOT FOUND"));
        }
        Check("commands sorted alphabetically in palette", idx.All(i => i >= 0) && idx.SequenceEqual(idx.OrderBy(i => i)));

        // 4b) USER BUG REPORT: filtering the palette to NO matches used to crash the
        //     TUI (the list's SelectedItem setter threw on an empty result set).
        conpty.Send("zzz");
        await Task.Delay(600);
        CheckAlive(conpty, "process alive after empty palette filter");
        conpty.Send("\x1b");
        await Task.Delay(500);

        // 5) Command sweep: every command launched from the palette must not kill the
        //    TUI (the palette-close crash hit every command, not just /model). Commands
        //    with external side effects (docs/web/tts/voice) are excluded. Dialogs that
        //    a command opens are closed with one Esc; a stray Esc is harmless because
        //    the next "/" resets the app's double-Esc-exit counter.
        foreach (var (cmd, closeKey) in new[]
        {
            ("help", "\x1b"), ("shortcuts", "\x1b"), ("status", "\x1b"),
            ("features", ""), ("clear", ""), ("new", ""), ("retry", ""), ("health", ""),
            ("files", "\x1b"), ("attach", "\x1b"), ("agent", "\x1b"), ("telegram", "\x1b"),
            ("model", "\x1b"), ("modelsetup", "\x1b"),
        })
        {
            conpty.Send("/" + cmd + "\r");
            await Task.Delay(1500);
            var alive1 = !conpty.Exited;
            if (closeKey.Length > 0) { conpty.Send(closeKey); await Task.Delay(700); }
            Check($"/{cmd} runs from palette (no crash)", alive1 && !conpty.Exited);
            if (conpty.Exited) break;   // the rest would only produce noise
        }

        // 5b) The "/" palette still opens after the sweep (modelsetup's dialog closed).
        conpty.Send("/");
        await Task.Delay(800);
        Check("palette still opens after sweep", conpty.Screen.Contains(paletteMarker));
        conpty.Send("\x1b");
        await Task.Delay(400);

        // 5c) USER REQUEST: /agent opens the TOOLS dialog — untranslated preset ids and
        //     tool names (API contract) prove the checklist rendered; Esc closes it.
        conpty.Send("/agent\r");
        await conpty.WaitForNewTextAsync("default-agent", TimeSpan.FromSeconds(15));
        var toolsDlg = conpty.ScreenSinceMark();
        if (!toolsDlg.Contains("default-agent") || !toolsDlg.Contains("FileTool"))
            Console.WriteLine("[diag] tools dialog screen:\n" + (toolsDlg.Length > 2500 ? toolsDlg[^2500..] : toolsDlg));
        Check("tools dialog lists presets and tools", toolsDlg.Contains("default-agent") && toolsDlg.Contains("FileTool"));
        CheckAlive(conpty, "process alive after /agent tools dialog");
        conpty.Send("\x1b");
        await Task.Delay(500);

        // 5d) /telegram opens the interactive panel (no crash); Esc closes it.
        conpty.Send("/telegram\r");
        await Task.Delay(1500);
        CheckAlive(conpty, "process alive after /telegram panel");
        conpty.Send("\x1b");
        await Task.Delay(500);

        // 6) Chat: the user's message appears immediately in the conversation, and the
        //    startup ASCII-art banner collapses (no block chars in the fresh frames).
        conpty.Mark();
        conpty.Send("ciao\r");
        await Task.Delay(1500);
        var afterChat = conpty.ScreenSinceMark();
        Check("user message shown in conversation", afterChat.Contains("ciao") && (afterChat.Contains("you") || afterChat.Contains("❯")));
        Check("ascii art banner collapsed after first message", !afterChat.Contains("█"));

        // 7) Process still alive (no crash).
        CheckAlive(conpty, "process still alive after the interactions");

        // 8) /exit shuts the app down cleanly (it must exit, not hang).
        conpty.Send("/exit\r");
        var exitSw = Stopwatch.StartNew();
        while (!conpty.Exited && exitSw.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(100);
        Check("/exit shuts the app down cleanly", conpty.Exited);

        // Diagnostics only on failure (dump output for debugging).
        if (_fail > 0)
        {
            DumpScreen(conpty, $"FAILED ROUND (passed={_pass}, failed={_fail})");
            var dbg = conpty.Output;
            Console.WriteLine();
            Console.WriteLine($"[diag] captured bytes: {dbg.Length}");
            if (dbg.Length > 0)
                Console.WriteLine("[diag] tail (clean): " + (conpty.Screen.Length > 8000 ? conpty.Screen[^8000..] : conpty.Screen).Replace("\r", "\\r").Replace("\n", "\\n"));
            if (conpty.Exited)
            {
                try { Console.WriteLine($"[diag] exit code: {conpty.ExitCode}"); } catch { }
            }
        }
        return true;
    }
}

/// <summary>Minimal ConPTY host: launches a console app inside a pseudoconsole,
/// lets the test send keystrokes and capture the rendered output.</summary>
internal sealed class ConPty : IDisposable
{
    private readonly IntPtr _hProcess;
    private readonly IntPtr _hInWrite;   // we send keystrokes here
    private readonly IntPtr _hOutRead;   // we read the rendered output here
    private readonly IntPtr _pc;
    private readonly StringBuilder _output = new();
    private long _mark;

    public bool Exited => WaitForSingleObject(_hProcess, 0) == 0; // WAIT_OBJECT_0
    public int? ExitCode
    {
        get
        {
            if (!Exited) return null;
            GetExitCodeProcess(_hProcess, out var code);
            return (int)code;
        }
    }
    public string Output { get { lock (_output) return _output.ToString(); } }
    /// <summary>Output without ANSI sequences — for text checks.</summary>
    public string Screen => Program.StripAnsi(Output);

    public ConPty(string exe, string args)
    {
        if (!CreatePipe(out var inRead, out _hInWrite, IntPtr.Zero, 0)) ThrowWin32("CreatePipe(in)");
        if (!CreatePipe(out _hOutRead, out var outWrite, IntPtr.Zero, 0)) ThrowWin32("CreatePipe(out)");

        var size = new Coord { X = 100, Y = 30 };
        var hr = CreatePseudoConsole(size, inRead, outWrite, 0, out _pc);
        if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        var attrSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        si.LpAttributeList = Marshal.AllocHGlobal(attrSize);
        if (!InitializeProcThreadAttributeList(si.LpAttributeList, 1, 0, ref attrSize)) ThrowWin32("InitAttrList");
        var pc = _pc;
        // IMPORTANT: lpValue = the VALUE of the HPCON handle (not a pointer to it),
        // and CreateProcess requires EXTENDED_STARTUPINFO_PRESENT = 0x00080000 for
        // the attribute list to be processed.
        if (!UpdateProcThreadAttribute(si.LpAttributeList, 0, (IntPtr)0x00020016 /*PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE*/, pc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            ThrowWin32("UpdateAttr");

        var cmdLine = $"\"{exe}\" {args}";
        const uint ExtendedStartupInfoPresent = 0x00080000;
        if (!CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent,
                IntPtr.Zero, null, ref si, out var pi))
            ThrowWin32("CreateProcessW");
        // After CreateProcess: the ConPTY owns the inner ends — close ours.
        CloseHandle(inRead);
        CloseHandle(outWrite);
        CloseHandle(pi.hThread);
        _hProcess = pi.hProcess;

        // Background output reader.
        _ = Task.Run(ReadLoop);
    }

    private void ReadLoop()
    {
        var buf = new byte[8192];
        while (true)
        {
            if (!ReadFile(_hOutRead, buf, buf.Length, out var read, IntPtr.Zero) || read == 0) break;
            lock (_output)
            {
                _output.Append(Encoding.UTF8.GetString(buf, 0, read));
            }
        }
    }

    public void Send(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (!WriteFile(_hInWrite, bytes, bytes.Length, out _, IntPtr.Zero))
            ThrowWin32("WriteFile(in)");
    }

    public string OutputSinceMark()
    {
        lock (_output)
        {
            var s = _output.ToString();
            return _mark >= s.Length ? "" : s[(int)_mark..];
        }
    }

    /// <summary>Output from the marker, without ANSI.</summary>
    public string ScreenSinceMark() => Program.StripAnsi(OutputSinceMark());

    /// <summary>Sets the point from which OutputSinceMark reads (after waits).</summary>
    public void Mark()
    {
        lock (_output) _mark = _output.Length;
    }

    public async Task WaitForText(string text, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (Screen.Contains(text)) return;
            await Task.Delay(100);
        }
    }

    /// <summary>Waits for <paramref name="text"/> to appear in output produced AFTER the
    /// call. The raw ConPTY capture accumulates every frame ever rendered, so a plain
    /// screen.Contains() can match stale rows from a previous dialog — this marks first
    /// and only inspects the fresh output.</summary>
    public async Task WaitForNewTextAsync(string text, TimeSpan timeout)
    {
        Mark();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (ScreenSinceMark().Contains(text)) return;
            await Task.Delay(100);
        }
    }

    public void Dispose()
    {
        TerminateProcess(_hProcess, 1);
        CloseHandle(_hProcess);
        ClosePseudoConsole(_pc);
        CloseHandle(_hInWrite);
        CloseHandle(_hOutRead);
    }

    private static void ThrowWin32(string what) => throw new InvalidOperationException($"{what} failed: {Marshal.GetLastWin32Error()}");

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr LpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, uint nSize);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, [In] ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}
