using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Spectre.Console;

/// <summary>
/// The Qwen-Code-style terminal UI of AgentBridge: a plain HTTP client of the
/// server (chat, slash commands, voice/TTS/model/files) with keyboard + mouse
/// support. See README.md → "Terminal UI".
/// </summary>
public static class ConsoleTui
{
    /// <summary>Runs the terminal UI against the server at <paramref name="serverUrl"/> until the user exits.</summary>
    public static Task RunAsync(string serverUrl, string? hostError = null)
        => new Tui(serverUrl, hostError).RunAsync();

    private sealed class Tui
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
        private readonly string _serverUrl;
        private readonly string? _hostError;
        private readonly object _lock = new();

        private readonly List<Entry> _history = new();
        private readonly List<string> _promptHistory = new();
        private readonly List<FileRef> _files = new();
        private readonly List<string> _attached = new();

        private bool _exit;
        private bool _resized;
        private bool _compact;
        private int _height = 24, _width = 80;
        private int _logoLines = 6, _statusLine, _sepLine, _historyTop, _inputLine, _historyHeight, _paletteHeight;

        private string _input = "";
        private int _cursor;
        private int _escCount;
        private int _scrollFromBottom;
        private int _histIndex = -1;
        private string _histDraft = "";
        private Palette? _palette;

        private bool _chatRunning;
        private CancellationTokenSource? _chatCts;
        private Entry? _pending;
        private string _lastPrompt = "";
        private bool _lastFailed;

        private string _provider = "";
        private string _modelName = "";
        private int _contextWindow;
        private int _historyTokens;
        private string _sessionId = "";
        private string _agentSet = "default-agent";
        private readonly Dictionary<string, bool> _features = new();
        private bool _ttsAvailable, _voiceAvailable;
        private string _ttsDetail = "", _voiceDetail = "";
        private bool _connected;
        private string _statusNote = "";

        private Channel<UiEvent> _events = null!;
        private Thread? _inputThread;
        private IntPtr _hIn;
        private PickerState? _picker;
        private bool _mouseConfirm;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private sealed class Entry
        {
            public required string Role;
            public required string Text;
            public bool Error;
        }

        private sealed class FileRef
        {
            public required string Id;
            public required string FileName;
            public string Status = "";
            public bool Attached;
        }

        private sealed class Palette
        {
            public enum Kind { Commands, Files }
            public required Kind Type;
            public string Filter = "";
            public int Selected;
            public List<CliCommand> Commands = new();
            public List<FileRef> Files = new();
        }

        private sealed record CliCommand(
            string Name,
            string Args,
            string Help,
            Func<Tui, string, Task> Run,
            string[]? Aliases = null);

        private static readonly List<CliCommand> Commands = new()
        {
            new("help", "", "Show help: commands, shortcuts, API endpoints, online docs", (t, _) => t.ShowHelpAsync(), new[] { "/?" }),
            new("docs", "", "Open the online documentation in your browser", (t, _) => t.OpenDocsAsync()),
            new("model", "[name]", "Switch the LLM provider (menu when no name given)", (t, a) => t.SwitchModelAsync(a)),
            new("agent", "[name]", "Switch the agent set (default/web/search/word/spreadsheet/email/multi)", (t, a) => t.SwitchAgentAsync(a)),
            new("voice", "[lang]", "Dictate from the server microphone into the input (default = system language)", (t, a) => t.VoiceAsync(a)),
            new("tts", "[text]", "Speak the last agent reply (or the given text) via Kokoro TTS", (t, a) => t.TtsAsync(a)),
            new("features", "[name] [on|off]", "Show or toggle session feature flags (voice, tts, ...)", (t, a) => t.FeaturesAsync(a)),
            new("new", "", "Start a new session (fresh conversation, new id)", (t, _) => t.NewSessionAsync(), new[] { "/reset" }),
            new("clear", "", "Reset the current session history (keeps the session)", (t, _) => t.ClearHistoryAsync()),
            new("status", "", "Show session state and platform capabilities", (t, _) => t.ShowStatusAsync()),
            new("files", "add <path>|rm <id>|list", "Upload+attach a file, delete one, or list uploads", (t, a) => t.FilesAsync(a)),
            new("attach", "[id]", "Toggle a file attachment for the chat (menu when no id)", (t, a) => t.AttachAsync(a)),
            new("shortcuts", "", "Show the keyboard shortcuts overlay", (t, _) => t.ShowShortcutsAsync(), new[] { "/keys" }),
            new("health", "", "Ping the server and report latency", (t, _) => t.HealthAsync()),
            new("retry", "", "Resend the last prompt (also Ctrl+Y)", (t, _) => t.RetryAsync()),
            new("exit", "", "Exit the terminal UI (Ctrl+C twice, Ctrl+D)", (t, _) => t.ExitAsync(), new[] { "/quit" }),
        };

        private static readonly string[] AgentSets = { "default-agent", "web-agent", "search-agent", "research-agent", "word-agent", "spreadsheet-agent", "email-agent", "multi-agent" };

        private const uint SndAsync = 0x0001;
        private const uint SndFilename = 0x00020000;
        [DllImport("winmm.dll", SetLastError = true)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        // ── Terminal input (keys + mouse) ──
        // Windows uses the native console API (ReadConsoleInput): precise events for
        // keys, clicks, wheel and resize — like the Qwen Code TUI. QuickEdit
        // is disabled, otherwise mouse selection "steals" the clicks.
        // On Linux/macOS it falls back to Console.ReadKey (keyboard only).
        private enum UiKind { Key, Mouse, Resize, Quit }
        private enum MouseAction { None, LeftPress, DoubleClick, WheelUp, WheelDown }
        private readonly record struct UiEvent(UiKind Kind, ConsoleKeyInfo? Key = null, int X = 0, int Y = 0, MouseAction Action = MouseAction.None);

        private sealed class PickerState
        {
            public required string Title;
            public required List<string> Items;
            public int Selected;
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        private struct InputRecord
        {
            [FieldOffset(0)] public ushort EventType;
            [FieldOffset(4)] public KeyEventRecord KeyEvent;
            [FieldOffset(4)] public MouseEventRecord MouseEvent;
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
        private struct KeyEventRecord
        {
            [FieldOffset(0)] public int KeyDown;         // BOOL
            [FieldOffset(4)] public ushort RepeatCount;
            [FieldOffset(6)] public ushort VirtualKeyCode;
            [FieldOffset(8)] public ushort VirtualScanCode;
            [FieldOffset(10)] public char UnicodeChar;
            [FieldOffset(12)] public uint ControlKeyState;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct MouseEventRecord
        {
            [FieldOffset(0)] public short X;
            [FieldOffset(2)] public short Y;
            [FieldOffset(4)] public uint ButtonState;
            [FieldOffset(8)] public uint ControlKeyState;
            [FieldOffset(12)] public uint EventFlags;
        }

        private const ushort KeyEventType = 0x0001;
        private const ushort MouseEventType = 0x0002;
        private const ushort WindowBufferSizeEventType = 0x0004;
        private const uint MouseMovedFlag = 0x0001;
        private const uint DoubleClickFlag = 0x0002;
        private const uint MouseWheeledFlag = 0x0004;
        private const uint LeftButtonFlag = 0x0001;
        private const uint EnableExtendedFlags = 0x0080;
        private const uint EnableQuickEditMode = 0x0040;
        private const uint EnableMouseInputMode = 0x0010;
        private const uint EnableWindowInputMode = 0x0008;
        private const uint LeftAltFlag = 0x0002;
        private const uint RightAltFlag = 0x0001;
        private const uint LeftCtrlFlag = 0x0008;
        private const uint RightCtrlFlag = 0x0004;
        private const uint ShiftFlag = 0x0010;
        private const int StdInputHandle = -10;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadConsoleInput(IntPtr hConsoleInput, out InputRecord lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

        private static readonly string[] LogoLines =
        {
            " █████╗  ██████╗ ███████╗███╗   ██╗████████╗",
            "██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝",
            "███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║",
            "██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║",
            "██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║",
            "╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝",
        };

        public Tui(string serverUrl, string? hostError)
        {
            _serverUrl = serverUrl;
            _hostError = hostError;
        }

        public async Task RunAsync()
        {
            var oldEncoding = Console.OutputEncoding;
            var oldCtrlC = Console.TreatControlCAsInput;
            Console.OutputEncoding = Encoding.UTF8;
            Console.TreatControlCAsInput = true;
            EnterAltBuffer();
            StartInput();
            try
            {
                _http.BaseAddress = new Uri(_serverUrl);
                await SplashAsync();
                FullRedraw();
                await KeyLoopAsync();
            }
            finally
            {
                _chatCts?.Cancel();
                _events?.Writer.TryComplete();
                CursorShow();
                ExitAltBuffer();
                // Last message before exit (non‑UI)
                AnsiConsole.Write(new Markup("\n[grey]agent session closed.[/]\n"));
                Console.TreatControlCAsInput = oldCtrlC;
                Console.OutputEncoding = oldEncoding;
            }
        }

        // ── Input layer: event queue (keys + mouse) ──
        private void StartInput()
        {
            _events = Channel.CreateUnbounded<UiEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            if (OperatingSystem.IsWindows())
            {
                _hIn = GetStdHandle(StdInputHandle);
                if (_hIn != IntPtr.Zero && _hIn != new IntPtr(-1) && GetConsoleMode(_hIn, out var mode))
                {
                    // QuickEdit off: selecting text with the mouse must not steal the clicks.
                    mode &= ~EnableQuickEditMode;
                    mode |= EnableExtendedFlags | EnableMouseInputMode | EnableWindowInputMode;
                    SetConsoleMode(_hIn, mode);
                    _inputThread = new Thread(WinInputLoop) { IsBackground = true };
                    _inputThread.Start();
                    return;
                }
            }
            // Keyboard fallback (Linux/macOS or non-standard console): no mouse.
            _inputThread = new Thread(KeyInputLoop) { IsBackground = true };
            _inputThread.Start();
        }

        private void WinInputLoop()
        {
            var rec = default(InputRecord);
            while (!_exit)
            {
                if (!ReadConsoleInput(_hIn, out rec, 1, out _)) break; // console closed
                switch (rec.EventType)
                {
                    case KeyEventType when rec.KeyEvent.KeyDown != 0:
                        EnqueueKey(rec.KeyEvent);
                        break;
                    case MouseEventType:
                        EnqueueMouse(rec.MouseEvent);
                        break;
                    case WindowBufferSizeEventType:
                        _events.Writer.TryWrite(new UiEvent(UiKind.Resize));
                        break;
                }
            }
            _events.Writer.TryWrite(new UiEvent(UiKind.Quit)); // wake up the main loop
        }

        private void EnqueueKey(in KeyEventRecord k)
        {
            var state = k.ControlKeyState;
            var shift = (state & ShiftFlag) != 0;
            var alt = (state & (LeftAltFlag | RightAltFlag)) != 0;
            var ctrl = (state & (LeftCtrlFlag | RightCtrlFlag)) != 0;
            _events.Writer.TryWrite(new UiEvent(UiKind.Key, new ConsoleKeyInfo(k.UnicodeChar, (ConsoleKey)k.VirtualKeyCode, shift, alt, ctrl)));
        }

        private void EnqueueMouse(in MouseEventRecord m)
        {
            var x = m.X + 1; // 1-based coordinates like the layout
            var y = m.Y + 1;
            if ((m.EventFlags & MouseWheeledFlag) != 0)
            {
                var delta = (short)(m.ButtonState >> 16);
                _events.Writer.TryWrite(new UiEvent(UiKind.Mouse, null, x, y, delta > 0 ? MouseAction.WheelUp : MouseAction.WheelDown));
                return;
            }
            if ((m.EventFlags & MouseMovedFlag) != 0) return; // movement only: ignore
            var left = (m.ButtonState & LeftButtonFlag) != 0;
            if (!left) return;
            var dbl = (m.EventFlags & DoubleClickFlag) != 0;
            _events.Writer.TryWrite(new UiEvent(UiKind.Mouse, null, x, y, dbl ? MouseAction.DoubleClick : MouseAction.LeftPress));
        }

        private void KeyInputLoop()
        {
            while (!_exit)
            {
                try
                {
                    if (Console.KeyAvailable)
                        _events.Writer.TryWrite(new UiEvent(UiKind.Key, Console.ReadKey(true)));
                    else Thread.Sleep(20);
                }
                catch (InvalidOperationException) { break; } // redirected input
                catch { }
            }
            _events.Writer.TryWrite(new UiEvent(UiKind.Quit)); // wake up the main loop
        }

        private static void EnterAltBuffer()
        {
            try { Console.Write("\x1b[?1049h"); } catch { }
        }

        private static void ExitAltBuffer()
        {
            try { Console.Write("\x1b[?1049l"); } catch { }
        }

        // ── Splash (only at startup, uses MarkupLine because it's temporary) ──
        private async Task SplashAsync()
        {
            CursorHide();
            AnsiConsole.Clear();
            WriteLogo(centered: true);
            AnsiConsole.MarkupLine("\n  [bold]AgentBridge[/] — OpenAI-compatible agent server · terminal UI");
            AnsiConsole.MarkupLine($"  server: [cyan]{Markup.Escape(_serverUrl)}[/]" +
                (string.IsNullOrEmpty(_hostError) ? "" : $"   [red]local host start failed ({Markup.Escape(_hostError)}) — connecting to an existing instance[/]"));
            AnsiConsole.MarkupLine("  connecting…");
            await RefreshServerStateAsync();
        }

        // ── Main loop: consumes the event queue (keys + mouse + resize) ──
        private async Task KeyLoopAsync()
        {
            while (!_exit)
            {
                UiEvent ev;
                try { ev = await _events.Reader.ReadAsync(); }
                catch (ChannelClosedException) { break; }

                if (ev.Kind == UiKind.Quit) { _exit = true; break; }
                if (ev.Kind == UiKind.Resize) { FullRedraw(); continue; }
                if (ev.Kind == UiKind.Mouse)
                {
                    HandleMouse(ev);
                    if (_mouseConfirm && _palette != null)   // double click on a palette row
                    {
                        _mouseConfirm = false;
                        await RunPaletteSelectionAsync();
                    }
                    continue;
                }

                TrackSize();
                try { await HandleKeyAsync(ev.Key!.Value); }
                catch (Exception ex) { AddNote($"internal error: {ex.Message}"); }
                if (_exit) break;
                if (_resized) FullRedraw();
                else RenderBottom(); // every key updates input and palette
            }
        }

        // ── Mouse: click on the input, wheel to scroll, palette click/navigation ──
        private void HandleMouse(in UiEvent ev)
        {
            // Click on the input line → position the cursor (like Qwen).
            if (ev.Y == _inputLine && ev.Action is MouseAction.LeftPress or MouseAction.DoubleClick)
            {
                var col = Math.Max(1, ev.X - 2); // removes the "> " prefix
                _cursor = Math.Clamp(col, 0, _input.Length);
                RenderBottom();
                return;
            }
            // Palette: wheel/click navigate the selection, double click confirms.
            if (_palette != null && ev.Y > _inputLine)
            {
                var rows = _palette.Type == Palette.Kind.Commands ? _palette.Commands.Count : _palette.Files.Count;
                if (ev.Action == MouseAction.WheelUp) { _palette.Selected = Math.Max(0, _palette.Selected - 1); }
                else if (ev.Action == MouseAction.WheelDown) { MoveSelectionDown(); }
                else if (ev.Action == MouseAction.LeftPress)
                {
                    var row = ev.Y - _inputLine - 1;
                    if (row >= 0 && row < rows) _palette.Selected = row;
                }
                else if (ev.Action == MouseAction.DoubleClick)
                {
                    var row = ev.Y - _inputLine - 1;
                    if (row >= 0 && row < rows) { _palette.Selected = row; _mouseConfirm = true; }
                }
                RenderBottom();
                return;
            }
            // Wheel over the conversation area → scroll.
            if (ev.Y < _inputLine)
            {
                if (ev.Action == MouseAction.WheelUp) { _scrollFromBottom += 3; RenderHistory(); }
                else if (ev.Action == MouseAction.WheelDown) { _scrollFromBottom = Math.Max(0, _scrollFromBottom - 3); RenderHistory(); }
            }
        }

        // ── In-layout picker (replaces Spectre's SelectionPrompt): clean rendering,
        //    Esc cancels, ↑↓/wheel/click navigate, Enter/double click confirms. ──
        private async Task<string?> PickAsync(string title, List<string> items)
        {
            if (items.Count == 0) return null;
            _picker = new PickerState { Title = title, Items = items, Selected = 0 };
            RenderBottom();
            while (!_exit)
            {
                UiEvent ev;
                try { ev = await _events.Reader.ReadAsync(); }
                catch (ChannelClosedException) { break; }

                if (ev.Kind == UiKind.Quit) { _exit = true; break; }
                if (ev.Kind == UiKind.Resize) { FullRedraw(); continue; }
                if (ev.Kind == UiKind.Mouse)
                {
                    if (ev.Action == MouseAction.WheelUp) { _picker.Selected = Math.Max(0, _picker.Selected - 1); RenderBottom(); continue; }
                    if (ev.Action == MouseAction.WheelDown) { _picker.Selected = Math.Min(items.Count - 1, _picker.Selected + 1); RenderBottom(); continue; }
                    if (ev.Y > _inputLine)
                    {
                        var row = ev.Y - _inputLine - 1;
                        if (row >= 0 && row < items.Count) _picker.Selected = row;
                        if (ev.Action == MouseAction.DoubleClick)
                        {
                            var sel = items[_picker.Selected];
                            _picker = null;
                            RenderBottom();
                            return sel;
                        }
                        RenderBottom();
                    }
                    continue;
                }

                var k = ev.Key!.Value;
                if (k.Key == ConsoleKey.Escape) { _picker = null; RenderBottom(); return null; }
                if (k.Key == ConsoleKey.Enter) { var sel = items[_picker.Selected]; _picker = null; RenderBottom(); return sel; }
                if (k.Key == ConsoleKey.UpArrow) { _picker.Selected = Math.Max(0, _picker.Selected - 1); }
                else if (k.Key == ConsoleKey.DownArrow) { _picker.Selected = Math.Min(items.Count - 1, _picker.Selected + 1); }
                else if (k.Key == ConsoleKey.PageUp) { _picker.Selected = Math.Max(0, _picker.Selected - 5); }
                else if (k.Key == ConsoleKey.PageDown) { _picker.Selected = Math.Min(items.Count - 1, _picker.Selected + 5); }
                RenderBottom();
            }
            _picker = null;
            return null;
        }

        private async Task HandleKeyAsync(ConsoleKeyInfo k)
        {
            if (k.Key != ConsoleKey.Escape && k.Key != ConsoleKey.C) _escCount = 0;

            if (_palette != null)
            {
                switch (k.Key)
                {
                    case ConsoleKey.Escape: _palette = null; return;
                    case ConsoleKey.UpArrow: _palette.Selected = Math.Max(0, _palette.Selected - 1); RecomputePalette(); return;
                    case ConsoleKey.DownArrow: MoveSelectionDown(); return;
                    case ConsoleKey.Tab: CompleteSelected(); return;
                    case ConsoleKey.Enter:
                        await RunPaletteSelectionAsync();
                        return;
                }
            }

            var ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;
            var alt = (k.Modifiers & ConsoleModifiers.Alt) != 0;

            switch (k.Key)
            {
                case ConsoleKey.Enter:
                    await SubmitAsync();
                    break;

                case ConsoleKey.Escape:
                    if (_input.Length > 0) { _input = ""; _cursor = 0; }
                    else if (++_escCount >= 2) _exit = true;
                    else _statusNote = "Press Esc again to exit · or Ctrl+C twice · or Ctrl+D";
                    break;

                case ConsoleKey.C when ctrl:
                    if (_chatRunning) { _chatCts?.Cancel(); _statusNote = "cancelling…"; }
                    else if (_input.Length > 0) { _input = ""; _cursor = 0; }
                    else if (++_escCount >= 2) _exit = true;
                    else _statusNote = "Press Ctrl+C again to exit";
                    break;

                case ConsoleKey.D when ctrl:
                    if (_input.Length == 0) _exit = true;
                    break;

                case ConsoleKey.L when ctrl:
                    FullRedraw();
                    break;

                case ConsoleKey.R when ctrl:
                    await ReverseSearchAsync();
                    break;

                case ConsoleKey.Y when ctrl:
                    await RetryAsync();
                    break;

                case ConsoleKey.LeftArrow when ctrl || alt:
                    MoveCursorWord(-1);
                    break;
                case ConsoleKey.LeftArrow:
                    _cursor = Math.Max(0, _cursor - 1);
                    break;

                case ConsoleKey.RightArrow when ctrl || alt:
                    MoveCursorWord(1);
                    break;
                case ConsoleKey.RightArrow:
                    _cursor = Math.Min(_input.Length, _cursor + 1);
                    break;

                case ConsoleKey.Home:
                case ConsoleKey.A when ctrl:
                    _cursor = 0;
                    break;
                case ConsoleKey.End:
                case ConsoleKey.E when ctrl:
                    _cursor = _input.Length;
                    break;

                case ConsoleKey.B when ctrl: _cursor = Math.Max(0, _cursor - 1); break;
                case ConsoleKey.F when ctrl: _cursor = Math.Min(_input.Length, _cursor + 1); break;
                case ConsoleKey.P when ctrl: await HistoryPrevAsync(); break;
                case ConsoleKey.N when ctrl: await HistoryNextAsync(); break;

                case ConsoleKey.U when ctrl:
                    _input = _input[_cursor..]; _cursor = 0;
                    break;
                case ConsoleKey.K when ctrl:
                    _input = _input[.._cursor];
                    break;
                case ConsoleKey.W when ctrl:
                    DeleteWordLeft();
                    break;
                case ConsoleKey.Backspace:
                    if (_cursor > 0) { _input = _input[..(_cursor - 1)] + _input[_cursor..]; _cursor--; }
                    break;
                case ConsoleKey.Delete:
                    if (_cursor < _input.Length) _input = _input[.._cursor] + _input[(_cursor + 1)..];
                    break;

                case ConsoleKey.UpArrow: await HistoryPrevAsync(); break;
                case ConsoleKey.DownArrow: await HistoryNextAsync(); break;

                case ConsoleKey.PageUp:
                    _scrollFromBottom += Math.Max(1, _historyHeight - 2);
                    RenderHistory();
                    break;
                case ConsoleKey.PageDown:
                    _scrollFromBottom = Math.Max(0, _scrollFromBottom - Math.Max(1, _historyHeight - 2));
                    RenderHistory();
                    break;

                case ConsoleKey.F1:
                    await ShowHelpAsync();
                    break;

                default:
                    // INVARIANT (project rule): input adapts to the user's ACTIVE
                    // KEYBOARD LAYOUT — ReadConsoleInput delivers the character already
                    // translated by the configured keyboard, and no specific layout is
                    // assumed here (Italian, international, ...). The distinction between
                    // shortcuts and text is based on the character (Ctrl shortcuts
                    // produce control characters), never on the layout or language.
                    // International keyboards: AltGr / Alt+letter / dead keys produce
                    // a PRINTABLE character with a modifier → it's text input, NOT a
                    // Ctrl shortcut (e.g. AltGr+e = "é" must not jump to end of line).
                    if (!char.IsControl(k.KeyChar) && (ctrl || alt))
                    {
                        InsertChar(k.KeyChar);
                        break;
                    }
                    if (k.KeyChar == '/' && _input.Length == 0 && _palette == null)
                    {
                        _input = "/"; _cursor = 1;
                        _palette = new Palette { Type = Palette.Kind.Commands };
                        RecomputePalette();
                    }
                    else if (k.KeyChar == '@' && _input.Length == 0 && _palette == null)
                    {
                        _input = "@"; _cursor = 1;
                        _palette = new Palette { Type = Palette.Kind.Files };
                        _ = Task.Run(RefreshFilesAsync);
                        RecomputePalette();
                    }
                    else if (k.KeyChar == '?' && _input.Length == 0 && _palette == null)
                    {
                        await ShowShortcutsAsync();
                    }
                    else if (!char.IsControl(k.KeyChar))
                    {
                        InsertChar(k.KeyChar);
                    }
                    break;
            }

            if (_palette != null)
            {
                if (_palette.Type == Palette.Kind.Commands)
                {
                    if (_input.StartsWith('/')) _palette.Filter = _input[1..];
                    else _palette = null;
                }
                else
                {
                    if (_input.StartsWith('@')) _palette.Filter = _input[1..];
                    else _palette = null;
                }
                if (_palette != null) RecomputePalette();
            }
        }

        // ── Input manipulation methods ──
        private void InsertChar(char c)
        {
            _input = _input[.._cursor] + c + _input[_cursor..];
            _cursor++;
        }

        private void MoveCursorWord(int dir)
        {
            if (dir < 0)
            {
                if (_cursor == 0) return;
                var i = _cursor - 1;
                while (i > 0 && _input[i - 1] == ' ') i--;
                while (i > 0 && _input[i - 1] != ' ') i--;
                _cursor = i;
            }
            else
            {
                if (_cursor >= _input.Length) return;
                var i = _cursor;
                while (i < _input.Length && _input[i] != ' ') i++;
                while (i < _input.Length && _input[i] == ' ') i++;
                _cursor = i;
            }
        }

        private void DeleteWordLeft()
        {
            var start = _cursor;
            if (start == 0) return;
            var i = start - 1;
            while (i > 0 && _input[i - 1] == ' ') i--;
            while (i > 0 && _input[i - 1] != ' ') i--;
            _input = _input[..i] + _input[start..];
            _cursor = i;
        }

        private Task HistoryPrevAsync()
        {
            if (_promptHistory.Count == 0) return Task.CompletedTask;
            if (_histIndex < 0) { _histDraft = _input; _histIndex = _promptHistory.Count - 1; }
            else if (_histIndex > 0) _histIndex--;
            _input = _promptHistory[_histIndex];
            _cursor = _input.Length;
            return Task.CompletedTask;
        }

        private Task HistoryNextAsync()
        {
            if (_histIndex < 0) return Task.CompletedTask;
            _histIndex++;
            if (_histIndex >= _promptHistory.Count) { _histIndex = -1; _input = _histDraft; }
            else { _input = _promptHistory[_histIndex]; _cursor = _input.Length; }
            return Task.CompletedTask;
        }

        private void MoveSelectionDown()
        {
            var count = _palette!.Type == Palette.Kind.Commands ? _palette.Commands.Count : _palette.Files.Count;
            _palette.Selected = Math.Min(Math.Max(0, count - 1), _palette.Selected + 1);
            RecomputePalette(); // refresh if the filter changed? (it doesn't here, but for safety)
        }

        // ── Submit and commands ──
        private async Task SubmitAsync()
        {
            var text = _input.Trim();
            _input = ""; _cursor = 0; _histIndex = -1;
            if (text.Length == 0) return;

            if (text[0] == '/')
            {
                await RunCommandLineAsync(text);
                return;
            }
            _promptHistory.Add(text);
            StartChat(text);
        }

        private async Task RunCommandLineAsync(string text)
        {
            var rest = text[1..].TrimStart();
            var sp = rest.IndexOf(' ');
            var name = sp < 0 ? rest : rest[..sp];
            var args = sp < 0 ? "" : rest[(sp + 1)..].Trim();
            var cmd = Commands.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
                (c.Aliases?.Any(a => a.Equals("/" + name, StringComparison.OrdinalIgnoreCase)) ?? false));
            if (cmd == null)
            {
                AddNote($"[red]unknown command /{Markup.Escape(name)}[/] — type [cyan]/[/] to see the command list, or /help");
                return;
            }
            await RunCommandAsync(cmd, args);
        }

        private async Task RunCommandAsync(CliCommand cmd, string args)
        {
            try { await cmd.Run(this, args); }
            catch (Exception ex) { AddNote($"[red]/{cmd.Name} failed:[/] {Markup.Escape(ex.Message)}"); }
            await RefreshSessionStateAsync();
            RenderTop(); // refresh the status after the command
        }

        private async Task RunPaletteSelectionAsync()
        {
            RecomputePalette();
            var p = _palette!;
            if (p.Type == Palette.Kind.Files)
            {
                if (p.Files.Count > 0)
                {
                    var f = p.Files[Math.Min(p.Selected, p.Files.Count - 1)];
                    ToggleAttach(f);
                }
                _palette = null;
                return;
            }

            if (p.Commands.Count == 0) { _palette = null; return; }
            var cmd = p.Commands[Math.Min(p.Selected, p.Commands.Count - 1)];

            var text = _input.TrimStart();
            string args = "";
            if (text.StartsWith('/') && text.Length > 1)
            {
                var rest = text[1..].TrimStart();
                var sp = rest.IndexOf(' ');
                var name = sp < 0 ? rest : rest[..sp];
                var match = Commands.FirstOrDefault(c =>
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    (c.Aliases?.Any(a => a.Equals("/" + name, StringComparison.OrdinalIgnoreCase)) ?? false));
                if (match != null) { cmd = match; args = sp < 0 ? "" : rest[(sp + 1)..].Trim(); }
                else if (sp >= 0) args = rest[(sp + 1)..].Trim();
            }
            _palette = null;
            _input = ""; _cursor = 0;
            await RunCommandAsync(cmd, args);
        }

        private void CompleteSelected()
        {
            var p = _palette!;
            if (p.Type == Palette.Kind.Commands && p.Commands.Count > 0)
            {
                var cmd = p.Commands[Math.Min(p.Selected, p.Commands.Count - 1)];
                _input = "/" + cmd.Name;
                _cursor = _input.Length;
                p.Filter = cmd.Name;
                RecomputePalette();
            }
            else if (p.Type == Palette.Kind.Files && p.Files.Count > 0)
            {
                var f = p.Files[Math.Min(p.Selected, p.Files.Count - 1)];
                ToggleAttach(f);
                _palette = null;
            }
        }

        private void ToggleAttach(FileRef f)
        {
            f.Attached = !f.Attached;
            if (f.Attached) { if (!_attached.Contains(f.Id)) _attached.Add(f.Id); AddNote($"attached [cyan]{Markup.Escape(f.FileName)}[/] to the chat"); }
            else { _attached.Remove(f.Id); AddNote($"detached [cyan]{Markup.Escape(f.FileName)}[/]"); }
        }

        // ── Chat ──
        private void StartChat(string prompt) => _ = Task.Run(() => SendChatAsync(prompt));

        private async Task SendChatAsync(string prompt)
        {
            if (_chatRunning) { _statusNote = "generating… wait, or Ctrl+C to stop"; return; }
            _chatRunning = true;
            _lastPrompt = prompt;
            _lastFailed = false;
            _chatCts = new CancellationTokenSource();
            _history.Add(new Entry { Role = "user", Text = prompt });   // the user's message appears immediately (like Qwen)
            _pending = new Entry { Role = "agent", Text = "" };
            var sw = Stopwatch.StartNew();
            RenderTop();
            RenderHistory();

            try
            {
                if (!_connected) await RefreshServerStateAsync();
                if (string.IsNullOrEmpty(_sessionId))
                    throw new InvalidOperationException("no session — server unreachable");

                var body = JsonSerializer.Serialize(new
                {
                    model = _agentSet,
                    messages = new[] { new { role = "user", content = prompt } },
                    session_id = _sessionId,
                    file_ids = _attached.Count > 0 ? _attached : (List<string>?)null,
                    stream = true,
                }, JsonOpts);

                using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _chatCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await ReadErrorAsync(response);
                    _pending.Error = true;
                    _pending.Text = err;
                    _lastFailed = true;
                    AddPendingToHistory();
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(_chatCts.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (true)
                {
                    _chatCts.Token.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync(_chatCts.Token);
                    if (line == null) break;
                    if (!line.StartsWith("data: ")) continue;
                    var data = line["data: ".Length..];
                    if (data == "[DONE]") break;
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        {
                            _pending.Text += c.GetString();
                            if (sw.ElapsedMilliseconds > 80) { sw.Restart(); RenderHistory(); }
                        }
                    }
                    catch { }
                }

                _connected = true;
                _statusNote = $"replied in {sw.ElapsedMilliseconds / 1000.0:0.0}s";
                AddPendingToHistory();
            }
            catch (OperationCanceledException)
            {
                _pending.Error = true;
                _pending.Text = "(cancelled)";
                AddPendingToHistory();
                _statusNote = "cancelled";
                _lastFailed = true;
            }
            catch (Exception ex)
            {
                _pending.Error = true;
                _pending.Text = $"request failed: {ex.Message}";
                AddPendingToHistory();
                _statusNote = "error";
                _lastFailed = true;
                _connected = false;
            }
            finally
            {
                _chatRunning = false;
                _pending = null;
                _chatCts.Dispose();
                _chatCts = null;
                RenderHistory();
                RenderTop();
                _ = Task.Run(async () =>
                {
                    await RefreshSessionStateAsync();
                    RenderTop();
                });
            }
        }

        private void AddPendingToHistory()
        {
            if (_pending != null) _history.Add(_pending);
        }

        private async Task<string> ReadErrorAsync(HttpResponseMessage response)
        {
            try
            {
                var raw = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : "error";
                var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
                return detail ?? err ?? $"HTTP {(int)response.StatusCode}";
            }
            catch { return $"HTTP {(int)response.StatusCode}"; }
        }

        // ── Server state ──
        private async Task RefreshServerStateAsync()
        {
            try
            {
                using var health = await _http.GetAsync("/health").WaitAsync(TimeSpan.FromSeconds(8));
                _connected = health.IsSuccessStatusCode;
                if (!_connected) { _statusNote = "server unreachable — starting it headless keeps the API alive"; return; }

                if (string.IsNullOrEmpty(_sessionId))
                {
                    using var create = await _http.PostAsync("/v1/control",
                        new StringContent("{\"create\":true}", Encoding.UTF8, "application/json"));
                    if (create.IsSuccessStatusCode)
                    {
                        var json = await create.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(json);
                        _sessionId = doc.RootElement.GetProperty("session_id").GetString() ?? "";
                    }
                }
                await RefreshSessionStateAsync();
            }
            catch (Exception ex)
            {
                _connected = false;
                _statusNote = $"server unreachable: {ex.Message}";
            }
        }

        private async Task RefreshSessionStateAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_sessionId)) return;
                using var resp = await _http.GetAsync($"/v1/control?session_id={Uri.EscapeDataString(_sessionId)}").WaitAsync(TimeSpan.FromSeconds(8));
                if (!resp.IsSuccessStatusCode) return;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;

                if (root.TryGetProperty("llm", out var llm))
                {
                    _provider = GetStr(llm, "provider") ?? "";
                    _modelName = GetStr(llm, "model_name") ?? "";
                    _contextWindow = GetInt(llm, "context_window");
                    _historyTokens = GetInt(llm, "history_tokens_estimate");
                }
                if (root.TryGetProperty("features", out var feats) && feats.ValueKind == JsonValueKind.Object)
                {
                    _features.Clear();
                    foreach (var p in feats.EnumerateObject()) _features[p.Name] = p.Value.GetBoolean();
                }
                if (root.TryGetProperty("capabilities", out var caps))
                {
                    if (caps.TryGetProperty("tts", out var tts))
                    {
                        _ttsAvailable = GetBool(tts, "available");
                        _ttsDetail = GetStr(tts, "detail") ?? "";
                    }
                    if (caps.TryGetProperty("voice", out var voice))
                    {
                        _voiceAvailable = GetBool(voice, "available");
                        _voiceDetail = GetStr(voice, "detail") ?? "";
                    }
                }
            }
            catch { }
        }

        private async Task RefreshFilesAsync()
        {
            try
            {
                using var resp = await _http.GetAsync("/v1/files").WaitAsync(TimeSpan.FromSeconds(8));
                if (!resp.IsSuccessStatusCode) return;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("data", out var data)) return;
                _files.Clear();
                foreach (var f in data.EnumerateArray())
                {
                    var id = GetStr(f, "id") ?? "";
                    var name = GetStr(f, "filename") ?? id;
                    _files.Add(new FileRef { Id = id, FileName = name, Status = GetStr(f, "status") ?? "", Attached = _attached.Contains(id) });
                }
                RecomputePalette();
                RenderBottom();
            }
            catch { }
        }

        private static string? GetStr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static int GetInt(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
        private static bool GetBool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        // ── Commands ──
        private Task ExitAsync() { _exit = true; return Task.CompletedTask; }

        private async Task HealthAsync()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await _http.GetAsync("/health").WaitAsync(TimeSpan.FromSeconds(5));
                sw.Stop();
                AddNote(resp.IsSuccessStatusCode
                    ? $"server [green]healthy[/] · {sw.ElapsedMilliseconds} ms"
                    : $"server returned [red]HTTP {(int)resp.StatusCode}[/]");
                _connected = resp.IsSuccessStatusCode;
            }
            catch (Exception ex) { AddNote($"[red]server unreachable:[/] {Markup.Escape(ex.Message)}"); _connected = false; }
        }

        private async Task SwitchModelAsync(string args)
        {
            string name;
            if (string.IsNullOrWhiteSpace(args))
            {
                using var resp = await _http.GetAsync("/v1/models").WaitAsync(TimeSpan.FromSeconds(8));
                if (!resp.IsSuccessStatusCode) { AddNote("[red]could not load providers[/]"); return; }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var providers = doc.RootElement.GetProperty("data").EnumerateArray()
                    .Where(x => GetStr(x, "owned_by") == "llm-provider")
                    .Select(x => $"{GetStr(x, "id")} — {GetStr(x, "model_name")} · ctx {GetInt(x, "context_window"):N0}")
                    .ToList();
                if (providers.Count == 0) { AddNote("[red]no providers reported by the server[/]"); return; }
                var pick = await PickAsync("Switch LLM provider", providers);
                if (pick == null) return;   // Esc → cancel, clean screen
                name = pick[..pick.IndexOf(" —")];
            }
            else name = args.Trim();

            if (string.Equals(name, _provider, StringComparison.OrdinalIgnoreCase))
            { AddNote($"already on [cyan]{Markup.Escape(name)}[/]"); return; }

            AddNote($"switching provider to [cyan]{Markup.Escape(name)}[/]… (some providers take minutes to warm up)");
            var body = JsonSerializer.Serialize(new { session_id = _sessionId, llm_provider = name }, JsonOpts);
            using var resp2 = await _http.PostAsync("/v1/control", new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp2.IsSuccessStatusCode)
            {
                AddNote($"provider is now [green]{Markup.Escape(name)}[/]");
            }
            else
            {
                AddNote($"[red]switch refused (HTTP {(int)resp2.StatusCode}):[/] {Markup.Escape(await ReadErrorAsync(resp2))}");
            }
        }

        private async Task SwitchAgentAsync(string args)
        {
            string name;
            if (string.IsNullOrWhiteSpace(args))
            {
                var pick = await PickAsync("Switch agent set", AgentSets.ToList());
                if (pick == null) return;
                name = pick;
            }
            else name = args.Trim().ToLowerInvariant();
            if (!AgentSets.Contains(name)) { AddNote($"[red]unknown agent set '{Markup.Escape(name)}'[/] — {string.Join(", ", AgentSets)}"); return; }
            _agentSet = name;
            AddNote($"agent set: [cyan]{name}[/]");
        }

        private async Task VoiceAsync(string lang)
        {
            if (!_voiceAvailable)
            {
                AddNote($"[red]voice unavailable:[/] {Markup.Escape(string.IsNullOrEmpty(_voiceDetail) ? "POST /v1/voice/listen is disabled" : _voiceDetail)}");
                return;
            }
            // INVARIANT (project rule): no hardcoded language — the machine's language
            // comes from SystemLang.Get() and execution adapts to any computer
            // settings (dictation in the user's language).
            var l = string.IsNullOrWhiteSpace(lang) ? SystemLang.Get() : lang.Trim();
            AddNote("listening… (server microphone) — speak now");
            var body = JsonSerializer.Serialize(new { lang = l, timeout_seconds = 15 }, JsonOpts);
            using var resp = await _http.PostAsync("/v1/voice/listen", new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var text = GetStr(doc.RootElement, "text") ?? "";
                if (string.IsNullOrWhiteSpace(text)) { AddNote("no speech recognised"); return; }
                _input = text; _cursor = _input.Length;
                AddNote($"dictated [cyan]{Markup.Escape(text)}[/] — press Enter to send");
            }
            else
            {
                var err = await ReadErrorAsync(resp);
                AddNote(resp.StatusCode == System.Net.HttpStatusCode.RequestTimeout
                    ? "[yellow]listening timed out[/] (no speech detected)"
                    : $"[red]voice failed (HTTP {(int)resp.StatusCode}):[/] {Markup.Escape(err)}");
            }
        }

        private async Task TtsAsync(string text)
        {
            if (!_ttsAvailable)
            {
                AddNote($"[red]tts unavailable:[/] {Markup.Escape(string.IsNullOrEmpty(_ttsDetail) ? "POST /v1/audio/speech is disabled" : _ttsDetail)}");
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                var last = _history.LastOrDefault(e => e.Role == "agent" && !e.Error)?.Text;
                if (string.IsNullOrWhiteSpace(last)) { AddNote("nothing to speak — give text: /tts <text>"); return; }
                text = last;
            }
            AddNote("synthesising…");
            // INVARIANT (project rule): no fixed voice/language — the server picks by
            // the machine's language (SystemLang.Get()), so every machine speaks its
            // own language regardless of its settings.
            var body = JsonSerializer.Serialize(new { input = text, lang = SystemLang.Get(), speed = 1.0 }, JsonOpts);
            using var resp = await _http.PostAsync("/v1/audio/speech", new StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) { AddNote($"[red]tts failed (HTTP {(int)resp.StatusCode}):[/] {Markup.Escape(await ReadErrorAsync(resp))}"); return; }

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            var path = Path.Combine(Path.GetTempPath(), $"agent_tts_{DateTime.Now:yyyyMMddHHmmss}.wav");
            await File.WriteAllBytesAsync(path, bytes);
            AddNote($"saved [cyan]{Markup.Escape(path)}[/] ({bytes.Length:N0} bytes)");
            if (OperatingSystem.IsWindows())
            {
                if (!PlaySound(path, IntPtr.Zero, SndAsync | SndFilename))
                    AddNote($"[yellow]playback failed — open the file with your media player:[/] {Markup.Escape(path)}");
            }
            else
            {
                AddNote("[grey]playback is Windows-only here — open the file with your media player[/]");
            }
        }

        private async Task FeaturesAsync(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                var current = _features.Count == 0 ? "(none set)" :
                    string.Join(", ", _features.Select(kv => $"[cyan]{kv.Key}[/]={(kv.Value ? "on" : "off")}"));
                AddNote($"session features: {current}");
                return;
            }
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var name = parts[0].ToLowerInvariant();
            bool value;
            if (parts.Length >= 2 && parts[1].ToLowerInvariant() is "on" or "true") value = true;
            else if (parts.Length >= 2 && parts[1].ToLowerInvariant() is "off" or "false") value = false;
            else value = !(_features.TryGetValue(name, out var cur) && cur);
            _features[name] = value;
            var body = JsonSerializer.Serialize(new { session_id = _sessionId, features = new Dictionary<string, bool> { [name] = value } }, JsonOpts);
            using var resp = await _http.PostAsync("/v1/control", new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode) AddNote($"feature [cyan]{name}[/] = {(value ? "on" : "off")}");
            else AddNote($"[red]failed (HTTP {(int)resp.StatusCode}):[/] {Markup.Escape(await ReadErrorAsync(resp))}");
        }

        private async Task NewSessionAsync()
        {
            using var resp = await _http.PostAsync("/v1/control", new StringContent("{\"create\":true}", Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) { AddNote($"[red]could not create session (HTTP {(int)resp.StatusCode})[/]"); return; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            _sessionId = GetStr(doc.RootElement, "session_id") ?? "";
            _history.Clear(); _scrollFromBottom = 0; _attached.Clear();
            foreach (var f in _files) f.Attached = false;
            AddNote($"new session [cyan]{_sessionId[..Math.Min(8, _sessionId.Length)]}[/]");
        }

        private async Task ClearHistoryAsync()
        {
            var body = JsonSerializer.Serialize(new { session_id = _sessionId, reset_history = true }, JsonOpts);
            using var resp = await _http.PostAsync("/v1/control", new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode) { _history.Clear(); _scrollFromBottom = 0; AddNote("session history cleared"); }
            else AddNote($"[red]failed (HTTP {(int)resp.StatusCode}):[/] {Markup.Escape(await ReadErrorAsync(resp))}");
        }

        private async Task ShowStatusAsync()
        {
            var lines = new List<string>
            {
                $"[bold]Session[/]        [cyan]{Markup.Escape(_sessionId)}[/]",
                $"Provider        [cyan]{Markup.Escape(_provider)}[/]  ({Markup.Escape(_modelName)})",
                $"Context window  {_contextWindow:N0} tokens · history ≈ {_historyTokens:N0}",
                $"Agent set       {Markup.Escape(_agentSet)}",
                $"Features        {( _features.Count == 0 ? "(none)" : string.Join(", ", _features.Select(kv => $"{kv.Key}={kv.Value}")))}",
                $"Attachments     {(_attached.Count == 0 ? "(none)" : string.Join(", ", _attached))}",
                "",
                $"[bold]Capabilities[/]   tts [{( _ttsAvailable ? "green" : "red")}]{( _ttsAvailable ? "available" : "unavailable")}[/] · voice [{( _voiceAvailable ? "green" : "red")}]{( _voiceAvailable ? "available" : "unavailable")}[/]",
                $"               server: [cyan]{Markup.Escape(_serverUrl)}[/] [{( _connected ? "green" : "red")}]{( _connected ? "connected" : "unreachable")}[/]",
                $"               prompt history: {_promptHistory.Count} entries · chat sessions in the server: (see /health)",
            };
            await ShowPageAsync("[bold]agent status[/]", lines);
        }

        private async Task FilesAsync(string args)
        {
            if (string.IsNullOrWhiteSpace(args) || args == "list")
            {
                await RefreshFilesAsync();
                if (_files.Count == 0) { AddNote("no uploaded files — use [cyan]/files add <path>[/]"); return; }
                var lines = _files.Select(f =>
                    $"[cyan]{Markup.Escape(f.FileName)}[/]  [grey]{f.Id} · {f.Status}[/]{(f.Attached ? "  [green]attached[/]" : "")}").ToList();
                await ShowPageAsync("[bold]uploaded files[/]", lines);
                return;
            }
            var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var sub = parts[0].ToLowerInvariant();
            if (sub == "add" && parts.Length == 2)
            {
                var path = parts[1].Trim('"');
                if (!File.Exists(path)) { AddNote($"[red]file not found:[/] {Markup.Escape(path)}"); return; }
                AddNote($"uploading [cyan]{Markup.Escape(Path.GetFileName(path))}[/]…");
                await using var fs = File.OpenRead(path);
                using var form = new MultipartFormDataContent();
                var fileContent = new StreamContent(fs);
                form.Add(fileContent, "file", Path.GetFileName(path));
                form.Add(new StringContent("assistants"), "purpose");
                using var resp = await _http.PostAsync("/v1/files", form);
                if (!resp.IsSuccessStatusCode) { AddNote($"[red]upload failed (HTTP {(int)resp.StatusCode}):[/] {Markup.Escape(await ReadErrorAsync(resp))}"); return; }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var id = GetStr(doc.RootElement, "id") ?? "";
                var name = GetStr(doc.RootElement, "filename") ?? Path.GetFileName(path);
                _files.RemoveAll(x => x.Id == id);
                _files.Add(new FileRef { Id = id, FileName = name, Status = GetStr(doc.RootElement, "status") ?? "", Attached = true });
                if (!_attached.Contains(id)) _attached.Add(id);
                AddNote($"uploaded + attached [cyan]{Markup.Escape(name)}[/] ({id})");
            }
            else if (sub == "rm" && parts.Length == 2)
            {
                var id = parts[1].Trim();
                using var resp = await _http.DeleteAsync($"/v1/files/{Uri.EscapeDataString(id)}");
                if (resp.IsSuccessStatusCode)
                {
                    _files.RemoveAll(x => x.Id == id);
                    _attached.Remove(id);
                    AddNote($"deleted [cyan]{id}[/]");
                }
                else AddNote($"[red]delete failed (HTTP {(int)resp.StatusCode}):[/] {Markup.Escape(await ReadErrorAsync(resp))}");
            }
            else
            {
                AddNote("usage: [cyan]/files add <path>[/] | [cyan]/files rm <id>[/] | [cyan]/files[/]");
            }
        }

        private async Task AttachAsync(string args)
        {
            await RefreshFilesAsync();
            if (string.IsNullOrWhiteSpace(args))
            {
                if (_files.Count == 0) { AddNote("no uploaded files — use [cyan]/files add <path>[/]"); return; }
                var choices = _files.Select(f => $"{f.FileName}  ({f.Id}){(f.Attached ? "  [attached]" : "")}").ToList();
                var pick = await PickAsync("Toggle file attachment", choices);
                if (pick == null) return;
                var name = pick[..pick.IndexOf("  (")];
                var f = _files.First(x => x.FileName == name);
                ToggleAttach(f);
                return;
            }
            var file = _files.FirstOrDefault(x => x.Id == args.Trim() || x.FileName.Equals(args.Trim(), StringComparison.OrdinalIgnoreCase));
            if (file == null) { AddNote($"[red]unknown file '{Markup.Escape(args.Trim())}'[/] — /files to list"); return; }
            ToggleAttach(file);
        }

        private async Task RetryAsync()
        {
            if (string.IsNullOrEmpty(_lastPrompt)) { AddNote("nothing to retry yet"); return; }
            if (!_lastFailed && _history.Count > 0) { AddNote("the last reply succeeded — still resending"); }
            StartChat(_lastPrompt);
        }

        private Task OpenDocsAsync()
        {
            const string url = "https://github.com/Graphene-Lab/AgentBridge";
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                AddNote($"opened [cyan]{url}[/] in your browser");
            }
            catch (Exception ex) { AddNote($"[red]could not open the browser:[/] {Markup.Escape(ex.Message)}"); }
            return Task.CompletedTask;
        }

        private async Task ShowHelpAsync()
        {
            var lines = new List<string>
            {
                "[bold]COMMANDS[/]  (type [cyan]/[/] to see this list live, filtered as you type)",
            };
            foreach (var c in Commands)
                lines.Add($"  [cyan]/{c.Name}[/] {Markup.Escape(c.Args).PadRight(16)} {c.Help}");
            lines.Add("");
            lines.Add("[bold]KEYBOARD SHORTCUTS[/]  (press [cyan]?[/] on an empty input for the overlay)");
            foreach (var (keys, what) in ShortcutTable)
                lines.Add($"  {keys.PadRight(24)} {what}");
            lines.Add("");
            lines.Add("[bold]MOUSE[/]");
            lines.Add("  wheel        Scroll the conversation · navigate palette/picker");
            lines.Add("  click input  Position the text cursor");
            lines.Add("  click row    Select a palette/picker row (double-click runs it)");
            lines.Add("");
            lines.Add("[bold]API (the same server keeps answering while you chat)[/]");
            lines.Add("  POST /v1/chat/completions · /v1/control · /v1/audio/speech · /v1/voice/listen");
            lines.Add("  GET  /v1/models · /v1/files · /v1/control · /v1/audio/voices · /health");
            lines.Add("  POST /v1/files (upload · attach via [cyan]@[/])");
            lines.Add("");
            lines.Add("[bold]ONLINE HELP[/]");
            lines.Add($"  AgentBridge repo/docs: https://github.com/Graphene-Lab/AgentBridge  ([cyan]/docs[/] opens it)");
            lines.Add("  Qwen Code (the TUI this one is inspired by): https://qwenlm.github.io/qwen-code-docs/");
            lines.Add("  Terminal UI model & improvements: README.md → \"Terminal UI\"");
            await ShowPageAsync("[bold]agent help[/]", lines);
        }

        private static readonly (string Keys, string What)[] ShortcutTable =
        {
            ("Enter", "Send the message / run the selected command"),
            ("/", "Open the slash-command palette (contextual help below the input)"),
            ("@", "Open the file palette (toggle chat attachments)"),
            ("?", "Show this shortcuts overlay (empty input)"),
            ("Tab", "Complete the selected command / attach the selected file"),
            ("Esc", "Close palette · clear input · twice: exit"),
            ("Ctrl+C", "Cancel the reply · clear input · twice: exit"),
            ("Ctrl+D", "Exit (empty input)"),
            ("Ctrl+L", "Clear the screen"),
            ("Ctrl+R", "Reverse-search prompt history"),
            ("Ctrl+Y", "Retry the last prompt"),
            ("Up / Down", "Prompt history (with Ctrl+P / Ctrl+N)"),
            ("Left / Right", "Move the cursor (with Ctrl: by word)"),
            ("Ctrl+A / Ctrl+E", "Jump to start / end of the input"),
            ("Ctrl+U / Ctrl+K", "Delete to start / end of the line"),
            ("Ctrl+W", "Delete the word before the cursor"),
            ("PgUp / PgDn", "Scroll the conversation history"),
            ("F1", "Show the full help page"),
        };

        private async Task ShowShortcutsAsync()
        {
            var lines = ShortcutTable.Select(s => $"  {s.Keys.PadRight(24)} {s.What}").ToList();
            lines.Insert(0, "[bold]KEYBOARD SHORTCUTS[/]");
            lines.Add("");
            lines.Add("[grey]Full help: /help · commands: type /[/]");
            await ShowPageAsync("[bold]shortcuts[/]", lines);
        }

        private async Task ReverseSearchAsync()
        {
            var q = "";
            var sel = 0;
            while (!_exit)
            {
                var matches = _promptHistory.Where(p => p.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
                sel = Math.Clamp(sel, 0, Math.Max(0, matches.Count - 1));
                lock (_lock)
                {
                    CursorHide();
                    AnsiConsole.Clear();
                    AnsiConsole.MarkupLine("[bold]reverse prompt history[/]   (Esc close · Enter pick · Up/Down move)");
                    for (var i = 0; i < Math.Min(matches.Count, _height - 5); i++)
                        AnsiConsole.MarkupLine((i == sel ? "[reverse] " : "  ") + Markup.Escape(matches[i]));
                    AnsiConsole.Write(new Rule());
                    AnsiConsole.MarkupLine("search> " + Markup.Escape(q));
                }
                UiEvent ev;
                try { ev = await _events.Reader.ReadAsync(); }
                catch (ChannelClosedException) { break; }
                if (ev.Kind == UiKind.Quit) { _exit = true; break; }
                if (ev.Kind == UiKind.Mouse)
                {
                    if (ev.Action == MouseAction.WheelUp) sel = Math.Max(0, sel - 1);
                    else if (ev.Action == MouseAction.WheelDown) sel = Math.Min(matches.Count - 1, sel + 1);
                    else if (ev.Action is (MouseAction.LeftPress or MouseAction.DoubleClick) && matches.Count > 0)
                    {
                        var row = ev.Y - 3; // below the title
                        if (row >= 0 && row < matches.Count) sel = row;
                        if (ev.Action == MouseAction.DoubleClick) { _input = matches[sel]; _cursor = _input.Length; break; }
                    }
                    continue;
                }
                var k = ev.Key!.Value;
                if (k.Key == ConsoleKey.Escape) break;
                if (k.Key == ConsoleKey.Enter)
                {
                    if (matches.Count > 0) { _input = matches[sel]; _cursor = _input.Length; }
                    break;
                }
                if (k.Key == ConsoleKey.UpArrow) sel = Math.Max(0, sel - 1);
                else if (k.Key == ConsoleKey.DownArrow) sel = Math.Min(matches.Count - 1, sel + 1);
                else if (k.Key == ConsoleKey.Backspace) { if (q.Length > 0) q = q[..^1]; }
                else if (!char.IsControl(k.KeyChar)) q += k.KeyChar;
            }
            FullRedraw();
        }

        // Full-screen page (help/status): any key or click closes it and
        // FullRedraw restores the layout — no residue.
        private async Task ShowPageAsync(string title, List<string> lines)
        {
            lock (_lock)
            {
                CursorHide();
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine(title);
                AnsiConsole.Write(new Rule());
                foreach (var l in lines) AnsiConsole.MarkupLine(l);
                AnsiConsole.Write(new Rule());
                AnsiConsole.MarkupLine("[grey]— any key or click to close —[/]");
            }
            while (!_exit)
            {
                UiEvent ev;
                try { ev = await _events.Reader.ReadAsync(); }
                catch (ChannelClosedException) { break; }
                if (ev.Kind == UiKind.Quit) { _exit = true; break; }
                if (ev.Kind is UiKind.Key or UiKind.Mouse) break;
            }
            FullRedraw();
        }

        // ── Layout and rendering ──
        private void TrackSize()
        {
            var h = Console.WindowHeight;
            var w = Console.WindowWidth;
            if (h != _height || w != _width) _resized = true;
        }

        private void ComputeLayout()
        {
            _height = Math.Max(6, Console.WindowHeight);
            _width = Math.Max(20, Console.WindowWidth);
            _compact = _height < 18;
            _logoLines = _compact ? 1 : 6;
            _statusLine = 1 + _logoLines;
            _sepLine = _statusLine + 1;
            _historyTop = _sepLine + 1;
            var available = Math.Max(1, _height - _historyTop - 2);
            _paletteHeight = _picker != null
                ? Math.Min(_picker.Items.Count + 1, available)     // title + items
                : (_palette == null ? 0 : Math.Min(PaletteCount(), available));
            _inputLine = _height - _paletteHeight;
            _historyHeight = Math.Max(1, _inputLine - _historyTop);
        }

        private int PaletteCount()
        {
            if (_palette == null) return 0;
            return _palette.Type == Palette.Kind.Commands ? _palette.Commands.Count : _palette.Files.Count;
        }

        private void FullRedraw()
        {
            lock (_lock)
            {
                ComputeLayout();
                CursorHide();
                AnsiConsole.Clear();
                RenderTop();
                RenderHistory();
                RenderBottom();
                _resized = false;
            }
        }

        private void RenderTop()
        {
            // Logo
            for (int i = 0; i < _logoLines && i < LogoLines.Length; i++)
            {
                int line = 1 + i;
                ClearLineAt(line);
                AnsiConsole.Cursor.SetPosition(1, line);
                WriteColoredLogoLine(LogoLines[i]);
            }
            // Status line
            ClearLineAt(_statusLine);
            AnsiConsole.Cursor.SetPosition(1, _statusLine);
            AnsiConsole.Write(new Markup(StatusLine()));

            // Separator
            ClearLineAt(_sepLine);
            AnsiConsole.Cursor.SetPosition(1, _sepLine);
            AnsiConsole.Write(new string('─', Math.Max(1, _width)));
        }

        private void WriteColoredLogoLine(string line)
        {
            var len = Math.Max(1, line.Length - 1);
            for (int col = 0; col < line.Length; col++)
            {
                var ch = line[col];
                if (ch == ' ') { Console.Write(' '); continue; }
                var (r, g, b) = GradientColor(col / (double)len);
                Console.Write($"\x1b[38;2;{r};{g};{b}m{ch}\x1b[0m");
            }
        }

        private string StatusLine()
        {
            var dot = _connected ? "[green]●[/]" : "[red]●[/]";
            var sess = _sessionId.Length > 8 ? _sessionId[..8] : (_sessionId.Length == 0 ? "-" : _sessionId);
            var ctx = _contextWindow > 0 ? $"{_historyTokens:N0}/{_contextWindow:N0}" : "";
            var feats = _features.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            var parts = new List<string>
            {
                dot,
                _serverUrl,
                $"[cyan]{Markup.Escape(_provider)}[/]",
                Markup.Escape(_modelName),
                Markup.Escape(_agentSet),
                $"sess:{sess}",
                ctx,
                _ttsAvailable ? "tts:✓" : "tts:✗",
                _voiceAvailable ? "mic:✓" : "mic:✗",
                feats.Count > 0 ? "f:" + string.Join(",", feats) : "",
                _chatRunning ? "[yellow]generating…[/]" : "",
                _statusNote == "" ? "" : $"[dim]{Markup.Escape(_statusNote)}[/]",
            };
            var nonEmpty = parts.Where(p => p.Length > 0).ToList();
            var text = string.Join(" · ", nonEmpty);
            if (text.Length > _width - 2)
            {
                var kept = new List<string> { dot, _serverUrl };
                foreach (var p in nonEmpty.Skip(2))
                {
                    var candidate = string.Join(" · ", kept.Append(p));
                    if (candidate.Length > _width - 2) break;
                    kept.Add(p);
                }
                text = string.Join(" · ", kept);
            }
            return text;
        }

        private void RenderHistory()
        {
            lock (_lock)
            {
                ComputeLayout();
                var lines = BuildHistoryLines();
                var total = lines.Count;
                var start = Math.Max(0, total - _historyHeight - _scrollFromBottom);
                for (int i = 0; i < _historyHeight; i++)
                {
                    int line = _historyTop + i;
                    ClearLineAt(line);
                    int idx = start + i;
                    if (idx < total)
                    {
                        AnsiConsole.Cursor.SetPosition(1, line);
                        AnsiConsole.Write(new Markup(lines[idx]));
                    }
                }
            }
        }

        private List<string> BuildHistoryLines()
        {
            var lines = new List<string>();
            foreach (var e in _history)
                AppendEntry(lines, e);
            if (_pending != null)
                AppendEntry(lines, _pending);
            return lines;
        }

        private void AppendEntry(List<string> lines, Entry e)
        {
            var label = e.Role switch
            {
                "user" => "[bold cyan]❯ you[/]",
                "agent" => e.Error ? "[bold red]✗ error[/]" : "[bold green]◆ agent[/]",
                _ => "[bold grey]·[/]",
            };
            var wrapped = WrapText(e.Text, Math.Max(1, _width - 3));
            lines.Add(label);
            lines.AddRange(wrapped.Select(w => "   " + Markup.Escape(w)));
        }

        private void RenderBottom()
        {
            lock (_lock)
            {
                ComputeLayout();

                // Input line
                ClearLineAt(_inputLine);
                AnsiConsole.Cursor.SetPosition(1, _inputLine);
                var (view, viewStart) = InputView();
                AnsiConsole.Write(new Markup($"[bold cyan]>[/] {Markup.Escape(view)}"));

                // Palette or picker below the input (dynamic contextual menu)
                if (_picker != null)
                {
                    var start = Math.Max(0, _picker.Selected - (_paletteHeight - 2));
                    start = Math.Min(start, _picker.Items.Count - (_paletteHeight - 1));
                    start = Math.Max(0, start);
                    // Row 1: title + hint
                    int titleLine = _inputLine + 1;
                    ClearLineAt(titleLine);
                    AnsiConsole.Cursor.SetPosition(1, titleLine);
                    AnsiConsole.Write(new Markup($"[bold cyan]{Markup.Escape(_picker.Title)}[/]  [grey]↑↓/wheel move · Enter ok · Esc cancel[/]"));
                    // Items (scrollable)
                    for (int i = 1; i < _paletteHeight; i++)
                    {
                        int line = _inputLine + 1 + i;
                        ClearLineAt(line);
                        int idx = start + i - 1;
                        if (idx >= _picker.Items.Count) continue;
                        AnsiConsole.Cursor.SetPosition(1, line);
                        var sel = idx == _picker.Selected;
                        AnsiConsole.Write(new Markup((sel ? "[reverse]" : "  ") + Markup.Escape(_picker.Items[idx]) + (sel ? "[/]" : "")));
                    }
                }
                else if (_palette != null)
                {
                    var rows = PaletteRows();
                    var start = Math.Max(0, _palette.Selected - _paletteHeight + 1);
                    start = Math.Min(start, Math.Max(0, rows.Count - _paletteHeight));
                    for (int i = 0; i < _paletteHeight; i++)
                    {
                        int line = _inputLine + 1 + i;
                        ClearLineAt(line);
                        int idx = start + i;
                        if (idx >= rows.Count) continue;
                        AnsiConsole.Cursor.SetPosition(1, line);
                        var row = rows[idx];
                        var prefix = (idx == _palette.Selected) ? "[reverse]" : "";
                        var suffix = (idx == _palette.Selected) ? "[/]" : "";
                        AnsiConsole.Write(new Markup(prefix + row + suffix));
                    }
                }

                // Position the hardware cursor on the input line and MAKE IT VISIBLE
                // (it was hidden by FullRedraw): the blinking caret is the input
                // indicator, like in Qwen Code.
                AnsiConsole.Cursor.SetPosition(2 + (_cursor - viewStart), _inputLine);
                CursorShow();
            }
        }

        private List<string> PaletteRows()
        {
            var p = _palette!;
            if (p.Type == Palette.Kind.Commands)
                return p.Commands.Select(c =>
                    $"[cyan]/{c.Name}[/] [dim]{Markup.Escape(c.Args)}[/]  [grey]{c.Help}[/]").ToList();
            return p.Files.Select(f =>
                $"[cyan]{Markup.Escape(f.FileName)}[/] [dim]{f.Id}[/]{(f.Attached ? "  [green]✓ attached[/]" : "")}").ToList();
        }

        private (string View, int ViewStart) InputView()
        {
            var maxLen = Math.Max(1, _width - 2);
            var viewStart = Math.Max(0, _cursor - maxLen + 1);
            var view = _input.Length > viewStart ? _input[viewStart..] : "";
            if (view.Length > maxLen) view = view[..maxLen];
            return (view, viewStart);
        }

        private void RecomputePalette()
        {
            if (_palette == null) return;
            if (_palette.Type == Palette.Kind.Commands)
            {
                var f = _palette.Filter;
                var first = f.Split(' ')[0];
                _palette.Commands = Commands.Where(c =>
                    f.Length == 0 ||
                    c.Name.StartsWith(f, StringComparison.OrdinalIgnoreCase) ||
                    (c.Name + " " + c.Args).StartsWith(f, StringComparison.OrdinalIgnoreCase) ||
                    (c.Aliases?.Any(a => a.StartsWith("/" + f, StringComparison.OrdinalIgnoreCase)) ?? false) ||
                    (first.Length > 0 && c.Name.StartsWith(first, StringComparison.OrdinalIgnoreCase)))  // "/model zai" keeps /model visible
                    .ToList();
            }
            else
            {
                var f = _palette.Filter;
                _palette.Files = _files.Where(x =>
                    x.FileName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                    x.Id.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            _palette.Selected = Math.Clamp(_palette.Selected, 0, Math.Max(0, PaletteCount() - 1));
        }

        private void AddNote(string markup)
        {
            _history.Add(new Entry { Role = "system", Text = markup });
            RenderHistory();
        }

        private void ClearLineAt(int line)
        {
            if (line < 1 || line > _height) return;
            AnsiConsole.Cursor.SetPosition(1, line);
            AnsiConsole.Write(new string(' ', Math.Max(1, _width)));
            AnsiConsole.Cursor.SetPosition(1, line);
        }

        private static void CursorHide()
        {
            try { AnsiConsole.Cursor.Hide(); } catch { }
        }

        private static void CursorShow()
        {
            try { AnsiConsole.Cursor.Show(); } catch { }
        }

        private static (byte R, byte G, byte B) GradientColor(double t)
        {
            (byte r, byte g, byte b) a = (66, 133, 244), b2 = (139, 92, 246), c = (236, 72, 153);
            byte L(byte x, byte y, double u) => (byte)(x + (y - x) * u);
            return t < 0.5
                ? (L(a.r, b2.r, t * 2), L(a.g, b2.g, t * 2), L(a.b, b2.b, t * 2))
                : (L(b2.r, c.r, (t - 0.5) * 2), L(b2.g, c.g, (t - 0.5) * 2), L(b2.b, c.b, (t - 0.5) * 2));
        }

        private void WriteLogo(bool centered)
        {
            var width = Math.Max(20, Console.WindowWidth);
            for (var li = 0; li < LogoLines.Length; li++)
            {
                var line = LogoLines[li].TrimEnd();
                if (centered) Console.Write(new string(' ', Math.Max(0, (width - line.Length) / 2)));
                var len = Math.Max(1, line.Length - 1);
                for (var col = 0; col < line.Length; col++)
                {
                    var ch = line[col];
                    if (ch == ' ') { Console.Write(' '); continue; }
                    var (r, g, b) = GradientColor(col / (double)len);
                    Console.Write($"\x1b[38;2;{r};{g};{b}m{ch}\x1b[0m");
                }
                if (li < LogoLines.Length - 1) Console.WriteLine();
            }
        }

        private static List<string> WrapText(string text, int width)
        {
            var result = new List<string>();
            if (width < 1) width = 1;
            foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = new StringBuilder();
                foreach (var word in para.Split(' '))
                {
                    var sep = line.Length > 0 ? 1 : 0;
                    if (line.Length + word.Length + sep > width)
                    {
                        if (line.Length > 0) { result.Add(line.ToString()); line.Clear(); }
                        var w = word;
                        while (w.Length > width) { result.Add(w[..width]); w = w[width..]; }
                        line.Append(w);
                        continue;
                    }
                    if (sep == 1) line.Append(' ');
                    line.Append(word);
                }
                if (line.Length > 0) result.Add(line.ToString());
            }
            return result;
        }
    }
}