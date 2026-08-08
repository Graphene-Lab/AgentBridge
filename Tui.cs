using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
        private TextField? _inputField;
        private Label? _statusLabel;
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

        private static readonly TuiAttribute[] LogoAttributes =
        {
            new TuiAttribute(Color.BrightMagenta, Color.Black),
            new TuiAttribute(Color.BrightCyan, Color.Black),
            new TuiAttribute(Color.BrightBlue, Color.Black),
            new TuiAttribute(Color.Magenta, Color.Black),
            new TuiAttribute(Color.Cyan, Color.Black),
            new TuiAttribute(Color.Blue, Color.Black),
        };

        private static readonly (string Keys, string What)[] ShortcutTable =
        {
            ("Enter", "Send the message / run the selected command"),
            ("/", "Open the slash-command palette (live, while you type)"),
            ("@", "Open the file palette (toggle chat attachments)"),
            ("?", "Show this shortcuts overlay (empty input)"),
            ("Tab", "Complete the selected command in the palette"),
            ("Esc", "Close dialog · clear input · twice: exit"),
            ("Ctrl+C", "Cancel the reply · clear input · twice: exit"),
            ("Ctrl+D", "Exit (empty input)"),
            ("Ctrl+Y", "Retry the last prompt"),
            ("Ctrl+R", "Reverse-search prompt history"),
            ("Up / Down", "Prompt history (also Ctrl+P / Ctrl+N)"),
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

            _inputField = new TextField
            {
                X = 0, Y = 0, Width = Dim.Fill() - 10, Height = 1,
            };
            SetPlaceholder();
            _inputField.HasFocusChanged += OnInputFocusChanged;
            _inputField.KeyDown += OnInputKeyDown;
            _inputField.ValueChanged += OnInputChanged;
            inputArea.Add(_inputField);

            var sendButton = new Button
            {
                Text = " Send ",
                X = Pos.Right(_inputField), Y = 0,
                Width = 10, Height = 1,
            };
            sendButton.Accepted += (_, _) => Submit();
            inputArea.Add(sendButton);

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

        private void OnInputChanged(object? sender, ValueChangedEventArgs<string?> e)
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

            if (key == Key.Enter)
            {
                key.Handled = true;
                Submit();
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
                // Exit on an empty input; otherwise the TextField's native
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
            else if (key == Key.CursorUp || key == Key.P.WithCtrl)
            {
                key.Handled = true;
                HistoryPrev();
            }
            else if (key == Key.CursorDown || key == Key.N.WithCtrl)
            {
                key.Handled = true;
                HistoryNext();
            }
            else if (key == Key.U.WithCtrl)
            {
                // Delete from the insertion point to the start of the input.
                key.Handled = true;
                if (_inputField is { } field)
                {
                    var t = field.Text ?? "";
                    field.Text = t[Math.Min(field.InsertionPoint, t.Length)..];
                    field.InsertionPoint = 0;
                }
            }
            else if (key == Key.W.WithCtrl)
            {
                key.Handled = true;
                _inputField?.KillWordBackwards();
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
            if (_inputField != null) _inputField.Text = _promptHistory[_histIndex];
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
