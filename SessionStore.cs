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
