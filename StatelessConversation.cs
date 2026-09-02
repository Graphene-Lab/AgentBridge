using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// ═══════════════════════════════════════════════════════════════════════
//  StatelessConversation — dynamic-hash correlation for clients without session_id
//
//  `session_id` is a proprietary ADDITIVE extension of /v1/chat/completions
//  (see docs/API.md); a third-party OpenAI client may never send it, so its
//  "chat" would otherwise be a sequence of stateless one-shot agent instances
//  — one OfficeManager employee per message — instead of ONE conversation.
//  This correlator solves exactly that with a minimal device:
//
//    • after every exchange the FULL transcript (role+content, including the
//      assistant reply) is hashed (SHA-256) into a hash → session dictionary;
//    • the next request's transcript minus its last message (the "previous
//      part" the client resends) is hashed the same way: a hit routes the
//      request to that conversation, so the chat stays ONE session — and
//      therefore ONE persistent employee in OfficeManager.
//
//  The hash is "dynamic" by construction: it changes at every output message,
//  so the dictionary entry always refers to the latest transcript. Entries
//  are bounded (hard cap + idle TTL) and dropped when their session is
//  disposed. Collisions are practically impossible (full SHA-256 of the
//  transcript, which includes the LLM replies).
//
//  Lifecycle of a stateless chat without session_id:
//    1. first message (no assistant history): processed one-shot; the full
//       transcript hash is marked PENDING (no session exists yet);
//    2. second message resending the transcript: PENDING hit → a real session
//       is created and SEEDED with the transcript the client resends (the
//       earlier turns are preserved for the LLM) → one persistent employee;
//    3. following messages: exact transcript match → same session.
//
//  LIMIT (inherent, documented): correlation needs the client to RESEND the
//  accumulated transcript. A client that sends only the newest message has no
//  "previous part" to hash — those requests stay one-shot (short-lived
//  employees). This is BY DESIGN: stateless instances are also legitimate
//  small tasks created by agents/tools, and OfficeManager shows them as
//  granular monitoring — never hidden.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Content-based conversation correlation for stateless chat requests (see the
/// class comment above).</summary>
public static class StatelessConversation
{
    private const int MaxEntries = 2000;

    // transcript-hash → (sessionId | "" = pending, stamp). "" marks a transcript we have seen
    // but that has no session yet (the first message of a chat was processed one-shot).
    private static readonly ConcurrentDictionary<string, (string SessionId, DateTime Stamp)> ByTranscript = new();
    // sessionId → the transcript hashes recorded for it (removed when the session is disposed).
    private static readonly ConcurrentDictionary<string, HashSet<string>> SessionHashes = new();
    private static readonly object Sync = new();

    /// <summary>Wires the cleanup hook. Called once at startup (Program.cs).</summary>
    public static void Init() => SessionStore.SessionRemoved += OnSessionRemoved;

    private static void OnSessionRemoved(ActiveSession session)
    {
        lock (Sync)
        {
            if (!SessionHashes.TryRemove(session.Id, out var hashes)) return;
            foreach (var h in hashes) ByTranscript.TryRemove(h, out _);
        }
    }

    /// <summary>The hash of the transcript that PRECEDES the last user message — the "previous
    /// part" a continuing client resends. Null when there is nothing to correlate (no history).</summary>
    public static string? ContinuationKey(IReadOnlyList<RequestMessage>? messages)
    {
        if (messages is not { Count: > 1 }) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < messages.Count - 1; i++)
            AppendMessage(sb, messages[i]);
        return sb.Length == 0 ? null : Hash(sb.ToString());
    }

    /// <summary>The rolling hash of the FULL transcript INCLUDING the assistant reply — the
    /// "output" hash, recorded after every run so the next request correlates.</summary>
    public static string FullKey(IReadOnlyList<RequestMessage>? messages, string assistantText)
    {
        var sb = new StringBuilder();
        if (messages != null)
            foreach (var m in messages) AppendMessage(sb, m);
        if (!string.IsNullOrWhiteSpace(assistantText))
            sb.Append("assistant:").Append(assistantText).Append('\n');
        return Hash(sb.ToString());
    }

    /// <summary>Marks a transcript hash as seen but session-less (a chat's first message was
    /// processed one-shot). The next message that resends it starts the real session.</summary>
    public static void MarkPending(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return;
        lock (Sync)
        {
            ByTranscript[hash] = ("", DateTime.UtcNow);
            TrimLocked();
        }
    }

    /// <summary>Resolves a continuation hash: the session id, "" when the transcript is known
    /// but still pending (start a seeded session), or null when the transcript is unknown.</summary>
    public static string? Lookup(string hash)
    {
        if (!ByTranscript.TryGetValue(hash, out var entry)) return null;
        if (entry.SessionId.Length > 0 && SessionStore.Get(entry.SessionId) == null)
        {
            ByTranscript.TryRemove(hash, out _);        // session gone — treat as unknown
            return null;
        }
        return entry.SessionId;                          // "" = pending
    }

    /// <summary>Associates the rolling transcript hash with its session (after a run).</summary>
    public static void Record(string sessionId, string hash)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(hash)) return;
        lock (Sync)
        {
            ByTranscript[hash] = (sessionId, DateTime.UtcNow);
            var hashes = SessionHashes.GetOrAdd(sessionId, _ => new HashSet<string>());
            hashes.Add(hash);
            TrimLocked();
        }
    }

    private static void AppendMessage(StringBuilder sb, RequestMessage m)
    {
        sb.Append(m.Role).Append(':').Append(ExtractTextContent(m.Content)).Append('\n');
    }

    // Mirrors Program.cs ExtractTextContent (plain string | JSON string | text/image array).
    private static string ExtractTextContent(object? content)
    {
        if (content is string text) return text;
        if (content is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.String) return json.GetString() ?? "";
            if (json.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var item in json.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type))
                    {
                        if (type.GetString() == "text" && item.TryGetProperty("text", out var txt))
                            parts.Add(txt.GetString() ?? "");
                        else if (type.GetString() == "image_url")
                            parts.Add("[Image attached]");
                    }
                }
                return string.Join("\n", parts);
            }
        }
        return content?.ToString() ?? "";
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static void TrimLocked()
    {
        if (ByTranscript.Count <= MaxEntries) return;
        var cutoff = DateTime.UtcNow - SessionStore.IdleTimeout;
        foreach (var kv in ByTranscript)
            if (kv.Value.Stamp < cutoff) ByTranscript.TryRemove(kv.Key, out _);
        if (ByTranscript.Count > MaxEntries)                  // defensive: drop the oldest entries
            foreach (var kv in ByTranscript.OrderBy(k => k.Value.Stamp).Take(ByTranscript.Count - MaxEntries))
                ByTranscript.TryRemove(kv.Key, out _);
    }
}
