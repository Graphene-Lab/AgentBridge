// ConsolePuppetInjector v2 — Uses SendInput (modern Windows API) to inject
// keyboard events into ANY window, including Windows Terminal / conhost.
//
// Usage:
//   ConsolePuppetInjector.exe --pid 10096 --key f10
//   ConsolePuppetInjector.exe --pid 10096 --key alt+c
//   ConsolePuppetInjector.exe --pid 10096 --text "hello"
//   ConsolePuppetInjector.exe --title "AGENT - Console chat IA" --key enter

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ConsolePuppetInjector
{
    class Program
    {
        // ── Win32 API: SendInput ──

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public KEYBDINPUT ki;
            public static int Size => Marshal.SizeOf<INPUT>();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll")]
        static extern bool FreeConsole();

        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint KEYEVENTF_SCANCODE = 0x0008;

        // Virtual key codes
        static class VK
        {
            public const byte RETURN = 0x0D;
            public const byte ESC = 0x1B;
            public const byte TAB = 0x09;
            public const byte BACKSPACE = 0x08;
            public const byte DELETE = 0x2E;
            public const byte UP = 0x26;
            public const byte DOWN = 0x28;
            public const byte LEFT = 0x25;
            public const byte RIGHT = 0x27;
            public const byte PAGEUP = 0x21;
            public const byte PAGEDOWN = 0x22;
            public const byte HOME = 0x24;
            public const byte END = 0x23;
            public const byte F1 = 0x70;
            public const byte F10 = 0x79;
            public const byte A = 0x41;
            public const byte B = 0x42;
            public const byte C = 0x43;
            public const byte D = 0x44;
            public const byte E = 0x45;
            public const byte F = 0x46;
            public const byte G = 0x47;
            public const byte H = 0x48;
            public const byte I = 0x49;
            public const byte J = 0x4A;
            public const byte K = 0x4B;
            public const byte L = 0x4C;
            public const byte M = 0x4D;
            public const byte N = 0x4E;
            public const byte O = 0x4F;
            public const byte P = 0x50;
            public const byte Q = 0x51;
            public const byte R = 0x52;
            public const byte S = 0x53;
            public const byte T = 0x54;
            public const byte U = 0x55;
            public const byte V = 0x56;
            public const byte W = 0x57;
            public const byte X = 0x58;
            public const byte Y = 0x59;
            public const byte Z = 0x5A;
        }

        // Control key states
        static class ControlKeyState
        {
            public const uint LEFT_ALT_PRESSED = 0x0002;
            public const uint RIGHT_ALT_PRESSED = 0x0001;
            public const uint LEFT_CTRL_PRESSED = 0x0008;
            public const uint RIGHT_CTRL_PRESSED = 0x0004;
            public const uint SHIFT_PRESSED = 0x0010;
        }

        // ── Main ──

        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }

            int pid = 0;
            string? title = null;
            string? keyName = null;
            string? text = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--pid":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out pid))
                            i++;
                        else { PrintUsage(); return 1; }
                        break;
                    case "--title":
                        if (i + 1 < args.Length)
                        {
                            title = args[i + 1];
                            i++;
                        }
                        else { PrintUsage(); return 1; }
                        break;
                    case "--key":
                        if (i + 1 < args.Length)
                        {
                            keyName = args[i + 1].ToLowerInvariant();
                            i++;
                        }
                        else { PrintUsage(); return 1; }
                        break;
                    case "--text":
                        if (i + 1 < args.Length)
                        {
                            text = args[i + 1];
                            i++;
                        }
                        else { PrintUsage(); return 1; }
                        break;
                    default:
                        PrintUsage();
                        return 1;
                }
            }

            if ((pid == 0 && title == null) || (keyName == null && text == null))
            {
                Console.Error.WriteLine("Error: --pid or --title required, plus --key or --text");
                PrintUsage();
                return 1;
            }

            // Try to find the terminal window
            IntPtr hWnd = IntPtr.Zero;
            if (title != null)
            {
                // Search all top-level windows for matching title
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (proc.MainWindowTitle.Contains(title, StringComparison.OrdinalIgnoreCase))
                        {
                            hWnd = proc.MainWindowHandle;
                            Console.Error.WriteLine($"Found window '{proc.MainWindowTitle}' (hWnd={hWnd:X})");
                            break;
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
            else if (pid > 0)
            {
                // Try to get the console window handle for this PID
                AttachConsole((uint)pid);
                try
                {
                    var handle = GetConsoleWindow();
                    if (handle != IntPtr.Zero)
                    {
                        hWnd = handle;
                        Console.Error.WriteLine($"Got console window for PID {pid} (hWnd={hWnd:X})");
                    }
                    else
                    {
                        Console.Error.WriteLine($"Warning: No console window found for PID {pid}, will use global SendInput");
                    }
                }
                finally { FreeConsole(); }
            }

            // Bring window to foreground if found
            if (hWnd != IntPtr.Zero && hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, 9); // SW_RESTORE
                Thread.Sleep(50);
                SetForegroundWindow(hWnd);
                Thread.Sleep(100);
            }

            try
            {
                if (text != null)
                {
                    // Type each character
                    foreach (char ch in text)
                    {
                        InjectChar(ch, false);
                        InjectChar(ch, true);
                    }
                }
                else if (keyName != null)
                {
                    var vk = ResolveKey(keyName);
                    if (vk == null)
                    {
                        Console.Error.WriteLine($"Error: unknown key '{keyName}'");
                        return 1;
                    }
                    InjectKey(vk.Value.code, vk.Value.state, false);
                    InjectKey(vk.Value.code, vk.Value.state, true);
                }

                Console.WriteLine("OK");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        static void InjectChar(char ch, bool keyUp)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].ki.wVk = 0;
            inputs[0].ki.wScan = (ushort)ch;
            inputs[0].ki.dwFlags = KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0);
            inputs[0].ki.time = 0;
            inputs[0].ki.dwExtraInfo = IntPtr.Zero;
            SendInput(1, inputs, INPUT.Size);
        }

        static void InjectKey(byte vkCode, uint controlState, bool keyUp)
        {
            INPUT[] inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].ki.wVk = vkCode;
            inputs[0].ki.wScan = 0;
            inputs[0].ki.dwFlags = (keyUp ? KEYEVENTF_KEYUP : 0);
            inputs[0].ki.time = 0;
            inputs[0].ki.dwExtraInfo = IntPtr.Zero;
            SendInput(1, inputs, INPUT.Size);
        }

        static (byte code, uint state)? ResolveKey(string name)
        {
            // Ctrl combinations
            if (name.StartsWith("ctrl+"))
            {
                char baseKey = char.ToUpperInvariant(name[5]);
                byte vk = (byte)(baseKey >= 'A' && baseKey <= 'Z' ? baseKey : 0);
                if (vk == 0) return null;
                return (vk, ControlKeyState.LEFT_CTRL_PRESSED);
            }

            // Alt combinations
            if (name.StartsWith("alt+"))
            {
                char baseKey = char.ToUpperInvariant(name[4]);
                byte vk = (byte)(baseKey >= 'A' && baseKey <= 'Z' ? baseKey : 0);
                if (vk == 0) return null;
                return (vk, ControlKeyState.LEFT_ALT_PRESSED);
            }

            // Shift combinations
            if (name.StartsWith("shift+"))
            {
                char baseKey = char.ToUpperInvariant(name[6]);
                byte vk = (byte)(baseKey >= 'A' && baseKey <= 'Z' ? baseKey : 0);
                if (vk == 0) return null;
                return (vk, ControlKeyState.SHIFT_PRESSED);
            }

            // Single keys
            return name switch
            {
                "enter" => (VK.RETURN, 0),
                "escape" or "esc" => (VK.ESC, 0),
                "tab" => (VK.TAB, 0),
                "backspace" => (VK.BACKSPACE, 0),
                "delete" => (VK.DELETE, 0),
                "up" or "cursorup" => (VK.UP, 0),
                "down" or "cursordown" => (VK.DOWN, 0),
                "left" or "cursorleft" => (VK.LEFT, 0),
                "right" or "cursorright" => (VK.RIGHT, 0),
                "pageup" => (VK.PAGEUP, 0),
                "pagedown" => (VK.PAGEDOWN, 0),
                "home" => (VK.HOME, 0),
                "end" => (VK.END, 0),
                "f1" => (VK.F1, 0),
                "f10" => (VK.F10, 0),
                "a" => (VK.A, 0),
                "b" => (VK.B, 0),
                "c" => (VK.C, 0),
                "d" => (VK.D, 0),
                "e" => (VK.E, 0),
                "f" => (VK.F, 0),
                "g" => (VK.G, 0),
                "h" => (VK.H, 0),
                "i" => (VK.I, 0),
                "j" => (VK.J, 0),
                "k" => (VK.K, 0),
                "l" => (VK.L, 0),
                "m" => (VK.M, 0),
                "n" => (VK.N, 0),
                "o" => (VK.O, 0),
                "p" => (VK.P, 0),
                "q" => (VK.Q, 0),
                "r" => (VK.R, 0),
                "s" => (VK.S, 0),
                "t" => (VK.T, 0),
                "u" => (VK.U, 0),
                "v" => (VK.V, 0),
                "w" => (VK.W, 0),
                "x" => (VK.X, 0),
                "y" => (VK.Y, 0),
                "z" => (VK.Z, 0),
                _ => null,
            };
        }

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        static void PrintUsage()
        {
            Console.Error.WriteLine("ConsolePuppetInjector v2 — Inject keyboard events via SendInput API");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Usage:");
            Console.Error.WriteLine("  ConsolePuppetInjector.exe --pid <PID> --key <keyname>");
            Console.Error.WriteLine("  ConsolePuppetInjector.exe --title \"window title\" --key <keyname>");
            Console.Error.WriteLine("  ConsolePuppetInjector.exe --pid <PID> --text <text>");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Keys: enter, escape, tab, backspace, delete, up, down, left, right,");
            Console.Error.WriteLine("      pageup, pagedown, home, end, f1-f12, a-z,");
            Console.Error.WriteLine("      ctrl+a, ctrl+c, ctrl+q, alt+c, shift+a");
        }
    }
}
