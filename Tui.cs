using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AIOrchestrator;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Terminal.Gui.Drawing.Attribute collides with System.Attribute (implicit using).
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

/// <summary>
/// The Terminal.Gui terminal UI of AgentBridge: a menu bar, an AGENT logo panel, a
/// streaming chat panel with an input line, a status bar, slash-command and file
/// palettes, keyboard shortcuts and mouse support — while the HTTP server keeps
/// answering API calls in the same process. See README.md → "Terminal UI".
/// </summary>
public static class ConsoleTui
{
    /// <summary>Runs the terminal UI against the server at <paramref name="serverUrl"/> until the user exits.</summary>
    public static Task RunAsync(string serverUrl, string? hostError = null)
        => new Tui(serverUrl, hostError).RunAsync();

    private sealed class Tui : IDisposable
    {
        private readonly IApplication _app;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
        private readonly string _serverUrl;
        private readonly string? _hostError;

        private readonly List<Entry> _history = new();
        private readonly List<string> _promptHistory = new();
        private readonly List<FileRef> _files = new();
        private readonly List<string> _attached = new();
        private readonly Dictionary<string, bool> _features = new();

        private Window? _mainWindow;
        private Editor? _chatView;
        private Editor? _inputField;
        private View? _inputArea;
        private Label? _statusLabel;
        private int _inputLines = 1;
        private Scheme _baseScheme = new();
        private bool _inputPlaceholderActive = true;
        private bool _suppressCommandMenu;
        private bool _disposed;

        // Shared state (files/attachments/features/chat control) is touched from the
        // background tasks (HTTP, streaming) as well as the UI thread: guard the
        // collections and the chat CancellationTokenSource with one lock, and make
        // the chat-running flag atomic so a double Enter cannot start two streams.
        private readonly object _stateLock = new();
        private bool _connected;
        private int _chatRunning;   // 0 idle, 1 generating (Interlocked)
        private CancellationTokenSource? _chatCts;
        private Entry? _pending;
        private bool _followBottom = true;
        private string _lastPrompt = "";
        private bool _lastFailed;
        private int _escCount;
        private int _histIndex = -1;
        private string _histDraft = "";

        private string _provider = "";
        private string _modelName = "";
        private int _contextWindow;
        private int _historyTokens;
        private string _sessionId = "";
        private string _agentSet = "default-agent";
        private bool _ttsAvailable, _voiceAvailable;
        private string _ttsDetail = "", _voiceDetail = "";
        private string _statusNote = "";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private const string PlaceholderText = "Type a message or / for commands...";
        private const int MaxHistory = 1000;
        private const int MaxInputLines = 4;

        // Web GUI (Giraffe AI): a static chat client served by its own launcher. First run
        // downloads the repo zip from GitHub and extracts it into a GiraffeAIWebClient folder.
        private const string WebClientZipUrl = "https://github.com/Graphene-Lab/GiraffeAI/archive/refs/heads/main.zip";
        private const string WebClientDirName = "GiraffeAIWebClient";
        // Marker the installed index.html must contain: clients installed before --provider
        // auto-config (urlParams.get('provider')) cannot auto-connect and are re-downloaded.
        private const string WebClientMarker = "urlParams.get('provider')";

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

        private sealed record CliCommand(
            string Name, string Args, string Help, Func<Tui, string, Task> Run, string[]? Aliases = null);

        private static readonly List<CliCommand> Commands = new()
        {
            new("help", "", "Show help: commands, shortcuts, API endpoints, online docs", (t, _) => t.ShowHelpAsync(), new[] { "/?" }),
            new("docs", "", "Open the online documentation in your browser", (t, _) => t.OpenDocsAsync()),
            new("web", "", "Install (first run) and launch the Giraffe AI web client in the browser", (t, _) => t.LaunchWebClientAsync()),
            new("modelsetup", "", "Configure LLM models & providers (add/edit/remove, active model, API keys)", (t, _) => t.ShowModelSetupAsync()),
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
            new("exit", "", "Exit the terminal UI (Ctrl+Q, Ctrl+C twice, Ctrl+D)", (t, _) => t.ExitAsync(), new[] { "/quit" }),
        };

        private static readonly string[] AgentSets = { "default-agent", "web-agent", "search-agent", "research-agent", "word-agent", "spreadsheet-agent", "email-agent", "multi-agent" };

        private const uint SndAsync = 0x0001;
        private const uint SndFilename = 0x00020000;
        [DllImport("winmm.dll", SetLastError = true)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        // ── AGENT logo (ASCII art, one color per line) ──
        private static readonly string[] LogoLines =
        {
            " █████╗  ██████╗ ███████╗███╗   ██╗████████╗",
            "██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝",
            "███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║",
            "██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║",
            "██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║",
            "╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝",
        };

        // Qwen Code CLI brand gradient (#4796E4 → #847ACE → #C3677F), top → bottom.
        private static readonly TuiAttribute[] LogoAttributes =
        {
            new TuiAttribute(Color.BrightBlue, Color.Black),
            new TuiAttribute(Color.BrightBlue, Color.Black),
            new TuiAttribute(Color.BrightMagenta, Color.Black),
            new TuiAttribute(Color.BrightMagenta, Color.Black),
            new TuiAttribute(Color.BrightRed, Color.Black),
            new TuiAttribute(Color.BrightRed, Color.Black),
        };

        private static readonly (string Keys, string What)[] ShortcutTable =
        {
            ("Enter", "Send the message (Shift+Enter: new line) / run the selected command"),
            ("/", "Open the slash-command palette (live, while you type)"),
            ("@", "Open the file palette (toggle chat attachments)"),
            ("?", "Show this shortcuts overlay (empty input)"),
            ("Tab", "Complete the selected command in the palette"),
            ("Esc", "Close dialog · clear input · twice: exit"),
            ("Ctrl+C", "Cancel the reply · clear input · twice: exit"),
            ("Ctrl+D", "Exit (empty input)"),
            ("Ctrl+Y", "Retry the last prompt"),
            ("Ctrl+R", "Reverse-search prompt history"),
            ("Up / Down", "Prompt history at the first/last line (also Ctrl+P / Ctrl+N); otherwise move the caret"),
            ("Left / Right", "Move the cursor (with Ctrl: by word)"),
            ("Ctrl+A / Ctrl+E", "Select all / jump to end of the input"),
            ("Ctrl+U / Ctrl+K", "Delete to start / to end of the line"),
            ("Ctrl+W", "Delete the word before the cursor (also Ctrl+Backspace)"),
            ("PgUp / PgDn", "Scroll the conversation history"),
            ("F1", "Show the full help page"),
            ("F10", "Activate the menu bar"),
        };

        public Tui(string serverUrl, string? hostError)
        {
            _serverUrl = serverUrl;
            _hostError = hostError;
            _http.BaseAddress = new Uri(serverUrl);
            _app = Application.Create().Init();

            // Modern dark theme for the whole main window (views reference it by name).
            _baseScheme = new Scheme
            {
                Normal = new TuiAttribute(Color.White, Color.Black),
                Focus = new TuiAttribute(Color.Black, Color.BrightCyan),
                HotNormal = new TuiAttribute(Color.BrightCyan, Color.Black),
                HotFocus = new TuiAttribute(Color.Black, Color.BrightMagenta),
            };
            SchemeManager.AddScheme("Dark", _baseScheme);
            SchemeManager.AddScheme("Hint", new Scheme
            {
                Normal = new TuiAttribute(Color.Gray, Color.Black),
                Focus = new TuiAttribute(Color.Gray, Color.Black),
            });
            for (int i = 0; i < LogoAttributes.Length; i++)
                SchemeManager.AddScheme($"Logo{i}", new Scheme { Normal = LogoAttributes[i] });

            BuildUI();
            _history.Add(new Entry
            {
                Role = "system",
                Text = "Welcome to AGENT — talk to the agents straight from the terminal.\n"
                     + "Type a message and press Enter · / opens commands · @ files · ? shortcuts · F1 help · F10 the menu.",
            });
            _history.Add(new Entry { Role = "system", Text = $"server: {_serverUrl} — the API keeps answering in parallel ({_serverUrl}/v1/chat/completions)" });
            if (!string.IsNullOrEmpty(_hostError))
                _history.Add(new Entry { Role = "system", Text = $"local host start failed ({_hostError}) — connecting to an existing instance" });
        }

        public Task RunAsync()
        {
            if (_mainWindow is { } window)
            {
                // Give the input the focus once the window is up. The framework's initial
                // focus lands on the first focusable child (the menu bar), so re-assert
                // the input focus after the first iterations: without it, typing and the
                // Esc/Ctrl+C/Ctrl+D exit handling never reach the input.
                window.Initialized += (_, _) => _inputField?.SetFocus();
                _app.AddTimeout(TimeSpan.FromMilliseconds(60), () =>
                {
                    _inputField?.SetFocus();
                    UpdateInputLayout();
                    return false;   // one-shot
                });
                RefreshHistory();
                _ = Task.Run(RefreshServerStateAsync);
                try
                {
                    _app.Run(window);
                }
                finally
                {
                    CancelChat();
                    Dispose();
                }
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CancelChat();
            try { _app.Dispose(); } catch { }
            _http.Dispose();
        }

        private void CancelChat()
        {
            lock (_stateLock) _chatCts?.Cancel();
        }

        // ── UI-thread marshalling ──
        // Terminal.Gui mutates views only on the main loop thread. Every UI touch
        // goes through Ui(); background tasks (HTTP, streaming) queue their updates
        // via IApplication.Invoke. Actions posted after dispose are dropped.
        private void Ui(Action action)
        {
            if (_disposed) return;
            try { _app.Invoke(action); } catch { }
        }

        // ── Layout ──
        private void BuildUI()
        {
            _mainWindow = new Window
            {
                Title = "AGENT - AI Chat Console",
                X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
                SchemeName = "Dark",
            };

            var menu = new MenuBar(new MenuBarItem[]
            {
                new("_File", new MenuItem[]
                {
                    new MenuItem("_New Chat", Key.Empty, () => RunCommandByName("new", "")),
                    new MenuItem("_Models & Providers (/modelsetup)", Key.Empty, () => RunCommandByName("modelsetup", "")),
                    new MenuItem("_Exit", Key.Q.WithCtrl, () => RequestExit()),
                }),
                new("_Chat", new MenuItem[]
                {
                    new MenuItem("_Clear History", Key.L.WithCtrl, () => RunCommandByName("clear", "")),
                    new MenuItem("_Commands (/...)", Key.Empty, () => ShowCommandMenu("")),
                    new MenuItem("_Retry Last (/retry)", Key.Y.WithCtrl, () => RunCommandByName("retry", "")),
                }),
                new("_Session", new MenuItem[]
                {
                    new MenuItem("_LLM Model (/model)", Key.Empty, () => RunCommandByName("model", "")),
                    new MenuItem("_Agent (/agent)", Key.Empty, () => RunCommandByName("agent", "")),
                    new MenuItem("_Status (/status)", Key.Empty, () => RunCommandByName("status", "")),
                    new MenuItem("_Health (/health)", Key.Empty, () => RunCommandByName("health", "")),
                }),
                new("_Web", new MenuItem[]
                {
                    new MenuItem("_GUI (/web)", Key.Empty, () => RunCommandByName("web", "")),
                }),
                new("_Help", new MenuItem[]
                {
                    new MenuItem("_Help (/help)", Key.F1, () => RunCommandByName("help", "")),
                    new MenuItem("_Shortcuts (/shortcuts)", Key.Empty, () => RunCommandByName("shortcuts", "")),
                    new MenuItem("_Documentation (/docs)", Key.Empty, () => RunCommandByName("docs", "")),
                    new MenuItem("_About", Key.Empty, () => ShowAbout()),
                }),
            });
            _mainWindow.Add(menu);

            // Esc never quits the app directly: it is handled by the focused view
            // (input line, dialogs, menus). Guard the window's default Esc→Quit
            // binding so an Esc pressed on a non-handling view (e.g. the send
            // button) cannot close the whole UI accidentally.
            _mainWindow.KeyDown += (_, key) =>
            {
                if (key == Key.Esc) key.Handled = true;
            };

            // Content area below the menu bar (the status bar owns the last row).
            // CanFocus: a plain View defaults to CanFocus=false, which would block
            // focus for every focusable child below it (the input field).
            var contentArea = new View
            {
                X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() - 2,
                CanFocus = true,
            };
            _mainWindow.Add(contentArea);

            // Left panel: the AGENT logo.
            var logoFrame = new FrameView
            {
                Title = "AGENT",
                X = 0, Y = 0, Width = 48, Height = Dim.Fill(),
            };
            contentArea.Add(logoFrame);
            for (int i = 0; i < LogoLines.Length; i++)
            {
                logoFrame.Add(new Label
                {
                    Text = LogoLines[i],
                    X = 1, Y = i + 1,
                    SchemeName = $"Logo{i}",
                });
            }

            // Right panel: chat history + input line.
            var chatFrame = new FrameView
            {
                Title = "Chat",
                X = Pos.Right(logoFrame), Y = 0,
                Width = Dim.Fill(), Height = Dim.Fill(),
            };
            contentArea.Add(chatFrame);

            _chatView = new Editor
            {
                X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() - 1,
                ReadOnly = true,
                WordWrap = true,
                CanFocus = false,
                SchemeName = "Dark",
            };
            chatFrame.Add(_chatView);
            // Auto-follow the stream only while the user is at the bottom; scrolling
            // up (wheel or PgUp) stops the yank until they scroll down or send a message.
            _chatView.MouseEvent += (_, e) =>
            {
                if ((e.Flags & MouseFlags.WheeledUp) != 0) _followBottom = false;
                else if ((e.Flags & MouseFlags.WheeledDown) != 0) _followBottom = true;
            };

            var inputArea = new View
            {
                X = 0, Y = Pos.Bottom(_chatView), Width = Dim.Fill(), Height = 1,
                CanFocus = true,
            };
            chatFrame.Add(inputArea);
            _inputArea = inputArea;

            // Multi-line prompt box: soft-wraps and grows up to MaxInputLines rows (see
            // UpdateInputLayout); full width, so it reaches the right margin of the frame.
            _inputField = new Editor
            {
                X = 0, Y = 0, Width = Dim.Fill(), Height = 1,
                WordWrap = true,
                Multiline = true,
                GutterOptions = GutterOptions.None,
            };
            SetPlaceholder();
            _inputField.HasFocusChanged += OnInputFocusChanged;
            _inputField.KeyDown += OnInputKeyDown;
            _inputField.ContentChanged += (_, _) => { OnInputChanged(); UpdateInputLayout(); };
            _inputField.ViewportChanged += (_, _) => UpdateInputLayout();
            inputArea.Add(_inputField);

            // The Editor consumes the movement keys natively; at a text boundary the key
            // is left unhandled and would bubble up to the Application-level arrow-key
            // focus navigation, moving the focus out of the prompt. Swallow the movement
            // keys at the input's parent so the prompt can never lose focus to an arrow.
            inputArea.KeyDownNotHandled += (_, key) =>
            {
                if (key == Key.CursorLeft || key == Key.CursorRight
                    || key == Key.CursorUp || key == Key.CursorDown
                    || key == Key.CursorLeft.WithCtrl || key == Key.CursorRight.WithCtrl
                    || key == Key.CursorUp.WithCtrl || key == Key.CursorDown.WithCtrl
                    || key == Key.Home || key == Key.End
                    || key == Key.PageUp || key == Key.PageDown)
                    key.Handled = true;
            };

            _statusLabel = new Label
            {
                X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1,
                SchemeName = "Hint",
                Text = "",
            };
            _mainWindow.Add(_statusLabel);
        }

        // ── Input field ──
        private void SetPlaceholder()
        {
            if (_inputField == null) return;
            _inputPlaceholderActive = true;
            _inputField.Text = PlaceholderText;
            _inputField.SchemeName = "Hint";
        }

        private void ClearPlaceholder()
        {
            if (_inputField == null) return;
            _inputPlaceholderActive = false;
            _inputField.Text = "";
            _inputField.SchemeName = "Dark";
        }

        private void OnInputFocusChanged(object? sender, HasFocusEventArgs e)
        {
            if (e.NewValue)
            {
                if (_inputPlaceholderActive) ClearPlaceholder();
            }
            else if (string.IsNullOrWhiteSpace(_inputField?.Text))
            {
                SetPlaceholder();
            }
        }

        private void OnInputChanged()
        {
            if (_suppressCommandMenu || _inputPlaceholderActive) return;
            var t = _inputField?.Text ?? "";
            if (t == "/")
            {
                _suppressCommandMenu = true;
                ShowCommandMenu("");
                _suppressCommandMenu = false;
                if ((_inputField?.Text ?? "") == "/") _inputField!.Text = "";
            }
            else if (t == "@")
            {
                _suppressCommandMenu = true;
                ShowFilesDialog();
                _suppressCommandMenu = false;
                if ((_inputField?.Text ?? "") == "@") _inputField!.Text = "";
            }
            else if (t == "?")
            {
                _suppressCommandMenu = true;
                _ = ShowShortcutsAsync();
                _suppressCommandMenu = false;
                if ((_inputField?.Text ?? "") == "?") _inputField!.Text = "";
            }
        }

        private void OnInputKeyDown(object? sender, Key key)
        {
            if (_inputPlaceholderActive)
                ClearPlaceholder();   // the first keystroke dismisses the hint, then falls through

            if (key == Key.Enter && !key.IsShift)
            {
                key.Handled = true;
                Submit();
            }
            else if (key == Key.Enter.WithShift)
            {
                // Shift+Enter inserts a newline (the Editor binds only plain Enter to NewLine).
                key.Handled = true;
                if (_inputField is { Document: { } doc })
                {
                    var at = _inputField.CaretOffset;
                    doc.Insert(at, "\n");
                    _inputField.CaretOffset = at + 1;
                }
            }
            else if (key == Key.Esc)
            {
                // Keep the window's default "Esc quits" binding from firing: Esc here
                // clears the input, and twice on an empty input exits the app.
                key.Handled = true;
                if ((_inputField?.Text ?? "").Length > 0)
                {
                    _inputField!.Text = "";
                }
                else if (++_escCount >= 2)
                {
                    RequestExit();
                }
                else
                {
                    _statusNote = "Press Esc again to exit · or Ctrl+C twice";
                    UpdateStatusUi();
                }
            }
            else if (key == Key.C.WithCtrl)
            {
                key.Handled = true;
                if (_chatRunning != 0)
                {
                    CancelChat();
                    _statusNote = "cancelling…";
                    UpdateStatusUi();
                }
                else if ((_inputField?.Text ?? "").Length > 0)
                {
                    _inputField!.Text = "";
                }
                else if (++_escCount >= 2)
                {
                    RequestExit();
                }
                else
                {
                    _statusNote = "Press Ctrl+C again to exit";
                    UpdateStatusUi();
                }
            }
            else if (key == Key.D.WithCtrl)
            {
                // Exit on an empty input; otherwise the Editor's native
                // "delete char in front" applies (same guard as the old TUI).
                if ((_inputField?.Text ?? "").Length == 0)
                {
                    key.Handled = true;
                    RequestExit();
                }
            }
            else if (key == Key.Y.WithCtrl)
            {
                key.Handled = true;
                _ = RetryAsync();
            }
            else if (key == Key.R.WithCtrl)
            {
                key.Handled = true;
                ReverseSearch();
            }
            else if (key == Key.F1)
            {
                key.Handled = true;
                _ = ShowHelpAsync();
            }
            else if (key == Key.P.WithCtrl || (key == Key.CursorUp && CaretOnFirstLine()))
            {
                key.Handled = true;
                HistoryPrev();
            }
            else if (key == Key.N.WithCtrl || (key == Key.CursorDown && CaretOnLastLine()))
            {
                key.Handled = true;
                HistoryNext();
            }
            else if (key == Key.U.WithCtrl)
            {
                // Delete from the start of the current line to the insertion point.
                key.Handled = true;
                if (_inputField is { Document: { } doc })
                {
                    var caret = _inputField.CaretOffset;
                    var line = doc.GetLineByOffset(caret);
                    doc.Remove(line.Offset, caret - line.Offset);
                }
            }
            else if (key == Key.K.WithCtrl)
            {
                // Delete from the insertion point to the end of the current line.
                key.Handled = true;
                if (_inputField is { Document: { } doc })
                {
                    var caret = _inputField.CaretOffset;
                    var line = doc.GetLineByOffset(caret);
                    doc.Remove(caret, line.Offset + line.Length - caret);
                }
            }
            else if (key == Key.W.WithCtrl)
            {
                // Delete the word before the insertion point.
                key.Handled = true;
                if (_inputField is { Document: { } doc })
                {
                    var caret = _inputField.CaretOffset;
                    var t = doc.Text;
                    var start = caret;
                    while (start > 0 && char.IsWhiteSpace(t[start - 1])) start--;
                    while (start > 0 && !char.IsWhiteSpace(t[start - 1])) start--;
                    if (start < caret) doc.Remove(start, caret - start);
                }
            }
            else if (key == Key.PageUp)
            {
                key.Handled = true;
                _followBottom = false;
                _chatView?.ScrollVertical(-(_chatView.Viewport.Height - 1));
            }
            else if (key == Key.PageDown)
            {
                key.Handled = true;
                _followBottom = true;
                _chatView?.ScrollVertical(_chatView.Viewport.Height - 1);
            }
            else
            {
                _escCount = 0;
            }
        }

        // ── Multi-line input layout ──
        // The prompt box height tracks its wrapped content (max MaxInputLines rows) and
        // the chat panel shrinks accordingly. Re-runs on content changes and viewport
        // resizes (the wrap column is the editor's viewport width).
        private void UpdateInputLayout()
        {
            if (_inputField is not { } ed || _inputArea is not { } area || _chatView is not { } chat) return;
            if (ed.Viewport.Width <= 0) return;   // not laid out yet
            var rows = Math.Clamp(EstimateWrapRows(ed.Text ?? "", ed.Viewport.Width), 1, MaxInputLines);
            if (rows == _inputLines) return;
            _inputLines = rows;
            ed.Height = rows;
            area.Height = rows;
            chat.Height = Dim.Fill() - rows;
        }

        // Approximate the soft-wrapped visual rows (the Editor wraps at the viewport width).
        private static int EstimateWrapRows(string text, int width)
        {
            if (text.Length == 0) return 1;
            var rows = 0;
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                rows += Math.Max(1, (line.Length + width - 1) / width);
            return rows;
        }

        private bool CaretOnFirstLine()
        {
            if (_inputField is not { Document: { } doc }) return true;
            var line = doc.GetLineByOffset(_inputField.CaretOffset);
            return line.Offset == 0;
        }

        private bool CaretOnLastLine()
        {
            if (_inputField is not { Document: { } doc }) return true;
            var line = doc.GetLineByOffset(_inputField.CaretOffset);
            return line.Offset + line.Length >= doc.TextLength;
        }

        private void HistoryPrev()
        {
            if (_promptHistory.Count == 0) return;
            if (_histIndex < 0)
            {
                _histDraft = _inputField?.Text ?? "";
                _histIndex = _promptHistory.Count - 1;
            }
            else if (_histIndex > 0)
            {
                _histIndex--;
            }
            if (_inputField != null)
            {
                _inputField.Text = _promptHistory[_histIndex];
                _inputField.CaretOffset = _inputField.Document?.TextLength ?? 0;
            }
        }

        private void HistoryNext()
        {
            if (_histIndex < 0) return;
            _histIndex++;
            if (_inputField == null) return;
            if (_histIndex >= _promptHistory.Count)
            {
                _histIndex = -1;
                _inputField.Text = _histDraft;
            }
            else
            {
                _inputField.Text = _promptHistory[_histIndex];
            }
            _inputField.CaretOffset = _inputField.Document?.TextLength ?? 0;
        }

        private void Submit()
        {
            var text = (_inputField?.Text ?? "").Trim();
            if (_inputPlaceholderActive || text.Length == 0) return;
            _inputField!.Text = "";
            _inputPlaceholderActive = false;
            _inputField.SchemeName = "Dark";
            _followBottom = true;

            if (text[0] == '/')
            {
                RunCommandLine(text);
                return;
            }
            _promptHistory.Add(text);
            if (_promptHistory.Count > 200) _promptHistory.RemoveAt(0);
            StartChat(text);
        }

        // ── Chat ──
        private void StartChat(string prompt) => _ = Task.Run(() => SendChatAsync(prompt));

        private async Task SendChatAsync(string prompt)
        {
            if (Interlocked.CompareExchange(ref _chatRunning, 1, 0) != 0)
            {
                _statusNote = "generating… wait, or Ctrl+C to stop";
                UpdateStatusUi();
                return;
            }
            _lastPrompt = prompt;
            _lastFailed = false;
            lock (_stateLock) _chatCts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            Ui(() =>
            {
                _history.Add(new Entry { Role = "user", Text = prompt });
                _pending = new Entry { Role = "agent", Text = "" };
                RefreshHistory();
                UpdateStatus();
            });

            try
            {
                if (!_connected) await RefreshServerStateAsync();
                if (string.IsNullOrEmpty(_sessionId))
                    throw new InvalidOperationException("no session — server unreachable");

                var attached = SnapshotAttached();
                var body = JsonSerializer.Serialize(new
                {
                    model = _agentSet,
                    messages = new[] { new { role = "user", content = prompt } },
                    session_id = _sessionId,
                    file_ids = attached.Count > 0 ? attached : (List<string>?)null,
                    stream = true,
                }, JsonOpts);

                using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _chatCts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await ReadErrorAsync(response);
                    FinishChat(new Entry { Role = "agent", Text = err, Error = true });
                    _lastFailed = true;
                    _statusNote = $"HTTP {(int)response.StatusCode}";
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
                            var chunk = c.GetString();
                            if (string.IsNullOrEmpty(chunk)) continue;
                            _pending!.Text += chunk;
                            if (sw.ElapsedMilliseconds > 80) { sw.Restart(); Ui(RefreshHistory); }
                        }
                    }
                    catch { }
                }

                _connected = true;
                _statusNote = $"replied in {sw.ElapsedMilliseconds / 1000.0:0.0}s";
                FinishChat(_pending);
            }
            catch (OperationCanceledException)
            {
                _statusNote = "cancelled";
                _lastFailed = true;
                FinishChat(new Entry { Role = "agent", Text = "(cancelled)", Error = true });
            }
            catch (Exception ex)
            {
                _statusNote = "error";
                _lastFailed = true;
                _connected = false;
                FinishChat(new Entry { Role = "agent", Text = $"request failed: {ex.Message}", Error = true });
            }
            finally
            {
                Interlocked.Exchange(ref _chatRunning, 0);
                lock (_stateLock)
                {
                    _chatCts?.Dispose();
                    _chatCts = null;
                }
                Ui(() => { RefreshHistory(); UpdateStatus(); });
                _ = Task.Run(RefreshSessionStateAsync);
            }
        }

        // Captures the completed reply on the background thread and appends it to the
        // history on the UI thread (the List<Entry> must only be touched on the main loop).
        private void FinishChat(Entry? done)
        {
            Ui(() =>
            {
                _pending = null;
                if (done != null) _history.Add(done);
                RefreshHistory();
                UpdateStatus();
            });
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

        // ── History rendering ──
        private void RefreshHistory()
        {
            if (_chatView == null) return;
            if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory);
            var sb = new StringBuilder();
            foreach (var e in _history) AppendEntry(sb, e);
            var pending = _pending;   // local copy: FinishChat clears it on the same thread
            if (pending != null) AppendEntry(sb, pending);
            if (_chatView.Document is { } doc)
            {
                doc.Text = sb.ToString();
            }
            else
            {
                _chatView.Document = new TextDocument(sb.ToString());
            }
            // Setting the caret scrolls the viewport so it stays visible (auto-follow),
            // but only while the user has not scrolled up (see _followBottom).
            if (_followBottom)
                _chatView.CaretOffset = Math.Max(0, (_chatView.Document?.TextLength ?? 1) - 1);
        }

        private static void AppendEntry(StringBuilder sb, Entry e)
        {
            sb.Append(e.Role switch
            {
                "user" => "❯ you",
                "agent" => e.Error ? "✗ error" : "◆ agent",
                _ => "·",
            }).Append('\n');
            sb.Append(e.Text.Replace("\r\n", "\n")).Append("\n\n");
        }

        private void AddNote(string text)
        {
            Ui(() =>
            {
                _history.Add(new Entry { Role = "system", Text = text });
                RefreshHistory();
            });
        }

        // Thread-safe snapshots of the state collections (mutated by background tasks).
        private List<string> SnapshotAttached()
        {
            lock (_stateLock) return new List<string>(_attached);
        }

        private string FeaturesSummary()
        {
            lock (_stateLock) return string.Join(",", _features.Where(kv => kv.Value).Select(kv => kv.Key));
        }

        // ── Status bar ──
        private void UpdateStatus()
        {
            if (_statusLabel == null) return;
            var dot = _connected ? "●" : "○";
            var sess = _sessionId.Length > 8 ? _sessionId[..8] : (_sessionId.Length == 0 ? "-" : _sessionId);
            var ctx = _contextWindow > 0 ? $"{_historyTokens:N0}/{_contextWindow:N0}" : "";
            var feats = FeaturesSummary();
            var parts = new List<string>
            {
                dot, _serverUrl, _provider, _modelName, _agentSet,
                $"sess:{sess}", ctx,
                _ttsAvailable ? "tts:✓" : "tts:✗",
                _voiceAvailable ? "mic:✓" : "mic:✗",
                feats.Length > 0 ? "f:" + feats : "",
                _chatRunning != 0 ? "generating…" : "",
                _statusNote,
            };
            var text = string.Join(" · ", parts.Where(p => p.Length > 0));
            _statusLabel.Text = text.Length > 240 ? text[..240] : text;
        }

        private void UpdateStatusUi() => Ui(UpdateStatus);

        // ── Commands ──
        private void RunCommandLine(string text)
        {
            var rest = text[1..].TrimStart();
            var sp = rest.IndexOf(' ');
            var name = sp < 0 ? rest : rest[..sp];
            var args = sp < 0 ? "" : rest[(sp + 1)..].Trim();
            if (name.Length == 0)
            {
                ShowCommandMenu("");
                return;
            }
            var cmd = Commands.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
                (c.Aliases?.Any(a => a.Equals("/" + name, StringComparison.OrdinalIgnoreCase)) ?? false));
            if (cmd == null)
            {
                AddNote($"unknown command /{name} — type / to see the list, or /help");
                return;
            }
            RunCommand(cmd, args);
        }

        private void RunCommand(CliCommand cmd, string args) => _ = RunCommandAsync(cmd, args);

        // Menu items route through the same guarded path as the slash commands, so
        // every command has identical error handling and state refresh.
        private void RunCommandByName(string name, string args)
        {
            var cmd = Commands.FirstOrDefault(c => c.Name == name);
            if (cmd != null) _ = RunCommandAsync(cmd, args);
        }

        private async Task RunCommandAsync(CliCommand cmd, string args)
        {
            try
            {
                await cmd.Run(this, args);
            }
            catch (Exception ex)
            {
                AddNote($"/{cmd.Name} failed: {ex.Message}");
            }
            await RefreshSessionStateAsync();
            UpdateStatusUi();
        }

        private Task ExitAsync()
        {
            Ui(RequestExit);
            return Task.CompletedTask;
        }

        private void RequestExit() => _app.RequestStop();

        private async Task HealthAsync()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await _http.GetAsync("/health").WaitAsync(TimeSpan.FromSeconds(5));
                sw.Stop();
                AddNote(resp.IsSuccessStatusCode
                    ? $"server healthy · {sw.ElapsedMilliseconds} ms"
                    : $"server returned HTTP {(int)resp.StatusCode}");
                _connected = resp.IsSuccessStatusCode;
                UpdateStatusUi();
            }
            catch (Exception ex)
            {
                AddNote($"server unreachable: {ex.Message}");
                _connected = false;
                UpdateStatusUi();
            }
        }

        private async Task SwitchModelAsync(string args)
        {
            string name = args.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                List<string> providers;
                try
                {
                    using var resp = await _http.GetAsync("/v1/models").WaitAsync(TimeSpan.FromSeconds(8));
                    if (!resp.IsSuccessStatusCode) { AddNote("could not load providers"); return; }
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    providers = doc.RootElement.GetProperty("data").EnumerateArray()
                        .Where(x => GetStr(x, "owned_by") == "llm-provider")
                        .Select(x => $"{GetStr(x, "id")} — {GetStr(x, "model_name")} · ctx {GetInt(x, "context_window"):N0}")
                        .ToList();
                }
                catch (Exception ex)
                {
                    AddNote($"could not load providers: {ex.Message}");
                    return;
                }
                if (providers.Count == 0) { AddNote("no providers reported by the server"); return; }
                var pick = await PickOnUiThreadAsync("Switch LLM provider", providers);
                if (pick == null) return;   // Esc → cancel
                name = pick[..pick.IndexOf(" —", StringComparison.Ordinal)];
            }

            if (string.Equals(name, _provider, StringComparison.OrdinalIgnoreCase))
            {
                AddNote($"already on {name}");
                return;
            }

            AddNote($"switching provider to {name}… (some providers take minutes to warm up)");
            var body = JsonSerializer.Serialize(new { session_id = _sessionId, llm_provider = name }, JsonOpts);
            using var resp2 = await _http.PostAsync("/v1/control", new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp2.IsSuccessStatusCode)
            {
                AddNote($"provider is now {name}");
            }
            else
            {
                AddNote($"switch refused (HTTP {(int)resp2.StatusCode}): {await ReadErrorAsync(resp2)}");
            }
        }

        private async Task SwitchAgentAsync(string args)
        {
            string name;
            if (string.IsNullOrWhiteSpace(args.Trim()))
            {
                var pick = await PickOnUiThreadAsync("Switch agent set", AgentSets.ToList());
                if (pick == null) return;
                name = pick;
            }
            else
            {
                name = args.Trim().ToLowerInvariant();
            }
            if (!AgentSets.Contains(name))
            {
                AddNote($"unknown agent set '{name}' — {string.Join(", ", AgentSets)}");
                return;
            }
            _agentSet = name;
            AddNote($"agent set: {name}");
        }

        private async Task VoiceAsync(string lang)
        {
            if (!_voiceAvailable)
            {
                AddNote($"voice unavailable: {(string.IsNullOrEmpty(_voiceDetail) ? "POST /v1/voice/listen is disabled" : _voiceDetail)}");
                return;
            }
            var l = string.IsNullOrWhiteSpace(lang) ? SystemLang.Get() : lang.Trim();
            AddNote("listening… (server microphone) — speak now");
            var body = JsonSerializer.Serialize(new { lang = l, timeout_seconds = 15 }, JsonOpts);
            try
            {
                using var resp = await _http.PostAsync("/v1/voice/listen", new StringContent(body, Encoding.UTF8, "application/json"));
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var text = GetStr(doc.RootElement, "text") ?? "";
                    if (string.IsNullOrWhiteSpace(text)) { AddNote("no speech recognised"); return; }
                    Ui(() =>
                    {
                        if (_inputField == null) return;
                        _inputField.Text = text;
                        _inputPlaceholderActive = false;
                        _inputField.SchemeName = "Dark";
                        _inputField.CaretOffset = _inputField.Document?.TextLength ?? 0;
                    });
                    AddNote($"dictated {text} — press Enter to send");
                }
                else
                {
                    var err = await ReadErrorAsync(resp);
                    AddNote(resp.StatusCode == System.Net.HttpStatusCode.RequestTimeout
                        ? "listening timed out (no speech detected)"
                        : $"voice failed (HTTP {(int)resp.StatusCode}): {err}");
                }
            }
            catch (Exception ex)
            {
                AddNote($"voice failed: {ex.Message}");
            }
        }

        private async Task TtsAsync(string text)
        {
            if (!_ttsAvailable)
            {
                AddNote($"tts unavailable: {(string.IsNullOrEmpty(_ttsDetail) ? "POST /v1/audio/speech is disabled" : _ttsDetail)}");
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                var last = _history.LastOrDefault(e => e.Role == "agent" && !e.Error)?.Text;
                if (string.IsNullOrWhiteSpace(last)) { AddNote("nothing to speak — give text: /tts <text>"); return; }
                text = last;
            }
            AddNote("synthesising…");
            var body = JsonSerializer.Serialize(new { input = text, lang = SystemLang.Get(), speed = 1.0 }, JsonOpts);
            try
            {
                using var resp = await _http.PostAsync("/v1/audio/speech", new StringContent(body, Encoding.UTF8, "application/json"));
                if (!resp.IsSuccessStatusCode)
                {
                    AddNote($"tts failed (HTTP {(int)resp.StatusCode}): {await ReadErrorAsync(resp)}");
                    return;
                }
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                var path = Path.Combine(Path.GetTempPath(), $"agent_tts_{DateTime.Now:yyyyMMddHHmmss}.wav");
                await File.WriteAllBytesAsync(path, bytes);
                AddNote($"saved {path} ({bytes.Length:N0} bytes)");
                if (OperatingSystem.IsWindows())
                {
                    if (!PlaySound(path, IntPtr.Zero, SndAsync | SndFilename))
                        AddNote($"playback failed — open the file with your media player: {path}");
                }
                else
                {
                    AddNote("playback is Windows-only here — open the file with your media player");
                }
            }
            catch (Exception ex)
            {
                AddNote($"tts failed: {ex.Message}");
            }
        }

        private async Task FeaturesAsync(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                string current;
                lock (_stateLock)
                    current = _features.Count == 0 ? "(none set)" :
                        string.Join(", ", _features.Select(kv => $"{kv.Key}={(kv.Value ? "on" : "off")}"));
                AddNote($"session features: {current}");
                return;
            }
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var name = parts[0].ToLowerInvariant();
            bool value;
            if (parts.Length >= 2 && parts[1].ToLowerInvariant() is "on" or "true") value = true;
            else if (parts.Length >= 2 && parts[1].ToLowerInvariant() is "off" or "false") value = false;
            else
            {
                lock (_stateLock) value = !(_features.TryGetValue(name, out var cur) && cur);
            }
            lock (_stateLock) _features[name] = value;
            var body = JsonSerializer.Serialize(new { session_id = _sessionId, features = new Dictionary<string, bool> { [name] = value } }, JsonOpts);
            using var resp = await _http.PostAsync("/v1/control", new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode) AddNote($"feature {name} = {(value ? "on" : "off")}");
            else AddNote($"failed (HTTP {(int)resp.StatusCode}): {await ReadErrorAsync(resp)}");
        }

        private async Task NewSessionAsync()
        {
            using var resp = await _http.PostAsync("/v1/control", new StringContent("{\"create\":true}", Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) { AddNote($"could not create session (HTTP {(int)resp.StatusCode})"); return; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            _sessionId = GetStr(doc.RootElement, "session_id") ?? "";
            var shortId = _sessionId[..Math.Min(8, _sessionId.Length)];
            Ui(() =>
            {
                _history.Clear();
                lock (_stateLock)
                {
                    _attached.Clear();
                    foreach (var f in _files) f.Attached = false;
                }
                _history.Add(new Entry { Role = "system", Text = $"new session {shortId}" });
                RefreshHistory();
            });
            await RefreshSessionStateAsync();
        }

        private async Task ClearHistoryAsync()
        {
            var body = JsonSerializer.Serialize(new { session_id = _sessionId, reset_history = true }, JsonOpts);
            using var resp = await _http.PostAsync("/v1/control", new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode)
            {
                Ui(() =>
                {
                    _history.Clear();
                    _history.Add(new Entry { Role = "system", Text = "session history cleared" });
                    RefreshHistory();
                });
            }
            else
            {
                AddNote($"failed (HTTP {(int)resp.StatusCode}): {await ReadErrorAsync(resp)}");
            }
        }

        private Task ShowStatusAsync()
        {
            string feats, attached;
            lock (_stateLock)
            {
                feats = _features.Count == 0 ? "(none)" : string.Join(", ", _features.Select(kv => $"{kv.Key}={kv.Value}"));
                attached = _attached.Count == 0 ? "(none)" : string.Join(", ", _attached);
            }
            var lines = new List<string>
            {
                $"Session        {_sessionId}",
                $"Provider       {_provider}  ({_modelName})",
                $"Context window {_contextWindow:N0} tokens · history ≈ {_historyTokens:N0}",
                $"Agent set      {_agentSet}",
                $"Features       {feats}",
                $"Attachments    {attached}",
                "",
                $"Capabilities   tts {(_ttsAvailable ? "available" : "unavailable")} · voice {(_voiceAvailable ? "available" : "unavailable")}",
                $"               server: {_serverUrl} {(_connected ? "connected" : "unreachable")}",
                $"               prompt history: {_promptHistory.Count} entries",
            };
            return ShowPageUiAsync("agent status", lines);
        }

        private async Task FilesAsync(string args)
        {
            if (string.IsNullOrWhiteSpace(args) || args == "list")
            {
                await RefreshFilesAsync();
                List<string> lines;
                lock (_stateLock)
                {
                    if (_files.Count == 0) { AddNote("no uploaded files — use /files add <path>"); return; }
                    lines = _files.Select(f => $"{f.FileName}  {f.Id} · {f.Status}{(f.Attached ? "  attached" : "")}").ToList();
                }
                await ShowPageUiAsync("uploaded files", lines);
                return;
            }
            var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var sub = parts[0].ToLowerInvariant();
            if (sub == "add" && parts.Length == 2)
            {
                var path = parts[1].Trim('"');
                if (!File.Exists(path)) { AddNote($"file not found: {path}"); return; }
                AddNote($"uploading {Path.GetFileName(path)}…");
                try
                {
                    await using var fs = File.OpenRead(path);
                    using var form = new MultipartFormDataContent();
                    form.Add(new StreamContent(fs), "file", Path.GetFileName(path));
                    form.Add(new StringContent("assistants"), "purpose");
                    using var resp = await _http.PostAsync("/v1/files", form);
                    if (!resp.IsSuccessStatusCode)
                    {
                        AddNote($"upload failed (HTTP {(int)resp.StatusCode}): {await ReadErrorAsync(resp)}");
                        return;
                    }
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var id = GetStr(doc.RootElement, "id") ?? "";
                    var name = GetStr(doc.RootElement, "filename") ?? Path.GetFileName(path);
                    lock (_stateLock)
                    {
                        _files.RemoveAll(x => x.Id == id);
                        _files.Add(new FileRef { Id = id, FileName = name, Status = GetStr(doc.RootElement, "status") ?? "", Attached = true });
                        if (!_attached.Contains(id)) _attached.Add(id);
                    }
                    AddNote($"uploaded + attached {name} ({id})");
                }
                catch (Exception ex)
                {
                    AddNote($"upload failed: {ex.Message}");
                }
            }
            else if (sub == "rm" && parts.Length == 2)
            {
                var id = parts[1].Trim();
                try
                {
                    using var resp = await _http.DeleteAsync($"/v1/files/{Uri.EscapeDataString(id)}");
                    if (resp.IsSuccessStatusCode)
                    {
                        lock (_stateLock)
                        {
                            _files.RemoveAll(x => x.Id == id);
                            _attached.Remove(id);
                        }
                        AddNote($"deleted {id}");
                    }
                    else
                    {
                        AddNote($"delete failed (HTTP {(int)resp.StatusCode}): {await ReadErrorAsync(resp)}");
                    }
                }
                catch (Exception ex)
                {
                    AddNote($"delete failed: {ex.Message}");
                }
            }
            else
            {
                AddNote("usage: /files add <path> | /files rm <id> | /files");
            }
        }

        private async Task AttachAsync(string args)
        {
            await RefreshFilesAsync();
            List<FileRef> files;
            lock (_stateLock) files = _files.ToList();
            if (string.IsNullOrWhiteSpace(args))
            {
                if (files.Count == 0) { AddNote("no uploaded files — use /files add <path>"); return; }
                var choices = files.Select(f => $"{f.FileName}  ({f.Id}){(f.Attached ? "  [attached]" : "")}").ToList();
                var pick = await PickOnUiThreadAsync("Toggle file attachment", choices);
                if (pick == null) return;
                var name = pick[..pick.IndexOf("  (", StringComparison.Ordinal)];
                ToggleAttach(files.First(x => x.FileName == name));
                return;
            }
            var byArg = files.FirstOrDefault(x => x.Id == args.Trim() || x.FileName.Equals(args.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byArg == null) { AddNote($"unknown file '{args.Trim()}' — /files to list"); return; }
            ToggleAttach(byArg);
        }

        private async Task RefreshFilesAsync()
        {
            try
            {
                using var resp = await _http.GetAsync("/v1/files").WaitAsync(TimeSpan.FromSeconds(8));
                if (!resp.IsSuccessStatusCode) return;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("data", out var data)) return;
                lock (_stateLock)
                {
                    _files.Clear();
                    foreach (var f in data.EnumerateArray())
                    {
                        var id = GetStr(f, "id") ?? "";
                        var name = GetStr(f, "filename") ?? id;
                        _files.Add(new FileRef { Id = id, FileName = name, Status = GetStr(f, "status") ?? "", Attached = _attached.Contains(id) });
                    }
                }
            }
            catch { }
        }

        private void ToggleAttach(FileRef f)
        {
            bool attached;
            lock (_stateLock)
            {
                f.Attached = !f.Attached;
                attached = f.Attached;
                if (attached) { if (!_attached.Contains(f.Id)) _attached.Add(f.Id); }
                else _attached.Remove(f.Id);
            }
            AddNote(attached ? $"attached {f.FileName} to the chat" : $"detached {f.FileName}");
        }

        private Task RetryAsync()
        {
            if (string.IsNullOrEmpty(_lastPrompt))
            {
                AddNote("nothing to retry yet");
                return Task.CompletedTask;
            }
            if (!_lastFailed && _history.Count > 0) AddNote("the last reply succeeded — still resending");
            StartChat(_lastPrompt);
            return Task.CompletedTask;
        }

        private Task OpenDocsAsync()
        {
            const string url = "https://github.com/Graphene-Lab/AgentBridge";
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                AddNote($"opened {url} in your browser");
            }
            catch (Exception ex)
            {
                AddNote($"could not open the browser: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        // ── Web client (Giraffe AI) ──
        // The web GUI is a tiny static app (single index.html + its own launcher) hosted on
        // GitHub. First use downloads the repo zip into a GiraffeAIWebClient folder next to
        // the working directory, then the platform launcher (start.bat / start.sh) serves it
        // on http://localhost:8000 and opens the browser. Connectivity failures surface as
        // friendly notes instead of crashing the UI.
        private async Task LaunchWebClientAsync()
        {
            var dir = Path.Combine(Environment.CurrentDirectory, WebClientDirName);
            try
            {
                if (!IsWebClientCurrent(dir))
                {
                    AddNote($"web client missing or outdated at {dir} — downloading {WebClientZipUrl}…");
                    await InstallWebClientAsync(dir);
                }
                AddNote($"launching web client from {dir}…");
                LaunchWebClientProcess(dir);
            }
            catch (Exception ex)
            {
                AddNote($"web client failed: {ex.Message}");
            }
        }

        // A valid install has an index.html carrying the --provider auto-config marker.
        private static bool IsWebClientCurrent(string dir)
        {
            try
            {
                var index = Path.Combine(dir, "index.html");
                return File.Exists(index) && File.ReadAllText(index).Contains(WebClientMarker);
            }
            catch { return false; }
        }

        private async Task InstallWebClientAsync(string dir)
        {
            if (Directory.Exists(dir) && !IsWebClientCurrent(dir))
                Directory.Delete(dir, true);   // stale/partial install from a previous run

            var tmpZip = Path.Combine(Path.GetTempPath(), $"giraffe_{Guid.NewGuid():N}.zip");
            var tmpDir = Path.Combine(Path.GetTempPath(), $"giraffe_{Guid.NewGuid():N}");
            try
            {
                try
                {
                    using var resp = await _http.GetAsync(WebClientZipUrl, HttpCompletionOption.ResponseHeadersRead)
                        .WaitAsync(TimeSpan.FromSeconds(60));
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"download failed (HTTP {(int)resp.StatusCode})");
                    await using (var fs = File.Create(tmpZip))
                        await resp.Content.CopyToAsync(fs);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    throw new InvalidOperationException(
                        $"no internet connection or GitHub unreachable ({ex.Message})");
                }

                Directory.CreateDirectory(tmpDir);
                ZipFile.ExtractToDirectory(tmpZip, tmpDir);

                // The GitHub archive wraps everything in a GiraffeAI-main/ root folder:
                // hoist its contents into the target directory.
                var root = Directory.GetDirectories(tmpDir).FirstOrDefault() ?? tmpDir;
                Directory.CreateDirectory(dir);
                foreach (var item in Directory.GetFileSystemEntries(root))
                {
                    var target = Path.Combine(dir, Path.GetFileName(item));
                    if (Directory.Exists(item)) Directory.Move(item, target);
                    else File.Move(item, target);
                }
                if (!File.Exists(Path.Combine(dir, "index.html")))
                    throw new InvalidOperationException("the downloaded archive did not contain index.html");
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
        }

        private void LaunchWebClientProcess(string dir)
        {
            // Auto-connect the web client to this server: the launcher appends the JSON to
            // the opened URL (?provider=...) and index.html registers+selects the provider.
            var provider = JsonSerializer.Serialize(new
            {
                name = "AgentBridge",
                format = "openai",
                model = _agentSet,
                endpoint = $"{_serverUrl.TrimEnd('/')}/v1/chat/completions",
            }, JsonOpts);

            if (OperatingSystem.IsWindows())
            {
                var bat = Path.Combine(dir, "start.bat");
                if (!File.Exists(bat)) throw new InvalidOperationException($"missing {bat}");
                // cmd start: detached window, returns immediately without blocking the TUI.
                // The JSON travels base64url-encoded (no padding): embedded quotes or '='
                // would be mangled by the cmd.exe command line (start.bat decodes it
                // before building the browser URL).
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(provider))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" \"{bat}\" --provider {b64}")
                {
                    UseShellExecute = false,
                    WorkingDirectory = dir,
                });
            }
            else
            {
                var sh = Path.Combine(dir, "start.sh");
                if (!File.Exists(sh)) throw new InvalidOperationException($"missing {sh}");
                Process.Start(new ProcessStartInfo("bash", new[] { sh, "--provider", provider })
                {
                    UseShellExecute = false,
                    WorkingDirectory = dir,
                });
            }
        }

        private Task ShowHelpAsync()
        {
            var lines = new List<string>
            {
                "QUICK START",
                "  • Type a message and press Enter to talk to the agents (default/web/search/word/spreadsheet/email/multi).",
                "  • / opens the command palette (filters as you type): /model switches LLM, /files uploads documents, /voice dictates, /tts speaks.",
                "  • @ opens the uploaded files (attach/detach) · ? shortcuts · F1 this help · F10 the top menu.",
                "  • The bottom bar shows server, provider, model, session and context.",
                "",
                "COMMANDS  (type / to open the live list)",
            };
            foreach (var c in Commands)
                lines.Add($"  /{c.Name} {c.Args}".TrimEnd().PadRight(28) + c.Help);
            lines.Add("");
            lines.Add("KEYBOARD SHORTCUTS  (press ? on an empty input for the overlay)");
            foreach (var (keys, what) in ShortcutTable)
                lines.Add($"  {keys.PadRight(24)} {what}");
            lines.Add("");
            lines.Add("MOUSE  (Terminal.Gui native, cross-platform)");
            lines.Add("  wheel        Scroll the conversation and menus");
            lines.Add("  click input  Position the text cursor");
            lines.Add("  click row    Select a list/dialog row (double-click runs it)");
            lines.Add("");
            lines.Add("API (the same server keeps answering while you chat)");
            lines.Add("  POST /v1/chat/completions · /v1/control · /v1/audio/speech · /v1/voice/listen");
            lines.Add("  GET  /v1/models · /v1/files · /v1/control · /v1/audio/voices · /health");
            lines.Add("  POST /v1/files (upload · attach via @)");
            lines.Add("");
            lines.Add("ONLINE HELP");
            lines.Add("  AgentBridge repo/docs: https://github.com/Graphene-Lab/AgentBridge  (/docs opens it)");
            lines.Add("  README.md → \"Terminal UI\"");
            return ShowPageUiAsync("agent help", lines);
        }

        private Task ShowShortcutsAsync()
        {
            var lines = ShortcutTable.Select(s => $"  {s.Keys.PadRight(24)} {s.What}").ToList();
            lines.Insert(0, "KEYBOARD SHORTCUTS");
            lines.Add("");
            lines.Add("Full help: /help · commands: type /");
            return ShowPageUiAsync("shortcuts", lines);
        }

        private void ReverseSearch()
        {
            var dlg = new Dialog
            {
                Title = "reverse prompt history",
                Width = Dim.Percent(85),
                Height = Dim.Percent(50),
                SchemeName = "Dark",
            };
            var q = new TextField { X = 0, Y = 0, Width = Dim.Fill() };
            var list = new ListView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() - 2 };
            var hint = new Label
            {
                Text = "type to filter the history · ↑↓ · Enter selects · Esc closes",
                X = 0, Y = Pos.Bottom(list), Width = Dim.Fill(),
            };
            var matches = new List<string>();
            void Recompute()
            {
                var f = (q.Text ?? "").Trim();
                matches = _promptHistory.Where(p => p.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
                list.Source = new ListWrapper<string>(new ObservableCollection<string>(matches));
                list.SelectedItem = Math.Clamp(list.SelectedItem ?? 0, 0, Math.Max(0, matches.Count - 1));
            }
            Recompute();
            q.ValueChanged += (_, _) => Recompute();
            string? picked = null;
            q.KeyDown += (_, key) =>
            {
                if (key == Key.Enter)
                {
                    key.Handled = true;
                    if (matches.Count > 0) picked = matches[Math.Max(0, list.SelectedItem ?? 0)];
                    _app.RequestStop(dlg);
                }
            };
            list.Accepted += (_, e) =>
            {
                e.Handled = true;
                if (matches.Count > 0) picked = matches[Math.Max(0, list.SelectedItem ?? 0)];
                _app.RequestStop(dlg);
            };
            dlg.Add(q, list, hint);
            dlg.Initialized += (_, _) => q.SetFocus();
            _app.Run(dlg);
            dlg.Dispose();
            if (picked != null && _inputField != null)
            {
                _inputField.Text = picked;
                _inputPlaceholderActive = false;
                _inputField.SchemeName = "Dark";
                _inputField.CaretOffset = _inputField.Document?.TextLength ?? 0;
            }
            _inputField?.SetFocus();
        }

        // ── Dialogs ──
        // Runs a modal picker (title + items) on the UI thread; returns the selected
        // item or null when cancelled with Esc. Must only be called from Ui(...).
        private string? RunPickerDialog(string title, IReadOnlyList<string> items)
        {
            if (items.Count == 0) return null;
            var dlg = new Dialog
            {
                Title = title,
                Width = Dim.Percent(70),
                Height = Dim.Percent(60),
                SchemeName = "Dark",
            };
            var list = new ListView
            {
                X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() - 1,
                Source = new ListWrapper<string>(new ObservableCollection<string>(items)),
            };
            var hint = new Label
            {
                Text = "↑↓ navigate · Enter select · Esc cancel",
                X = 0, Y = Pos.Bottom(list), Width = Dim.Fill(),
            };
            string? result = null;
            list.Accepted += (_, e) =>
            {
                e.Handled = true;
                result = items[Math.Max(0, list.SelectedItem ?? 0)];
                _app.RequestStop(dlg);
            };
            dlg.Add(list, hint);
            dlg.Initialized += (_, _) => list.SetFocus();
            _app.Run(dlg);
            dlg.Dispose();
            _inputField?.SetFocus();
            return result;
        }

        private Task<string?> PickOnUiThreadAsync(string title, IReadOnlyList<string> items)
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(() =>
            {
                try { tcs.TrySetResult(RunPickerDialog(title, items)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        // Full-screen page (help/status): a modal dialog, any key or click closes it.
        private void ShowPage(string title, IReadOnlyList<string> lines)
        {
            var dlg = new Dialog
            {
                Title = title,
                Width = Dim.Percent(92),
                Height = Dim.Percent(92),
                SchemeName = "Dark",
            };
            var tv = new Editor
            {
                ReadOnly = true,
                WordWrap = false,
                CanFocus = false,
                X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() - 2,
                Document = new TextDocument(string.Join("\n", lines)),
            };
            var hint = new Label
            {
                Text = "— scroll with ↑↓ / PgUp-PgDn · close with Esc or Enter —",
                X = 0, Y = Pos.Bottom(tv), Width = Dim.Fill(),
            };
            dlg.Add(tv, hint);
            dlg.AddButton(new Button { Text = "Close" });
            _app.Run(dlg);
            dlg.Dispose();
            _inputField?.SetFocus();
        }

        private Task ShowPageUiAsync(string title, IReadOnlyList<string> lines)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(() =>
            {
                try { ShowPage(title, lines); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        // The "/" live palette: filter field + command list (filters as you type),
        // Enter runs the selected command (or the typed command line), Esc cancels.
        private void ShowCommandMenu(string initial)
        {
            var dlg = new Dialog
            {
                Title = "Available commands",
                Width = Dim.Percent(80),
                Height = Dim.Percent(60),
                SchemeName = "Dark",
            };
            var filter = new TextField { Text = initial, X = 0, Y = 0, Width = Dim.Fill() };
            var list = new ListView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() - 2 };
            var hint = new Label
            {
                Text = "type to filter · ↑↓ · Tab completes · Enter runs · Esc closes",
                X = 0, Y = Pos.Bottom(list), Width = Dim.Fill(),
            };
            var visible = new List<CliCommand>();
            void Recompute()
            {
                var f = (filter.Text ?? "").Trim();
                visible = Commands.Where(c => MatchCommand(c, f)).ToList();
                list.Source = new ListWrapper<string>(new ObservableCollection<string>(
                    visible.Select(c => $"/{c.Name} {c.Args}".TrimEnd() + "  —  " + c.Help)));
                list.SelectedItem = Math.Clamp(list.SelectedItem ?? 0, 0, Math.Max(0, visible.Count - 1));
            }
            Recompute();
            filter.ValueChanged += (_, _) => Recompute();
            filter.KeyDown += (_, key) =>
            {
                if (key == Key.Enter)
                {
                    key.Handled = true;
                    RunCommandLine(CommandTextFromDialog(filter.Text, visible, list));
                    _app.RequestStop(dlg);
                }
                else if (key == Key.Tab)
                {
                    // Complete the selected command name into the filter (like the old TUI).
                    key.Handled = true;
                    if (visible.Count > 0)
                    {
                        var cmd = visible[Math.Max(0, list.SelectedItem ?? 0)];
                        filter.Text = "/" + cmd.Name + (cmd.Args.Length > 0 ? " " : "");
                    }
                }
            };
            list.Accepted += (_, e) =>
            {
                e.Handled = true;
                RunCommandLine(CommandTextFromDialog(filter.Text, visible, list));
                _app.RequestStop(dlg);
            };
            dlg.Add(filter, list, hint);
            dlg.Initialized += (_, _) => filter.SetFocus();
            _app.Run(dlg);
            dlg.Dispose();
            _inputField?.SetFocus();
        }

        private static string CommandTextFromDialog(string? filterText, List<CliCommand> visible, ListView list)
        {
            var text = (filterText ?? "").Trim();
            if (text.Length == 0 && visible.Count > 0)
                text = "/" + visible[Math.Max(0, list.SelectedItem ?? 0)].Name;
            if (!text.StartsWith('/')) text = "/" + text;
            return text;
        }

        private static bool MatchCommand(CliCommand c, string filter)
        {
            if (filter.Length == 0) return true;
            var first = filter.Split(' ')[0];
            return c.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)
                || (c.Name + " " + c.Args).StartsWith(filter, StringComparison.OrdinalIgnoreCase)
                || (c.Aliases?.Any(a => a.StartsWith("/" + filter, StringComparison.OrdinalIgnoreCase)) ?? false)
                || (first.Length > 0 && c.Name.StartsWith(first, StringComparison.OrdinalIgnoreCase));
        }

        // The "@" live palette: uploaded files, Enter toggles the attachment.
        private void ShowFilesDialog()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshFilesAsync();
                    Ui(() =>
                    {
                        List<FileRef> files;
                        lock (_stateLock) files = _files.ToList();
                        if (files.Count == 0) { AddNote("no uploaded files — use /files add <path>"); return; }
                        var choices = files.Select(f => $"{f.FileName}  ({f.Id}){(f.Attached ? "  [attached]" : "")}").ToList();
                        var pick = RunPickerDialog("Toggle file attachment", choices);
                        if (pick != null)
                        {
                            var name = pick[..pick.IndexOf("  (", StringComparison.Ordinal)];
                            ToggleAttach(files.First(x => x.FileName == name));
                        }
                    });
                }
                catch (Exception ex)
                {
                    AddNote($"could not load files: {ex.Message}");
                }
            });
        }

        private void ShowAbout()
        {
            _ = MessageBox.Query(_app, "AGENT", "AGENT v2 - Modern TUI\nPowered by Terminal.Gui", "OK");
        }

        // ── Models & providers setup ──
        // A tabbed modal window (models/providers, email SMTP, IMAP, general) mirroring the
        // AIOrchestrator settings. Field edits are applied on Save; provider list operations
        // (Add/Edit/Remove) apply immediately and persist to providers.json.
        private Task ShowModelSetupAsync()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(() =>
            {
                try { ShowModelSetupDialog(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        private void ShowModelSetupDialog()
        {
            var dlg = new Dialog
            {
                Title = "Models & Providers setup",
                Width = Dim.Percent(80),
                Height = Dim.Percent(70),
                SchemeName = "Dark",
            };

            var tabs = new Tabs
            {
                X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            };

            // ── LLM / Providers tab ──
            var providerDropdown = new DropDownList { ReadOnly = true };
            var providersList = new ListView();
            TextField deepSeekKey = null!, zaiKey = null!, geminiKey = null!;
            var llmTab = new View { Title = "LLM & Providers", CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill() };
            {
                providerDropdown.X = 17; providerDropdown.Y = 0; providerDropdown.Width = 46;
                llmTab.Add(new Label { Text = "Active provider", X = 1, Y = 0, Width = 15 }, providerDropdown);

                int y = 2;
                deepSeekKey = AddField(llmTab, "DeepSeek API key", AIOrchestrator.Setup.DeepSeekApiKey, y++);
                zaiKey = AddField(llmTab, "Z.ai API key", AIOrchestrator.Setup.ZaiApiKey, y++);
                geminiKey = AddField(llmTab, "Gemini API key", AIOrchestrator.Setup.GeminiApiKey, y++);

                llmTab.Add(new Label { Text = "Configured providers (Add/Edit/Remove apply immediately):", X = 1, Y = ++y, Width = Dim.Fill() });
                y++;
                providersList.X = 1; providersList.Y = y; providersList.Width = 62; providersList.Height = 6;
                llmTab.Add(providersList);
                y += 7;
                var addBtn = new Button { Text = "Add…", X = 1, Y = y };
                var editBtn = new Button { Text = "Edit…", X = 9, Y = y };
                var removeBtn = new Button { Text = "Remove", X = 17, Y = y };
                llmTab.Add(addBtn, editBtn, removeBtn);

                // The dropdown re-marks the active provider in the list below it.
                providerDropdown.ValueChanged += (_, _) => RefreshProviderList();
                void RefreshProviderList()
                {
                    providersList.Source = new ListWrapper<string>(new ObservableCollection<string>(
                        ProviderConfigs.All.Select(p => p.ProviderName == providerDropdown.Text
                            ? $"{p.ProviderName}  ← active" : p.ProviderName)));
                }
                // Full refresh after a provider was added/edited/removed (dropdown included).
                void RefreshProviders()
                {
                    var names = ProviderConfigs.All.Select(p => p.ProviderName).ToList();
                    providerDropdown.Source = new ListWrapper<string>(new ObservableCollection<string>(names));
                    if (!names.Contains(providerDropdown.Text))
                        providerDropdown.Text = ProviderConfigs.Default.ProviderName;
                    RefreshProviderList();
                }
                RefreshProviderList();

                string? SelectedProviderName()
                {
                    var i = providersList.SelectedItem;
                    return i is >= 0 && i < ProviderConfigs.All.Count ? ProviderConfigs.All[i.Value].ProviderName : null;
                }

                addBtn.Accepted += (_, _) =>
                {
                    var cfg = ShowProviderDialog(null);
                    if (cfg == null) return;
                    if (ProviderConfigs.Add(cfg, persist: true))
                        AddNote($"provider {cfg.ProviderName} added");
                    else
                        AddNote($"provider {cfg.ProviderName} already exists");
                    RefreshProviders();
                };
                editBtn.Accepted += (_, _) =>
                {
                    var name = SelectedProviderName();
                    if (name == null) { AddNote("select a provider to edit"); return; }
                    var cfg = ShowProviderDialog(ProviderConfigs.Get(name));
                    if (cfg == null) return;
                    ProviderConfigs.Upsert(cfg, persist: true);
                    AddNote($"provider {cfg.ProviderName} updated");
                    RefreshProviders();
                };
                removeBtn.Accepted += (_, _) =>
                {
                    var name = SelectedProviderName();
                    if (name == null) { AddNote("select a provider to remove"); return; }
                    if (MessageBox.Query(_app, "Remove provider", $"Remove '{name}' from the configuration?", "Cancel", "Remove") != 1)
                        return;
                    if (!ProviderConfigs.Remove(name, persist: true))
                    {
                        AddNote($"cannot remove '{name}' — not configured or it is the last provider");
                        return;
                    }
                    AddNote($"provider {name} removed");
                    if (providerDropdown.Text == name)
                    {
                        providerDropdown.Text = ProviderConfigs.Default.ProviderName;
                        _ = SwitchModelAsync(ProviderConfigs.Default.ProviderName);
                    }
                    RefreshProviders();
                };
            }

            // ── Email (SMTP) tab ──
            var emailTab = new View { Title = "Email (SMTP)", CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill() };
            var smtpServer = AddField(emailTab, "SMTP server", AIOrchestrator.Setup.SmtpServer, 0);
            var smtpPort = AddField(emailTab, "SMTP port", AIOrchestrator.Setup.SmtpPort.ToString(), 1);
            var smtpUser = AddField(emailTab, "SMTP user", AIOrchestrator.Setup.SmtpUser, 2);
            var smtpPswd = AddField(emailTab, "SMTP password", AIOrchestrator.Setup.SmtpPassword, 3);
            var recipientEmail = AddField(emailTab, "Recipient email", AIOrchestrator.Setup.Email, 4);

            // ── Mail reading (IMAP) tab ──
            var imapTab = new View { Title = "Mail (IMAP)", CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill() };
            var imapServer = AddField(imapTab, "IMAP server", AIOrchestrator.Setup.ImapServer, 0);
            var imapPort = AddField(imapTab, "IMAP port", AIOrchestrator.Setup.ImapPort.ToString(), 1);
            var imapUser = AddField(imapTab, "IMAP user", AIOrchestrator.Setup.ImapUser, 2);
            var imapPswd = AddField(imapTab, "IMAP password", AIOrchestrator.Setup.ImapPassword, 3);

            // ── General tab ──
            var generalTab = new View { Title = "General", CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill() };
            var logEnabled = new CheckBox
            {
                Text = "Enable step logging (logs/ folder)",
                Value = AIOrchestrator.Log.IsEnabled ? CheckState.Checked : CheckState.UnChecked,
                X = 1, Y = 0,
            };
            generalTab.Add(logEnabled);
            var docsPath = AddField(generalTab, "Documents path", AIOrchestrator.Setup.DocumentsPath, 2);

            var save = new Button { Text = "Save", IsDefault = true };
            save.Accepted += (_, _) =>
            {
                AIOrchestrator.Setup.DeepSeekApiKey = (deepSeekKey.Text ?? "").Trim();
                AIOrchestrator.Setup.ZaiApiKey = (zaiKey.Text ?? "").Trim();
                AIOrchestrator.Setup.GeminiApiKey = (geminiKey.Text ?? "").Trim();

                AIOrchestrator.Setup.SmtpServer = (smtpServer.Text ?? "").Trim();
                if (int.TryParse((smtpPort.Text ?? "").Trim(), out var sp)) AIOrchestrator.Setup.SmtpPort = sp;
                AIOrchestrator.Setup.SmtpUser = (smtpUser.Text ?? "").Trim();
                AIOrchestrator.Setup.SmtpPassword = (smtpPswd.Text ?? "").Trim();
                AIOrchestrator.Setup.Email = (recipientEmail.Text ?? "").Trim();

                AIOrchestrator.Setup.ImapServer = (imapServer.Text ?? "").Trim();
                if (int.TryParse((imapPort.Text ?? "").Trim(), out var ip)) AIOrchestrator.Setup.ImapPort = ip;
                AIOrchestrator.Setup.ImapUser = (imapUser.Text ?? "").Trim();
                AIOrchestrator.Setup.ImapPassword = (imapPswd.Text ?? "").Trim();

                AIOrchestrator.Log.IsEnabled = logEnabled.Value == CheckState.Checked;
                AIOrchestrator.Setup.DocumentsPath = (docsPath.Text ?? "").Trim();

                var chosen = (providerDropdown.Text ?? "").Trim();
                if (chosen.Length > 0 && !string.Equals(chosen, _provider, StringComparison.OrdinalIgnoreCase))
                    _ = SwitchModelAsync(chosen);   // same path as /model (HTTP /v1/control)

                AddNote("model setup saved");
                _app.RequestStop(dlg);
            };
            var close = new Button { Text = "Close" };
            close.Accepted += (_, _) => _app.RequestStop(dlg);
            dlg.AddButton(save);
            dlg.AddButton(close);

            tabs.Add(llmTab, emailTab, imapTab, generalTab);
            dlg.Add(tabs);
            dlg.Initialized += (_, _) => providerDropdown.SetFocus();
            _app.Run(dlg);
            dlg.Dispose();
            _inputField?.SetFocus();
        }

        // Modal form to add a new provider or edit an existing one (returns the edited
        // config on OK, null on cancel). No API-key field: keys live in Setup.ApiKey's
        // hardcoded per-name switch, so a dynamically named cloud provider cannot use one.
        private ProviderConfig? ShowProviderDialog(ProviderConfig? existing)
        {
            var dlg = new Dialog
            {
                Title = existing == null ? "Add provider" : $"Edit provider: {existing.ProviderName}",
                Width = 66,
                Height = 13,
                SchemeName = "Dark",
            };
            int y = 0;
            var nameField = AddField(dlg, "Name", existing?.ProviderName, y++);
            var protocol = new DropDownList
            {
                ReadOnly = true,
                X = 20, Y = y, Width = 32,
                Source = new ListWrapper<string>(new ObservableCollection<string>(Enum.GetNames<ProviderProtocol>())),
                Text = (existing?.Protocol ?? ProviderProtocol.OpenAI).ToString(),
            };
            dlg.Add(new Label { Text = "Protocol", X = 1, Y = y, Width = 18 }, protocol);
            y++;
            var modelField = AddField(dlg, "Model", existing?.ModelName, y++);
            var baseField = AddField(dlg, "Base address", existing?.BaseAddress.ToString(), y++);
            var endPointField = AddField(dlg, "Endpoint path", existing?.EndPoint, y++);
            var ctxField = AddField(dlg, "Context window", (existing?.ContextWindow ?? 32768).ToString(), y++);
            var timeoutField = AddField(dlg, "Timeout (sec)", ((int)(existing?.Timeout.TotalSeconds ?? 30)).ToString(), y++);

            ProviderConfig? result = null;
            var ok = new Button { Text = "OK", IsDefault = true };
            ok.Accepted += (_, _) =>
            {
                var providerName = (nameField.Text ?? "").Trim();
                if (providerName.Length == 0)
                {
                    _ = MessageBox.Query(_app, "Add provider", "Name is required", "OK");
                    return;
                }
                if (!Uri.TryCreate((baseField.Text ?? "").Trim(), UriKind.Absolute, out var uri))
                {
                    _ = MessageBox.Query(_app, "Add provider", "Base address must be an absolute URL (http://…)", "OK");
                    return;
                }
                if (!int.TryParse((ctxField.Text ?? "").Trim(), out var ctx) || ctx <= 0)
                {
                    _ = MessageBox.Query(_app, "Add provider", "Context window must be a positive number", "OK");
                    return;
                }
                if (!int.TryParse((timeoutField.Text ?? "").Trim(), out var secs) || secs <= 0) secs = 30;
                if (!Enum.TryParse<ProviderProtocol>((protocol.Text ?? "").Trim(), out var proto)) proto = ProviderProtocol.OpenAI;
                result = new ProviderConfig
                {
                    ProviderName = providerName,
                    Protocol = proto,
                    ModelName = (modelField.Text ?? "").Trim(),
                    BaseAddress = uri,
                    EndPoint = (endPointField.Text ?? "").Trim(),
                    ContextWindow = ctx,
                    Timeout = TimeSpan.FromSeconds(secs),
                };
                _app.RequestStop(dlg);
            };
            var cancel = new Button { Text = "Cancel" };
            cancel.Accepted += (_, _) => _app.RequestStop(dlg);
            dlg.AddButton(ok);
            dlg.AddButton(cancel);
            dlg.Initialized += (_, _) => nameField.SetFocus();
            _app.Run(dlg);
            dlg.Dispose();
            return result;
        }

        // Adds a labelled single-line field to a form and returns the field.
        private static TextField AddField(View parent, string label, string? value, int y, int labelWidth = 18, int fieldWidth = 44)
        {
            parent.Add(new Label { Text = label, X = 1, Y = y, Width = labelWidth });
            var field = new TextField { Text = value ?? "", X = labelWidth + 2, Y = y, Width = fieldWidth };
            parent.Add(field);
            return field;
        }

        // ── Server state ──
        private async Task RefreshServerStateAsync()
        {
            try
            {
                using var health = await _http.GetAsync("/health").WaitAsync(TimeSpan.FromSeconds(8));
                _connected = health.IsSuccessStatusCode;
                if (!_connected)
                {
                    _statusNote = "server unreachable — starting it headless keeps the API alive";
                    UpdateStatusUi();
                    return;
                }

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
                UpdateStatusUi();
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
                    lock (_stateLock)
                    {
                        _features.Clear();
                        foreach (var p in feats.EnumerateObject()) _features[p.Name] = p.Value.GetBoolean();
                    }
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
                UpdateStatusUi();
            }
            catch { }
        }

        private static string? GetStr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static int GetInt(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
        private static bool GetBool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
