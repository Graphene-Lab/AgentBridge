using AIOrchestrator;
using AgentBridge.Resources;

// ═══════════════════════════════════════════════════════════════════════
//  AgentResultDisplay — single source of truth for turning a locale-neutral
//  AgentResult into a human-readable, localized phrase.
//
//  Every chat surface in this host (the HTTP /v1/chat/completions path, the
//  MCP endpoint, the OfficeManager employee bubbles) renders a result with
//  the SAME formula:
//      result.Message ?? AgentResultDisplay.ResultText(result) ?? Dictionary.NoOutputGenerated
//  so a failure never collapses to a bare "error" word: a rejected key, a
//  missing model, an exhausted quota, a timeout or a network failure each
//  say so (and where to fix it), and max-iterations / empty turns get their
//  own phrase. The engine carries only locale-neutral codes (AgentResultCode)
//  plus a machine FailureReason; the wording lives here, in the localized
//  Resources/Dictionary.
//
//  Before this helper the OfficeManager employee used its own formula and
//  showed just "error" for every system-level failure, leaving the user with
//  nothing to act on (issue #32).
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Maps a locale-neutral <see cref="AgentResult"/> to a localized display phrase,
/// shared by every chat surface so the wording can never drift between them.</summary>
public static class AgentResultDisplay
{
    /// <summary>The localized phrase for a result's outcome code, or null when the result
    /// carries real LLM text (the caller shows <c>result.Message</c> first) or completed
    /// normally with nothing to say. Never returns a bare "error".</summary>
    public static string? ResultText(AgentResult result) => result.Code switch
    {
        AgentResultCode.MaxIterationsReached => string.Format(Dictionary.MaxIterationsReached, result.Iterations),
        AgentResultCode.NoLlmResponse => LlmFailureText(result.FailureReason),
        AgentResultCode.NoMessage => Dictionary.Done,
        _ => null,
    };

    /// <summary>Maps the engine's machine <c>FailureReason</c> (carried on a no-answer result)
    /// to a specific, actionable localized message, so a failed provider call (bad key, model
    /// not found, quota, timeout, network) no longer looks like a plain "no response". Falls
    /// back to the generic no-response text for an empty turn or an unknown reason.</summary>
    public static string LlmFailureText(string? reason) => reason switch
    {
        "no_key" => Dictionary.LlmNoKey,
        "http_401" or "http_403" => Dictionary.LlmAuthError,
        "http_404" => Dictionary.LlmModelNotFound,
        "http_429" => Dictionary.LlmQuotaExceeded,
        "timeout" => Dictionary.LlmTimeout,
        "network" => Dictionary.LlmNetworkError,
        _ => Dictionary.NoLlmResponse,
    };
}
