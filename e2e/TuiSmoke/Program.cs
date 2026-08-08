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
    private static readonly System.Text.RegularExpressions.Regex AnsiRe = new(
        "\x1b\\[[0-9;?]*[ -/]*[@-~]|\x1b\\][^\\x07]*\x07|\x1b[()][A-Za-z0-9]|\x1b[=>]",
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
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            _pass = _fail = 0;
            if (attempt > 1)
            {
                Console.WriteLine("  (UI did not render — relaunching once)");
                await Task.Delay(1000);   // let ConDrv breathe
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

        // 1) The UI renders: AGENT logo + input line.
        await conpty.WaitForText("> ", TimeSpan.FromSeconds(20));
        var out0 = conpty.Screen;
        var uiRendered = out0.Contains("> ");
        Check("logo AGENT rendered (block chars)", out0.Contains("█"));
        Check("input line rendered", out0.Contains("> "));
        Check("status bar shows server url", out0.Contains(baseUrl.Replace("http://", "")));

        // If the child did not attach (headless), no point continuing: the retry relaunches.
        if (!uiRendered) return false;

        // 2) "/model" with no arguments opens the in-layout picker.
        conpty.Send("/model\r");
        await conpty.WaitForText("Switch LLM provider", TimeSpan.FromSeconds(20));
        Check("/model picker opens", conpty.Screen.Contains("Switch LLM provider"));
        Check("picker shows Esc hint", conpty.Screen.Contains("Esc cancel"));

        // 3) Esc closes the picker with a clean screen (the input becomes visible again).
        conpty.Send("\x1b");
        conpty.Mark();
        await Task.Delay(800);
        var afterEsc = conpty.ScreenSinceMark();
        Check("Esc closes picker (input line back)", afterEsc.Contains("> "));
        Check("no residue after Esc", !afterEsc.Contains("Switch LLM provider"));

        // 4) Chat: the user's message appears immediately in the conversation.
        conpty.Mark();
        conpty.Send("ciao\r");
        await Task.Delay(1500);
        var afterChat = conpty.ScreenSinceMark();
        Check("user message shown in conversation", afterChat.Contains("ciao") && (afterChat.Contains("you") || afterChat.Contains("❯")));

        // 5) Process still alive (no crash).
        Check("process still alive after the interactions", !conpty.Exited);

        // Diagnostics only on failure (dump output for debugging).
        if (_fail > 0)
        {
            var dbg = conpty.Output;
            Console.WriteLine();
            Console.WriteLine($"[diag] captured bytes: {dbg.Length}");
            if (dbg.Length > 0)
                Console.WriteLine("[diag] tail (clean): " + (conpty.Screen.Length > 400 ? conpty.Screen[^400..] : conpty.Screen).Replace("\r", "\\r").Replace("\n", "\\n"));
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
