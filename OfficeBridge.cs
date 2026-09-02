using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AIOrchestrator;

// ═══════════════════════════════════════════════════════════════════════
//  OfficeBridge — duplex WebSocket hub between AgentBridge and OfficeManager
//
//  OfficeManager (the 16-bit office app, served at /OfficeManager) renders an
//  employee for EVERY live agent instance: a session (whatever medium created
//  it — TUI chat, /v1/chat/completions, SIP, Telegram, OfficeManager itself),
//  a stateless one-shot API call, or a subagent of any depth. The mapping is
//  keyed on AgentHarness.GlobalProgress (process-wide: every orchestrator in
//  THIS process, however it was created) plus the SessionStore lifecycle
//  events (sessions exist even before their first ExecuteAction). Agents
//  created by OTHER processes (AIOffice desktop app, voice panels, schedulers)
//  are forwarded here via AgentHarness.ForwardGlobalProgressTo → POST
//  /v1/office/events (see IngestExternalEvent) and get the same treatment.
//
//  Wire protocol (JSON text frames, camelCase):
//    Server → Client:
//      {"type":"snapshot","employees":[{empId,agentId,kind,sprite,label,running}...]}
//      {"type":"spawn",  empId, agentId, kind:"idle"|"session"|"stateless"|"subagent", sprite, label}
//      {"type":"assign", empId, agentId, label}          // an idle employee became a session agent
//      {"type":"running",empId, value:bool}              // agent run started/finished
//      {"type":"method", empId, method}                  // tool method the agent is executing
//      {"type":"closed", empId}                          // agent instance closed → return to door, despawn
//      {"type":"chat",   empId, role:"user"|"assistant"|"sys", text}
//      {"type":"error",  text}
//    Client → Server:
//      {"type":"hello"}
//      {"type":"chat_send", empId, prompt}
//      {"type":"close", empId}
//
//  chat_send on an idle employee creates a session (AgentHarness) for it and
//  spawns a replacement idle employee at the door; on a session employee it
//  runs ExecuteAction on that session (the same conversation the TUI would
//  use). Subagent/stateless employees are visual-only and refuse chat.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>WebSocket hub + agent-lifecycle tracker for the OfficeManager visual protocol.</summary>
public static class OfficeBridge
{
    private const int IdleCount = 1;                 // one always-present roamable employee (hire pool)
    private const int SpriteCount = 5;               // employee A..E sprite sheets

    private static string _provider = "DeepSeekBridge";
    private static bool _anonymize;

    /// <summary>One office employee. Server-authoritative; the browser mirrors it via events.</summary>
    private sealed class Employee
    {
        public string EmpId = "";
        public string? AgentId;                       // agentKey: instanceId / instanceId::subagentId
        public string Kind = "idle";                  // idle | session | stateless | subagent
        public int Sprite;
        public string Label = "";
        public bool Running;
    }

    private static readonly ConcurrentDictionary<string, Employee> Employees = new();   // empId → employee
    private static readonly ConcurrentDictionary<string, string> ByAgent = new();       // agentKey → empId
    private static readonly HashSet<string> SessionAgents = new();                      // instanceIds backed by a session
    private static readonly HashSet<string> PendingAssign = new();                      // idle empIds waiting for the next session
    private static readonly ConcurrentDictionary<string, WebSocketClient> Clients = new();

    private static readonly object Sync = new();
    private static int _nextEmp = 1;
    private static int _nextAgentNo = 1;
    private static int _nextSprite;

    private sealed class WebSocketClient
    {
        public WebSocket Socket = null!;
        public Channel<string> Out = Channel.CreateUnbounded<string>();
        public Task Sender = Task.CompletedTask;
    }

    /// <summary>Called once at startup (Program.cs) with the same provider/anonymize the
    /// session store uses, then the hub tracks every agent instance in this process.</summary>
    public static void Init(string provider, bool anonymize)
    {
        _provider = provider;
        _anonymize = anonymize;
        SessionStore.SessionCreated += OnSessionCreated;
        SessionStore.SessionRemoved += OnSessionRemoved;
        AgentHarness.GlobalProgress += OnGlobalProgress;
        lock (Sync)
        {
            for (int i = 0; i < IdleCount; i++) SpawnLocked("idle", null, "");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Agent lifecycle → employee events
    // ────────────────────────────────────────────────────────────────────

    private static void OnSessionCreated(ActiveSession session)
    {
        lock (Sync)
        {
            SessionAgents.Add(session.Id);
            // A session created because the user started a chat with an idle employee takes over
            // that employee (assign) instead of spawning a duplicate at the door.
            if (PendingAssign.Count > 0)
            {
                var empId = PendingAssign.First();
                PendingAssign.Remove(empId);
                var emp = Employees[empId];
                emp.AgentId = session.Id;
                emp.Kind = "session";
                emp.Label = ShortLabel(session.Id);
                ByAgent[session.Id] = empId;
                Broadcast(new { type = "assign", empId, agentId = session.Id, label = emp.Label });
                SpawnLocked("idle", null, "");                       // replacement idle appears at the door
            }
            else
            {
                SpawnLocked("session", session.Id, ShortLabel(session.Id));
            }
        }
    }

    private static void OnSessionRemoved(ActiveSession session)
    {
        lock (Sync)
        {
            SessionAgents.Remove(session.Id);
            if (ByAgent.TryRemove(session.Id, out var empId))
            {
                Employees.TryRemove(empId, out _);
                Broadcast(new { type = "closed", empId });
            }
        }
    }

    private static void OnGlobalProgress(object? sender, AgentHarness.AgentProgressEventArgs e)
    {
        lock (Sync)
        {
            var key = e.SubagentId == null ? e.InstanceId ?? "" : $"{e.InstanceId}::{e.SubagentId}";
            if (key.Length == 0) return;
            ByAgent.TryGetValue(key, out var empId);
            var known = empId != null;

            if (e.SubagentId != null)
            {
                // Subagent events: spawn on first Running, methods while running, closed on completion.
                if (!known && e.State == AgentHarness.AgentState.Running)
                    empId = SpawnLocked("subagent", key, e.SubagentId);
                if (empId == null) return;
                switch (e.State)
                {
                    case AgentHarness.AgentState.Running: SetRunningLocked(empId, true); break;
                    case AgentHarness.AgentState.Iteration:
                        SetRunningLocked(empId, true);
                        if (!string.IsNullOrEmpty(e.MethodName))
                            Broadcast(new { type = "method", empId, method = e.MethodName });
                        break;
                    case AgentHarness.AgentState.Completed:
                    case AgentHarness.AgentState.Failed:
                        CloseLocked(empId);
                        break;
                }
            }
            else
            {
                // Main-agent events: a stateless instance appears at its first Running and closes
                // when the run ends (its instance dies with the request); session instances stay
                // until SessionRemoved (conversation alive between runs).
                if (!known && e.State == AgentHarness.AgentState.Running)
                {
                    var kind = SessionAgents.Contains(e.InstanceId ?? "") ? "session" : "stateless";
                    empId = SpawnLocked(kind, key, kind == "session" ? ShortLabel(key) : $"agent {_nextAgentNo++}");
                }
                if (empId == null) return;
                switch (e.State)
                {
                    case AgentHarness.AgentState.Running: SetRunningLocked(empId, true); break;
                    case AgentHarness.AgentState.Iteration:
                        SetRunningLocked(empId, true);
                        if (!string.IsNullOrEmpty(e.MethodName))
                            Broadcast(new { type = "method", empId, method = e.MethodName });
                        break;
                    case AgentHarness.AgentState.Initiative:
                        SetRunningLocked(empId, false);
                        if (!string.IsNullOrEmpty(e.Message))
                            Broadcast(new { type = "chat", empId, role = "assistant", text = e.Message });
                        break;
                    case AgentHarness.AgentState.Completed:
                    case AgentHarness.AgentState.Failed:
                        SetRunningLocked(empId, false);
                        if (!SessionAgents.Contains(e.InstanceId ?? ""))
                            CloseLocked(empId);                        // one-shot instance: done → home
                        break;
                }
            }
        }
    }

    /// <summary>Creates an employee, registers it and broadcasts the spawn event. Call under Sync.</summary>
    private static string SpawnLocked(string kind, string? agentKey, string label)
    {
        var empId = $"emp-{_nextEmp++}";
        var emp = new Employee
        {
            EmpId = empId,
            AgentId = agentKey,
            Kind = kind,
            Sprite = _nextSprite++ % SpriteCount,
            Label = label,
        };
        Employees[empId] = emp;
        if (agentKey != null) ByAgent[agentKey] = empId;
        Broadcast(new { type = "spawn", empId, agentId = agentKey, kind, sprite = emp.Sprite, label });
        return empId;
    }

    private static void SetRunningLocked(string empId, bool value)
    {
        if (!Employees.TryGetValue(empId, out var emp)) return;
        emp.Running = value;
        Broadcast(new { type = "running", empId, value });
    }

    private static void CloseLocked(string empId)
    {
        if (!Employees.TryRemove(empId, out var emp)) return;
        if (emp.AgentId != null) ByAgent.TryRemove(emp.AgentId, out _);
        Broadcast(new { type = "closed", empId });
    }

    // ────────────────────────────────────────────────────────────────────
    // Client protocol
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Accepts one WebSocket client (the browser tab) and serves it until it disconnects.</summary>
    public static async Task HandleClientAsync(WebSocket ws, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString("N");
        var client = new WebSocketClient { Socket = ws };
        Clients[key] = client;
        client.Sender = Task.Run(() => SendLoopAsync(client, ct));
        try
        {
            SendSnapshot(client);
            var buffer = new byte[16384];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                HandleClientMessage(text);
            }
        }
        catch { /* disconnect or aborted — cleanup below */ }
        finally
        {
            Clients.TryRemove(key, out _);
            client.Out.Writer.TryComplete();
            try { await client.Sender; } catch { }
            try { if (ws.State != WebSocketState.Closed) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
    }

    private static void HandleClientMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "hello":
                    foreach (var c in Clients.Values) SendSnapshot(c);
                    break;
                case "chat_send":
                {
                    var empId = Str(root, "empId");
                    var prompt = Str(root, "prompt");
                    if (empId != null && !string.IsNullOrWhiteSpace(prompt))
                        _ = Task.Run(() => HandleChatSendAsync(empId, prompt.Trim()));
                    break;
                }
                case "close":
                {
                    var empId = Str(root, "empId");
                    if (empId != null) HandleClose(empId);
                    break;
                }
            }
        }
        catch { /* malformed frame — ignore */ }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static async Task HandleChatSendAsync(string empId, string prompt)
    {
        ActiveSession? session = null;
        lock (Sync)
        {
            if (!Employees.TryGetValue(empId, out var emp)) { Error("Unknown employee."); return; }
            switch (emp.Kind)
            {
                case "subagent":
                    Chat(empId, "sys", "This employee is a subagent — its conversation is managed by the main agent.");
                    return;
                case "stateless":
                    Chat(empId, "sys", "This agent is a one-shot API call — it cannot be chatted with.");
                    return;
                case "idle":
                    // Start a conversation with the idle employee: it becomes a real agent (same
                    // rules as any session) and a replacement idle employee appears at the door.
                    PendingAssign.Add(empId);
                    try
                    {
                        session = SessionStore.Create(_provider, _anonymize);
                    }
                    catch (Exception ex)
                    {
                        PendingAssign.Remove(empId);
                        Error($"Could not start an agent: {ex.Message}");
                        return;
                    }
                    break;
                default:
                    session = SessionStore.Get(emp.AgentId ?? "");
                    if (session == null)
                    {
                        Chat(empId, "sys", "This conversation has expired — start a new one.");
                        return;
                    }
                    break;
            }
        }

        Chat(empId, "user", prompt);
        await RunAgentAsync(empId, session, prompt);
    }

    private static async Task RunAgentAsync(string empId, ActiveSession session, string prompt)
    {
        SetRunning(empId, true);
        try
        {
            // One chat at a time per conversation (same gate the HTTP endpoints use); the agent
            // set matches a chat without a model (default-agent preset + core tools).
            await session.Gate.WaitAsync();
            AgentResult result;
            try
            {
                result = await Task.Run(() => session.Orchestrator.ExecuteAction(
                    prompt, AgentTools.Resolve(null), maxIterations: 200, isLocalUser: true));
            }
            finally { session.Gate.Release(); }

            var text = result.Message
                ?? (result.Success ? AgentBridge.Resources.Dictionary.NoOutputGenerated : result.Error ?? "error");
            Chat(empId, "assistant", text);
        }
        catch (Exception ex)
        {
            Chat(empId, "sys", $"Agent failed: {ex.Message}");
        }
        finally
        {
            SetRunning(empId, false);
        }
    }

    private static void HandleClose(string empId)
    {
        string? agentId = null;
        lock (Sync)
        {
            if (Employees.TryGetValue(empId, out var emp) && emp.Kind == "session")
                agentId = emp.AgentId;
        }
        // SessionStore.TryRemove fires SessionRemoved → the closed event reaches the browser.
        if (agentId != null) SessionStore.TryRemove(agentId);
    }

    /// <summary>Accepts events forwarded by AgentHarness.ForwardGlobalProgressTo from other
    /// processes (AIOffice app, voice panels, ...) — the same stream as OnGlobalProgress.</summary>
    public static void IngestExternalEvent(JsonElement root)
    {
        var state = Str(root, "state");
        if (!Enum.TryParse<AgentHarness.AgentState>(state, out var parsed)) return;
        OnGlobalProgress(null, new AgentHarness.AgentProgressEventArgs
        {
            State = parsed,
            Iteration = root.TryGetProperty("iteration", out var it) && it.ValueKind == JsonValueKind.Number ? it.GetInt32() : 0,
            MethodName = Str(root, "methodName"),
            Message = Str(root, "message"),
            Error = Str(root, "error"),
            SubagentId = Str(root, "subagentId"),
            Level = root.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetInt32() : 0,
            InstanceId = Str(root, "instanceId"),
        });
    }

    // ────────────────────────────────────────────────────────────────────
    // Broadcast helpers
    // ────────────────────────────────────────────────────────────────────

    private static void Broadcast(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        foreach (var c in Clients.Values) c.Out.Writer.TryWrite(json);
    }

    private static void SendSnapshot(WebSocketClient client)
    {
        Employee[] employees;
        lock (Sync) employees = Employees.Values.ToArray();
        client.Out.Writer.TryWrite(JsonSerializer.Serialize(new
        {
            type = "snapshot",
            employees = employees.Select(e => new
            {
                empId = e.EmpId, agentId = e.AgentId, kind = e.Kind,
                sprite = e.Sprite, label = e.Label, running = e.Running
            }),
        }));
    }

    private static void SetRunning(string empId, bool value)
    {
        lock (Sync) SetRunningLocked(empId, value);
    }

    private static void Chat(string empId, string role, string text) =>
        Broadcast(new { type = "chat", empId, role, text });

    private static void Error(string text) => Broadcast(new { type = "error", text });

    private static string ShortLabel(string id) =>
        id.Length > 12 ? id[..12] : id;

    private static async Task SendLoopAsync(WebSocketClient client, CancellationToken ct)
    {
        try
        {
            await foreach (var json in client.Out.Reader.ReadAllAsync(ct))
            {
                if (client.Socket.State != WebSocketState.Open) break;
                var bytes = Encoding.UTF8.GetBytes(json);
                await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        catch { /* client gone — receive loop cleans up */ }
    }
}
