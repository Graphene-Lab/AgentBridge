using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIOrchestrator;
using AgentBridge.Resources;
using TL;
using UISupportGeneric;
using WTelegram;

/// <summary>
/// Telegram text-chat medium (WTelegramClient userbot). Telegram is treated as a chat client
/// like the HTML one — NOT as a SIP-style call medium: a private message (text and/or file
/// attachments) is handed to the agent through a per-user chat session and the reply (text +
/// attachments) is sent back to the same chat. No audio: the Telegram Client API has no
/// audio-call support, so only text and files travel.
///
/// Configuration lives in telegram.json next to the executable (never overwritten by updates,
/// see AutoUpdate.cs) and is editable from the TUI (/telegram) or by hand. First login is
/// guided from the TUI: api_id/api_hash/phone_number come from the config file, the
/// verification_code (and 2FA password if enabled) is requested via the pending-login flow
/// (POST /v1/telegram/login-code) and the session is persisted in a .session file.
/// </summary>
public static class TelegramBridge
{
    /// <summary>Lifecycle phase surfaced to GET /v1/telegram/status and the TUI.</summary>
    public enum TelegramPhase
    {
        /// <summary>Not enabled (telegram.json Enabled=false).</summary>
        Disabled,
        /// <summary>Login in progress.</summary>
        Connecting,
        /// <summary>Waiting for the verification code (TUI POST /v1/telegram/login-code).</summary>
        LoginPendingCode,
        /// <summary>Waiting for the 2FA password.</summary>
        LoginPendingPassword,
        /// <summary>Logged in, updates polling.</summary>
        Connected,
        /// <summary>Start/login failed (see <see cref="Error"/>).</summary>
        Failed,
    }

    /// <summary>Mirrors the telegram.json file.</summary>
    public sealed class TelegramConfig
    {
        /// <summary>Master switch; the bridge starts at boot only when true.</summary>
        public bool Enabled { get; set; }

        /// <summary>App api_id from https://my.telegram.org/apps.</summary>
        public long ApiId { get; set; }

        /// <summary>App api_hash from https://my.telegram.org/apps.</summary>
        public string ApiHash { get; set; } = "";

        /// <summary>Account phone number in international format (e.g. +393331234567).</summary>
        public string PhoneNumber { get; set; } = "";

        /// <summary>Session file (auth keys) relative to the executable directory. One pairing
        /// is enough: after the first login the session persists and no code is asked again.</summary>
        public string SessionPath { get; set; } = "telegram.session";

        /// <summary>Users allowed to talk to the agent. Empty = everyone in private chats
        /// (like the HTML client). Entries are numeric user ids and/or @usernames.</summary>
        public List<string> AllowedUsers { get; set; } = new();

        /// <summary>Agent set used for the conversations (see AgentTools.Resolve).</summary>
        public string Agent { get; set; } = "default-agent";
    }

    private static readonly object Sync = new();
    private static readonly ConcurrentDictionary<long, string> PeerSessions = new(); // user id → session id
    private static readonly ConcurrentDictionary<long, object> PeerLocks = new();   // per-user session-creation lock
    private static readonly ConcurrentDictionary<long, Task> PeerChains = new();    // per-user FIFO reply chain

    private static string _startupProvider = "";
    private static bool _anonymize;
    private static TelegramConfig _cfg = new();
    private static Client? _client;
    private static UpdateManager? _manager;
    private static User? _me;
    private static TelegramPhase _phase = TelegramPhase.Disabled;
    private static string? _error;
    private static TaskCompletionSource<string>? _pendingLogin;
    private static CancellationTokenSource? _cts;

    /// <summary>Effective configuration (load telegram.json at startup).</summary>
    public static TelegramConfig Cfg => _cfg;

    /// <summary>True when the telegram.json Enabled flag is set.</summary>
    public static bool IsEnabled => _cfg.Enabled;

    /// <summary>Current lifecycle phase.</summary>
    public static TelegramPhase Phase { get { lock (Sync) return _phase; } }

    /// <summary>Last start/login error message (null when none).</summary>
    public static string? Error { get { lock (Sync) return _error; } }

    /// <summary>True while logged in and polling updates.</summary>
    public static bool IsConnected => Phase == TelegramPhase.Connected;

    /// <summary>Loads telegram.json and remembers the provider/anonymize used for sessions.</summary>
    public static void Init(string startupProvider, bool anonymize)
    {
        _startupProvider = startupProvider;
        _anonymize = anonymize;
        LoadFromFile();
    }

    /// <summary>Starts the Telegram client: loads the session, logs in (pending-login flow when
    /// a verification code is required) and begins polling private-chat updates. Returns null on
    /// success or the error message. Runs the login loop to completion; callers that must not
    /// block (boot, /enable) wrap it in Task.Run.</summary>
    public static async Task<string?> StartAsync()
    {
        lock (Sync)
        {
            if (_client != null) return null;
            if (!_cfg.Enabled) return null;
            if (_cfg.ApiId <= 0 || string.IsNullOrWhiteSpace(_cfg.ApiHash) || string.IsNullOrWhiteSpace(_cfg.PhoneNumber))
            {
                _phase = TelegramPhase.Failed;
                _error = "telegram.json: set ApiId, ApiHash and PhoneNumber (TUI /telegram config set or the setup scripts)";
                return _error;
            }
            _phase = TelegramPhase.Connecting;
            _error = null;
            _cts = new CancellationTokenSource();
            // Client + manager are created under the lock so a concurrent StartAsync (boot task
            // + TUI config set) cannot double-create the client.
            _client = new Client(Config);
            _manager = _client.WithUpdateManager(OnUpdate);
        }

        try
        {
            // Pending-login flow: while the client.User is null, Login returns which config item
            // is needed next ("verification_code" / "password" / "name"); the TUI completes the
            // pending code/password via POST /v1/telegram/login-code and the await unblocks.
            var loginInfo = _cfg.PhoneNumber;
            while (_client.User == null)
            {
                var needed = await _client.Login(loginInfo);
                switch (needed)
                {
                    case "verification_code":
                        SetPhase(TelegramPhase.LoginPendingCode);
                        loginInfo = await AwaitLoginInputAsync();
                        break;
                    case "password":
                        SetPhase(TelegramPhase.LoginPendingPassword);
                        loginInfo = await AwaitLoginInputAsync();
                        break;
                    case "name": // sign-up (unknown phone): pick a neutral identity
                        loginInfo = "AgentBridge";
                        break;
                    default:
                        loginInfo = null;
                        break;
                }
            }
            _me = _client.User;
            SetPhase(TelegramPhase.Connected);
            Log.LogStep($"Telegram connected as {_me.username ?? _me.first_name + " " + _me.last_name} (id {_me.id})");
            return null;
        }
        catch (Exception ex)
        {
            Log.LogStep($"Telegram start failed: {ex.Message}");
            // If Stop() already ran (e.g. the user disabled the bridge while the login was
            // pending), the client is gone and the phase was set to Disabled — don't overwrite
            // that clean state with a Failed status for a cancellation we initiated ourselves.
            if (_client != null)
            {
                SetPhase(TelegramPhase.Failed);
                _error = ex.Message;
            }
            try { _client?.Dispose(); } catch { }
            _client = null;
            _manager = null;
            return ex.Message;
        }
    }

    /// <summary>Disconnects the Telegram client (cancels any pending login).</summary>
    public static void Stop()
    {
        Client? client;
        TaskCompletionSource<string>? pending;
        lock (Sync)
        {
            client = _client;
            pending = _pendingLogin;
            _pendingLogin = null;
            _client = null;
            _manager = null;
            _me = null;
            _phase = TelegramPhase.Disabled;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
        // Unblock a pending login so the loop can exit (the cancelled TCS makes AwaitLoginInputAsync throw).
        pending?.TrySetCanceled();
        try { client?.Dispose(); } catch { }
    }

    /// <summary>Completes the pending login input (verification code or 2FA password) from the
    /// TUI / HTTP endpoint. Returns an error when no login is pending.</summary>
    public static string? SubmitLoginInput(string value)
    {
        TaskCompletionSource<string>? pending;
        lock (Sync) pending = _pendingLogin;
        if (pending == null) return "no pending Telegram login (start the bridge first)";
        pending.TrySetResult(value ?? "");
        return null;
    }

    /// <summary>Read-only snapshot for the TUI status page.</summary>
    public sealed record TelegramStatus
    {
        public bool Enabled { get; init; }
        public string Phase { get; init; } = "";
        public string? Error { get; init; }
        public TelegramUser? User { get; init; }
        public List<string> AllowedUsers { get; init; } = new();
        public string Agent { get; init; } = "";
        public string SessionPath { get; init; } = "";
        public bool PendingLogin { get; init; }
        public int PeerCount { get; init; }
    }

    /// <summary>Logged-in Telegram user, as surfaced by <see cref="Status"/>.</summary>
    public sealed record TelegramUser
    {
        public long Id { get; init; }
        public string? Username { get; init; }
        public string Name { get; init; } = "";
    }

    /// <summary>Current lifecycle status (in-process — no HTTP surface; the TUI reads it directly).</summary>
    public static TelegramStatus Status
    {
        get
        {
            TelegramPhase phase;
            string? error;
            User? me;
            lock (Sync)
            {
                phase = _phase;
                error = _error;
                me = _me;
            }
            return new TelegramStatus
            {
                Enabled = _cfg.Enabled,
                Phase = phase.ToString().ToLowerInvariant(),
                Error = error,
                User = me == null ? null : new TelegramUser { Id = me.id, Username = me.username, Name = me.first_name + " " + me.last_name },
                AllowedUsers = _cfg.AllowedUsers,
                Agent = _cfg.Agent,
                SessionPath = _cfg.SessionPath,
                PendingLogin = phase == TelegramPhase.LoginPendingCode || phase == TelegramPhase.LoginPendingPassword,
                PeerCount = PeerSessions.Count,
            };
        }
    }

    /// <summary>Read-only config snapshot (api_hash masked) — in-process, read by the TUI.</summary>
    public static TelegramConfig ConfigSnapshot
    {
        get
        {
            var c = _cfg;
            return new TelegramConfig
            {
                Enabled = c.Enabled,
                ApiId = c.ApiId,
                ApiHash = string.IsNullOrEmpty(c.ApiHash) ? "" : "••••",
                PhoneNumber = c.PhoneNumber,
                SessionPath = c.SessionPath,
                AllowedUsers = c.AllowedUsers,
                Agent = c.Agent,
            };
        }
    }

    /// <summary>Sets one config key and persists telegram.json. Connection-affecting keys
    /// (Enabled, ApiId, ApiHash, PhoneNumber, SessionPath) require a bridge restart, which is
    /// performed automatically when the change applies.</summary>
    public static async Task<(string? Error, bool RestartRequired, string Message)> SetConfigAsync(string key, string? value)
    {
        var prop = typeof(TelegramConfig).GetProperty(key, System.Reflection.BindingFlags.IgnoreCase |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (prop == null) return ($"unknown Telegram config key: {key}", false, "");

        object? parsed;
        try { parsed = ParseConfigValue(prop.PropertyType, value); }
        catch (Exception ex) { return ($"invalid value for {key}: {ex.Message}", false, ""); }

        var restarting = RestartKeys.Contains(key);
        var previous = prop.GetValue(_cfg);
        prop.SetValue(_cfg, parsed);
        var persistError = PersistConfig();
        if (persistError != null)
        {
            prop.SetValue(_cfg, previous);   // roll back the in-memory change
            return (persistError, false, "");
        }

        if (!restarting) return (null, false, $"{key} set to {DisplayValue(key, parsed)} — active from the next message");

        // Restart the bridge so the new value takes effect (Stop cancels a pending login too).
        // A restart key changed while the bridge was down (Disabled/Failed) must START it when
        // Enabled=true — otherwise /telegram config set Enabled true would do nothing.
        var wasRunning = Phase == TelegramPhase.Connected || Phase == TelegramPhase.Connecting ||
                         Phase == TelegramPhase.LoginPendingCode || Phase == TelegramPhase.LoginPendingPassword;
        if (wasRunning) Stop();

        if (_cfg.Enabled)
        {
            var startError = await StartAsync();
            return (startError, true, startError ?? $"{key} set to {DisplayValue(key, parsed)} — bridge started");
        }
        return (null, true, $"{key} set to {DisplayValue(key, parsed)} — bridge stopped");
    }

    /// <summary>Re-reads telegram.json from disk (hand edits made outside the TUI) and applies
    /// the change, restarting the bridge when a connection-affecting key differs.</summary>
    public static async Task<(string? Error, bool RestartRequired, string Message)> ReloadConfigAsync()
    {
        try
        {
            var path = ConfigFilePath();
            if (!File.Exists(path)) return ($"telegram.json not found at {path}", false, "");
            var fileCfg = JsonSerializer.Deserialize<TelegramConfig>(File.ReadAllText(path)) ?? new TelegramConfig();

            var restarting = RestartKeys.Any(k => ConfigValueDiffers(_cfg, fileCfg, k));
            var wasConnected = IsConnected;
            _cfg = fileCfg;

            if (restarting && (wasConnected || _client != null))
            {
                Stop();
                if (_cfg.Enabled)
                {
                    var startError = await StartAsync();
                    return (startError, true, startError ?? "Telegram config reloaded — bridge restarted");
                }
            }
            return (null, restarting && _cfg.Enabled, "Telegram config reloaded from telegram.json");
        }
        catch (Exception ex)
        {
            return ($"Telegram config reload failed: {ex.Message}", false, "");
        }
    }

    /// <summary>Adds a user (numeric id or @username) to the allow-list and persists.</summary>
    public static (string? Error, string Message) AddAllowedUser(string user)
    {
        var entry = (user ?? "").Trim();
        if (entry.Length == 0) return ("user is required (numeric id or @username)", "");
        lock (Sync)
        {
            if (_cfg.AllowedUsers.Any(u => string.Equals(u, entry, StringComparison.OrdinalIgnoreCase)))
                return (null, $"{entry} is already allowed");
            _cfg.AllowedUsers.Add(entry);
        }
        var persistError = PersistConfig();
        return persistError == null ? (null, $"{entry} added to the Telegram allow-list") : (persistError, "");
    }

    /// <summary>Removes a user from the allow-list and persists.</summary>
    public static (string? Error, string Message) RemoveAllowedUser(string user)
    {
        var entry = (user ?? "").Trim();
        bool removed;
        lock (Sync)
        {
            removed = _cfg.AllowedUsers.RemoveAll(u => string.Equals(u, entry, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        if (!removed) return (null, $"{entry} was not in the allow-list");
        var persistError = PersistConfig();
        return persistError == null ? (null, $"{entry} removed from the Telegram allow-list") : (persistError, "");
    }

    // ─── WTelegramClient wiring ───────────────────────────────────────

    private static string Config(string what) => what switch
    {
        "api_id" => _cfg.ApiId.ToString(),
        "api_hash" => _cfg.ApiHash,
        "phone_number" => _cfg.PhoneNumber,
        "session_pathname" => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _cfg.SessionPath),
        // verification_code / password / name: return null so WTelegramClient signals the
        // pending item through Client.Login() (the "request/response" dance in RunLoginAsync)
        // instead of asking this callback. The StartAsync login loop then sets the phase and
        // waits for the TUI input via AwaitLoginInputAsync — otherwise the phase would stay
        // "connecting" and the TUI would never prompt for the code.
        _ => null!,
    };

    private static async Task<string> AwaitLoginInputAsync()
    {
        TaskCompletionSource<string> pending;
        lock (Sync)
        {
            _pendingLogin = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = _pendingLogin;
        }
        try { return await pending.Task; }
        catch (TaskCanceledException) { throw new InvalidOperationException("Telegram login cancelled"); }
    }

    private static Task OnUpdate(Update update)
    {
        if (update is not UpdateNewMessage { message: Message message }) return Task.CompletedTask;
        if (message.flags.HasFlag(Message.Flags.out_)) return Task.CompletedTask; // our own message (echo)
        if (message.peer_id is not PeerUser) return Task.CompletedTask;           // private chats only
        var userId = (message.from_id as PeerUser)?.user_id ?? message.peer_id.ID;
        if (userId == 0) return Task.CompletedTask;

        // Allow-list: empty = everyone (HTML-client behaviour). Disallowed users are silently ignored.
        if (!IsAllowed(userId))
        {
            Log.LogStep($"Telegram: ignoring message from user {userId} (not in the allow-list)");
            return Task.CompletedTask;
        }

        // Fire-and-forget: the agent run can take minutes and the UpdateManager must keep
        // processing the other updates meanwhile. Per-user ordering is guaranteed by the
        // per-user FIFO chain (EnqueuePeer) — without it, concurrent Task.Run runs could
        // reply to message 2 before message 1 (the session gate serializes access but does
        // not order it).
        EnqueuePeer(userId, message);
        return Task.CompletedTask;
    }

    // FIFO chain per user: every incoming message waits for the previous one of the same
    // user before running, so replies come back in the order the messages arrived.
    private static void EnqueuePeer(long userId, Message message)
    {
        object peerLock = PeerLocks.GetOrAdd(userId, _ => new object());
        lock (peerLock)
        {
            var prev = PeerChains.TryGetValue(userId, out var p) ? p : Task.CompletedTask;
            var next = prev.IsCompleted
                ? HandleMessageAsync(userId, message)
                : prev.ContinueWith(_ => HandleMessageAsync(userId, message), TaskScheduler.Default).Unwrap();
            PeerChains[userId] = next;
        }
    }

    private static async Task HandleMessageAsync(long userId, Message message)
    {
        try
        {
            if (!_manager!.Users.TryGetValue(userId, out var sender))
            {
                await _client!.Messages_GetAllDialogs(); // populate the manager caches
                _manager.Users.TryGetValue(userId, out sender);
            }
            if (sender == null || sender.access_hash == 0)
            {
                Log.LogStep($"Telegram: cannot resolve user {userId} (no access hash) — reply skipped");
                return;
            }
            var peer = new InputPeerUser(userId, sender.access_hash);

            // Incoming attachments: Telegram documents/photos are downloaded and handed to the
            // harness as FileAttachment — the server-side Markdown conversion pipeline then
            // makes them readable by the agent (same path as the HTML client uploads).
            // Oversized documents are skipped with a notice instead of being loaded in memory.
            var attachments = new List<FileAttachment>();
            switch (message.media)
            {
                case MessageMediaDocument { document: Document document }:
                    if (document.size > MaxTelegramIncomingBytes)
                    {
                        await _client!.SendMessageAsync(peer,
                            $"The file is too large ({document.size / (1024 * 1024)} MB) — the limit is {MaxTelegramIncomingBytes / (1024 * 1024)} MB.");
                        return;
                    }
                    var fileName = document.Filename ?? $"{document.id}.{ExtensionFromMime(document.mime_type)}";
                    attachments.Add(await DownloadAsync(fileName, stream => _client!.DownloadFileAsync(document, stream)));
                    break;
                case MessageMediaPhoto { photo: Photo photo }:
                    attachments.Add(await DownloadAsync($"{photo.id}.jpg", stream => _client!.DownloadFileAsync(photo, stream)));
                    break;
            }

            // Per-user chat session (multi-turn history); the SessionStore idle cleanup drops it
            // after 30 minutes of silence and a fresh one starts on the next message. Creation
            // is guarded per user so two concurrent messages cannot mint two sessions.
            object peerLock = PeerLocks.GetOrAdd(userId, _ => new object());
            string sessionId;
            lock (peerLock)
            {
                if (!PeerSessions.TryGetValue(userId, out sessionId) || SessionStore.Get(sessionId) == null)
                {
                    var session = SessionStore.Create(_startupProvider, _anonymize);
                    PeerSessions[userId] = session.Id;
                    sessionId = session.Id;
                }
            }
            var active = SessionStore.Get(sessionId)!;
            await active.Gate.WaitAsync();

            try
            {
                var prompt = message.message?.Trim();
                if (string.IsNullOrEmpty(prompt) && attachments.Count > 0)
                    prompt = "Examine the attached file(s) and answer accordingly.";
                if (string.IsNullOrEmpty(prompt))
                {
                    await _client!.SendMessageAsync(peer, "I received an empty message. Try again with some text or a file.");
                    return;
                }

                var result = active.Orchestrator.ExecuteAction(prompt, AgentTools.Resolve(_cfg.Agent),
                    maxIterations: MaxAgentIterations, attachments: attachments, isLocalUser: false);

                var reply = result.Message ?? result.Error;
                if (string.IsNullOrWhiteSpace(reply))
                    reply = Dictionary.NoOutputGenerated;

                await _client!.SendMessageAsync(peer, reply);

                // Outgoing attachments: the done method's MCP-shaped resources are uploaded and
                // sent as Telegram documents, exactly like the HTML client downloads them.
                if (result.Attachments is { Count: > 0 })
                {
                    foreach (var att in result.Attachments)
                    {
                        var resource = att.Resource;
                        if (resource?.Blob == null) continue;
                        string tmp = "";
                        try
                        {
                            tmp = Path.Combine(Path.GetTempPath(), "agent-telegram-" + Guid.NewGuid().ToString("N")[..8] + "-" + SanitizeName(att.Name));
                            await File.WriteAllBytesAsync(tmp, Convert.FromBase64String(resource.Blob));
                            // UploadFileAsync(stream, filename) preserves the original file name
                            // on the Telegram side — the tmp path name must never leak into the chat.
                            await using var fs = File.OpenRead(tmp);
                            var inputFile = await _client!.UploadFileAsync(fs, att.Name);
                            await _client!.SendMediaAsync(peer, null, inputFile);
                        }
                        catch (Exception ex)
                        {
                            Log.LogStep($"Telegram: failed to send attachment '{att.Name}': {ex.Message}");
                        }
                        finally
                        {
                            if (tmp.Length > 0)
                                try { File.Delete(tmp); } catch { }
                        }
                    }
                }
            }
            finally
            {
                active.Gate.Release();
            }
        }
        catch (Exception ex)
        {
            Log.LogStep($"Telegram: message handling failed: {ex.Message}");
        }
    }

    private static async Task<FileAttachment> DownloadAsync(string name, Func<Stream, Task> download)
    {
        using var ms = new MemoryStream();
        await download(ms);
        return new FileAttachment(name, ms.ToArray());
    }

    private static bool IsAllowed(long userId)
    {
        // Snapshot under the lock: AddAllowedUser/RemoveAllowedUser mutate the list from the
        // TUI thread while OnUpdate (background) reads it — iterating the live list could throw.
        List<string> allowed;
        lock (Sync) allowed = _cfg.AllowedUsers.ToList();
        if (allowed.Count == 0) return true;
        if (allowed.Any(u => long.TryParse(u.Trim(), out var id) && id == userId)) return true;
        if (_manager?.Users.TryGetValue(userId, out var user) == true && user.username != null)
            return allowed.Any(u => u.Trim().TrimStart('@').Equals(user.username, StringComparison.OrdinalIgnoreCase));
        return false;
    }

    // ─── Config file persistence (telegram.json, protected from updates) ─

    private static string ConfigFilePath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "telegram.json");

    private static void LoadFromFile()
    {
        try
        {
            var path = ConfigFilePath();
            if (File.Exists(path))
                _cfg = JsonSerializer.Deserialize<TelegramConfig>(File.ReadAllText(path)) ?? new TelegramConfig();
        }
        catch (Exception ex)
        {
            Log.LogStep($"Telegram: failed to read telegram.json ({ex.Message}) — using defaults");
        }
    }

    private static string? PersistConfig()
    {
        try
        {
            File.WriteAllText(ConfigFilePath(),
                JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true }));
            Log.LogStep("Telegram config persisted to telegram.json");
            return null;
        }
        catch (Exception ex)
        {
            return $"Telegram config persist failed: {ex.Message}";
        }
    }

    /// <summary>Connection-affecting keys: changing any of them restarts the bridge.</summary>
    private static readonly HashSet<string> RestartKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(TelegramConfig.Enabled), nameof(TelegramConfig.ApiId), nameof(TelegramConfig.ApiHash),
        nameof(TelegramConfig.PhoneNumber), nameof(TelegramConfig.SessionPath),
    };

    private static object? ParseConfigValue(Type type, string? value)
    {
        if (type == typeof(bool)) return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                                        value == "1" || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        if (type == typeof(long)) return long.Parse((value ?? "").Trim(), System.Globalization.CultureInfo.InvariantCulture);
        if (type == typeof(List<string>))
            return (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return value ?? "";
    }

    private static bool ConfigValueDiffers(TelegramConfig a, TelegramConfig b, string key) =>
        !Equals(typeof(TelegramConfig).GetProperty(key)!.GetValue(a), typeof(TelegramConfig).GetProperty(key)!.GetValue(b));

    private static string DisplayValue(string key, object? value)
    {
        if (key.Equals(nameof(TelegramConfig.ApiHash), StringComparison.OrdinalIgnoreCase)) return "•••• (api_hash)";
        if (value is System.Collections.IEnumerable items && value is not string)
            return string.Join(", ", items.Cast<object?>());
        return value?.ToString() ?? "";
    }

    private static string ExtensionFromMime(string mime)
    {
        var i = mime?.IndexOf('/') ?? -1;
        if (i <= 0 || i >= mime!.Length - 1) return "bin";
        var ext = mime[(i + 1)..].Split(';')[0].Trim().ToLowerInvariant();
        return ext.Length > 0 && ext.Length <= 8 ? ext : "bin";
    }

    private static string SanitizeName(string name) =>
        string.Concat((name ?? "attachment").Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != ' '));

    private static void SetPhase(TelegramPhase phase)
    {
        lock (Sync) _phase = phase;
    }

    /// <summary>Max agent loop iterations per message (same budget as the HTTP chat endpoint).</summary>
    private const int MaxAgentIterations = 50;

    /// <summary>Cap on incoming Telegram documents (bytes). Files above it are refused with a
    /// notice instead of being downloaded into memory — mirrors the harness's outgoing cap.</summary>
    private const long MaxTelegramIncomingBytes = 25 * 1024 * 1024;
}
