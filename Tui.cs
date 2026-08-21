using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AIOrchestrator;
using AgentBridge.Resources;
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
        private string _interactionMode = "";
        private int _contextWindow;
        private int _historyTokens;
        private string _sessionId = "";
        private string _agentSet = "default-agent";
        private bool _ttsAvailable, _voiceAvailable;
        private string _ttsDetail = "", _voiceDetail = "";
        private string _statusNote = "";
        // SIP telephony state (from GET /v1/sip/status; polled while the server reports it available).
        private bool _sipAvailable;
        private string _sipState = "";

        private static readonly string[] SpinnerChars = { "⣾", "⣽", "⣻", "⢿", "⡿", "⣟", "⣯", "⣷" };
        private int _spinnerIndex;
        private volatile bool _spinnerActive;
        private Label? _spinnerLabel;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private const int MaxHistory = 1000;
        private const int MaxInputLines = 4;

        // UI strings come from the localized dictionary (system language, English fallback) —
        // see Resources/Dictionary.*.resx. Command names (/help, /model...) are NOT translated.
        private static string PlaceholderText => Dictionary.InputPlaceholder;

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
            new("help", "", Dictionary.CmdHelp, (t, _) => t.ShowHelpAsync(), new[] { "/?" }),
            new("docs", "", Dictionary.CmdDocs, (t, _) => t.OpenDocsAsync()),
            new("web", "", Dictionary.CmdWeb, (t, _) => t.LaunchWebClientAsync()),
            new("modelsetup", "", Dictionary.CmdModelSetup, (t, _) => t.ShowModelSetupAsync()),
            new("model", "[name]", Dictionary.CmdModel, (t, a) => t.SwitchModelAsync(a)),
            new("agent", "[name]", Dictionary.CmdAgent, (t, a) => t.SwitchAgentAsync(a)),
            new("voice", "[lang]", Dictionary.CmdVoice, (t, a) => t.VoiceAsync(a)),
            new("tts", "[text]", Dictionary.CmdTts, (t, a) => t.TtsAsync(a)),
            new("features", "[name] [on|off]", Dictionary.CmdFeatures, (t, a) => t.FeaturesAsync(a)),
            new("new", "", Dictionary.CmdNew, (t, _) => t.NewSessionAsync(), new[] { "/reset" }),
            new("clear", "", Dictionary.CmdClear, (t, _) => t.ClearHistoryAsync()),
            new("status", "", Dictionary.CmdStatus, (t, _) => t.ShowStatusAsync()),
            new("sip", "status|call <sip-uri>|answer on|off|hangup", Dictionary.CmdSip, (t, a) => t.SipAsync(a)),
            new("files", "add <path>|rm <id>|list", Dictionary.CmdFiles, (t, a) => t.FilesAsync(a)),
            new("attach", "[id]", Dictionary.CmdAttach, (t, a) => t.AttachAsync(a)),
            new("shortcuts", "", Dictionary.CmdShortcuts, (t, _) => t.ShowShortcutsAsync(), new[] { "/keys" }),
            new("health", "", Dictionary.CmdHealth, (t, _) => t.HealthAsync()),
            new("retry", "", Dictionary.CmdRetry, (t, _) => t.RetryAsync()),
            new("exit", "", Dictionary.CmdExit, (t, _) => t.ExitAsync(), new[] { "/quit" }),
        };

        private static readonly string[] AgentSets = { "default-agent", "web-agent", "search-agent", "research-agent", "document-agent", "spreadsheet-agent", "email-agent", "multi-agent" };

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
            ("Enter", Dictionary.ShortEnter),
            ("/", Dictionary.ShortSlash),
            ("@", Dictionary.ShortAt),
            ("?", Dictionary.ShortQuestion),
            ("Tab", Dictionary.ShortTab),
            ("Esc", Dictionary.ShortEsc),
            ("Ctrl+C", Dictionary.ShortCtrlC),
            ("Ctrl+D", Dictionary.ShortCtrlD),
            ("Ctrl+Y", Dictionary.ShortCtrlY),
            ("Ctrl+R", Dictionary.ShortCtrlR),
            ("Up / Down", Dictionary.ShortUpDown),
            ("Left / Right", Dictionary.ShortLeftRight),
            ("Ctrl+A / Ctrl+E", Dictionary.ShortCtrlAE),
            ("Ctrl+U / Ctrl+K", Dictionary.ShortCtrlUK),
            ("Ctrl+W", Dictionary.ShortCtrlW),
            ("PgUp / PgDn", Dictionary.ShortPgUpDn),
            ("F1", Dictionary.ShortF1),
            ("F10", Dictionary.ShortF10),
        };

        public Tui(string serverUrl, string? hostError)
        {
            _serverUrl = serverUrl;
            _hostError = hostError;
            _http.BaseAddress = new Uri(serverUrl);
            _app = Application.Create().Init();
            // Surface auto-update progress in the status bar (fires from background tasks).
            AutoUpdate.OnStatus += OnUpdateStatus;

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
                Text = Dictionary.WelcomeMessage,
            });
            _history.Add(new Entry { Role = "system", Text = string.Format(Dictionary.ServerNote, _serverUrl) });
            if (!string.IsNullOrEmpty(_hostError))
                _history.Add(new Entry { Role = "system", Text = string.Format(Dictionary.HostErrorNote, _hostError) });
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
                _app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
                {
                    TickSpinner();
                    return true;   // recurring spinner animation
                });
                RefreshHistory();
                _ = Task.Run(RefreshServerStateAsync);
                StartSipPolling();
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
            AutoUpdate.OnStatus -= OnUpdateStatus;
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
                Title = Dictionary.WindowTitle,
                X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
                SchemeName = "Dark",
            };

            // Terminal.Gui v2 has no checkmark on menu items — the state is shown in the title.
            MenuItem autoUpdateItem = null!;
            autoUpdateItem = new MenuItem(string.Format(Dictionary.MenuAutoUpdate, AutoUpdate.Enabled ? Dictionary.On : Dictionary.Off), Key.Empty, () =>
            {
                AutoUpdate.Toggle();
                autoUpdateItem.Title = string.Format(Dictionary.MenuAutoUpdate, AutoUpdate.Enabled ? Dictionary.On : Dictionary.Off);
            });

            var menu = new MenuBar(new MenuBarItem[]
            {
                new(Dictionary.MenuFile, new MenuItem[]
                {
                    new MenuItem(Dictionary.MenuNewChat, Key.Empty, () => RunCommandByName("new", "")),
                    new MenuItem(Dictionary.MenuModelsProviders, Key.Empty, () => RunCommandByName("modelsetup", "")),
                    autoUpdateItem,
                    new MenuItem(Dictionary.MenuExit, Key.Q.WithCtrl, () => RequestExit()),
                }),
                new(Dictionary.MenuChat, new MenuItem[]
                {
                    new MenuItem(Dictionary.MenuClearHistory, Key.L.WithCtrl, () => RunCommandByName("clear", "")),
                    new MenuItem(Dictionary.MenuCommands, Key.Empty, () => ShowCommandMenu("")),
                    new MenuItem(Dictionary.MenuRetryLast, Key.Y.WithCtrl, () => RunCommandByName("retry", "")),
                }),
                new(Dictionary.MenuSession, new MenuItem[]
                {
                    new MenuItem(Dictionary.MenuLlmModel, Key.Empty, () => RunCommandByName("model", "")),
                    new MenuItem(Dictionary.MenuAgent, Key.Empty, () => RunCommandByName("agent", "")),
                    new MenuItem(Dictionary.MenuStatus, Key.Empty, () => RunCommandByName("status", "")),
                    new MenuItem(Dictionary.MenuHealth, Key.Empty, () => RunCommandByName("health", "")),
                }),
                new(Dictionary.MenuWeb, new MenuItem[]
                {
                    new MenuItem(Dictionary.MenuGui, Key.Empty, () => RunCommandByName("web", "")),
                }),
                new(Dictionary.MenuHelp, new MenuItem[]
                {
                    new MenuItem(Dictionary.MenuHelpItem, Key.F1, () => RunCommandByName("help", "")),
                    new MenuItem(Dictionary.MenuShortcuts, Key.Empty, () => RunCommandByName("shortcuts", "")),
                    new MenuItem(Dictionary.MenuDocumentation, Key.Empty, () => RunCommandByName("docs", "")),
                    new MenuItem(Dictionary.MenuAbout, Key.Empty, () => ShowAbout()),
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
                Title = Dictionary.ChatFrameTitle,
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

            // One-character spinner shown next to the prompt while generating.
            _spinnerLabel = new Label
            {
                Text = " ",
                X = 0, Y = 0, Width = 1,
                SchemeName = "Dark",
            };
            inputArea.Add(_spinnerLabel);

            // Multi-line prompt box: soft-wraps and grows up to MaxInputLines rows (see
            // UpdateInputLayout); full width minus the spinner column, so it reaches
            // the right margin of the frame without covering the spinner.
            _inputField = new Editor
            {
                X = 1, Y = 0, Width = Dim.Fill() - 1, Height = 1,
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
                ClearInputWhen("/");
            }
            else if (t == "@")
            {
                _suppressCommandMenu = true;
                ShowFilesDialog();
                _suppressCommandMenu = false;
                ClearInputWhen("@");
            }
            else if (t == "?")
            {
                _suppressCommandMenu = true;
                _ = ShowShortcutsAsync();
                _suppressCommandMenu = false;
                ClearInputWhen("?");
            }
        }

        // Fallback for when a trigger character (/, @, ?) reaches the input by paste
        // (typing is intercepted in OnInputKeyDown before insertion). This handler runs
        // inside the Editor's DocumentChanged callback, so mutating the document here
        // throws "Cannot change document within another document change"; Application.Invoke
        // executes synchronously on the main thread, so the clear must go through a
        // main-loop timeout, which fires only after the change completes.
        private void ClearInputWhen(string expected)
        {
            _app.AddTimeout(TimeSpan.Zero, () =>
            {
                if (_inputField != null && _inputField.Text == expected) _inputField.Text = "";
                return false;   // one-shot
            });
        }

        private void OnInputKeyDown(object? sender, Key key)
        {
            if (_inputPlaceholderActive)
                ClearPlaceholder();   // the first keystroke dismisses the hint, then falls through

            // "/" "@" "?" only act as the first character. Intercepting them here
            // (before the Editor inserts the char) keeps the trigger character out of
            // the document: when the palette runs inside the Editor's DocumentChanged
            // handler, clearing it back throws "Cannot change document within another
            // document change" (Application.Invoke executes synchronously on the main
            // thread, so even a deferred clear failed). Consuming the key avoids the
            // insert entirely — no crash, no lingering "/".
            if (!_suppressCommandMenu && (_inputField?.Text ?? "").Length == 0)
            {
                // Opening a palette/dialog is a fresh start: reset the double-Esc exit
                // counter (these keys are consumed and no longer reach the reset below).
                if (key == (Key)'/')
                {
                    key.Handled = true;
                    _escCount = 0;
                    ShowCommandMenu("");
                    return;
                }
                if (key == (Key)'@')
                {
                    key.Handled = true;
                    _escCount = 0;
                    ShowFilesDialog();
                    return;
                }
                if (key == (Key)'?')
                {
                    key.Handled = true;
                    _escCount = 0;
                    _ = ShowShortcutsAsync();
                    return;
                }
            }

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
                    _statusNote = Dictionary.StatusEscAgain;
                    UpdateStatusUi();
                }
            }
            else if (key == Key.C.WithCtrl)
            {
                key.Handled = true;
                if (_chatRunning != 0)
                {
                    CancelChat();
                    _statusNote = Dictionary.StatusCancelling;
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
                    _statusNote = Dictionary.StatusCtrlCAgain;
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

        // ── Spinner ──
        private void StartSpinner()
        {
            _spinnerActive = true;
            _spinnerIndex = 0;
            if (_spinnerLabel != null)
                _spinnerLabel.Text = SpinnerChars[0];
        }

        private void StopSpinner()
        {
            _spinnerActive = false;
            if (_spinnerLabel != null)
                _spinnerLabel.Text = " ";
        }

        private void TickSpinner()
        {
            if (_spinnerLabel == null || !_spinnerActive) return;
            _spinnerIndex = (_spinnerIndex + 1) % SpinnerChars.Length;
            _spinnerLabel.Text = SpinnerChars[_spinnerIndex];
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
                _statusNote = Dictionary.StatusGenerating;
                UpdateStatusUi();
                return;
            }
            _lastPrompt = prompt;
            _lastFailed = false;
            lock (_stateLock) _chatCts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            Ui(() =>
            {
                StartSpinner();
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
                _statusNote = string.Format(Dictionary.StatusReplied, sw.ElapsedMilliseconds / 1000.0);
                FinishChat(_pending);
            }
            catch (OperationCanceledException)
            {
                _statusNote = Dictionary.StatusCancelled;
                _lastFailed = true;
                FinishChat(new Entry { Role = "agent", Text = Dictionary.StatusCancelledEntry, Error = true });
            }
            catch (Exception ex)
            {
                _statusNote = Dictionary.StatusError;
                _lastFailed = true;
                _connected = false;
                FinishChat(new Entry { Role = "agent", Text = string.Format(Dictionary.StatusRequestFailed, ex.Message), Error = true });
            }
            finally
            {
                Interlocked.Exchange(ref _chatRunning, 0);
                lock (_stateLock)
                {
                    _chatCts?.Dispose();
                    _chatCts = null;
                }
                Ui(() => { StopSpinner(); RefreshHistory(); UpdateStatus(); });
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
                "user" => Dictionary.HistoryYou,
                "agent" => e.Error ? Dictionary.HistoryError : Dictionary.HistoryAgent,
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
                string.Format(Dictionary.StatusSess, sess), ctx,
                _ttsAvailable ? "tts:✓" : "tts:✗",
                _voiceAvailable ? "mic:✓" : "mic:✗",
                _sipAvailable ? "sip:" + (_sipState.Length > 0 ? _sipState : "✓") : "",
                feats.Length > 0 ? "f:" + feats : "",
                _chatRunning != 0 ? Dictionary.StatusGeneratingShort : "",
                _statusNote,
            };
            var text = string.Join(" · ", parts.Where(p => p.Length > 0));
            _statusLabel.Text = text.Length > 240 ? text[..240] : text;
        }

        private void OnUpdateStatus(string message) => Ui(() => { _statusNote = message; UpdateStatusUi(); });

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
                AddNote(string.Format(Dictionary.NoteUnknownCommand, name));
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
                AddNote(string.Format(Dictionary.NoteCommandFailed, cmd.Name, ex.Message));
            }
            await RefreshSessionStateAsync();
            UpdateStatusUi();
        }

        private Task ExitAsync()
        {
            Ui(RequestExit);
            return Task.CompletedTask;
        }

        private void RequestExit() => _app.RequestStop(_mainWindow);

        private async Task HealthAsync()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await _http.GetAsync("/health").WaitAsync(TimeSpan.FromSeconds(5));
                sw.Stop();
                AddNote(resp.IsSuccessStatusCode
                    ? string.Format(Dictionary.StatusServerHealthy, sw.ElapsedMilliseconds)
                    : string.Format(Dictionary.StatusServerHttp, (int)resp.StatusCode));
                _connected = resp.IsSuccessStatusCode;
                UpdateStatusUi();
            }
            catch (Exception ex)
            {
                AddNote(string.Format(Dictionary.StatusServerUnreachable, ex.Message));
                _connected = false;
                UpdateStatusUi();
            }
        }

        private async Task SipAsync(string args)
        {
            var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var sub = parts.Length == 0 ? "status" : parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (sub)
            {
                case "call":
                {
                    if (string.IsNullOrEmpty(arg)) { AddNote(Dictionary.NoteSipUsage); return; }
                    AddNote(string.Format(Dictionary.NoteSipCalling, arg));
                    var body = JsonSerializer.Serialize(new { uri = arg }, JsonOpts);
                    using var resp = await _http.PostAsync("/v1/sip/call", new StringContent(body, Encoding.UTF8, "application/json"));
                    var text = await ReadErrorAsync(resp);
                    if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                        AddNote(string.Format(Dictionary.NoteSipCallOk, arg));
                    else if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        AddNote(Dictionary.NoteSipUnavailableCall);
                    else
                        AddNote(string.Format(Dictionary.NoteSipCallFailed, arg, text));
                    break;
                }
                case "hangup":
                {
                    using var resp = await _http.PostAsync("/v1/sip/hangup", new StringContent("{}", Encoding.UTF8, "application/json"));
                    if (resp.IsSuccessStatusCode) AddNote(Dictionary.NoteSipHangup);
                    else AddNote(string.Format(Dictionary.NoteSipCallFailed, "hangup", await ReadErrorAsync(resp)));
                    break;
                }
                case "answer":
                {
                    var on = !arg.Equals("off", StringComparison.OrdinalIgnoreCase);
                    var body = JsonSerializer.Serialize(new { on }, JsonOpts);
                    using var resp = await _http.PostAsync("/v1/sip/answer", new StringContent(body, Encoding.UTF8, "application/json"));
                    if (resp.IsSuccessStatusCode)
                        AddNote(string.Format(Dictionary.NoteSipAnswerChanged, on ? Dictionary.On : Dictionary.Off));
                    else
                        AddNote(string.Format(Dictionary.NoteSipCallFailed, "answer", await ReadErrorAsync(resp)));
                    break;
                }
                default:   // status
                {
                    using var resp = await _http.GetAsync("/v1/sip/status").WaitAsync(TimeSpan.FromSeconds(5));
                    if (!resp.IsSuccessStatusCode) { AddNote(string.Format(Dictionary.NoteSipUnavailable, (int)resp.StatusCode)); return; }
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var sip = doc.RootElement.GetProperty("sip");
                    var lines = new List<string>
                    {
                        $"{Dictionary.StatusSip.PadRight(18)}{(GetBool(sip, "enabled") ? Dictionary.On : Dictionary.Off)}",
                        $"{Dictionary.StatusSipListen.PadRight(18)}{(GetBool(sip, "listening") ? Dictionary.Available : Dictionary.Unavailable)}",
                        $"{Dictionary.StatusSipAnswer.PadRight(18)}{(GetBool(sip, "answer_enabled") ? Dictionary.On : Dictionary.Off)} · mode: {GetStr(sip, "answer_mode") ?? ""}",
                        $"{Dictionary.StatusSipRegistered.PadRight(18)}{(GetBool(sip, "registered") ? Dictionary.On : Dictionary.Off)}",
                        $"{Dictionary.StatusSipCall.PadRight(18)}{PhaseLabel(GetStr(sip, "phase") ?? "idle")}{(GetStr(sip, "remote") is { } r && r.Length > 0 ? "  " + r : "")}",
                        $"{Dictionary.StatusSipPin.PadRight(18)}{GetInt(sip, "pin_remaining")}",
                        $"{Dictionary.StatusSipLocked.PadRight(18)}{GetStr(sip, "locked_until") ?? Dictionary.None}",
                        $"{Dictionary.StatusSipStt.PadRight(18)}{(GetBool(sip, "stt_available") ? Dictionary.Available : Dictionary.Unavailable)} · tts {(GetBool(sip, "tts_available") ? Dictionary.Available : Dictionary.Unavailable)}",
                    };
                    await ShowPageUiAsync(Dictionary.PageSipStatus, lines);
                    break;
                }
            }
            await RefreshSipStatusAsync();
        }

        private async Task SwitchModelAsync(string args)
        {
            string name = args.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                List<(string Id, string Display)> providers;
                try
                {
                    using var resp = await _http.GetAsync("/v1/models").WaitAsync(TimeSpan.FromSeconds(8));
                    if (!resp.IsSuccessStatusCode) { AddNote(Dictionary.NoteCouldNotLoadProviders); return; }
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    providers = doc.RootElement.GetProperty("data").EnumerateArray()
                        .Where(x => GetStr(x, "owned_by") == "llm-provider")
                        .Select(x => (Id: GetStr(x, "id") ?? "",
                                      Display: $"{GetStr(x, "id")} — {GetStr(x, "model_name")} · ctx {GetInt(x, "context_window"):N0}"))
                        .ToList();
                }
                catch (Exception ex)
                {
                    AddNote(string.Format(Dictionary.NoteCouldNotLoadProvidersEx, ex.Message));
                    return;
                }
                if (providers.Count == 0) { AddNote(Dictionary.NoteNoProviders); return; }
                var pick = await PickProviderOnUiThreadAsync(Dictionary.PickSwitchProvider, providers);
                if (pick == null) return;   // Esc → cancel
                name = pick.Trim();
            }

            if (string.Equals(name, _provider, StringComparison.OrdinalIgnoreCase))
            {
                AddNote(string.Format(Dictionary.NoteAlreadyOn, name));
                return;
            }

            AddNote(string.Format(Dictionary.NoteSwitchingProvider, name));
            var body = JsonSerializer.Serialize(new { session_id = _sessionId, llm_provider = name }, JsonOpts);
            using var resp2 = await _http.PostAsync("/v1/control", new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp2.IsSuccessStatusCode)
            {
                AddNote(string.Format(Dictionary.NoteProviderNow, name));
            }
            else
            {
                AddNote(string.Format(Dictionary.NoteSwitchRefused, (int)resp2.StatusCode, await ReadErrorAsync(resp2)));
            }
        }

        private async Task SwitchAgentAsync(string args)
        {
            string name;
            if (string.IsNullOrWhiteSpace(args.Trim()))
            {
                var pick = await PickOnUiThreadAsync(Dictionary.PickSwitchAgent, AgentSets.ToList());
                if (pick == null) return;
                name = pick;
            }
            else
            {
                name = args.Trim().ToLowerInvariant();
            }
            if (!AgentSets.Contains(name))
            {
                AddNote(string.Format(Dictionary.NoteUnknownAgentSet, name, string.Join(", ", AgentSets)));
                return;
            }
            _agentSet = name;
            AddNote(string.Format(Dictionary.NoteAgentSet, name));
        }

        private async Task VoiceAsync(string lang)
        {
            if (!_voiceAvailable)
            {
                AddNote(string.Format(Dictionary.NoteVoiceUnavailable, string.IsNullOrEmpty(_voiceDetail) ? Dictionary.NoteVoiceDisabled : _voiceDetail));
                return;
            }
            var l = string.IsNullOrWhiteSpace(lang) ? SystemLang.Get() : lang.Trim();
            AddNote(Dictionary.NoteListening);
            var body = JsonSerializer.Serialize(new { lang = l, timeout_seconds = 15 }, JsonOpts);
            try
            {
                using var resp = await _http.PostAsync("/v1/voice/listen", new StringContent(body, Encoding.UTF8, "application/json"));
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var text = GetStr(doc.RootElement, "text") ?? "";
                    if (string.IsNullOrWhiteSpace(text)) { AddNote(Dictionary.NoteNoSpeech); return; }
                    Ui(() =>
                    {
                        if (_inputField == null) return;
                        _inputField.Text = text;
                        _inputPlaceholderActive = false;
                        _inputField.SchemeName = "Dark";
                        _inputField.CaretOffset = _inputField.Document?.TextLength ?? 0;
                    });
                    AddNote(string.Format(Dictionary.NoteDictated, text));
                }
                else
                {
                    var err = await ReadErrorAsync(resp);
                    AddNote(resp.StatusCode == System.Net.HttpStatusCode.RequestTimeout
                        ? Dictionary.NoteListeningTimeout
                        : string.Format(Dictionary.NoteVoiceFailedHttp, (int)resp.StatusCode, err));
                }
            }
            catch (Exception ex)
            {
                AddNote(string.Format(Dictionary.NoteVoiceFailed, ex.Message));
            }
        }

        private async Task TtsAsync(string text)
        {
            if (!_ttsAvailable)
            {
                AddNote(string.Format(Dictionary.NoteTtsUnavailable, string.IsNullOrEmpty(_ttsDetail) ? Dictionary.NoteTtsDisabled : _ttsDetail));
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                var last = _history.LastOrDefault(e => e.Role == "agent" && !e.Error)?.Text;
                if (string.IsNullOrWhiteSpace(last)) { AddNote(Dictionary.NoteNothingToSpeak); return; }
                text = last;
            }
            AddNote(Dictionary.NoteSynthesising);
            var body = JsonSerializer.Serialize(new { input = text, lang = SystemLang.Get(), speed = 1.0 }, JsonOpts);
            try
            {
                using var resp = await _http.PostAsync("/v1/audio/speech", new StringContent(body, Encoding.UTF8, "application/json"));
                if (!resp.IsSuccessStatusCode)
                {
                    AddNote(string.Format(Dictionary.NoteTtsFailedHttp, (int)resp.StatusCode, await ReadErrorAsync(resp)));
                    return;
                }
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                var path = Path.Combine(Path.GetTempPath(), $"agent_tts_{DateTime.Now:yyyyMMddHHmmss}.wav");
                await File.WriteAllBytesAsync(path, bytes);
                AddNote(string.Format(Dictionary.NoteSaved, path, bytes.Length));
                if (OperatingSystem.IsWindows())
                {
                    if (!PlaySound(path, IntPtr.Zero, SndAsync | SndFilename))
                        AddNote(string.Format(Dictionary.NotePlaybackFailed, path));
                }
                else
                {
                    AddNote(Dictionary.NotePlaybackWindowsOnly);
                }
            }
            catch (Exception ex)
            {
                AddNote(string.Format(Dictionary.NoteTtsFailed, ex.Message));
            }
        }

        private async Task FeaturesAsync(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                string current;
                lock (_stateLock)
                    current = _features.Count == 0 ? Dictionary.NoneSet :
                        string.Join(", ", _features.Select(kv => $"{kv.Key}={(kv.Value ? "on" : "off")}"));
                AddNote(string.Format(Dictionary.NoteSessionFeatures, current));
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
            if (resp.IsSuccessStatusCode) AddNote(string.Format(Dictionary.NoteFeatureSet, name, value ? "on" : "off"));
            else AddNote(string.Format(Dictionary.NoteFailedHttp, (int)resp.StatusCode, await ReadErrorAsync(resp)));
        }

        private async Task NewSessionAsync()
        {
            using var resp = await _http.PostAsync("/v1/control", new StringContent("{\"create\":true}", Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode) { AddNote(string.Format(Dictionary.NoteCreateSessionFailed, (int)resp.StatusCode)); return; }
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
                _history.Add(new Entry { Role = "system", Text = string.Format(Dictionary.NoteNewSession, shortId) });
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
                    _history.Add(new Entry { Role = "system", Text = Dictionary.NoteHistoryCleared });
                    RefreshHistory();
                });
            }
            else
            {
                AddNote(string.Format(Dictionary.NoteFailedHttp, (int)resp.StatusCode, await ReadErrorAsync(resp)));
            }
        }

        private Task ShowStatusAsync()
        {
            string feats, attached;
            lock (_stateLock)
            {
                feats = _features.Count == 0 ? Dictionary.None : string.Join(", ", _features.Select(kv => $"{kv.Key}={kv.Value}"));
                attached = _attached.Count == 0 ? Dictionary.None : string.Join(", ", _attached);
            }
            var lines = new List<string>
            {
                $"{Dictionary.StatusSession.PadRight(18)}{_sessionId}",
                $"{Dictionary.StatusProvider.PadRight(18)}{_provider}  ({_modelName}, {(_interactionMode.Length > 0 ? _interactionMode : Dictionary.InteractionModeDefault)})",
                string.Format(Dictionary.StatusContextWindow, _contextWindow, _historyTokens),
                $"{Dictionary.StatusAgentSet.PadRight(18)}{_agentSet}",
                $"{Dictionary.StatusFeatures.PadRight(18)}{feats}",
                $"{Dictionary.StatusAttachments.PadRight(18)}{attached}",
                "",
                $"{Dictionary.StatusCapabilities.PadRight(18)}tts {(_ttsAvailable ? Dictionary.Available : Dictionary.Unavailable)} · voice {(_voiceAvailable ? Dictionary.Available : Dictionary.Unavailable)}",
                $"{"".PadRight(18)}server: {_serverUrl} {(_connected ? Dictionary.Connected : Dictionary.Unreachable)}",
                string.Format(Dictionary.StatusPromptHistory.PadRight(18) + "{0}", _promptHistory.Count),
            };
            return ShowPageUiAsync(Dictionary.PageAgentStatus, lines);
        }

        private async Task FilesAsync(string args)
        {
            if (string.IsNullOrWhiteSpace(args) || args == "list")
            {
                await RefreshFilesAsync();
                List<string> lines;
                lock (_stateLock)
                {
                    if (_files.Count == 0) { AddNote(Dictionary.NoteNoFiles); return; }
                    lines = _files.Select(f => $"{f.FileName}  {f.Id} · {f.Status}{(f.Attached ? "  " + Dictionary.NoteAttachedSuffix : "")}").ToList();
                }
                await ShowPageUiAsync(Dictionary.PageUploadedFiles, lines);
                return;
            }
            var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var sub = parts[0].ToLowerInvariant();
            if (sub == "add" && parts.Length == 2)
            {
                var path = parts[1].Trim('"');
                if (!File.Exists(path)) { AddNote(string.Format(Dictionary.NoteFileNotFound, path)); return; }
                AddNote(string.Format(Dictionary.NoteUploading, Path.GetFileName(path)));
                try
                {
                    await using var fs = File.OpenRead(path);
                    using var form = new MultipartFormDataContent();
                    form.Add(new StreamContent(fs), "file", Path.GetFileName(path));
                    form.Add(new StringContent("assistants"), "purpose");
                    using var resp = await _http.PostAsync("/v1/files", form);
                    if (!resp.IsSuccessStatusCode)
                    {
                        AddNote(string.Format(Dictionary.NoteUploadFailedHttp, (int)resp.StatusCode, await ReadErrorAsync(resp)));
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
                    AddNote(string.Format(Dictionary.NoteUploaded, name, id));
                }
                catch (Exception ex)
                {
                    AddNote(string.Format(Dictionary.NoteUploadFailed, ex.Message));
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
                        AddNote(string.Format(Dictionary.NoteDeleted, id));
                    }
                    else
                    {
                        AddNote(string.Format(Dictionary.NoteDeleteFailedHttp, (int)resp.StatusCode, await ReadErrorAsync(resp)));
                    }
                }
                catch (Exception ex)
                {
                    AddNote(string.Format(Dictionary.NoteDeleteFailed, ex.Message));
                }
            }
            else
            {
                AddNote(Dictionary.NoteFilesUsage);
            }
        }

        private async Task AttachAsync(string args)
        {
            await RefreshFilesAsync();
            List<FileRef> files;
            lock (_stateLock) files = _files.ToList();
            if (string.IsNullOrWhiteSpace(args))
            {
                if (files.Count == 0) { AddNote(Dictionary.NoteNoFiles); return; }
                var choices = files.Select(f => $"{f.FileName}  ({f.Id}){(f.Attached ? "  " + Dictionary.AttachMarker : "")}").ToList();
                var pick = await PickOnUiThreadAsync(Dictionary.DlgToggleAttach, choices);
                if (pick == null) return;
                var name = pick[..pick.IndexOf("  (", StringComparison.Ordinal)];
                ToggleAttach(files.First(x => x.FileName == name));
                return;
            }
            var byArg = files.FirstOrDefault(x => x.Id == args.Trim() || x.FileName.Equals(args.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byArg == null) { AddNote(string.Format(Dictionary.NoteUnknownFile, args.Trim())); return; }
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
            AddNote(attached
                ? string.Format(Dictionary.NoteAttached, f.FileName)
                : string.Format(Dictionary.NoteDetached, f.FileName));
        }

        private Task RetryAsync()
        {
            if (string.IsNullOrEmpty(_lastPrompt))
            {
                AddNote(Dictionary.NoteNothingToRetry);
                return Task.CompletedTask;
            }
            if (!_lastFailed && _history.Count > 0) AddNote(Dictionary.NoteLastReplyOk);
            StartChat(_lastPrompt);
            return Task.CompletedTask;
        }

        private Task OpenDocsAsync()
        {
            const string url = "https://github.com/Graphene-Lab/AgentBridge";
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                AddNote(string.Format(Dictionary.NoteOpenedUrl, url));
            }
            catch (Exception ex)
            {
                AddNote(string.Format(Dictionary.NoteOpenBrowserFailed, ex.Message));
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
                    AddNote(string.Format(Dictionary.NoteWebClientOutdated, dir, WebClientZipUrl));
                    await InstallWebClientAsync(dir);
                }
                AddNote(string.Format(Dictionary.NoteLaunchingWebClient, dir));
                LaunchWebClientProcess(dir);
            }
            catch (Exception ex)
            {
                AddNote(string.Format(Dictionary.NoteWebClientFailed, ex.Message));
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
                Dictionary.HelpQuickStart,
                Dictionary.HelpQuickStart1,
                Dictionary.HelpQuickStart2,
                Dictionary.HelpQuickStart3,
                Dictionary.HelpQuickStart4,
                "",
                Dictionary.HelpCommands,
            };
            foreach (var c in Commands)
                lines.Add($"  /{c.Name} {c.Args}".TrimEnd().PadRight(28) + c.Help);
            lines.Add("");
            lines.Add(Dictionary.HelpShortcuts);
            foreach (var (keys, what) in ShortcutTable)
                lines.Add($"  {keys.PadRight(24)} {what}");
            lines.Add("");
            lines.Add(Dictionary.HelpMouse);
            lines.Add(Dictionary.HelpMouseWheel);
            lines.Add(Dictionary.HelpMouseClickInput);
            lines.Add(Dictionary.HelpMouseClickRow);
            lines.Add("");
            lines.Add(Dictionary.HelpApi);
            lines.Add(Dictionary.HelpApi1);
            lines.Add(Dictionary.HelpApi2);
            lines.Add(Dictionary.HelpApi3);
            lines.Add("");
            lines.Add(Dictionary.HelpOnline);
            lines.Add(Dictionary.HelpOnlineRepo);
            lines.Add(Dictionary.HelpOnlineReadme);
            return ShowPageUiAsync(Dictionary.PageAgentHelp, lines);
        }

        private Task ShowShortcutsAsync()
        {
            var lines = ShortcutTable.Select(s => $"  {s.Keys.PadRight(24)} {s.What}").ToList();
            lines.Insert(0, Dictionary.HelpShortcutsTitle);
            lines.Add("");
            lines.Add(Dictionary.HelpShortcutsFooter);
            return ShowPageUiAsync(Dictionary.PageShortcuts, lines);
        }

        private void ReverseSearch()
        {
            var dlg = new Dialog
            {
                Title = Dictionary.DlgReverseSearch,
                Width = Dim.Percent(85),
                Height = Dim.Percent(50),
                SchemeName = "Dark",
            };
            var q = new TextField { X = 0, Y = 0, Width = Dim.Fill() };
            var list = new ListView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() - 2 };
            var hint = new Label
            {
                Text = Dictionary.DlgReverseHint,
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
                Text = Dictionary.DlgPickerHint,
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

        // The "/model" provider picker: a live filter field above the provider list
        // (same palette pattern). Enter on the list selects that provider; Enter on
        // the filter field uses the typed text as the provider name — nobody remembers
        // the configured ids, so typing a name is as valid as picking from the list.
        // Returns the provider name, or null when cancelled with Esc.
        private string? RunProviderPickerDialog(string title, IReadOnlyList<(string Id, string Display)> items)
        {
            if (items.Count == 0) return null;
            var dlg = new Dialog
            {
                Title = title,
                Width = Dim.Percent(80),
                Height = Dim.Percent(60),
                SchemeName = "Dark",
            };
            var filter = new TextField { X = 0, Y = 0, Width = Dim.Fill() };
            var list = new ListView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() - 2 };
            var hint = new Label
            {
                Text = Dictionary.DlgProviderPickerHint,
                X = 0, Y = Pos.Bottom(list), Width = Dim.Fill(),
            };
            var visible = new List<(string Id, string Display)>();
            void Recompute()
            {
                var f = (filter.Text ?? "").Trim();
                visible = items.Where(p =>
                        p.Id.StartsWith(f, StringComparison.OrdinalIgnoreCase)
                        || (f.Length > 0 && p.Display.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                list.Source = new ListWrapper<string>(new ObservableCollection<string>(visible.Select(p => p.Display)));
                list.SelectedItem = Math.Clamp(list.SelectedItem ?? 0, 0, Math.Max(0, visible.Count - 1));
            }
            string? result = null;
            Recompute();
            filter.ValueChanged += (_, _) => Recompute();
            filter.KeyDown += (_, key) =>
            {
                if (key == Key.Enter)
                {
                    key.Handled = true;
                    var text = (filter.Text ?? "").Trim();
                    if (text.Length == 0 && visible.Count > 0)
                        result = visible[Math.Max(0, list.SelectedItem ?? 0)].Id;
                    else if (visible.FirstOrDefault(p => string.Equals(p.Id, text, StringComparison.OrdinalIgnoreCase)).Id is { } id)
                        result = id;
                    else
                        result = text;   // typed provider name, matched against the list when possible
                    _app.RequestStop(dlg);
                }
                else if (key == Key.Tab)
                {
                    key.Handled = true;
                    if (visible.Count > 0)
                    {
                        var p = visible[Math.Max(0, list.SelectedItem ?? 0)];
                        filter.Text = p.Id;
                    }
                }
            };
            list.Accepted += (_, e) =>
            {
                e.Handled = true;
                if (visible.Count > 0) result = visible[Math.Max(0, list.SelectedItem ?? 0)].Id;
                _app.RequestStop(dlg);
            };
            dlg.Add(filter, list, hint);
            dlg.Initialized += (_, _) => filter.SetFocus();
            _app.Run(dlg);
            dlg.Dispose();
            _inputField?.SetFocus();
            return result;
        }

        private Task<string?> PickProviderOnUiThreadAsync(string title, IReadOnlyList<(string Id, string Display)> items)
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(() =>
            {
                try { tcs.TrySetResult(RunProviderPickerDialog(title, items)); }
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
                Text = Dictionary.DlgPageHint,
                X = 0, Y = Pos.Bottom(tv), Width = Dim.Fill(),
            };
            dlg.Add(tv, hint);
            dlg.AddButton(new Button { Text = Dictionary.Close });
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
                Title = Dictionary.DlgCommandsTitle,
                Width = Dim.Percent(80),
                Height = Dim.Percent(60),
                SchemeName = "Dark",
            };
            var filter = new TextField { Text = initial, X = 0, Y = 0, Width = Dim.Fill() };
            var list = new ListView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() - 2 };
            var hint = new Label
            {
                Text = Dictionary.DlgCommandsHint,
                X = 0, Y = Pos.Bottom(list), Width = Dim.Fill(),
            };
            var visible = new List<CliCommand>();
            void Recompute()
            {
                // Tab completion fills the filter with "/command " — strip the leading
                // slash (MatchCommand compares against the command name only) so the
                // completed command stays visible in the list instead of clearing it.
                var f = (filter.Text ?? "").Trim().TrimStart('/');
                visible = Commands.Where(c => MatchCommand(c, f))
                                  .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                                  .ToList();
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
            var selected = visible.Count > 0 ? visible[Math.Max(0, list.SelectedItem ?? 0)] : null;
            if (selected != null)
            {
                // A typed prefix plus a list selection ("m" → /model) runs what the user
                // highlighted — otherwise Enter would submit an unknown "/m". Exact names
                // (with or without args) still run as typed.
                var first = text.Split(' ')[0];
                if (text.Length == 0
                    || (first.Length > 0 && selected.Name.StartsWith(first, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(first, selected.Name, StringComparison.OrdinalIgnoreCase)))
                    return "/" + selected.Name;
            }
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
                        if (files.Count == 0) { AddNote(Dictionary.NoteNoFiles); return; }
                        var choices = files.Select(f => $"{f.FileName}  ({f.Id}){(f.Attached ? "  " + Dictionary.AttachMarker : "")}").ToList();
                        var pick = RunPickerDialog(Dictionary.DlgToggleAttach, choices);
                        if (pick != null)
                        {
                            var name = pick[..pick.IndexOf("  (", StringComparison.Ordinal)];
                            ToggleAttach(files.First(x => x.FileName == name));
                        }
                    });
                }
                catch (Exception ex)
                {
                    AddNote(string.Format(Dictionary.NoteLoadFilesFailed, ex.Message));
                }
            });
        }

        private void ShowAbout()
        {
            _ = MessageBox.Query(_app, Dictionary.AboutTitle, Dictionary.AboutText, Dictionary.Ok);
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
                Title = Dictionary.SetupTitle,
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
            var llmTab = new View { Title = Dictionary.SetupLlmTab, CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill() };
            {
                providerDropdown.X = 17; providerDropdown.Y = 0; providerDropdown.Width = 46;
                llmTab.Add(new Label { Text = Dictionary.SetupActiveProvider, X = 1, Y = 0, Width = 15 }, providerDropdown);

                // API keys are set per-provider in the Add/Edit dialog below (providers.json);
                // local providers (localhost/127.0.0.1 endpoint) simply leave the field empty.
                int y = 2;
                llmTab.Add(new Label { Text = Dictionary.SetupConfiguredProviders, X = 1, Y = y, Width = Dim.Fill() });
                y++;
                providersList.X = 1; providersList.Y = y; providersList.Width = 62; providersList.Height = 6;
                llmTab.Add(providersList);
                y += 7;
                var addBtn = new Button { Text = Dictionary.SetupAdd, X = 1, Y = y };
                var editBtn = new Button { Text = Dictionary.SetupEdit, X = 9, Y = y };
                var removeBtn = new Button { Text = Dictionary.SetupRemove, X = 17, Y = y };
                llmTab.Add(addBtn, editBtn, removeBtn);

                // The dropdown re-marks the active provider in the list below it.
                providerDropdown.ValueChanged += (_, _) => RefreshProviderList();
                void RefreshProviderList()
                {
                    providersList.Source = new ListWrapper<string>(new ObservableCollection<string>(
                        ProviderConfigs.All.Select(p => p.ProviderName == providerDropdown.Text
                            ? $"{p.ProviderName}  {Dictionary.SetupActiveMarker}" : p.ProviderName)));
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
                        AddNote(string.Format(Dictionary.SetupProviderAdded, cfg.ProviderName));
                    else
                        AddNote(string.Format(Dictionary.SetupProviderExists, cfg.ProviderName));
                    RefreshProviders();
                };
                editBtn.Accepted += (_, _) =>
                {
                    var name = SelectedProviderName();
                    if (name == null) { AddNote(Dictionary.SetupSelectToEdit); return; }
                    var cfg = ShowProviderDialog(ProviderConfigs.Get(name));
                    if (cfg == null) return;
                    ProviderConfigs.Upsert(cfg, persist: true);
                    AddNote(string.Format(Dictionary.SetupProviderUpdated, cfg.ProviderName));
                    RefreshProviders();
                };
                removeBtn.Accepted += (_, _) =>
                {
                    var name = SelectedProviderName();
                    if (name == null) { AddNote(Dictionary.SetupSelectToRemove); return; }
                    if (MessageBox.Query(_app, Dictionary.SetupRemoveProviderTitle,
                            string.Format(Dictionary.SetupRemoveProviderText, name), Dictionary.Cancel, Dictionary.SetupRemove) != 1)
                        return;
                    if (!ProviderConfigs.Remove(name, persist: true))
                    {
                        AddNote(string.Format(Dictionary.SetupCannotRemove, name));
                        return;
                    }
                    AddNote(string.Format(Dictionary.SetupProviderRemoved, name));
                    if (providerDropdown.Text == name)
                    {
                        providerDropdown.Text = ProviderConfigs.Default.ProviderName;
                        _ = SwitchModelAsync(ProviderConfigs.Default.ProviderName);
                    }
                    RefreshProviders();
                };
            }

            // ── Email (SMTP) tab ──
            var emailTab = new View { Title = Dictionary.SetupEmailTab, CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill() };
            var smtpServer = AddField(emailTab, Dictionary.SetupSmtpServer, AIOrchestrator.Setup.SmtpServer, 0);
            var smtpPort = AddField(emailTab, Dictionary.SetupSmtpPort, AIOrchestrator.Setup.SmtpPort.ToString(), 1);
            var smtpUser = AddField(emailTab, Dictionary.SetupSmtpUser, AIOrchestrator.Setup.SmtpUser, 2);
            var smtpPswd = AddField(emailTab, Dictionary.SetupSmtpPassword, AIOrchestrator.Setup.SmtpPassword, 3);
            var recipientEmail = AddField(emailTab, Dictionary.SetupRecipientEmail, AIOrchestrator.Setup.Email, 4);

            // ── Mail reading (IMAP) tab ──
            var imapTab = new View { Title = Dictionary.SetupImapTab, CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill() };
            var imapServer = AddField(imapTab, Dictionary.SetupImapServer, AIOrchestrator.Setup.ImapServer, 0);
            var imapPort = AddField(imapTab, Dictionary.SetupImapPort, AIOrchestrator.Setup.ImapPort.ToString(), 1);
            var imapUser = AddField(imapTab, Dictionary.SetupImapUser, AIOrchestrator.Setup.ImapUser, 2);
            var imapPswd = AddField(imapTab, Dictionary.SetupImapPassword, AIOrchestrator.Setup.ImapPassword, 3);

            // ── General tab ──
            var generalTab = new View { Title = Dictionary.SetupGeneralTab, CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill() };
            var logEnabled = new CheckBox
            {
                Text = Dictionary.SetupStepLogging,
                Value = AIOrchestrator.Log.IsEnabled ? CheckState.Checked : CheckState.UnChecked,
                X = 1, Y = 0,
            };
            generalTab.Add(logEnabled);
            var docsPath = AddField(generalTab, Dictionary.SetupDocumentsPath, AIOrchestrator.Setup.DocumentsPath, 2);

            var save = new Button { Text = Dictionary.SetupSave, IsDefault = true };
            save.Accepted += (_, _) =>
            {
                // Validate BEFORE committing anything: on error the dialog stays open and the
                // user sees why — "model setup saved" is only shown when everything applied.
                if (!string.IsNullOrWhiteSpace(smtpPort.Text) && !int.TryParse((smtpPort.Text ?? "").Trim(), out _))
                {
                    MessageBox.ErrorQuery(_app, Dictionary.SetupInvalidSmtpPortTitle,
                        Dictionary.SetupInvalidSmtpPortText, Dictionary.Ok);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(imapPort.Text) && !int.TryParse((imapPort.Text ?? "").Trim(), out _))
                {
                    MessageBox.ErrorQuery(_app, Dictionary.SetupInvalidImapPortTitle,
                        Dictionary.SetupInvalidImapPortText, Dictionary.Ok);
                    return;
                }
                if (!AIOrchestrator.Setup.TrySetDocumentsPath(docsPath.Text ?? "", out var pathNote))
                {
                    MessageBox.ErrorQuery(_app, Dictionary.SetupInvalidDocsPathTitle,
                        string.Format(Dictionary.SetupInvalidDocsPathText, pathNote), Dictionary.Ok);
                    return;
                }

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

                var chosen = (providerDropdown.Text ?? "").Trim();
                if (chosen.Length > 0 && !string.Equals(chosen, _provider, StringComparison.OrdinalIgnoreCase))
                    _ = SwitchModelAsync(chosen);   // same path as /model (HTTP /v1/control)

                AddNote(pathNote == null ? Dictionary.SetupSaved : string.Format(Dictionary.SetupSavedWithNote, pathNote));
                _app.RequestStop(dlg);
            };
            var close = new Button { Text = Dictionary.Close };
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
        // config on OK, null on cancel). The API-key field serves every cloud provider
        // (any non-loopback endpoint); local providers simply leave it empty. Keys are
        // persisted with the provider to providers.json.
        private ProviderConfig? ShowProviderDialog(ProviderConfig? existing)
        {
            var dlg = new Dialog
            {
                Title = existing == null ? Dictionary.ProviderAddTitle : string.Format(Dictionary.ProviderEditTitle, existing.ProviderName),
                Width = 66,
                Height = 14,
                SchemeName = "Dark",
            };
            int y = 0;
            var nameField = AddField(dlg, Dictionary.ProviderName, existing?.ProviderName, y++);
            var protocol = new DropDownList
            {
                ReadOnly = true,
                X = 20, Y = y, Width = 32,
                Source = new ListWrapper<string>(new ObservableCollection<string>(Enum.GetNames<ProviderProtocol>())),
                Text = (existing?.Protocol ?? ProviderProtocol.OpenAI).ToString(),
            };
            dlg.Add(new Label { Text = Dictionary.ProviderProtocol, X = 1, Y = y, Width = 18 }, protocol);
            y++;
            // Interaction mode: Default leaves the decision to the model size (CLI for small
            // models, API for large ones — ProviderConfig.EffectiveAgentInteractionMode).
            var interactionMode = new DropDownList
            {
                ReadOnly = true,
                X = 20, Y = y, Width = 32,
                Source = new ListWrapper<string>(new ObservableCollection<string>(
                    new[] { Dictionary.InteractionModeDefault, nameof(AgentInteractionMode.API), nameof(AgentInteractionMode.CLI) })),
                Text = existing?.AgentInteractionMode?.ToString() ?? Dictionary.InteractionModeDefault,
            };
            dlg.Add(new Label { Text = Dictionary.ProviderInteractionMode, X = 1, Y = y, Width = 18 }, interactionMode);
            y++;
            var modelField = AddField(dlg, Dictionary.ProviderModel, existing?.ModelName, y++);
            var baseField = AddField(dlg, Dictionary.ProviderBaseAddress, existing?.BaseAddress.ToString(), y++);
            var endPointField = AddField(dlg, Dictionary.ProviderEndpoint, existing?.EndPoint, y++);
            // Masked API-key field (secret on screen); leave empty for local providers
            // (localhost/127.0.0.1 endpoint — they are treated as keyless regardless of name).
            var apiKeyField = AddField(dlg, Dictionary.ProviderApiKey, existing?.ApiKey, y++, secret: true);
            var ctxField = AddField(dlg, Dictionary.ProviderContextWindow, (existing?.ContextWindow ?? 32768).ToString(), y++);
            var timeoutField = AddField(dlg, Dictionary.ProviderTimeout, ((int)(existing?.Timeout.TotalSeconds ?? 30)).ToString(), y++);

            ProviderConfig? result = null;
            var ok = new Button { Text = Dictionary.Ok, IsDefault = true };
            ok.Accepted += (_, _) =>
            {
                var providerName = (nameField.Text ?? "").Trim();
                if (providerName.Length == 0)
                {
                    _ = MessageBox.Query(_app, Dictionary.ProviderAddTitle, Dictionary.ProviderNameRequired, Dictionary.Ok);
                    return;
                }
                if (!Uri.TryCreate((baseField.Text ?? "").Trim(), UriKind.Absolute, out var uri))
                {
                    _ = MessageBox.Query(_app, Dictionary.ProviderAddTitle, Dictionary.ProviderBaseAddressInvalid, Dictionary.Ok);
                    return;
                }
                if (!int.TryParse((ctxField.Text ?? "").Trim(), out var ctx) || ctx <= 0)
                {
                    _ = MessageBox.Query(_app, Dictionary.ProviderAddTitle, Dictionary.ProviderContextInvalid, Dictionary.Ok);
                    return;
                }
                if (!int.TryParse((timeoutField.Text ?? "").Trim(), out var secs) || secs <= 0) secs = 30;
                if (!Enum.TryParse<ProviderProtocol>((protocol.Text ?? "").Trim(), out var proto)) proto = ProviderProtocol.OpenAI;
                // Interaction mode: "Default" (localized) → null → the model-size default applies.
                var modeText = (interactionMode.Text ?? "").Trim();
                AgentInteractionMode? mode = null;
                if (string.Equals(modeText, nameof(AgentInteractionMode.API), StringComparison.OrdinalIgnoreCase)) mode = AgentInteractionMode.API;
                else if (string.Equals(modeText, nameof(AgentInteractionMode.CLI), StringComparison.OrdinalIgnoreCase)) mode = AgentInteractionMode.CLI;
                result = new ProviderConfig
                {
                    ProviderName = providerName,
                    Protocol = proto,
                    AgentInteractionMode = mode,
                    ModelName = (modelField.Text ?? "").Trim(),
                    BaseAddress = uri,
                    EndPoint = (endPointField.Text ?? "").Trim(),
                    ApiKey = (apiKeyField.Text ?? "").Trim(),
                    ContextWindow = ctx,
                    Timeout = TimeSpan.FromSeconds(secs),
                };
                _app.RequestStop(dlg);
            };
            var cancel = new Button { Text = Dictionary.Cancel };
            cancel.Accepted += (_, _) => _app.RequestStop(dlg);
            dlg.AddButton(ok);
            dlg.AddButton(cancel);
            dlg.Initialized += (_, _) => nameField.SetFocus();
            _app.Run(dlg);
            dlg.Dispose();
            return result;
        }

        // Adds a labelled single-line field to a form and returns the field.
        // When secret is true the typed text is masked on screen (e.g. API keys, passwords).
        private static TextField AddField(View parent, string label, string? value, int y, int labelWidth = 18, int fieldWidth = 44, bool secret = false)
        {
            parent.Add(new Label { Text = label, X = 1, Y = y, Width = labelWidth });
            var field = new TextField { Text = value ?? "", X = labelWidth + 2, Y = y, Width = fieldWidth, Secret = secret };
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
                    _statusNote = Dictionary.StatusServerUnreachableHeadless;
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
                _statusNote = string.Format(Dictionary.StatusServerUnreachable, ex.Message);
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
                    _interactionMode = GetStr(llm, "interaction_mode") ?? "";
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
                    if (caps.TryGetProperty("sip", out var sip))
                    {
                        _sipAvailable = GetBool(sip, "available");
                        if (sip.TryGetProperty("status", out var s))
                            _sipState = PhaseLabel(GetStr(s, "phase") ?? "");
                    }
                }
                UpdateStatusUi();
            }
            catch { }
        }

        // While the SIP server is available its state changes on its own (incoming calls),
        // so the status bar polls /v1/sip/status every few seconds.
        private void StartSipPolling()
        {
            _app.AddTimeout(TimeSpan.FromSeconds(3), () =>
            {
                if (_sipAvailable) _ = RefreshSipStatusAsync();
                return true;   // recurring
            });
        }

        private async Task RefreshSipStatusAsync()
        {
            try
            {
                using var resp = await _http.GetAsync("/v1/sip/status").WaitAsync(TimeSpan.FromSeconds(5));
                if (!resp.IsSuccessStatusCode) return;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (!doc.RootElement.TryGetProperty("sip", out var sip)) return;
                _sipState = PhaseLabel(GetStr(sip, "phase") ?? "");
                UpdateStatusUi();
            }
            catch { }
        }

        private static string PhaseLabel(string phase) => phase switch
        {
            "ringing" => Dictionary.SipPhaseRinging,
            "pin" => Dictionary.SipPhasePin,
            "conversation" => Dictionary.SipPhaseConversation,
            "ended" => Dictionary.SipPhaseEnded,
            _ => "",
        };

        private static string? GetStr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static int GetInt(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
        private static bool GetBool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
