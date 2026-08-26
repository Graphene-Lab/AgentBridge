using System.IO.Compression;
using AIOrchestrator;

// The Giraffe AI web client is NOT part of this repository: it is installed at startup
// next to the executable (AppContext.BaseDirectory, never the source tree) from the
// latest GitHub release of the GiraffeAI repo, and kept at that latest version. The
// release carries the client zip giraffeai-<N>.zip under an incremental tag vN — the
// version indicator the plain repository archive zip cannot provide. The TUI /web
// command launches the installed client (see Tui.cs LaunchWebClientAsync).
/// <summary>
/// Installs and keeps up to date the Giraffe AI web client next to the executable,
/// from the GiraffeAI GitHub releases (see the class comment above).
/// </summary>
public static class WebClientUpdater
{
    /// <summary>The GiraffeAI repository the client is downloaded from.</summary>
    public const string Repo = "https://github.com/Graphene-Lab/GiraffeAI";
    private const string LatestUrl = Repo + "/releases/latest";
    private const string DirName = "GiraffeAIWebClient";
    private const string VersionFile = "version.txt";

    // Serializes install/update: EnsureAsync may be entered concurrently by the startup
    // task and the TUI /web command; a second caller simply re-checks and no-ops.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>The client install directory (next to the executable, never the project).</summary>
    public static string ClientDir => Path.Combine(AppContext.BaseDirectory, DirName);

    private static string VersionPath => Path.Combine(ClientDir, VersionFile);

    /// <summary>Whether a usable client is installed (index.html present).</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(ClientDir, "index.html"));

    /// <summary>The startup install/update task — kicked off by Program.cs, joined by /web.</summary>
    public static readonly Task Startup = RunStartupAsync();

    private static async Task RunStartupAsync()
    {
        try { await EnsureAsync(); }
        catch (Exception ex) { Log.LogStep($"WebClient: startup check failed — {ex.Message}"); }
    }

    /// <summary>Installs the client when missing and applies a newer release when one exists. Best-effort.</summary>
    public static async Task EnsureAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var latest = await GetLatestVersionAsync();
            if (latest is not { } version) return; // offline or no release yet — keep what we have
            if (!IsInstalled || ReadInstalledVersion() is not { } installed || version > installed)
                await InstallAsync(version);
        }
        finally { Gate.Release(); }
    }

    // Redirect trick (same as AutoUpdate): /releases/latest answers with a redirect whose
    // Location header carries the tag. No API call, so the unauthenticated rate limit is
    // never an issue. Tags are plain incrementals (v1, v2, ...) — see GiraffeAI release.yml.
    private static async Task<int?> GetLatestVersionAsync()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using var resp = await http.GetAsync(LatestUrl);
        if ((int)resp.StatusCode is < 300 or >= 400) return null;
        var loc = resp.Headers.Location?.OriginalString;
        var tag = string.IsNullOrEmpty(loc) ? null : Path.GetFileName(loc.TrimEnd('/'));
        return tag is { } t && t.StartsWith('v') && int.TryParse(t.AsSpan(1), out var v) ? v : null;
    }

    private static int? ReadInstalledVersion()
    {
        try { return File.Exists(VersionPath) && int.TryParse(File.ReadAllText(VersionPath).Trim(), out var v) ? v : null; }
        catch { return null; }
    }

    private static void WriteInstalledVersion(int version)
    {
        try { File.WriteAllText(VersionPath, version.ToString()); }
        catch { }
    }

    private static async Task InstallAsync(int version)
    {
        var url = $"{Repo}/releases/download/v{version}/giraffeai-{version}.zip";
        var tmpZip = Path.Combine(Path.GetTempPath(), $"giraffeai_{Guid.NewGuid():N}.zip");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"giraffeai_{Guid.NewGuid():N}");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var src = await http.GetStreamAsync(url))
            using (var dst = File.Create(tmpZip))
                await src.CopyToAsync(dst);

            // The release zip is FLAT (no root wrapper folder): the client files land
            // directly in the install directory.
            ZipFile.ExtractToDirectory(tmpZip, tmpDir);
            if (!File.Exists(Path.Combine(tmpDir, "index.html")))
                throw new InvalidOperationException("the release zip did not contain index.html");

            // Swap in the new client: extract into a staging dir, then replace the whole
            // install directory (no stale-file cleanup to maintain).
            var target = ClientDir;
            var staging = target + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.Move(tmpDir, staging);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            WriteInstalledVersion(version);
            Log.LogStep($"WebClient: installed v{version} at {target}");
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }
    }
}
