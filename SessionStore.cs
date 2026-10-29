using AIOrchestrator;
using System.Collections.Concurrent;

// ═══════════════════════════════════════════════════════════════════════
//  SessionStore — multi-turn chat sessions for AgentBridge
//
//  A session owns one AgentHarness (and therefore one LLMUtility with its
//  accumulated conversation history). This is what makes the "switch the LLM
//  currently in use" operation (POST /v1/control llm_provider) meaningful:
//  history survives the switch (AgentHarness.SwitchProvider) and the
//  context-window check protects the target provider from overflow.
//
//  Concurrency: each session serializes access to its orchestrator through
//  <see cref="ActiveSession.Gate"/> (LLMUtility's history lists are not
//  thread-safe). Idle sessions are disposed by a periodic cleanup timer.
//  Sessions are opt-in: a chat request without session_id keeps the historical
//  stateless per-request behaviour (fresh orchestrator per request).
// ═══════════════════════════════════════════════════════════════════════

/// <summary>A live chat session: orchestrator (history) + feature flags + serialization gate.</summary>
public sealed class ActiveSession : IDisposable
{
    /// <summary>Unique session id returned to the client (e.g. "sess-&lt;guid&gt;").</summary>
    public string Id { get; }

    /// <summary>The orchestrator backing this session (conversation history lives here).</summary>
    public AgentHarness Orchestrator { get; }

    /// <summary>Per-session feature flags (e.g. "voice", "tts") — the extensible slot for future features.</summary>
    public Dictionary<string, bool> Features { get; } = new();

    /// <summary>Serializes orchestrator access (one chat at a time per session).</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Last activity timestamp (UTC); idle sessions are cleaned up.</summary>
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;

    /// <summary>Tokens this session has spent, summed from every finished turn (the orchestrator's
    /// calls plus the subagent conversations they started). A provider that reports no usage
    /// leaves <see cref="AgentUsage.Reported"/> false, so the session knows it has no measurement
    /// rather than a zero. The host surfaces it on <c>GET /v1/control</c> and the TUI renders it.</summary>
    public AgentUsage Usage { get; private set; } = new();

    /// <summary>Adds one finished turn's usage to the session total (no-op when the provider
    /// reported nothing).</summary>
    public void AddTurnUsage(AgentUsage? turn)
    {
        if (turn is not { Reported: true }) return;
        Usage = new AgentUsage
        {
            PromptTokens = Usage.PromptTokens + turn.PromptTokens,
            CompletionTokens = Usage.CompletionTokens + turn.CompletionTokens,
            TotalTokens = Usage.TotalTokens + turn.TotalTokens,
            CachedTokens = Usage.CachedTokens + turn.CachedTokens,
            Calls = Usage.Calls + turn.Calls,
        };
    }

    /// <summary>Creates a session wrapping the given orchestrator.</summary>
    public ActiveSession(string id, AgentHarness orchestrator)
    {
        Id = id;
        Orchestrator = orchestrator;
    }

    /// <summary>Releases the gate and the orchestrator.</summary>
    public void Dispose()
    {
        Gate.Dispose();
        try { Orchestrator.Dispose(); } catch { }
    }
}

/// <summary>In-memory store of <see cref="ActiveSession"/> instances.</summary>
public static class SessionStore
{
    private static readonly ConcurrentDictionary<string, ActiveSession> Sessions = new();

    // Lifecycle events consumed by the OfficeManager hub (OfficeBridge): every session — whatever
    // medium created it (TUI chat, /v1/chat/completions, SIP, Telegram, OfficeManager chat) — is
    // represented by an employee in the office, so the visual protocol needs the same hooks.
    // Events fire synchronously on the creating/removing thread (inside Create/TryRemove).
    /// <summary>Fired after a new session is created and stored.</summary>
    public static event Action<ActiveSession>? SessionCreated;
    /// <summary>Fired after a session was disposed and removed (idle cleanup or explicit removal).</summary>
    public static event Action<ActiveSession>? SessionRemoved;

    // Periodic cleanup of idle sessions (the timer keeps the process alive — fine for a server).
    private static readonly System.Threading.Timer CleanupTimer =
        new(_ => Cleanup(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

    /// <summary>Idle timeout after which a session is disposed (default: the suggested
    /// conversation timeout from AIOrchestrator, 1 hour — the same value the AIOffice voice
    /// panel uses to expire its local conversation).</summary>
    public static TimeSpan IdleTimeout { get; set; } = AgentHarness.SuggestedConversationTimeout;

    /// <summary>Returns the session and bumps its idle timestamp; null when unknown.</summary>
    public static ActiveSession? Get(string id)
    {
        if (!Sessions.TryGetValue(id, out var session)) return null;
        session.LastUsed = DateTime.UtcNow;
        return session;
    }

    /// <summary>Creates and stores a new session backed by a fresh orchestrator.</summary>
    public static ActiveSession Create(string provider, bool anonymize)
    {
        var id = $"sess-{Guid.NewGuid():N}";
        // Responsive delivery of long-running tool results (see AgentHarness.AsyncTaskDeliveryEnabled):
        // a session conversation survives across requests, so a background task started by the agent can
        // deliver its completion event on the next chat request. Stateless (session-less) requests keep the
        // default synchronous behavior — their orchestrator dies with the request.
        // IsSessionBacked: this is a long-lived conversation — the end-of-conversation memory recording
        // fires at Dispose (idle timeout / explicit removal), exactly once.
        var session = new ActiveSession(id, new AgentHarness(provider, anonymize)
        {
            AsyncTaskDeliveryEnabled = true,
            IsSessionBacked = true
        });
        // The orchestrator carries the session id in its InstanceId so every AgentProgress/GlobalProgress
        // event of this conversation is correlated to the session (OfficeManager employee ↔ session).
        session.Orchestrator.InstanceId = id;
        Sessions[id] = session;
        try { SessionCreated?.Invoke(session); } catch { }
        return session;
    }

    /// <summary>Disposes and removes a session; returns false when it did not exist.</summary>
    public static bool TryRemove(string id)
    {
        if (!Sessions.TryRemove(id, out var session)) return false;
        try { session.Dispose(); } catch { }
        try { SessionRemoved?.Invoke(session); } catch { }
        return true;
    }

    /// <summary>Number of live sessions.</summary>
    public static int Count => Sessions.Count;

    /// <summary>Ids of the live sessions (snapshot).</summary>
    public static IReadOnlyList<string> Ids => Sessions.Keys.ToArray();

    /// <summary>
    /// Closes one session the "graceful" way: wait (bounded) for its gate, then remove it so
    /// <see cref="ActiveSession.Dispose"/> runs and the end-of-conversation memory recording
    /// fires. A session whose gate never frees is left alone — disposing an orchestrator under
    /// a running agent would corrupt the run — and reported as skipped.
    /// </summary>
    /// <returns>True when the session was closed, false when it was busy or already gone.</returns>
    public static async Task<bool> CloseAsync(string id, TimeSpan gateTimeout, CancellationToken ct = default)
    {
        if (!Sessions.TryGetValue(id, out var session)) return false;

        var entered = false;
        try
        {
            entered = await session.Gate.WaitAsync(gateTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }

        if (!entered)
        {
            Log.LogStep($"Session close skipped, still busy after {gateTimeout.TotalSeconds:0}s: {id}");
            return false;
        }

        try
        {
            // TryRemove disposes the session (and its gate) — do not release a disposed gate.
            if (!Sessions.TryRemove(new KeyValuePair<string, ActiveSession>(id, session))) return false;
            entered = false;
            try { session.Dispose(); } catch { }
            try { SessionRemoved?.Invoke(session); } catch { }
            Log.LogStep($"Session closed: {id} (memory recorded)", monitor: true);
            return true;
        }
        finally
        {
            if (entered) session.Gate.Release();
        }
    }

    /// <summary>
    /// Closes every live session so each one records its memory before the process goes away.
    /// This is what makes an interactive exit (TUI Ctrl+C, /exit) as lossless as an idle
    /// timeout: without it a session that is simply dropped at shutdown never reaches
    /// <see cref="AgentHarness.Dispose"/> and remembers nothing.
    /// </summary>
    /// <returns>The number of sessions closed and the ones skipped because a run was using them.</returns>
    public static async Task<(int Closed, int Skipped)> CloseAllAsync(TimeSpan gateTimeout, CancellationToken ct = default)
    {
        var ids = Sessions.Keys.ToArray();
        int closed = 0, skipped = 0;
        Log.LogStep($"Closing {ids.Length} live session(s) to record memory (timeout {gateTimeout.TotalSeconds:0}s)", monitor: true);

        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested)
            {
                Log.LogStep($"Session close cancelled with {ids.Length - closed - skipped} session(s) still open");
                break;
            }

            try
            {
                if (await CloseAsync(id, gateTimeout, ct).ConfigureAwait(false)) closed++;
                else if (Sessions.ContainsKey(id)) skipped++;
            }
            catch (OperationCanceledException)
            {
                Log.LogStep($"Session close cancelled while closing {id}");
                break;
            }
            catch (Exception ex)
            {
                skipped++;
                Log.LogStep($"Session close failed for {id}: {ex.Message}");
            }
        }

        Log.LogStep($"Session close finished: {closed} closed, {skipped} skipped", monitor: true);
        return (closed, skipped);
    }

    // Disposes sessions idle for longer than IdleTimeout.
    private static void Cleanup()
    {
        var cutoff = DateTime.UtcNow - IdleTimeout;
        foreach (var (id, session) in Sessions)
        {
            if (session.LastUsed < cutoff)
                TryRemove(id);
        }
    }
}
