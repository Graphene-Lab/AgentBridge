using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AgentBridge.Resources;
using AIOrchestrator;

// ═══════════════════════════════════════════════════════════════════════
//  WindowMinimizer — F12: get the console out of the way, and back
//
//  The TUI owns no window: it draws into the host terminal. "Hiding" it therefore means
//  acting on that terminal window, and every desktop does it differently:
//
//    Windows  the console window is hidden and a notification-area icon (left click) plus
//             the Ctrl+Alt+F12 global hotkey bring it back. Those are the only ways back,
//             because a hidden console receives no keyboard input at all.
//    macOS    the terminal app is hidden through System Events (or its front window is
//             minimized) and the Dock icon brings it back — the OS owns the restore gesture.
//    Linux    the window is marked hidden through wmctrl or minimized through xdotool and
//             the taskbar entry brings it back.
//
//  Where the platform cannot do it (no terminal window to act on, missing helper tool,
//  permissions refused) Hide returns a message and NOTHING changes: the app is never left
//  unreachable. Hiding is a convenience, never a state the console cannot leave.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Hides the terminal window the TUI runs in and brings it back (F12).</summary>
internal interface IWindowMinimizer
{
    /// <summary>
    /// True when the OS surface itself brings the window back (Dock, taskbar), so the TUI
    /// keeps no "hidden" state and F12 simply hides again. False on Windows, where the tray
    /// icon callback — not the keyboard — is the way back.
    /// </summary>
    bool RestoredByOs { get; }

    /// <summary>
    /// Hides the window. <paramref name="restoreRequested"/> is invoked (from any thread) when
    /// the user asks to come back — only platforms with a restore surface use it.
    /// Returns null on success, or a user-facing message when the window cannot be hidden.
    /// </summary>
    string? Hide(Action restoreRequested);

    /// <summary>Brings a hidden window back. No-op when it is not hidden.</summary>
    void Restore();

    /// <summary>Releases the OS resources (tray icon, hotkey, helper thread).</summary>
    void Shutdown();
}

internal static class WindowMinimizer
{
    /// <summary>Picks the implementation for the current desktop.</summary>
    public static IWindowMinimizer Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsTrayMinimizer();
        if (OperatingSystem.IsMacOS()) return new MacWindowMinimizer();
        if (OperatingSystem.IsLinux()) return new LinuxWindowMinimizer();
        return new UnsupportedWindowMinimizer();
    }
}

/// <summary>Platforms without a desktop window (or without a way to act on it).</summary>
internal sealed class UnsupportedWindowMinimizer : IWindowMinimizer
{
    public bool RestoredByOs => true;

    public string? Hide(Action restoreRequested) => Dictionary.NoteHideUnsupported;

    public void Restore() { }

    public void Shutdown() { }
}

// ───────────────────────────────────────────────────────────────────────
//  Windows: hide the console window, notification-area icon to come back
// ───────────────────────────────────────────────────────────────────────
[SupportedOSPlatform("windows")]
internal sealed class WindowsTrayMinimizer : IWindowMinimizer
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 20;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int TrayIconId = 1;
    private const int HotkeyId = 1;

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _sync = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hookWindow;
    private IntPtr _consoleWindow;
    private IntPtr _icon;
    private volatile bool _trayAdded;
    private volatile bool _hotkeyAdded;
    private Action? _restoreRequested;

    public bool RestoredByOs => false;

    public string? Hide(Action restoreRequested)
    {
        _restoreRequested = restoreRequested;

        lock (_sync)
        {
            if (_thread == null)
            {
                _thread = new Thread(RunTrayThread)
                {
                    IsBackground = true,
                    Name = "AgentBridge tray icon"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }

        // The icon (or the hotkey) must exist BEFORE the window disappears: without either
        // there would be no way back into a hidden console.
        if (!_ready.Wait(TimeSpan.FromSeconds(5)) || (!_trayAdded && !_hotkeyAdded))
        {
            Log.LogStep($"Window hide refused: no tray icon and no hotkey (tray={_trayAdded}, hotkey={_hotkeyAdded})");
            return Dictionary.NoteHideUnsupported;
        }

        _consoleWindow = Native.FindConsoleWindow();
        if (_consoleWindow == IntPtr.Zero)
        {
            Log.LogStep("Window hide refused: no terminal window to hide (headless or unknown host)");
            return Dictionary.NoteHideUnsupported;
        }

        if (!Native.ShowWindow(_consoleWindow, Native.SW_HIDE))
        {
            Log.LogStep("Window hide failed: ShowWindow(SW_HIDE) returned false");
            return Dictionary.NoteHideUnsupported;
        }

        Log.LogStep("TUI window hidden (tray icon restores it)", monitor: true);
        return null;
    }

    public void Restore()
    {
        var hwnd = _consoleWindow;
        if (hwnd == IntPtr.Zero) return;
        Native.ShowWindow(hwnd, Native.SW_SHOW);
        Native.ShowWindow(hwnd, Native.SW_RESTORE);
        Native.SetForegroundWindow(hwnd);
        Log.LogStep("TUI window restored", monitor: true);
    }

    // Owns the tray icon and the global hotkey: a message-only window plus a plain message
    // loop. Messages are inspected before dispatch — the predefined window class would
    // swallow every WM_APP, so the callback is read straight from the queue.
    private void RunTrayThread()
    {
        try
        {
            _threadId = Native.GetCurrentThreadId();
            _hookWindow = Native.CreateWindowEx(0, "STATIC", "AgentBridge tray", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);

            if (_hookWindow == IntPtr.Zero)
            {
                Log.LogStep("Tray icon unavailable: could not create the message window");
                _ready.Set();
                return;
            }

            _icon = Native.LoadAppIcon();
            var data = Native.NOTIFYICONDATA.Create(_hookWindow, TrayIconId, WM_TRAYICON, _icon, "AgentBridge");
            _trayAdded = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
            if (!_trayAdded) Log.LogStep("Tray icon unavailable: Shell_NotifyIcon(NIM_ADD) failed");

            _hotkeyAdded = RegisterHotkey();

            _ready.Set();

            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == _hookWindow && msg.message == WM_TRAYICON)
                {
                    var evt = (int)(msg.lParam.ToInt64() & 0xFFFF);
                    if (evt is WM_LBUTTONUP or WM_LBUTTONDBLCLK) InvokeRestore();
                    continue;   // right click: nothing (no menu yet) — keep the icon silent
                }

                if (msg.hwnd == _hookWindow && msg.message == WM_HOTKEY)
                {
                    InvokeRestore();
                    continue;
                }

                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Log.LogStep($"Tray icon thread failed: {ex.Message}");
            _ready.Set();
        }
        finally
        {
            Cleanup();
        }
    }

    // Tries the documented restore combination and two fallbacks: another app may already own
    // Ctrl+Alt+F12, and a failed hotkey is only a lost convenience — the tray icon is the
    // primary way back.
    private bool RegisterHotkey()
    {
        var candidates = new (string Name, uint Modifiers)[]
        {
            ("Ctrl+Alt+F12", Native.MOD_CONTROL | Native.MOD_ALT),
            ("Ctrl+Shift+F12", Native.MOD_CONTROL | Native.MOD_SHIFT),
            ("Alt+F12", Native.MOD_ALT),
        };
        foreach (var (name, modifiers) in candidates)
        {
            if (Native.RegisterHotKey(_hookWindow, HotkeyId, modifiers | Native.MOD_NOREPEAT, Native.VK_F12))
            {
                Log.LogStep($"Global restore hotkey registered: {name}");
                return true;
            }
            Log.LogStep($"Global hotkey {name} unavailable (win32 error {Marshal.GetLastWin32Error()})");
        }
        return false;
    }

    private void InvokeRestore()
    {
        try { _restoreRequested?.Invoke(); }
        catch (Exception ex) { Log.LogStep($"Tray restore callback failed: {ex.Message}"); }
    }

    private void Cleanup()
    {
        try
        {
            if (_trayAdded && _hookWindow != IntPtr.Zero)
            {
                var data = Native.NOTIFYICONDATA.Create(_hookWindow, TrayIconId, WM_TRAYICON, _icon, "AgentBridge");
                Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            }
            if (_hotkeyAdded && _hookWindow != IntPtr.Zero)
                Native.UnregisterHotKey(_hookWindow, HotkeyId);
            if (_icon != IntPtr.Zero) Native.DestroyIcon(_icon);
            if (_hookWindow != IntPtr.Zero) Native.DestroyWindow(_hookWindow);
        }
        catch { /* process is going away anyway */ }
        finally
        {
            _hookWindow = IntPtr.Zero;
            _trayAdded = false;
            _hotkeyAdded = false;
        }
    }

    /// <summary>Stops the tray thread (called when the TUI is disposed).</summary>
    public void Shutdown()
    {
        try
        {
            if (_hookWindow != IntPtr.Zero)
                Native.PostMessage(_hookWindow, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            else if (_threadId != 0)
                Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static class Native
    {
        public const int NIM_ADD = 0;
        public const int NIM_DELETE = 2;
        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;
        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTSIZE = 0x00000040;

        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;
        public const uint VK_F12 = 0x7B;
        public const int WM_CLOSE = 0x0010;
        public const int WM_QUIT = 0x0012;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;

            public static NOTIFYICONDATA Create(IntPtr hwnd, int id, int callback, IntPtr icon, string tip) => new()
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hwnd,
                uID = id,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = callback,
                hIcon = icon,
                szTip = tip,
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public int ptX;
            public int ptY;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

        /// <summary>
        /// The window to hide. A classic console (conhost, or a debugger-hosted console) has a
        /// real window handle. Windows Terminal and VS-hosted terminals run the app against a
        /// pseudoconsole instead, so the terminal window is located by its title — the tab
        /// title is what <see cref="Console.Title"/> reports.
        /// </summary>
        public static IntPtr FindConsoleWindow()
        {
            var hwnd = GetConsoleWindow();
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd)) return hwnd;

            string title;
            try { title = Console.Title; } catch { return IntPtr.Zero; }
            if (title.Length == 0) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            var className = new StringBuilder(256);
            EnumWindows((candidate, _) =>
            {
                if (!IsWindowVisible(candidate) || GetWindowTextLength(candidate) == 0) return true;
                var text = new StringBuilder(512);
                GetWindowText(candidate, text, text.Capacity);
                if (!string.Equals(text.ToString(), title, StringComparison.Ordinal)) return true;

                className.Clear();
                GetClassName(candidate, className, className.Capacity);
                var cls = className.ToString();
                if (cls is "CASCADIA_HOSTING_WINDOW_CLASS" or "ConsoleWindowClass")
                {
                    best = candidate;
                    return false;   // exact terminal host: stop looking
                }
                if (best == IntPtr.Zero) best = candidate;
                return true;
            }, IntPtr.Zero);
            return best;
        }

        /// <summary>
        /// The application icon embedded in the executable (csproj ApplicationIcon,
        /// branding\agentbridge.ico); a generic icon when extraction fails.
        /// </summary>
        public static IntPtr LoadAppIcon()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var icon = LoadImage(IntPtr.Zero, exe, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                    if (icon != IntPtr.Zero) return icon;
                }
            }
            catch { }
            return LoadIcon(IntPtr.Zero, (IntPtr)32512);   // IDI_APPLICATION
        }
    }
}

// ───────────────────────────────────────────────────────────────────────
//  macOS: hide the terminal app, the Dock brings it back
// ───────────────────────────────────────────────────────────────────────
internal sealed class MacWindowMinimizer : IWindowMinimizer
{
    // The terminal hosting the app, from TERM_PROGRAM (the only reliable hint).
    private static readonly string[] Candidates = BuildCandidates();

    private static string[] BuildCandidates()
    {
        var program = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";
        var names = new List<string>();
        switch (program.ToLowerInvariant())
        {
            case "iterm.app": names.Add("iTerm2"); break;
            case "vscode": names.Add("Code"); break;
            case "apple_terminal": names.Add("Terminal"); break;
            case "warpterminal": names.Add("Warp"); break;
            case "ghostty": names.Add("Ghostty"); break;
            case "kitty": names.Add("kitty"); break;
            case "wezterm": names.Add("WezTerm"); break;
        }
        names.Add("Terminal");
        names.Add("iTerm2");
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // The Dock (and the app switcher) restore a hidden app: the OS owns the gesture.
    public bool RestoredByOs => true;

    public string? Hide(Action restoreRequested)
    {
        // Hiding the whole app keeps its Dock icon, so a single click comes back; minimizing
        // the front window is the fallback when System Events automation is not permitted.
        foreach (var app in Candidates)
        {
            if (RunScript($"tell application \"System Events\" to set visible of process \"{app}\" to false"))
                return null;
            if (RunScript($"tell application \"{app}\" to set miniaturized of front window to true"))
                return null;
        }
        return Dictionary.NoteHideUnsupported;
    }

    public void Restore()
    {
        foreach (var app in Candidates)
        {
            if (RunScript($"tell application \"System Events\" to set visible of process \"{app}\" to true"))
            {
                RunScript($"tell application \"{app}\" to activate");
                return;
            }
        }
    }

    private static bool RunScript(string script) => Shell.Run("osascript", "-e", script);

    // Nothing to release: the OS owns both the hide and the restore surface.
    public void Shutdown() { }
}

// ───────────────────────────────────────────────────────────────────────
//  Linux: hide or minimize the window, the taskbar entry brings it back
// ───────────────────────────────────────────────────────────────────────
internal sealed class LinuxWindowMinimizer : IWindowMinimizer
{
    public bool RestoredByOs => true;

    public string? Hide(Action restoreRequested)
    {
        // "hidden" leaves the window in the taskbar (unmapping would remove the only way back);
        // minimizing is the fallback when wmctrl is absent (X11 only — Wayland has neither).
        if (Shell.Run("wmctrl", "-r", ":ACTIVE:", "-b", "add,hidden")) return null;
        if (Shell.Run("xdotool", "getactivewindow", "windowminimize")) return null;
        return Dictionary.NoteHideUnsupported;
    }

    public void Restore()
    {
        if (Shell.Run("wmctrl", "-r", ":ACTIVE:", "-b", "remove,hidden")) return;
        Shell.Run("xdotool", "getactivewindow", "windowmap");
    }

    // Nothing to release: the window manager owns both surfaces.
    public void Shutdown() { }
}

/// <summary>Runs a desktop helper tool without a shell, suppressing every failure.</summary>
internal static class Shell
{
    /// <summary>True when the tool ran and exited with code 0; false when it is missing,
    /// refused (permissions) or failed — callers then try the next mechanism.</summary>
    public static bool Run(string exe, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process == null) return false;
            // Drain both pipes: a full buffer would block the child forever.
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.LogStep($"Desktop helper '{exe}' unavailable: {ex.Message}");
            return false;
        }
    }
}
