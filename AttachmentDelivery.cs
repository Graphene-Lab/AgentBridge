using System.Text.Json;
using AIOrchestrator;

namespace AgentBridge;

/// <summary>
/// Host side of the agent-delivered files (the done method's "attachments", MCP embedded-resource
/// shape): turns ONE attachment into a readable local file.
///
/// The workspace copy is preferred — the server runs on this same machine, and a file above the
/// inline limit (a 30-35 minute podcast MP3) is delivered as a local reference with no payload, so
/// its ~33%-inflated base64 never travels through the chat stream. The inlined payload is the
/// fallback (a sandbox this host cannot resolve, or a resource without a uri).
/// </summary>
public static class AttachmentDelivery
{
    /// <summary>Raw-overload for the streaming client, which works on the SSE chunk's JSON.</summary>
    public static Task<(string? Path, bool Temporary)> ResolveAsync(JsonElement attachment, CancellationToken ct = default) =>
        attachment.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object
            ? ResolveAsync(
                resource.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String ? uri.GetString() : null,
                resource.TryGetProperty("blob", out var blob) && blob.ValueKind == JsonValueKind.String ? blob.GetString() : null,
                attachment.TryGetProperty("name", out var name) ? name.GetString() : null, ct)
            : Task.FromResult<(string?, bool)>((null, false));

    /// <summary>Typed overload for hosts holding the result object (e.g. the Telegram bridge).</summary>
    public static Task<(string? Path, bool Temporary)> ResolveAsync(AgentAttachment attachment, CancellationToken ct = default) =>
        ResolveAsync(attachment.Resource?.Uri, attachment.Resource?.Blob, attachment.Name, ct);

    /// <summary>
    /// Materializes the attachment as a local file for the caller to copy/upload: the workspace
    /// file behind <paramref name="uri"/> when it exists on this machine, otherwise a temporary file
    /// holding the decoded <paramref name="blob"/>. Returns the readable path plus whether it is
    /// temporary (the caller must delete it), or <c>(null, false)</c> when neither source yields one.
    /// </summary>
    private static async Task<(string? Path, bool Temporary)> ResolveAsync(string? uri, string? blob, string? name, CancellationToken ct)
    {
        if (uri != null && SandboxPath.TryResolve(uri, out var hostPath) && File.Exists(hostPath))
            return (hostPath, false);

        if (blob != null)
        {
            var temp = Path.Combine(Path.GetTempPath(),
                "agent-attachment-" + Guid.NewGuid().ToString("N")[..8] + "-" + SafeName(name));
            await File.WriteAllBytesAsync(temp, Convert.FromBase64String(blob), ct);
            return (temp, true);
        }
        return (null, false);
    }

    /// <summary>File-system-safe leaf name for a temporary delivery file.</summary>
    private static string SafeName(string? name) => string.Concat(
        (string.IsNullOrWhiteSpace(name) ? "attachment" : name!).Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
}
