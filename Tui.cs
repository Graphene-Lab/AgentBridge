using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

// ═══════════════════════════════════════════════════════════════════════
//  Terminal.Gui v2 — LOCAL DEVELOPER GUIDE (READ BEFORE EDITING THIS TUI)
//  docs-dev/TUI-DEVELOPMENT.md is the offline reference for the pinned package
//  versions: API cheat-sheet, pitfalls (focus, Invoke, Editor document
//  mutation, console leak) and cross-platform (Windows/Linux/macOS) rules.
//  The official API XML docs ship with the NuGet packages (see guide §1).
// ═══════════════════════════════════════════════════════════════════════

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
        private View? _asciiBanner;
        private bool _bannerVisible;

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
        // Non-null when the user enabled a custom tool combination in the /agent
        // checklist (overrides the agent-set preset in the chat request via `tools`).
        private List<string>? _customTools;
        private bool _ttsAvailable, _voiceAvailable;
        private string _ttsDetail = "", _voiceDetail = "";
        private string _statusNote = "";
        // SIP telephony state (from GET /v1/sip/status; polled while the server reports it available).
        private bool _sipAvailable;
        private string _sipState = "";
        // Telegram chat medium state (from GET /v1/telegram/status; polled while enabled).
        private bool _telegramAvailable;
        private string _telegramState = "";

        private static readonly string[] SpinnerChars = { "⣾", "⣽", "⣻", "⢿", "⡿", "⣟", "⣯", "⣷" };
        private int _spinnerIndex;
        private volatile bool _spinnerActive;
        private Label? _spinnerLabel;

        // Top-right busy indicator (right end of the menu-bar row): shows the most important
        // operation currently running — [indicizzazione…]/[indexing…] for the background
        // document reindex (from AIOrchestrator.Setup.IndexingChanged), the chat stream, voice
        // listening. Ops are tagged strings so several concurrent operations collapse into one.
        private readonly object _busyLock = new();
        private readonly HashSet<string> _busyOps = new(StringComparer.Ordinal);
        private Label? _busyLabel;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private const int MaxHistory = 1000;
        private const int MaxInputLines = 4;
        // Right-end slot on the menu-bar row reserved for the busy indicator (overlay label).
        private const int BusyLabelWidth = 20;

        // UI strings come from the localized dictionary (system language, English fallback) —
        // see Resources/Dictionary.*.resx. Command names (/help, /model...) are NOT translated.
        private static string PlaceholderText => Dictionary.InputPlaceholder;

        // Web GUI (Giraffe AI): a static chat client served by its own launcher, installed
        // and kept up to date next to the executable by WebClientUpdater (see that file).

        private sealed class Entry
        {
            public required string Role;
            public required string Text;
            public bool Error;
            // Files the agent attached to this message (done method's "attachments"): each is
            // saved to disk by the client so the terminal user can open/download it.
            public List<string>? Attachments;
        }

        private sealed class FileRef
        {
            public required string Id;
            public required string FileName;
            public string Status = "";
            public bool Attached;
        }

        // The single command registry: /help (alphabetical), the "/" palette and the menu
        // bars are all generated from this list. MenuGroup picks the top-level menu where
        // the command gets its voice (BuildUI logs a warning for commands that are
        // forgotten there); MenuTitle is the short localized label used by that voice.
        private sealed record CliCommand(
            string Name, string Args, string Help, Func<Tui, string, Task> Run,
            string[]? Aliases = null,
            string? MenuGroup = null, string? MenuTitle = null, Key Shortcut = default);

        private static readonly List<CliCommand> Commands = new()
        {
            // Chat
            new("new", "", Dictionary.CmdNew, (t, _) => t.NewSessionAsync(), new[] { "/reset" },
                MenuGroup: "chat", MenuTitle: Dictionary.MenuNewChat, Shortcut: Key.N.WithCtrl),
            new("clear", "", Dictionary.CmdClear, (t, _) => t.ClearHistoryAsync(),
                MenuGroup: "chat", MenuTitle: Dictionary.MenuClearHistory, Shortcut: Key.L.WithCtrl),
            new("tts", "[text]", Dictionary.CmdTts, (t, a) => t.TtsAsync(a),
                MenuGroup: "chat", MenuTitle: Dictionary.MenuTts),
            new("retry", "", Dictionary.CmdRetry, (t, _) => t.RetryAsync(),
                MenuGroup: "chat", MenuTitle: Dictionary.MenuRetryLast, Shortcut: Key.Y.WithCtrl),
            new("exit", "", Dictionary.CmdExit, (t, _) => t.ExitAsync(), new[] { "/quit" },
                MenuGroup: "chat", MenuTitle: Dictionary.MenuExit, Shortcut: Key.Q.WithCtrl),
            // File
            new("files", "add <path>|rm <id>|list", Dictionary.CmdFiles, (t, a) => t.FilesAsync(a),
                MenuGroup: "file", MenuTitle: Dictionary.MenuFiles),
            new("attach", "[id]", Dictionary.CmdAttach, (t, a) => t.AttachAsync(a),
                MenuGroup: "file", MenuTitle: Dictionary.MenuAttach),
            // Settings (menu Impostazioni/Settings): main setup, tool selection, voice and
            // the SIP/Telegram bridges. /model stays under Session: it switches the CURRENT
            // chat on the fly and never touches the default provider configured here.
            new("setup", "", Dictionary.CmdModelSetup, (t, _) => t.ShowModelSetupAsync(), new[] { "/modelsetup" },
                MenuGroup: "settings", MenuTitle: Dictionary.MenuMainSetup),
            new("tools", "[name]", Dictionary.CmdAgent, (t, a) => t.SwitchAgentAsync(a), new[] { "/agent" },
                MenuGroup: "settings", MenuTitle: Dictionary.MenuTools),
            new("voice", "[lang]", Dictionary.CmdVoice, (t, a) => t.VoiceAsync(a),
                MenuGroup: "settings", MenuTitle: Dictionary.MenuVoice),
            new("ttsengine", "[name]", Dictionary.CmdTtsEngine, (t, a) => t.TtsEngineAsync(a),
                MenuGroup: "settings", MenuTitle: Dictionary.MenuTtsEngine),
            new("sip", "status|config [set <key> <value>|reload]|call <sip-uri>|answer on|off|hangup", Dictionary.CmdSip, (t, a) => t.SipAsync(a),
                MenuGroup: "settings", MenuTitle: Dictionary.MenuSip),
            new("telegram", "status|config [set <key> <value>|reload]|login-code <code>|allow|disallow <user>", Dictionary.CmdTelegram, (t, a) => t.TelegramAsync(a),
                MenuGroup: "settings", MenuTitle: Dictionary.MenuTelegram),
            // Session
            new("model", "[name]", Dictionary.CmdModel, (t, a) => t.SwitchModelAsync(a),
                MenuGroup: "session", MenuTitle: Dictionary.MenuLlmModel),
            new("features", "[name] [on|off]", Dictionary.CmdFeatures, (t, a) => t.FeaturesAsync(a),
                MenuGroup: "session", MenuTitle: Dictionary.MenuFeatures),
            new("status", "", Dictionary.CmdStatus, (t, _) => t.ShowStatusAsync(),
                MenuGroup: "session", MenuTitle: Dictionary.MenuStatus),
            new("health", "", Dictionary.CmdHealth, (t, _) => t.HealthAsync(),
                MenuGroup: "session", MenuTitle: Dictionary.MenuHealth),
            // Web
            new("web", "", Dictionary.CmdWeb, (t, _) => t.LaunchWebClientAsync(),
                MenuGroup: "web", MenuTitle: Dictionary.MenuGui),
            new("officemanager", "", Dictionary.CmdOfficeManager, (t, _) => t.LaunchOfficeManagerAsync(),
                MenuGroup: "web", MenuTitle: Dictionary.MenuOfficeManager),
            // Help
            new("help", "", Dictionary.CmdHelp, (t, _) => t.ShowHelpAsync(), new[] { "/?" },
                MenuGroup: "help", MenuTitle: Dictionary.MenuHelpItem, Shortcut: Key.F1),
            new("shortcuts", "", Dictionary.CmdShortcuts, (t, _) => t.ShowShortcutsAsync(), new[] { "/keys" },
                MenuGroup: "help", MenuTitle: Dictionary.MenuShortcuts),
            new("docs", "", Dictionary.CmdDocs, (t, _) => t.OpenDocsAsync(),
                MenuGroup: "help", MenuTitle: Dictionary.MenuDocumentation),
            new("update", "", Dictionary.CmdUpdate, (t, _) => t.UpdateAsync(),
                MenuGroup: "help", MenuTitle: Dictionary.MenuCheckUpdates),
            // No menu voice: the Help menu hosts a dedicated state toggle for this one
            // (crashReportItem in BuildUI) — see ValidateMenuCoverage.
            new("crashreport", "", Dictionary.CmdCrashReport, (t, _) => t.CrashReportAsync()),
        };

        private const uint SndAsync = 0x0001;
        private const uint SndFilename = 0x00020000;
        [DllImport("winmm.dll", SetLastError = true)]
        private static extern bool PlaySound(string pszSound, IntPtr hmod, uint fdwSound);

        // ── AGENT ASCII art ──
        // ONE definition (embedded resource, see the csproj) used by both the startup
        // banner above the chat and Help → About. The per-line gradient follows the
        // Qwen CLI brand (#4796E4 → #847ACE → #C3677F → BrightBlue/BrightMagenta/BrightRed).
        private static readonly string[] AsciiArtLines = LoadAsciiArt();
        private static readonly TuiAttribute[] AsciiArtColors =
        {
            new(Color.BrightBlue, Color.Black),
            new(Color.BrightBlue, Color.Black),
            new(Color.BrightMagenta, Color.Black),
            new(Color.BrightMagenta, Color.Black),
            new(Color.BrightRed, Color.Black),
            new(Color.BrightRed, Color.Black),
        };

        private static string[] LoadAsciiArt()
        {
            try
            {
                using var s = typeof(ConsoleTui).Assembly
                    .GetManifestResourceStream("AgentBridge.assets.agent-ascii-art.txt");
                if (s == null) return Array.Empty<string>();
                using var r = new StreamReader(s);
                return r.ReadToEnd().Replace("\r\n", "\n")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
            catch { return Array.Empty<string>(); }
        }

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
            PuppetMode._app = _app;  // Share instance with PuppetMode (debug-only control surface)
            if (PuppetMode.Enabled) PuppetMode.StartPump();
            // Surface auto-update progress in the status bar (fires from background tasks).
            AutoUpdate.OnStatus += OnUpdateStatus;

            // Background document reindex events → top-right busy indicator. Subscribing and
            // the IsIndexingNow probe never create the processor, so merely running the TUI
            // cannot trigger a multi-minute index (see AIOrchestrator.Setup).
            AIOrchestrator.Setup.IndexingChanged += OnIndexingChanged;
            if (AIOrchestrator.Setup.IsIndexingNow) SetBusy("indexing", true);

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
            for (int i = 0; i < Math.Min(AsciiArtLines.Length, AsciiArtColors.Length); i++)
                SchemeManager.AddScheme($"Ascii{i}", new Scheme { Normal = AsciiArtColors[i] });

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
            AIOrchestrator.Setup.IndexingChanged -= OnIndexingChanged;
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

        // Slash commands whose menu voice is a custom item (state shown in the title,
        // not just "run the command") — see ValidateMenuCoverage.
        private static readonly HashSet<string> MenuVoiceExempt = new(StringComparer.Ordinal) { "crashreport" };

        // Commands placed in the menus by CommandMenuItem (see ValidateMenuCoverage).
        private readonly HashSet<string> _menuVoices = new(StringComparer.Ordinal);

        // Menu voice for a slash command: the label, the accelerator and the action come
        // from the command's registry entry, and the action runs through
        // RunCommandByName — the same guarded path as typing "/name". Every placed
        // command is recorded so ValidateMenuCoverage can flag forgotten ones.
        private MenuItem CommandMenuItem(string name)
        {
            var cmd = Commands.FirstOrDefault(c => c.Name == name);
            _menuVoices.Add(name);
            if (cmd == null)
            {
                Log.LogStep($"TUI menu: '/{name}' is not in the Commands registry", monitor: true);
                return new MenuItem(name, Key.Empty, () => { });
            }
            return new MenuItem(cmd.MenuTitle ?? cmd.Help, cmd.Shortcut, () => RunCommandByName(cmd.Name, ""));
        }

        // Every slash command must be reachable from the menus. New commands must either
        // get a CommandMenuItem("name") above or be listed in MenuVoiceExempt; anything
        // forgotten shows up here as a loud startup warning, so /help, the "/" palette
        // and the menu bars stay in sync by construction.
        private void ValidateMenuCoverage()
        {
            var missing = Commands.Select(c => c.Name)
                .Where(n => !_menuVoices.Contains(n) && !MenuVoiceExempt.Contains(n))
                .ToList();
            if (missing.Count == 0) return;
            Log.LogStep(
                $"TUI menu: commands without a menu voice: {string.Join(", ", missing.Select(n => "/" + n))} — add them in BuildUI (see Commands registry)",
                monitor: true);
        }

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
                Log.LogStep($"TUI AutoUpdate toggled: {AutoUpdate.Enabled}", monitor: true);
                autoUpdateItem.Title = string.Format(Dictionary.MenuAutoUpdate, AutoUpdate.Enabled ? Dictionary.On : Dictionary.Off);
            });

            // Crash-diagnostics toggle: whether sanitized crash reports are sent to the GitHub
            // repository (see CrashReporter.cs). State shown in the title, like Auto-Update.
            MenuItem crashReportItem = null!;
            crashReportItem = new MenuItem(string.Format(Dictionary.MenuCrashReport, CrashReporter.Enabled ? Dictionary.On : Dictionary.Off), Key.Empty, () =>
            {
                CrashReporter.Toggle();
                Log.LogStep($"TUI CrashReport toggled: {CrashReporter.Enabled}", monitor: true);
                AddNote(CrashReporter.Enabled ? Dictionary.NoteCrashReportEnabled : Dictionary.NoteCrashReportDisabled);
                crashReportItem.Title = string.Format(Dictionary.MenuCrashReport, CrashReporter.Enabled ? Dictionary.On : Dictionary.Off);
            });

            // Menus are assembled from the Commands registry (single source): each item
            // below is a command whose label/accelerator/action come from its registry
            // entry, so /help, the "/" palette and the menus can never drift apart.
            // ValidateMenuCoverage warns when a command was forgotten here.
            var menu = new MenuBar(new MenuBarItem[]
            {
                new(Dictionary.MenuChat, new MenuItem[]
                {
                    CommandMenuItem("new"),
                    CommandMenuItem("clear"),
                    CommandMenuItem("tts"),
                    new MenuItem(Dictionary.MenuCommands, Key.Empty, () => ShowCommandMenu("")),
                    CommandMenuItem("retry"),
                    CommandMenuItem("exit"),
                }),
                new(Dictionary.MenuFile, new MenuItem[]
                {
                    CommandMenuItem("files"),
                    CommandMenuItem("attach"),
                }),
                new(Dictionary.MenuSettings, new MenuItem[]
                {
                    CommandMenuItem("setup"),
                    CommandMenuItem("tools"),
                    CommandMenuItem("voice"),
                    CommandMenuItem("ttsengine"),
                    CommandMenuItem("sip"),
                    CommandMenuItem("telegram"),
                }),
                new(Dictionary.MenuSession, new MenuItem[]
                {
                    CommandMenuItem("model"),
                    CommandMenuItem("features"),
                    CommandMenuItem("status"),
                    CommandMenuItem("health"),
                }),
                new(Dictionary.MenuWeb, new MenuItem[]
                {
                    CommandMenuItem("web"),
                    CommandMenuItem("officemanager"),
                }),
                new(Dictionary.MenuHelp, new MenuItem[]
                {
                    autoUpdateItem,
                    crashReportItem,
                    CommandMenuItem("update"),
                    CommandMenuItem("help"),
                    CommandMenuItem("shortcuts"),
                    CommandMenuItem("docs"),
                    new MenuItem(Dictionary.MenuIssues, Key.Empty, () => OpenIssuesAsync()),
                    new MenuItem(Dictionary.MenuAbout, Key.Empty, () => ShowAbout()),
                }),
            });
            _mainWindow.Add(menu);

            // Busy indicator on the right end of the menu-bar row. Terminal.Gui's MenuBar
            // has no right-side widget slot, so a small overlay label (added after the menu,
            // therefore drawn above it) shows the current operation; it is hidden when idle.
            _busyLabel = new Label
            {
                Text = "",
                X = Pos.AnchorEnd(BusyLabelWidth), Y = 0, Width = BusyLabelWidth,
                TextAlignment = Alignment.End,
                SchemeName = "Hint",
                Visible = false,
            };
            _mainWindow.Add(_busyLabel);
            ValidateMenuCoverage();

            // Esc never quits the app directly: it is handled by the focused view
            // (input line, dialogs, menus). Guard the window's default Esc→Quit
            // binding so an Esc pressed on a non-handling view (e.g. the send
            // button) cannot close the whole UI accidentally.
            _mainWindow.KeyDown += (_, key) =>
            {
                if (key == Key.Esc) key.Handled = true;
                // Puppet mode only: PrintScreen dumps the current screen to a file.
                else if (PuppetMode.Enabled && key == Key.PrintScreen)
                {
                    key.Handled = true;
                    PuppetCapture();
                }
            };

            // Content area below the menu bar (status line + StatusBar own the last
            // two rows). CanFocus: a plain View defaults to CanFocus=false, which
            // would block focus for every focusable child below it (the input field).
            var contentArea = new View
            {
                X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() - 3,
                CanFocus = true,
            };
            _mainWindow.Add(contentArea);

            // Chat panel: history + input line, full width. The AGENT ASCII-art banner
            // (gradient) sits above the chat at startup — the welcome message is the
            // first history entry below it — and collapses on the first chat message.
            var chatFrame = new FrameView
            {
                Title = Dictionary.ChatFrameTitle,
                X = 0, Y = 0,
                Width = Dim.Fill(), Height = Dim.Fill(),
            };
            contentArea.Add(chatFrame);

            if (AsciiArtLines.Length > 0)
            {
                _asciiBanner = new View
                {
                    X = 0, Y = 0, Width = Dim.Fill(), Height = AsciiArtLines.Length + 1,
                };
                for (int i = 0; i < AsciiArtLines.Length; i++)
                    _asciiBanner.Add(new Label { Text = AsciiArtLines[i], X = 1, Y = i, SchemeName = $"Ascii{i}" });
                chatFrame.Add(_asciiBanner);
                _bannerVisible = true;
            }

            _chatView = new Editor
            {
                X = 0, Y = _bannerVisible ? Pos.Bottom(_asciiBanner!) : 0, Width = Dim.Fill(), Height = Dim.Fill() - 1,
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

            // Status line (dynamic state: connection, provider/model, tools, context,
            // capabilities, notes) above a real StatusBar with the static key hints.
            _statusLabel = new Label
            {
                X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1,
                SchemeName = "Hint",
                Text = "",
            };
            _mainWindow.Add(_statusLabel);
            _mainWindow.Add(new StatusBar(new[]
            {
                new Shortcut { Title = Dictionary.StatusBarHints },
            }));
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
            chat.Height = Dim.Fill() - rows - BannerRows;
        }

        // Rows the startup ASCII-art banner occupies above the chat (0 once collapsed).
        private int BannerRows => _bannerVisible ? AsciiArtLines.Length + 1 : 0;

        // The banner gives way to the conversation on the first chat message.
        private void CollapseBanner()
        {
            if (!_bannerVisible || _asciiBanner == null || _chatView == null) return;
            Log.LogStep("TUI banner collapsed (first chat message)", monitor: true);
            _bannerVisible = false;
            _asciiBanner.SuperView?.Remove(_asciiBanner);
            _chatView.Y = 0;
            _inputLines = 0;   // force UpdateInputLayout to recompute the chat height
            UpdateInputLayout();
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
            Log.LogStep("TUI spinner started");
            _spinnerActive = true;
            _spinnerIndex = 0;
            if (_spinnerLabel != null)
                _spinnerLabel.Text = SpinnerChars[0];
        }

        private void StopSpinner()
        {
            Log.LogStep("TUI spinner stopped");
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
                Log.LogStep($"TUI submit: {text}");
                RunCommandLine(text);
                return;
            }
            Log.LogStep($"TUI submit (chat): {(text.Length > 100 ? text[..100] + "…" : text)}", monitor: true);
            CollapseBanner();   // the conversation starts: the ASCII-art banner gives way
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
            SetBusy("generating", true);
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
                // Additive extension: an explicit tool combination (custom checklist
                // selection) overrides the agent set resolved from `model`.
                var custom = _customTools is { Count: > 0 } ? _customTools : null;
                var body = JsonSerializer.Serialize(new
                {
                    model = _agentSet,
                    tools = custom,
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
                    _statusNote = FriendlyHttp((int)response.StatusCode);
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
                        // Agent-attached files (standard MCP embedded-resource shape): saved to
                        // disk next to the executable so the terminal user can open/download them.
                        if (doc.RootElement.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var att in atts.EnumerateArray())
                            {
                                try
                                {
                                    if (!att.TryGetProperty("resource", out var res) ||
                                        !res.TryGetProperty("blob", out var blob) || blob.ValueKind != JsonValueKind.String)
                                        continue;
                                    var name = att.TryGetProperty("name", out var n) ? n.GetString() : "attachment";
                                    var safe = string.Concat((name ?? "attachment").Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
                                    var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "attachments");
                                    Directory.CreateDirectory(dir);
                                    var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}");
                                    await File.WriteAllBytesAsync(path, Convert.FromBase64String(blob.GetString()!), _chatCts.Token);
                                    (_pending!.Attachments ??= new List<string>()).Add(path);
                                }
                                catch { /* a broken attachment must never break the chat */ }
                            }
                        }
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
                SetBusy("generating", false);
                lock (_stateLock)
                {
                    _chatCts?.Dispose();
                    _chatCts = null;
                }
                Ui(() => { StopSpinner(); RefreshHistory(); UpdateStatus(); });
                _ = Task.Run(RefreshSessionStateAsync);
                Log.LogStep($"TUI chat finished: {sw.ElapsedMilliseconds} ms, reply {( _pending?.Text?.Length ?? 0)} chars, status: {_statusNote}");
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
            if (e.Attachments is { Count: > 0 })
            {
                foreach (var path in e.Attachments)
                    sb.Append(string.Format(Dictionary.ChatAttachmentMarker, Path.GetFileName(path))).Append('\n');
                sb.Append('\n');
            }
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

        // ── Status bar ──
        // One comprehensible line: connection, provider/model, the active tools,
        // context usage, capabilities and the transient note. No uuid and no cryptic
        // abbreviations (sess:, f:, tts:✓) — those told the user nothing.
        private void UpdateStatus()
        {
            if (_statusLabel == null) return;
            // host:port (not the full URL) tells the user which server they talk to.
            var parts = new List<string> { _connected ? "●" : "○", ShortServerUrl(_serverUrl) };
            if (_provider.Length > 0) parts.Add(_provider);
            if (_modelName.Length > 0 && !string.Equals(_modelName, _provider)) parts.Add(_modelName);
            var tools = EffectiveLoadedTools();
            if (tools.Length > 0)
                parts.Add($"{Dictionary.StatusTools}: {string.Join(", ", tools.Select(ToolShortName))}");
            if (_contextWindow > 0)
                parts.Add($"ctx {FormatTokens(_historyTokens)}/{FormatTokens(_contextWindow)}");
            parts.Add($"TTS {(_ttsAvailable ? "✓" : "✗")}");
            parts.Add($"mic {(_voiceAvailable ? "✓" : "✗")}");
            if (_sipAvailable && _sipState.Length > 0) parts.Add($"sip: {_sipState}");
            if (_telegramAvailable && _telegramState.Length > 0) parts.Add($"tg: {_telegramState}");
            if (_chatRunning != 0) parts.Add(Dictionary.StatusGeneratingShort);
            if (_statusNote.Length > 0) parts.Add(_statusNote);
            var text = string.Join(" · ", parts.Where(p => p.Length > 0));
            _statusLabel.Text = text.Length > 240 ? text[..240] : text;
        }

        // The tools that will actually run in the chat: the custom checklist selection
        // when set, otherwise the agent-set preset.
        private string[] EffectiveTools() => _customTools is { Count: > 0 }
            ? _customTools.ToArray()
            : AgentTools.Resolve(_agentSet);

        // The tools shown in the status bar: only the ones actually loaded at runtime
        // (a preset may name a plugin that is absent from Tools/ on this machine — the
        // server skips unknown names, so showing them would overstate the agent's tools).
        private string[] EffectiveLoadedTools()
        {
            var loaded = AgentTools.Catalog().Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return EffectiveTools().Where(t => loaded.Contains(t)).ToArray();
        }

        // What the status page shows as the agent set: the preset id, or "custom" with
        // the individually enabled tools when the /agent checklist was used.
        private string AgentSetDisplay() => _customTools is { Count: > 0 }
            ? $"custom ({string.Join(", ", _customTools.Select(ToolShortName))})"
            : _agentSet;

        // "FileTool" → "File", "EMailTool" → "Email": readable tool names for the UI.
        private static string ToolShortName(string name) =>
            string.Equals(name, "EMailTool", StringComparison.OrdinalIgnoreCase) ? "Email"
            : name.EndsWith("Tool", StringComparison.Ordinal) ? name[..^4]
            : name;

        private static string ShortServerUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                return $"{u.Host}:{u.Port}";
            }
            catch { return url; }
        }

        private static string FormatTokens(int n) => n switch
        {
            >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
            >= 1_000 => $"{n / 1_000.0:0.#}k",
            _ => n.ToString(),
        };

        // A note the user can understand instead of a bare HTTP code.
        private static string FriendlyHttp(int code) => code switch
        {
            401 or 403 => string.Format(Dictionary.HttpStatusAuth, code),
            404 => string.Format(Dictionary.HttpStatusNotFound, code),
            429 => string.Format(Dictionary.HttpStatusRateLimited, code),
            >= 500 => string.Format(Dictionary.HttpStatusServer, code),
            _ => string.Format(Dictionary.HttpStatusOther, code),
        };

        private void OnUpdateStatus(string message) => Ui(() => { _statusNote = message; UpdateStatusUi(); });

        private void UpdateStatusUi() => Ui(UpdateStatus);

        // ── Top-right busy indicator ──
        private void OnIndexingChanged(bool indexing) => SetBusy("indexing", indexing);

        private void SetBusy(string op, bool on)
        {
            lock (_busyLock)
            {
                if (on) _busyOps.Add(op);
                else _busyOps.Remove(op);
            }
            Ui(UpdateBusyLabel);
        }

        private void UpdateBusyLabel()
        {
            if (_busyLabel == null) return;
            string text;
            lock (_busyLock)
            {
                text = _busyOps.Contains("indexing") ? Dictionary.BusyIndexing
                    : _busyOps.Contains("generating") ? Dictionary.BusyGenerating
                    : _busyOps.Contains("listening") ? Dictionary.BusyListening
                    : "";
            }
            _busyLabel.Visible = text.Length > 0;
            _busyLabel.Text = text;
        }

        // Puppet mode (PrintScreen): dumps the current screen to a timestamped file
        // next to the executable so an agent tester can read it later.
        private void PuppetCapture()
        {
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "tui-screenshots");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"puppet-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(file, PuppetMode.ANSI_Tui_Capture());
                _statusNote = $"Puppet capture → {file}";
                Log.LogStep($"TUI PuppetCapture (PrintScreen) → {file}", monitor: true);
            }
            catch (Exception ex) { _statusNote = $"Puppet capture failed: {ex.Message}"; }
            UpdateStatusUi();
        }

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
            Log.LogStep($"TUI menu command: {name} {args}".TrimEnd(), monitor: true);
            var cmd = Commands.FirstOrDefault(c => c.Name == name);
            if (cmd != null) _ = RunCommandAsync(cmd, args);
        }

        private async Task RunCommandAsync(CliCommand cmd, string args)
        {
            Log.LogStep($"TUI running command: {cmd.Name} {(args.Length > 0 ? args : "")}".TrimEnd());
            try
            {
                await cmd.Run(this, args);
                Log.LogStep($"TUI command completed: {cmd.Name}");
            }
            catch (Exception ex)
            {
                Log.LogStep($"TUI command FAILED: {cmd.Name}: {ex.Message}");
                AddNote(string.Format(Dictionary.NoteCommandFailed, cmd.Name, ex.Message));
            }
            await RefreshSessionStateAsync();
            UpdateStatusUi();
        }

        private Task CrashReportAsync()
        {
            CrashReporter.Toggle();
            AddNote(CrashReporter.Enabled ? Dictionary.NoteCrashReportEnabled : Dictionary.NoteCrashReportDisabled);
            return Task.CompletedTask;
        }

        private Task ExitAsync()
        {
            Ui(RequestExit);
            return Task.CompletedTask;
        }

        private void RequestExit()
        {
            Log.LogStep("TUI exit requested", monitor: true);
            _app.RequestStop(_mainWindow);
        }

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
                case "config":
                {
                    var rest = arg.Length == 0 ? "" : arg;
                    var parts2 = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var sub2 = parts2.Length == 0 ? "" : parts2[0].ToLowerInvariant();

                    if (sub2 == "set")
                    {
                        var kv = parts2.Length > 1 ? parts2[1].Trim() : "";
                        var kvParts = kv.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (kvParts.Length == 0) { AddNote(Dictionary.NoteSipConfigUsage); return; }
                        var key = kvParts[0];
                        var value = kvParts.Length > 1 ? kvParts[1] : "";
                        var body = JsonSerializer.Serialize(new { key, value }, JsonOpts);
                        using var resp = await _http.PostAsync("/v1/sip/config", new StringContent(body, Encoding.UTF8, "application/json"));
                        if (resp.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                            var msg = GetStr(doc.RootElement, "message") ?? "";
                            AddNote(msg);
                            if (GetBool(doc.RootElement, "restart_required"))
                                AddNote(Dictionary.NoteSipConfigRestart);
                        }
                        else
                        {
                            AddNote(string.Format(Dictionary.NoteSipConfigFailed, await ReadErrorAsync(resp)));
                        }
                    }
                    else if (sub2 == "reload")
                    {
                        using var resp = await _http.PostAsync("/v1/sip/config/reload", new StringContent("{}", Encoding.UTF8, "application/json"));
                        if (resp.IsSuccessStatusCode)
                        {
                            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                            var msg = GetStr(doc.RootElement, "message") ?? "";
                            AddNote(msg);
                            if (GetBool(doc.RootElement, "restart_required"))
                                AddNote(Dictionary.NoteSipConfigRestart);
                        }
                        else
                        {
                            AddNote(string.Format(Dictionary.NoteSipConfigFailed, await ReadErrorAsync(resp)));
                        }
                    }
                    else if (sub2.Length > 0)
                    {
                        AddNote(Dictionary.NoteSipConfigUsage);
                    }
                    else
                    {
                        using var resp = await _http.GetAsync("/v1/sip/config").WaitAsync(TimeSpan.FromSeconds(5));
                        if (!resp.IsSuccessStatusCode) { AddNote(string.Format(Dictionary.NoteSipUnavailable, (int)resp.StatusCode)); return; }
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                        var sip = doc.RootElement.GetProperty("sip");
                        var allowed = sip.TryGetProperty("allowed_callers", out var ac) && ac.ValueKind == JsonValueKind.Array
                            ? string.Join(", ", ac.EnumerateArray().Select(x => x.GetString()))
                            : "";
                        var lines = new List<string>
                        {
                            $"{("enabled").PadRight(18)}{(GetBool(sip, "enabled") ? Dictionary.On : Dictionary.Off)}",
                            $"{("listen_port").PadRight(18)}{GetInt(sip, "listen_port")}",
                            $"{("registrar").PadRight(18)}{ValueOr(GetStr(sip, "registrar"), "(empty)")}",
                            $"{("username").PadRight(18)}{ValueOr(GetStr(sip, "username"), "(empty)")}",
                            $"{("password").PadRight(18)}{(GetBool(sip, "password_set") ? "set" : "not set")}",
                            $"{("answer_mode").PadRight(18)}{GetStr(sip, "answer_mode") ?? ""}",
                            $"{("pin").PadRight(18)}{(GetBool(sip, "pin_set") ? "set" : "not set")}",
                            $"{("max_pin_attempts").PadRight(18)}{GetInt(sip, "max_pin_attempts")}",
                            $"{("lockout_hours").PadRight(18)}{GetInt(sip, "lockout_hours")}",
                            $"{("register_expiry").PadRight(18)}{GetInt(sip, "register_expiry")}",
                            $"{("pin_timeout_seconds").PadRight(18)}{GetInt(sip, "pin_timeout_seconds")}",
                            $"{("indicator_delay_seconds").PadRight(18)}{GetInt(sip, "indicator_delay_seconds")}",
                            $"{("allowed_callers").PadRight(18)}{ValueOr(allowed, "(none)")}",
                            $"{("agent").PadRight(18)}{GetStr(sip, "agent") ?? ""}",
                            $"{("lang").PadRight(18)}{ValueOr(GetStr(sip, "lang"), "(system)")}",
                            $"{("stt_exe_path").PadRight(18)}{ValueOr(GetStr(sip, "stt_exe_path"), "(default)")}",
                            $"{("stt_model").PadRight(18)}{ValueOr(GetStr(sip, "stt_model"), "(small)")}",
                            $"{("stt_quant").PadRight(18)}{ValueOr(GetStr(sip, "stt_quant"), "(fp16)")}",
                            $"{("stt_device").PadRight(18)}{ValueOr(GetStr(sip, "stt_device"), "(auto)")}",
                            $"{("rtp_port_range").PadRight(18)}{ValueOr(GetStr(sip, "rtp_port_range"), "(default)")}",
                        };
                        await ShowPageUiAsync(Dictionary.PageSipConfig, lines);
                    }
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

        private async Task TelegramAsync(string args)
        {
            // No arguments → the interactive panel (menu Tools → Telegram): live
            // status plus login-code / allow / disallow / config / reload / toggle.
            if (string.IsNullOrWhiteSpace(args))
            {
                await ShowTelegramDialogAsync();
                return;
            }
            var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var sub = parts.Length == 0 ? "status" : parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (sub)
            {
                case "login-code":
                {
                    if (string.IsNullOrEmpty(arg)) { AddNote(Dictionary.NoteTelegramLoginPending); return; }
                    var error = TelegramBridge.SubmitLoginInput(arg);
                    if (error == null)
                        AddNote(Dictionary.NoteTelegramLoginCodeSubmitted);
                    else
                        AddNote(string.Format(Dictionary.NoteTelegramLoginCodeFailed, error));
                    break;
                }
                case "allow":
                case "disallow":
                {
                    if (string.IsNullOrEmpty(arg)) { AddNote(Dictionary.NoteTelegramUsage); return; }
                    var (error, message) = sub == "allow"
                        ? TelegramBridge.AddAllowedUser(arg)
                        : TelegramBridge.RemoveAllowedUser(arg);
                    if (error == null)
                        AddNote(message);
                    else
                        AddNote(string.Format(Dictionary.NoteTelegramAllowFailed, error));
                    break;
                }
                case "config":
                {
                    var rest = arg.Length == 0 ? "" : arg;
                    var parts2 = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var sub2 = parts2.Length == 0 ? "" : parts2[0].ToLowerInvariant();

                    if (sub2 == "set")
                    {
                        var kv = parts2.Length > 1 ? parts2[1].Trim() : "";
                        var kvParts = kv.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (kvParts.Length == 0) { AddNote(Dictionary.NoteTelegramConfigUsage); return; }
                        var (error, restart, message) = await TelegramBridge.SetConfigAsync(kvParts[0], kvParts.Length > 1 ? kvParts[1] : "");
                        if (error == null)
                        {
                            AddNote(message);
                            if (restart) AddNote(Dictionary.NoteTelegramConfigRestart);
                        }
                        else
                        {
                            AddNote(string.Format(Dictionary.NoteTelegramConfigFailed, error));
                        }
                    }
                    else if (sub2 == "reload")
                    {
                        var (error, restart, message) = await TelegramBridge.ReloadConfigAsync();
                        if (error == null)
                        {
                            AddNote(message);
                            if (restart) AddNote(Dictionary.NoteTelegramConfigRestart);
                        }
                        else
                        {
                            AddNote(string.Format(Dictionary.NoteTelegramConfigFailed, error));
                        }
                    }
                    else if (sub2.Length > 0)
                    {
                        AddNote(Dictionary.NoteTelegramConfigUsage);
                    }
                    else
                    {
                        var c = TelegramBridge.ConfigSnapshot;
                        var allowed = string.Join(", ", c.AllowedUsers);
                        var lines = new List<string>
                        {
                            $"{("enabled").PadRight(18)}{(c.Enabled ? Dictionary.On : Dictionary.Off)}",
                            $"{("phone_number").PadRight(18)}{ValueOr(c.PhoneNumber, "(empty)")}",
                            $"{("session_path").PadRight(18)}{ValueOr(c.SessionPath, "(default)")}",
                            $"{("allowed_users").PadRight(18)}{ValueOr(allowed, "(all private chats)")}",
                            $"{("agent").PadRight(18)}{c.Agent}",
                        };
                        await ShowPageUiAsync("Telegram config", lines);
                    }
                    break;
                }
                default:   // status
                {
                    var s = TelegramBridge.Status;
                    var user = s.User == null ? Dictionary.None : $"{s.User.Name} (@{s.User.Username}, id {s.User.Id})";
                    var lines = new List<string>
                    {
                        $"{("enabled").PadRight(18)}{(s.Enabled ? Dictionary.On : Dictionary.Off)}",
                        $"{("phase").PadRight(18)}{TelegramPhaseLabel(TelegramBridge.Phase)}",
                        $"{("user").PadRight(18)}{user}",
                        $"{("allowed_users").PadRight(18)}{(s.AllowedUsers is { Count: > 0 } au ? string.Join(", ", au) : "(all)")}",
                        $"{("agent").PadRight(18)}{s.Agent}",
                    };
                    if (s.Error is { Length: > 0 } err)
                        lines.Add($"{"error".PadRight(18)}{err}");
                    await ShowPageUiAsync("Telegram status", lines);
                    break;
                }
            }
            await RefreshTelegramStatusAsync();
        }

        private Task ShowTelegramDialogAsync()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(() =>
            {
                try { ShowTelegramDialog(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        // Interactive Telegram panel: live status plus the actions that used to require
        // memorising /telegram subcommands (login code, allow/disallow, config, ...).
        private void ShowTelegramDialog()
        {
            Log.LogStep("TUI /telegram panel opened", monitor: true);
            var dlg = new Dialog
            {
                Title = Dictionary.DlgTelegramTitle,
                Width = Dim.Percent(80),
                Height = Dim.Percent(70),   // 62% was too short once the action buttons
                                            // spanned two rows (2+2): rows 10/12/14 overflowed
                                            // the drawn area and the last buttons were invisible.
                SchemeName = "Dark",
            };

            var status = new Label
            {
                X = 1, Y = 0, Width = Dim.Fill() - 2, Height = 4,
            };
            void RefreshStatus()
            {
                var s = TelegramBridge.Status;
                var user = s.User == null ? Dictionary.None : $"{s.User.Name} (@{s.User.Username}, id {s.User.Id})";
                var allowed = s.AllowedUsers is { Count: > 0 } au ? string.Join(", ", au) : "(all)";
                var err = s.Error is { Length: > 0 } e ? " · " + e : "";
                status.Text = string.Join("\n",
                    $"enabled: {(s.Enabled ? Dictionary.On : Dictionary.Off)} · phase: {TelegramPhaseLabel(TelegramBridge.Phase)}",
                    $"user: {user}",
                    $"allowed: {allowed}",
                    $"agent: {s.Agent}{err}");
            }
            RefreshStatus();

            var hint = new Label
            {
                Text = Dictionary.DlgTelegramHint,
                X = 1, Y = 4, Width = Dim.Fill() - 2,
                SchemeName = "Hint",
            };

            // First login / 2FA: verification code row.
            var codeLabel = new Label { Text = Dictionary.DlgTelegramLoginLabel, X = 1, Y = 6, Width = 30 };
            var codeField = new TextField { X = 32, Y = 6, Width = Dim.Fill() - 34 };
            var sendBtn = new Button { Text = Dictionary.DlgTelegramSend, X = 1, Y = 8 };
            void SubmitLoginCode(string code)
            {
                if (string.IsNullOrWhiteSpace(code)) return;
                Log.LogStep("TUI /telegram: invio codice di verifica", monitor: true);
                var error = TelegramBridge.SubmitLoginInput(code.Trim());
                if (error == null) AddNote(Dictionary.NoteTelegramLoginCodeSubmitted);
                else AddNote(string.Format(Dictionary.NoteTelegramLoginCodeFailed, error));
                codeField.Text = "";
                RefreshStatus();
                _ = RefreshTelegramStatusAsync();
            }
            sendBtn.Accepted += (_, _) => SubmitLoginCode(codeField.Text ?? "");
            codeField.KeyDown += (_, key) =>
            {
                if (key == Key.Enter) { key.Handled = true; SubmitLoginCode(codeField.Text ?? ""); }
            };

            // Buttons lay out SEQUENTIALLY (Pos.Right) so the auto-width text can never
            // overlap the next button — fixed X coordinates broke once the localized
            // labels grew longer than the gaps (seen live: "Consenti utente…" drawn over
            // "Blocca utente…"). The four action buttons span TWO rows (2+2) because a
            // single row (~89 cols with the Italian labels) overflows the 80%-width panel.
            var allowBtn = new Button { Text = Dictionary.DlgTelegramAllow, X = 1, Y = 10 };
            var disallowBtn = new Button { Text = Dictionary.DlgTelegramDisallow, X = Pos.Right(allowBtn) + 1, Y = 10 };
            var configBtn = new Button { Text = Dictionary.DlgTelegramConfig, X = 1, Y = 12 };
            var reloadBtn = new Button { Text = Dictionary.DlgTelegramReload, X = Pos.Right(configBtn) + 1, Y = 12 };
            var toggleBtn = new Button { Text = Dictionary.DlgTelegramToggleEnable, X = 1, Y = 14 };

            allowBtn.Accepted += async (_, _) =>
            {
                Log.LogStep("TUI /telegram: Consenti utente", monitor: true);
                var who = await PromptOnUiThreadAsync(Dictionary.DlgTelegramAllow, "");
                if (who != null)
                {
                    var (error, message) = TelegramBridge.AddAllowedUser(who);
                    if (error == null) AddNote(message);
                    else AddNote(string.Format(Dictionary.NoteTelegramAllowFailed, error));
                    RefreshStatus();
                    _ = RefreshTelegramStatusAsync();
                }
                codeField.SetFocus();   // the nested prompt refocused the main input — bring focus back into the panel
            };
            disallowBtn.Accepted += async (_, _) =>
            {
                Log.LogStep("TUI /telegram: Blocca utente", monitor: true);
                var who = await PromptOnUiThreadAsync(Dictionary.DlgTelegramDisallow, "");
                if (who != null)
                {
                    var (error, message) = TelegramBridge.RemoveAllowedUser(who);
                    if (error == null) AddNote(message);
                    else AddNote(string.Format(Dictionary.NoteTelegramAllowFailed, error));
                    RefreshStatus();
                    _ = RefreshTelegramStatusAsync();
                }
                codeField.SetFocus();   // the nested prompt refocused the main input — bring focus back into the panel
            };
            configBtn.Accepted += (_, _) => { Log.LogStep("TUI /telegram: Mostra configurazione"); RunCommandByName("telegram", "config"); };
            reloadBtn.Accepted += (_, _) => { Log.LogStep("TUI /telegram: Ricarica configurazione", monitor: true); RunCommandByName("telegram", "config reload"); };
            toggleBtn.Accepted += async (_, _) =>
            {
                Log.LogStep("TUI /telegram: toggle abilitazione", monitor: true);
                var (error, restart, message) = await TelegramBridge.SetConfigAsync("Enabled", TelegramBridge.IsEnabled ? "false" : "true");
                if (error == null)
                {
                    AddNote(message);
                    if (restart) AddNote(Dictionary.NoteTelegramConfigRestart);
                }
                else
                {
                    AddNote(string.Format(Dictionary.NoteTelegramConfigFailed, error));
                }
                RefreshStatus();
                _ = RefreshTelegramStatusAsync();
            };

            dlg.Add(status, hint, codeLabel, codeField, sendBtn, allowBtn, disallowBtn, configBtn, reloadBtn, toggleBtn);
            var close = new Button { Text = Dictionary.Close };
            close.Accepted += (_, _) => { Log.LogStep("TUI /telegram panel closed"); _app.RequestStop(dlg); };
            dlg.AddButton(close);
            dlg.Initialized += (_, _) => codeField.SetFocus();
            _app.Run(dlg);
            Log.LogStep("TUI /telegram panel closed", monitor: true);
            dlg.Dispose();
            _inputField?.SetFocus();
        }

        // Modal single-field prompt (e.g. "allow user"); Enter/OK returns the trimmed
        // value, Esc/Cancel returns null.
        private string? RunPromptDialog(string title, string label)
        {
            var dlg = new Dialog
            {
                Title = title,
                Width = Dim.Percent(60),
                Height = 9,
                SchemeName = "Dark",
            };
            if (label.Length > 0)
                dlg.Add(new Label { Text = label, X = 1, Y = 0, Width = Dim.Fill() - 2 });
            var field = new TextField { X = 1, Y = 1, Width = Dim.Fill() - 2 };
            var hint = new Label
            {
                Text = Dictionary.DlgPromptHint,
                X = 1, Y = 3, Width = Dim.Fill() - 2,
                SchemeName = "Hint",
            };
            string? result = null;
            field.KeyDown += (_, key) =>
            {
                if (key == Key.Enter)
                {
                    key.Handled = true;
                    result = (field.Text ?? "").Trim();
                    _app.RequestStop(dlg);
                }
            };
            var ok = new Button { Text = Dictionary.Ok, IsDefault = true };
            ok.Accepted += (_, _) => { result = (field.Text ?? "").Trim(); _app.RequestStop(dlg); };
            var cancel = new Button { Text = Dictionary.Cancel };
            cancel.Accepted += (_, _) => _app.RequestStop(dlg);
            dlg.Add(field, hint);
            dlg.AddButton(ok);
            dlg.AddButton(cancel);
            dlg.Initialized += (_, _) => field.SetFocus();
            _app.Run(dlg);
            dlg.Dispose();
            return string.IsNullOrEmpty(result) ? null : result;
        }

        private Task<string?> PromptOnUiThreadAsync(string title, string label)
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(() =>
            {
                try { tcs.TrySetResult(RunPromptDialog(title, label)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
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
                // The switch is session-scoped by design (/model never touches the default
                // in the settings): refresh the session state so the status line and the
                // pickers reflect the provider actually in use right away.
                await RefreshSessionStateAsync();
            }
            else
            {
                AddNote(string.Format(Dictionary.NoteSwitchRefused, (int)resp2.StatusCode, await ReadErrorAsync(resp2)));
            }
        }

        // Asks the server to adopt the marked default provider for the current process: new
        // chat sessions start with it. Only the process-wide default changes — the session
        // currently open is NOT switched (that is /model's job).
        private async Task SetDefaultProviderAsync(string name)
        {
            try
            {
                var body = JsonSerializer.Serialize(new { set_default_provider = name }, JsonOpts);
                using var resp = await _http.PostAsync("/v1/control", new StringContent(body, Encoding.UTF8, "application/json"));
                if (!resp.IsSuccessStatusCode)
                    AddNote(string.Format(Dictionary.NoteSetDefaultFailedHttp, (int)resp.StatusCode, await ReadErrorAsync(resp)));
            }
            catch (Exception ex)
            {
                AddNote(string.Format(Dictionary.NoteSetDefaultFailed, ex.Message));
            }
        }

        private Task SwitchAgentAsync(string args)
        {
            if (string.IsNullOrWhiteSpace(args.Trim()))
                return ShowToolsDialogAsync();   // interactive checklist
            var name = args.Trim().ToLowerInvariant();
            if (!AgentTools.AllIds.Any(id => string.Equals(id, name, StringComparison.OrdinalIgnoreCase)))
            {
                AddNote(string.Format(Dictionary.NoteUnknownAgentSet, name,
                    string.Join(", ", AgentTools.AllIds)));
                return Task.CompletedTask;
            }
            ApplyPreset(name);
            return Task.CompletedTask;
        }

        // Applies an agent-set preset: the preset id becomes the chat `model` and any
        // custom tool combination is cleared (the preset wins).
        private void ApplyPreset(string id)
        {
            _agentSet = id;
            _customTools = null;
            AddNote(string.Format(Dictionary.NoteAgentSet, id));
            UpdateStatusUi();
        }

        private Task ShowToolsDialogAsync()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(() =>
            {
                try { ShowToolsDialog(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        // The /agent tool picker: an individual-tool checklist. Space toggles each row
        // independently; the marked tools become the custom combination (sent as `tools`).
        // Core tools are LOCKED: shown as a read-only line above the list (reflecting
        // tools.json), never toggleable — the custom combination always includes them.
        // The state is saved after the dialog closes by ANY means (Close button, Esc, ...).
        private void ShowToolsDialog()
        {
            Log.LogStep("TUI /agent tools dialog opened", monitor: true);
            var catalog = AgentTools.Catalog();
            var current = EffectiveTools();
            var dlg = new Dialog
            {
                Title = Dictionary.DlgToolsTitle,
                Width = Dim.Percent(80),
                Height = Dim.Percent(70),
                SchemeName = "Dark",
            };

            // The picker must never contradict the effective state: a core tool the config
            // disabled is not listed either (see docs-dev/ARCHITECTURE.md, "Agent sets & tool policy").
            var coreEnabled = AgentTools.CoreTools.Where(AgentTools.IsEnabled).ToArray();
            var toggleable = catalog.Where(c => !AgentTools.CoreTools.Contains(c.Name)).ToList();
            var coreLabel = new Label
            {
                Text = coreEnabled.Length > 0
                    ? string.Format(Dictionary.DlgToolsCore, string.Join(", ", coreEnabled))
                    : Dictionary.DlgToolsCoreNone,
                X = 1, Y = 1, Width = Dim.Fill() - 2,
            };

            var toolNames = toggleable.Select(c => c.Name).ToList();
            var source = new ListWrapper<string>(new ObservableCollection<string>(
                toggleable.Select(c => $"{c.Name} — {c.Description}")));
            var toolList = new ListView
            {
                X = 1, Y = 2, Width = Dim.Fill() - 2, Height = Dim.Fill() - 4,
                ShowMarks = true,
                MarkMultiple = true,   // independent checkboxes — SPACE toggles each row independently
                Source = source,
            };
            var hint = new Label
            {
                Text = Dictionary.DlgToolsHint,
                X = 1, Y = Pos.Bottom(toolList), Width = Dim.Fill() - 2,
            };
            dlg.Add(coreLabel, toolList, hint);

            // Reflect the currently active tools in the checklist (non-core rows only).
            for (int i = 0; i < toolNames.Count; i++)
                source.SetMark(i, current.Contains(toolNames[i], StringComparer.OrdinalIgnoreCase));

            // The Close button only closes the dialog — the checkbox state is saved below
            // right after Run returns, whatever path closed the dialog (button, Esc, ...).
            var close = new Button { Text = Dictionary.Close };
            close.Accepted += (_, _) => _app.RequestStop(dlg);
            dlg.AddButton(close);
            dlg.Initialized += (_, _) => toolList.SetFocus();
            _app.Run(dlg);

            // Save the checkbox state on ANY close path. The enabled core tools are always
            // included (locked, non-toggleable) — a custom combination can never drop them.
            var marked = new List<string>();
            for (int i = 0; i < toolNames.Count; i++)
                if (source.IsMarked(i)) marked.Add(toolNames[i]);
            foreach (var core in coreEnabled)
                if (!marked.Contains(core)) marked.Add(core);
            if (marked.Count > 0)
            {
                _agentSet = "default-agent";   // the `tools` field overrides it server-side
                _customTools = marked;
                AddNote(string.Format(Dictionary.NoteToolsApplied, string.Join(", ", marked.Select(ToolShortName))));
                Log.LogStep($"TUI /agent tools applied: {string.Join(", ", marked)}", monitor: true);
            }
            else
            {
                _customTools = null;
            }
            Log.LogStep("TUI /agent tools dialog closed");
            UpdateStatusUi();

            dlg.Dispose();
            _inputField?.SetFocus();
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
            SetBusy("listening", true);
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
            finally
            {
                SetBusy("listening", false);
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

        private async Task TtsEngineAsync(string args)
        {
            var engine = args.Trim().ToLowerInvariant();
            var known = string.Join(", ", TtsEngineSupport.KnownEngines);
            if (string.IsNullOrWhiteSpace(engine))
            {
                var current = Environment.GetEnvironmentVariable("PODCAST_TTS_ENGINE") ?? TtsEngineSupport.DefaultEngine;
                AddNote($"TTS engines on this machine: {known}\n" +
                        $"Current engine: {current}. Set with /ttsengine <{known}>.");
                return;
            }
            if (!TtsEngineSupport.IsKnown(engine))
            {
                AddNote($"Unknown TTS engine '{engine}' — known engines: {known}.");
                return;
            }
            if (!TtsEngineSupport.IsAvailable(engine, out var reason))
            {
                AddNote($"TTS engine '{engine}' is not available on this machine: {reason} The engine stays on {TtsEngineSupport.DefaultEngine}.");
                return;
            }
            Environment.SetEnvironmentVariable("PODCAST_TTS_ENGINE", engine);
            PersistTtsEngine(engine);
            AddNote($"TTS engine set to {engine} — persisted in appsettings Tts:Engine.");
        }

        /// <summary>Persists the preferred TTS engine into appsettings.json (Tts:Engine), the
        /// user-editable copy under PersistentData\ (AppConfig).</summary>
        private static void PersistTtsEngine(string engine)
        {
            try
            {
                var path = AppConfig.AppSettingsFile;
                if (!File.Exists(path)) return;
                var doc = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (doc == null) return;
                var tts = doc["Tts"] as JsonObject ?? new JsonObject();
                tts["Engine"] = engine;
                doc["Tts"] = tts;
                File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private async Task UpdateAsync()
        {
            AddNote(Dictionary.UpdateChecking);
            var result = await AutoUpdate.CheckAndApplyManualAsync();
            // When an update was found and applied, ApplyAsync spawns the updater and
            // exits the process — this note only surfaces when nothing was installed.
            AddNote(result.Status switch
            {
                AutoUpdate.ManualUpdateStatus.NotPublished => Dictionary.UpdateUseExecutable,
                AutoUpdate.ManualUpdateStatus.DebugBuild => Dictionary.UpdateDebugBuild,
                AutoUpdate.ManualUpdateStatus.NoArchive => Dictionary.UpdateNoArchive,
                AutoUpdate.ManualUpdateStatus.Unreachable => Dictionary.UpdateGitHubUnreachable,
                AutoUpdate.ManualUpdateStatus.UpToDate => string.Format(Dictionary.UpdateUpToDate, result.CurrentVersion),
                AutoUpdate.ManualUpdateStatus.NewerThanLatest => string.Format(Dictionary.UpdateNewerThanLatest, result.CurrentVersion, result.LatestVersion),
                AutoUpdate.ManualUpdateStatus.AgentsBusy => Dictionary.UpdateAgentsBusy,
                AutoUpdate.ManualUpdateStatus.AnotherInstance => Dictionary.UpdateAnotherInstance,
                _ => string.Format(Dictionary.UpdateFailed, result.Detail ?? "unknown error"),
            });
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
                $"{Dictionary.StatusAgentSet.PadRight(18)}{AgentSetDisplay()}",
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
                    // File names only — the internal file-… ids mean nothing to the user.
                    lines = _files.Select(f => $"{f.FileName} · {f.Status}{(f.Attached ? "  " + Dictionary.NoteAttachedSuffix : "")}").ToList();
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
            else if (sub == "rm")
            {
                var id = parts.Length > 1 ? parts[1].Trim() : "";
                if (id.Length == 0)
                {
                    // No cryptic ids in the UI: pick the file by name.
                    await RefreshFilesAsync();
                    List<FileRef> files;
                    lock (_stateLock) files = _files.ToList();
                    if (files.Count == 0) { AddNote(Dictionary.NoteNoFiles); return; }
                    var idx = await PickIndexOnUiThreadAsync(Dictionary.DlgPickFileToDelete,
                        files.Select(f => f.FileName).ToList());
                    if (idx is not { } i || i < 0 || i >= files.Count) return;
                    id = files[i].Id;
                }
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
                var choices = files.Select(f => $"{f.FileName}{(f.Attached ? "  " + Dictionary.AttachMarker : "")}").ToList();
                var idx = await PickIndexOnUiThreadAsync(Dictionary.DlgToggleAttach, choices);
                if (idx is { } i && i >= 0 && i < files.Count) ToggleAttach(files[i]);
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
                Log.LogStep($"TUI Docs: opening {url}", monitor: true);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                AddNote(string.Format(Dictionary.NoteOpenedUrl, url));
            }
            catch (Exception ex)
            {
                Log.LogStep($"TUI Docs FAILED: {ex.Message}");
                AddNote(string.Format(Dictionary.NoteOpenBrowserFailed, ex.Message));
            }
            return Task.CompletedTask;
        }

        private void OpenIssuesAsync()
        {
            const string url = "https://github.com/Graphene-Lab/AgentBridge/issues";
            try
            {
                Log.LogStep($"TUI OpenIssues: opening {url}", monitor: true);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                AddNote(string.Format(Dictionary.NoteOpenedUrl, url));
            }
            catch (Exception ex)
            {
                Log.LogStep($"TUI OpenIssues FAILED: {ex.Message}");
                AddNote(string.Format(Dictionary.NoteOpenBrowserFailed, ex.Message));
            }
        }

        // ── OfficeManager (agents' office) ──
        // The 16-bit office is served by THIS server at /OfficeManager (static files + the
        // /ws/office duplex hub — see OfficeBridge.cs): no external launcher or download needed.
        // /officemanager opens it in the OS default browser; on a server without a desktop
        // environment the browser cannot be started, so the command reports an error instead of
        // failing silently.
        private Task LaunchOfficeManagerAsync()
        {
            if (!HasDesktopSession())
            {
                AddNote(Dictionary.NoteOfficeManagerNoDesktop);
                return Task.CompletedTask;
            }
            var url = _serverUrl.TrimEnd('/') + "/OfficeManager";
            try
            {
                Log.LogStep($"TUI OfficeManager: opening {url}", monitor: true);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                AddNote(string.Format(Dictionary.NoteOpenedUrl, url));
            }
            catch (Exception ex)
            {
                Log.LogStep($"TUI OfficeManager FAILED: {ex.Message}");
                AddNote(string.Format(Dictionary.NoteOpenBrowserFailed, ex.Message));
            }
            return Task.CompletedTask;
        }

        // Desktop-session detection (same rule as AgentHarness.IsInteractiveDesktopSession): a
        // browser can only be launched from a machine with a graphical session — Windows/macOS
        // interactive console, or Linux with DISPLAY/WAYLAND_DISPLAY set (headless servers have
        // neither, so the office cannot be shown there).
        private static bool HasDesktopSession()
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                return Environment.UserInteractive;
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        }

        // ── Web client (Giraffe AI) ──
        // The web GUI is a tiny static app (single index.html + its own launcher) installed
        // next to the executable by WebClientUpdater (startup keeps it at the latest GitHub
        // release). /web joins that startup task (bounded internally: 15s version check, 5 min
        // download — no artificial timeout that would report a failure on a slow first
        // download), retries the install when it failed (e.g. the machine was offline at boot),
        // then the platform launcher (start.bat / start.sh) serves it on http://localhost:8000
        // and opens the browser. Connectivity failures surface as friendly notes instead of
        // crashing the UI.
        private async Task LaunchWebClientAsync()
        {
            var dir = WebClientUpdater.ClientDir;
            try
            {
                await WebClientUpdater.Startup;
                if (!WebClientUpdater.IsInstalled)
                {
                    AddNote(string.Format(Dictionary.NoteWebClientOutdated, dir, WebClientUpdater.Repo));
                    await WebClientUpdater.EnsureAsync();
                }
                var ver = WebClientUpdater.InstalledVersion is { } v ? $" (v{v})" : "";
                var upd = WebClientUpdater.LastStatus is { } s ? $" — {s}" : "";
                AddNote(string.Format(Dictionary.NoteLaunchingWebClient, dir) + ver + upd);
                LaunchWebClientProcess(dir);
            }
            catch (Exception ex)
            {
                AddNote(string.Format(Dictionary.NoteWebClientFailed, ex.Message));
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
                // ⚠️ CONSOLE LEAK (see docs-dev/TUI-DEVELOPMENT.md §8): the launcher must never
                // share the TUI console — a child writing into the caller's console floods
                // the screen with raw ANSI (^[[8;30;120t spam). UseShellExecute=true gives
                // the .bat its OWN console window and detaches its std handles completely.
                // The JSON travels base64url-encoded (no padding): embedded quotes or '='
                // would be mangled by the cmd.exe command line (start.bat decodes it
                // before building the browser URL).
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(provider))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                Process.Start(new ProcessStartInfo
                {
                    FileName = bat,
                    Arguments = $"--provider {b64}",
                    UseShellExecute = true,
                    WorkingDirectory = dir,
                });
            }
            else
            {
                var sh = Path.Combine(dir, "start.sh");
                if (!File.Exists(sh)) throw new InvalidOperationException($"missing {sh}");
                // Same console-leak rule: the launcher's output must never reach the TUI,
                // so its std handles are drained to a temp log instead of the terminal.
                // The drain tasks outlive this method (the launcher runs as a server).
                var logPath = Path.Combine(Path.GetTempPath(), $"giraffe_{DateTime.Now:yyyyMMddHHmmss}.log");
                var log = File.Create(logPath);
                var psi = new ProcessStartInfo("bash", new[] { sh, "--provider", provider })
                {
                    UseShellExecute = false,
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    _ = Task.Run(() =>
                    {
                        // StreamReader.CopyTo needs a TextWriter — drain the raw stream.
                        try { proc.StandardOutput.BaseStream.CopyTo(log); } catch { }
                        try { proc.StandardError.BaseStream.CopyTo(log); } catch { }
                        try { log.Dispose(); } catch { }
                        try { proc.Dispose(); } catch { }
                    });
                }
                else
                {
                    try { log.Dispose(); } catch { }
                }
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
            foreach (var c in Commands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var alias = c.Aliases is { Length: > 0 } ? $"  (also {string.Join(", ", c.Aliases)})" : "";
                lines.Add($"  /{c.Name}{alias} {c.Args}".TrimEnd().PadRight(30) + c.Help);
            }
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
                ClampSelection(list, matches.Count);
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
                SelectedItem = 0,
            };
            var hint = new Label
            {
                Text = Dictionary.DlgPickerHint,
                X = 0, Y = Pos.Bottom(list), Width = Dim.Fill(),
            };
            string? result = null;
            // In v2.4.17 the ListView does NOT raise Accepted from the keyboard: its default
            // key bindings are only movement keys, and the inherited Enter→Accept command is
            // not handled by the list, so it bubbles to the Dialog which closes with no result.
            // Handle Enter here (the same pattern the provider picker uses on its filter) so
            // the hint "Enter selects" matches the behaviour; Accepted stays for mouse
            // double-click.
            list.KeyDown += (_, key) =>
            {
                if (key == Key.Enter)
                {
                    key.Handled = true;
                    result = items[Math.Max(0, list.SelectedItem ?? 0)];
                    _app.RequestStop(dlg);
                }
            };
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

        // Like RunPickerDialog but returns the selected INDEX (the caller owns the items),
        // so the rendered rows never need parsing (e.g. to strip an id from a file name).
        private int? RunIndexPickerDialog(string title, IReadOnlyList<string> items)
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
                // A picker must have a usable initial selection: with SelectedItem = null an
                // immediate Enter closes the dialog with no result. Start on the first row so
                // Enter selects out of the box.
                SelectedItem = 0,
            };
            var hint = new Label
            {
                Text = Dictionary.DlgPickerHint,
                X = 0, Y = Pos.Bottom(list), Width = Dim.Fill(),
            };
            // In v2.4.17 the ListView does NOT raise Accepted from the keyboard: its default
            // key bindings are only movement keys, and the inherited Enter→Accept command is
            // not handled by the list, so it bubbles to the Dialog which closes with no result.
            // Handle Enter here so the hint "Enter selects" matches the behaviour; Accepted
            // stays for mouse double-click. Do NOT call KeyBindings.Add(Key.Enter, …) — the
            // binding already exists and re-adding it throws "A binding for Enter exists".
            int? result = null;
            list.KeyDown += (_, key) =>
            {
                if (key == Key.Enter)
                {
                    key.Handled = true;
                    result = list.SelectedItem;
                    _app.RequestStop(dlg);
                }
            };
            list.Accepted += (_, e) =>
            {
                e.Handled = true;
                result = list.SelectedItem;
                _app.RequestStop(dlg);
            };
            dlg.Add(list, hint);
            dlg.Initialized += (_, _) => list.SetFocus();
            _app.Run(dlg);
            dlg.Dispose();
            _inputField?.SetFocus();
            return result;
        }

        private Task<int?> PickIndexOnUiThreadAsync(string title, IReadOnlyList<string> items)
        {
            var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(() =>
            {
                try { tcs.TrySetResult(RunIndexPickerDialog(title, items)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
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
                // The currently active provider is marked with a bullet so the picker
                // always shows what is in use (not just what could be selected). The
                // non-active rows get the same leading width ("● " = 2 cells) so every
                // provider name starts at the same column.
                list.Source = new ListWrapper<string>(new ObservableCollection<string>(
                    visible.Select(p => string.Equals(p.Id, _provider, StringComparison.OrdinalIgnoreCase)
                        ? $"● {p.Display}" : $"  {p.Display}")));
                ClampSelection(list, visible.Count);
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

        // Full-screen page (help/status): a modal dialog closed by Esc/Close.
        // The read-only Editor is focusable so ↑↓ / PgUp / PgDn scroll the content
        // (an unfocusable page had dead arrow keys — see docs-dev/TUI-DEVELOPMENT.md §8).
        private void ShowPage(string title, IReadOnlyList<string> lines)
        {
            Log.LogStep($"TUI page opened: {title} ({lines.Count} righe)", monitor: true);
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
                CanFocus = true,   // focusable → ↑↓ / PgUp / PgDn scroll the page
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
            dlg.Initialized += (_, _) => tv.SetFocus();
            _app.Run(dlg);
            Log.LogStep($"TUI page closed: {title}");
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
            Log.LogStep($"TUI command palette opened (initial: '{initial}')", monitor: true);
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
                ClampSelection(list, visible.Count);
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
                        // Move cursor to end of the completed text so the user can keep typing.
                        filter.InsertionPoint = filter.Text.Length;
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
            Log.LogStep("TUI @ files palette opened", monitor: true);
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
                        var choices = files.Select(f => $"{f.FileName}{(f.Attached ? "  " + Dictionary.AttachMarker : "")}").ToList();
                        var idx = RunIndexPickerDialog(Dictionary.DlgToggleAttach, choices);
                        if (idx is { } i && i >= 0 && i < files.Count)
                        {
                            Log.LogStep($"TUI @ files: toggle allegato '{files[i].FileName}'", monitor: true);
                            ToggleAttach(files[i]);
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
            Ui(() =>
            {
                Log.LogStep("TUI About dialog opened", monitor: true);
                // A real About window: the AGENT ASCII art (same gradient as the startup
                // banner), the tagline and the © copyright with the project name.
                var dlg = new Dialog
                {
                    Title = Dictionary.AboutTitle,
                    Width = Dim.Percent(55),
                    Height = Dim.Percent(45),
                    SchemeName = "Dark",
                };
                int y = 1;
                for (int i = 0; i < AsciiArtLines.Length; i++)
                    dlg.Add(new Label { Text = AsciiArtLines[i], X = 2, Y = y++, SchemeName = $"Ascii{i}" });
                dlg.Add(new Label { Text = Dictionary.AboutText, X = 2, Y = y + 1, Width = Dim.Fill() - 4 });
                dlg.Add(new Label
                {
                    Text = string.Format(Dictionary.AboutCopyright, DateTime.Now.Year),
                    X = 2, Y = y + 3, Width = Dim.Fill() - 4,
                    SchemeName = "Hint",
                });
                var ok = new Button { Text = Dictionary.Ok, IsDefault = true };
                ok.Accepted += (_, _) => _app.RequestStop(dlg);
                dlg.AddButton(ok);
                _app.Run(dlg);
                dlg.Dispose();
                _inputField?.SetFocus();
            });
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
            Log.LogStep("TUI ModelSetup dialog opened", monitor: true);
            var dlg = new Dialog
            {
                Title = Dictionary.SetupTitle,
                Width = Dim.Percent(80),
                // 78% (up from 70%): the tab hint line below the pages eats one row of the
                // Tabs viewport, and with 70% the LLM tab's action buttons (Add/Edit/Remove
                // at content row 12) fell out of the visible area. The extra height restores
                // the same internal room the dialog had before the hint was added.
                Height = Dim.Percent(78),
                SchemeName = "Dark",
            };

            var tabs = new Tabs
            {
                X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() - 1,
            };
            // The Tabs headers have no mouse handler and the tab pages are not reachable by
            // focus traversal in v2.4.17 (OnSubViewAdding forces CanFocus=false during Add and
            // there is no NextTabGroup implementation): pressing Tab cycles only inside the
            // current page. Give the user real keyboard navigation — Ctrl+PageDown/Up are the
            // framework's documented TabGroup keys — by switching Value when an unhandled key
            // bubbles up from the focused control (KeyDownNotHandled runs before the Dialog).
            tabs.KeyDownNotHandled += (_, key) =>
            {
                if (key == Key.PageDown.WithCtrl)
                {
                    key.Handled = true;
                    var cur = tabs.TabCollection.ToList();
                    var i = cur.IndexOf(tabs.Value);
                    tabs.Value = cur[Math.Min(cur.Count - 1, i + 1)];
                }
                else if (key == Key.PageUp.WithCtrl)
                {
                    key.Handled = true;
                    var cur = tabs.TabCollection.ToList();
                    var i = cur.IndexOf(tabs.Value);
                    tabs.Value = cur[Math.Max(0, i - 1)];
                }
            };

            // ── LLM / Providers tab ──
            var providerDropdown = new DropDownList { ReadOnly = true };
            var providersList = new ListView();
            var llmTab = new View { Title = Dictionary.SetupLlmTab, CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill() };
            {
                providerDropdown.X = 17; providerDropdown.Y = 0; providerDropdown.Width = 46;
                llmTab.Add(new Label { Text = Dictionary.SetupActiveProvider, X = 1, Y = 0, Width = 15 }, providerDropdown);

                // Active model indicator shown right below the provider dropdown.
                var activeModelLabel = new Label
                {
                    Text = "",
                    X = 1, Y = 1, Width = 62,
                    SchemeName = "Hint",
                };
                llmTab.Add(activeModelLabel);

                // API keys are set per-provider in the Add/Edit dialog below (providers.json);
                // local providers (localhost/127.0.0.1 endpoint) simply leave the field empty.
                int y = 3;
                llmTab.Add(new Label { Text = Dictionary.SetupConfiguredProviders, X = 1, Y = y, Width = Dim.Fill() });
                y++;
                providersList.X = 1; providersList.Y = y; providersList.Width = 62; providersList.Height = 6;
                llmTab.Add(providersList);
                y += 7;
                var addBtn = new Button { Text = Dictionary.SetupAdd, X = 1, Y = y };
                var editBtn = new Button { Text = Dictionary.SetupEdit, X = Pos.Right(addBtn) + 1, Y = y };
                var removeBtn = new Button { Text = Dictionary.SetupRemove, X = Pos.Right(editBtn) + 1, Y = y };
                llmTab.Add(addBtn, editBtn, removeBtn);
                // Explicit persistent default ("Imposta come predefinito"): the provider new
                // chats start with. /model (Session menu) never touches it — it switches only
                // the current chat on the fly. See SetupDefaultHint below.
                var defaultBtn = new Button { Text = Dictionary.SetupSetDefault, X = 1, Y = y + 1 };
                llmTab.Add(defaultBtn);
                llmTab.Add(new Label
                {
                    Text = Dictionary.SetupDefaultHint,
                    X = 1, Y = y + 2, Width = 60,
                    SchemeName = "Hint",
                });

                // The dropdown re-marks the active provider in the list below it,
                // and updates the active-model indicator shown above the list.
                providerDropdown.ValueChanged += (_, _) => RefreshProviderList();
                void RefreshProviderList()
                {
                    var defaultName = ProviderConfigs.Default.ProviderName;
                    providersList.Source = new ListWrapper<string>(new ObservableCollection<string>(
                        ProviderConfigs.All.Select(p =>
                        {
                            var marks = "";
                            if (string.Equals(p.ProviderName, providerDropdown.Text, StringComparison.OrdinalIgnoreCase))
                                marks += $"  {Dictionary.SetupActiveMarker}";
                            if (string.Equals(p.ProviderName, defaultName, StringComparison.OrdinalIgnoreCase))
                                marks += Dictionary.SetupDefaultMarker;
                            return p.ProviderName + marks;
                        })));
                    // Active model: the REAL session model when the dropdown shows the active
                    // provider (the provider's configured ModelName is often empty even though
                    // a concrete model is in use — the session knows the truth); otherwise the
                    // selected provider's configured model as a preview of what would activate.
                    var sel = ProviderConfigs.All.FirstOrDefault(p => p.ProviderName == providerDropdown.Text);
                    if (sel != null && string.Equals(sel.ProviderName, _provider, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(_modelName))
                        activeModelLabel.Text = string.Format(Dictionary.SetupActiveModel, _modelName);
                    else if (sel != null && !string.IsNullOrWhiteSpace(sel.ModelName))
                        activeModelLabel.Text = string.Format(Dictionary.SetupActiveModel, sel.ModelName);
                    else
                        activeModelLabel.Text = Dictionary.SetupActiveModelDefault;
                }
                // Full refresh after a provider was added/edited/removed (dropdown included).
                void RefreshProviders()
                {
                    var names = ProviderConfigs.All.Select(p => p.ProviderName).ToList();
                    providerDropdown.Source = new ListWrapper<string>(new ObservableCollection<string>(names));
                    // Show the CURRENT active provider on open; fall back to the default only
                    // when the active one is not configured anymore (e.g. it was removed).
                    if (string.IsNullOrWhiteSpace(providerDropdown.Text)
                        || !names.Contains(providerDropdown.Text, StringComparer.OrdinalIgnoreCase))
                        providerDropdown.Text = names.Contains(_provider, StringComparer.OrdinalIgnoreCase)
                            ? _provider
                            : ProviderConfigs.Default.ProviderName;
                    RefreshProviderList();
                }
                RefreshProviders();

                string? SelectedProviderName()
                {
                    var i = providersList.SelectedItem;
                    return i is >= 0 && i < ProviderConfigs.All.Count ? ProviderConfigs.All[i.Value].ProviderName : null;
                }

                addBtn.Accepted += (_, _) =>
                {
                    Log.LogStep("TUI ModelSetup: Add provider button", monitor: true);
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
                    Log.LogStep($"TUI ModelSetup: Edit provider '{name}'", monitor: true);
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
                    Log.LogStep($"TUI ModelSetup: Remove provider '{name}'", monitor: true);
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
                defaultBtn.Accepted += (_, _) =>
                {
                    var name = SelectedProviderName();
                    if (name == null) { AddNote(Dictionary.SetupSelectForDefault); return; }
                    Log.LogStep($"TUI ModelSetup: Set default provider '{name}'", monitor: true);
                    // Persist the explicit marker locally (providers.json) and ask the server
                    // to adopt it for the current process too — new chats start with it while
                    // /model keeps switching only the running session.
                    if (ProviderConfigs.SetDefault(name, persist: true))
                    {
                        AddNote(string.Format(Dictionary.SetupDefaultSet, name));
                        RefreshProviderList();
                        _ = SetDefaultProviderAsync(name);
                    }
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
            // Auto-start at boot (Task Scheduler task on Windows, systemd service on
            // Linux/macOS). Shows the CURRENT persisted state; SetAutoStart below applies
            // the change on Save.
            var autoStart = new CheckBox
            {
                Text = Dictionary.SetupAutoStart,
                Value = SystemExtra.Util.GetAutoStart() ? CheckState.Checked : CheckState.UnChecked,
                X = 1, Y = 1,
            };
            generalTab.Add(autoStart);
            var docsPath = AddField(generalTab, Dictionary.SetupDocumentsPath, AIOrchestrator.Setup.DocumentsPath, 3);

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
                SystemExtra.Util.SetAutoStart(autoStart.Value == CheckState.Checked);

                var chosen = (providerDropdown.Text ?? "").Trim();
                if (chosen.Length > 0 && !string.Equals(chosen, _provider, StringComparison.OrdinalIgnoreCase))
                    _ = SwitchModelAsync(chosen);   // same path as /model (HTTP /v1/control)

                AddNote(pathNote == null ? Dictionary.SetupSaved : string.Format(Dictionary.SetupSavedWithNote, pathNote));
                Log.LogStep($"TUI ModelSetup saved (provider: {providerDropdown.Text})", monitor: true);
                _app.RequestStop(dlg);
            };
            var close = new Button { Text = Dictionary.Close };
            close.Accepted += (_, _) => { Log.LogStep("TUI ModelSetup dialog closed (Cancel)"); _app.RequestStop(dlg); };
            dlg.AddButton(save);
            dlg.AddButton(close);

            tabs.Add(llmTab, emailTab, imapTab, generalTab);
            dlg.Add(tabs);
            // The Tabs headers have no mouse handler in v2.4.17 — the user switches pages
            // with the keyboard (Tab/F6 = TabStop/TabGroup navigation). The hint makes the
            // shortcut discoverable instead of leaving the user to guess.
            dlg.Add(new Label { Text = Dictionary.SetupTabsHint, X = 1, Y = Pos.Bottom(tabs), SchemeName = "Hint" });
            dlg.Initialized += (_, _) => providerDropdown.SetFocus();
            _app.Run(dlg);
            Log.LogStep("TUI ModelSetup dialog closed", monitor: true);
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
                    // Editing a provider must never silently drop its default marker.
                    IsDefault = existing?.IsDefault ?? false,
                    ModelName = (modelField.Text ?? "").Trim(),
                    BaseAddress = uri,
                    EndPoint = (endPointField.Text ?? "").Trim(),
                    ApiKey = (apiKeyField.Text ?? "").Trim(),
                    ContextWindow = ctx,
                    Timeout = TimeSpan.FromSeconds(secs),
                };
                Log.LogStep($"TUI Provider dialog OK: {providerName} ({proto})", monitor: true);
                _app.RequestStop(dlg);
            };
            var cancel = new Button { Text = Dictionary.Cancel };
            cancel.Accepted += (_, _) => { Log.LogStep("TUI Provider dialog cancelled"); _app.RequestStop(dlg); };
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
                    // Telegram is an in-process chat medium (no HTTP surface): availability
                    // mirrors the telegram.json Enabled flag, refreshed directly from the bridge.
                    _telegramAvailable = TelegramBridge.IsEnabled;
                }
                UpdateStatusUi();
            }
            catch { }
        }

        // While the SIP server / Telegram bridge are available their state changes on their
        // own (incoming calls, login progress), so the status bar polls them every few seconds.
        private void StartSipPolling()
        {
            _app.AddTimeout(TimeSpan.FromSeconds(3), () =>
            {
                if (_sipAvailable) _ = RefreshSipStatusAsync();
                if (_telegramAvailable) _ = RefreshTelegramStatusAsync();
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

        private async Task RefreshTelegramStatusAsync()
        {
            try
            {
                _telegramState = TelegramPhaseLabel(TelegramBridge.Phase);
                UpdateStatusUi();
            }
            catch { }
        }

        private static string TelegramPhaseLabel(TelegramBridge.TelegramPhase phase) => phase switch
        {
            TelegramBridge.TelegramPhase.Connecting => Dictionary.TelegramPhaseConnecting,
            TelegramBridge.TelegramPhase.LoginPendingCode => Dictionary.TelegramPhaseLoginPendingCode,
            TelegramBridge.TelegramPhase.LoginPendingPassword => Dictionary.TelegramPhaseLoginPendingPassword,
            TelegramBridge.TelegramPhase.Connected => Dictionary.TelegramPhaseConnected,
            TelegramBridge.TelegramPhase.Failed => Dictionary.TelegramPhaseFailed,
            _ => Dictionary.TelegramPhaseDisabled,
        };

        private static string PhaseLabel(string phase) => phase switch
        {
            "ringing" => Dictionary.SipPhaseRinging,
            "pin" => Dictionary.SipPhasePin,
            "conversation" => Dictionary.SipPhaseConversation,
            "ended" => Dictionary.SipPhaseEnded,
            _ => "",
        };

        // Sets a ListView selection clamped to the item range; null (no selection) when
        // the list is empty — assigning a clamped 0 to an empty ListView throws
        // "SelectedItem must be greater than 0 or less than the number of items".
        private static void ClampSelection(ListView list, int count)
            => list.SelectedItem = count > 0 ? Math.Clamp(list.SelectedItem ?? 0, 0, count - 1) : null;

        private static string? GetStr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static int GetInt(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
        private static bool GetBool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        private static string ValueOr(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  PUPPET MODE — automated-testing control surface (debug builds only)
//
//  Lets an external tester (agent/script) read the current TUI screen as
//  plain text and inject keyboard/mouse input, always marshalled onto the
//  Terminal.Gui main loop via IApplication.Invoke so no UI-thread race can
//  occur. Program.cs starts a localhost TCP listener that drives these
//  methods (see docs-dev/PUPPET-MODE-GUIDE.md).
// ═══════════════════════════════════════════════════════════════════════
public static class PuppetMode
{
    /// <summary>Reference to the running Terminal.Gui application, set by <see cref="ConsoleTui"/>.</summary>
    internal static IApplication? _app;

    /// <summary>
    /// True once the puppet TCP listener is active. Only ever set in DEBUG builds
    /// (Program.cs); the release binary has no puppet surface at all.
    /// </summary>
    public static bool Enabled { get; internal set; }

    // ── Pump design (thread-safety) ──
    // The puppet TCP handlers run on background threads. They must NEVER call
    // Application.Invoke/TimedEvents.Add directly: Terminal.Gui v2.4.17 holds the
    // TimedEvents lock across the whole callback run, and a modal dialog opened from
    // an injected key runs its nested RunLoop INSIDE that callback — so the lock is
    // held for the dialog's entire lifetime and every background Invoke would block
    // forever (seen live: all puppet handlers stuck in Monitor.Enter_Slowpath).
    // Instead the handlers only ENQUEUE work; a recurring timer registered on the UI
    // thread (StartPump) drains the queue and refreshes the capture snapshot.
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _injections = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(System.Drawing.Point Pos, TaskCompletionSource<string> Tcs)> _hitTests = new();
    private static string _snapshot = "(puppet mode: waiting for the first capture)";

    /// <summary>
    /// Registers the puppet pump timer. Must be called on the Terminal.Gui UI thread
    /// (done by <see cref="ConsoleTui"/>'s constructor); the timer runs even while a
    /// modal dialog is open because the nested RunLoop re-enters TimedEvents on the
    /// same thread.
    /// </summary>
    internal static void StartPump()
    {
        if (_app == null) return;
        try
        {
            _app.AddTimeout(TimeSpan.FromMilliseconds(250), Pump);
            Log.LogStep("Puppet pump started (250ms)", monitor: true);
        }
        catch (Exception ex) { Log.LogStep($"Puppet pump start failed: {ex.Message}"); }
    }

    // Runs on the UI thread every 250 ms: executes queued injections and keeps the
    // screen snapshot fresh. The timer is RE-ARMED FIRST on purpose: dispatching a
    // key can synchronously open a modal dialog (menu item → _app.Run(dlg)), which
    // blocks this callback inside the dialog's nested RunLoop for its whole
    // lifetime. Because the timer is already re-armed, it keeps firing inside that
    // nested loop (TimedEvents is re-entrant on the same thread), so queued
    // injections and the snapshot stay live while the dialog is open.
    private static bool Pump()
    {
        try { _app?.AddTimeout(TimeSpan.FromMilliseconds(250), Pump); }
        catch { /* keep pumping */ }

        try
        {
            int n = 0;
            while (_injections.TryDequeue(out var action))
            {
                n++;
                try { action(); }
                catch (Exception ex) { Log.LogStep($"Puppet injection failed: {ex.Message}"); }
            }
            if (n > 0) Log.LogStep($"Puppet pump: executed {n} queued injection(s)");

            // Hit-test requests are resolved on the UI thread too (same queue discipline:
            // no Application.Invoke from background threads — that deadlocks with dialogs).
            while (_hitTests.TryDequeue(out var h))
            {
                try { h.Tcs.SetResult(HitTestNow(h.Pos)); }
                catch (Exception ex) { h.Tcs.SetResult($"(hit-test error: {ex.Message})"); }
            }

            var top = _app?.TopRunnableView;
            if (top != null)
                _snapshot = _app!.Driver.ToString();
        }
        catch { /* keep the pump alive */ }
        return false;   // already re-armed above
    }

    private static string HitTestNow(System.Drawing.Point pos)
    {
        var top = _app?.TopRunnableView;
        if (top == null) return "(no Terminal.Gui window visible)";
        var views = top.GetViewsUnderLocation(pos, ViewportSettingsFlags.TransparentMouse);
        var sb = new StringBuilder();
        int i = 0;
        foreach (var v in views)
            sb.AppendLine($"{i++}: {v?.GetType().Name} \"{v?.Title}\" {v?.Frame}");
        return sb.Length == 0 ? "(no view under this point)" : sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Returns the latest screen capture (plain text). The snapshot is refreshed by
    /// the pump on the UI thread, so this never blocks — at most ~250 ms stale.
    /// </summary>
    public static string ANSI_Tui_Capture()
    {
        if (_app == null) return "(puppet mode not initialized)";
        return _snapshot;
    }

    /// <summary>Queues a key-down for the keyboard pipeline, with the matching key-up
    /// ~200 ms later (executed on the UI thread).</summary>
    /// <remarks>
    /// A real key press holds for ~100–300 ms between down and up; controls that
    /// activate on the press→release gesture (buttons, list Accept) can miss an
    /// immediate Down+Up pair fired in the same pump tick. The KeyUp is scheduled
    /// from the UI thread (safe — no TimedEvents.Add from background threads).
    /// </remarks>
    public static void InjectKey(Key key)
    {
        if (_app == null) return;
        _injections.Enqueue(() =>
        {
            _app!.Keyboard.RaiseKeyDownEvent(key);
            _app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
            {
                _app.Keyboard.RaiseKeyUpEvent(key);
                return false;   // one-shot
            });
        });
    }

    /// <summary>Queues text injection, one character at a time (executed on the UI thread).</summary>
    public static void InjectText(string text)
    {
        if (_app == null || string.IsNullOrEmpty(text)) return;
        foreach (var ch in text)
            _injections.Enqueue(() =>
            {
                var key = (Key)ch;
                _app!.Keyboard.RaiseKeyDownEvent(key);
                _app.Keyboard.RaiseKeyUpEvent(key);
            });
    }

    /// <summary>Queues a mouse event at terminal-relative coordinates (executed on the UI thread).</summary>
    public static void InjectMouse(int x, int y, MouseFlags flags)
    {
        if (_app == null) return;
        _injections.Enqueue(() =>
        {
            // Only ScreenPosition is set: the mouse router resolves View and the
            // view-relative Position itself from the screen coordinates.
            _app!.Mouse.RaiseMouseEvent(new Mouse
            {
                ScreenPosition = new System.Drawing.Point(x, y),
                Flags = flags,
            });
        });
    }

    /// <summary>
    /// Hit-tests a screen coordinate and returns the views stacked under it (deepest
    /// last), so a tester can see exactly which control will receive a mouse event
    /// before sending it. Resolved by the pump on the UI thread (never blocks on the
    /// TimedEvents lock — see the pump design note above).
    /// </summary>
    public static string HitTest(int x, int y)
    {
        if (_app == null) return "(puppet mode not initialized)";
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _hitTests.Enqueue((new System.Drawing.Point(x, y), tcs));
        return tcs.Task.GetAwaiter().GetResult();
    }
}
