using System.IO.Compression;
using AIOrchestrator;

// The Giraffe AI web client is NOT part of this repository: it is installed at startup
// next to the executable (AppContext.BaseDirectory, never the source tree) from the
// latest GitHub release of the GiraffeAI repo, and kept at that latest version. The
// release carries the client zip giraffeai-<N>.zip under an incremental tag vN — the
// version indicator the plain repository archive zip cannot provide. The TUI /web
// command launches the installed client (see Tui.cs LaunchWebClientAsync).
//
// Update policy: the FIRST install is unconditional (the web GUI must be available even
// in --no-update mode — the flag governs binary/plugin updates, not first setup); the
// UPDATE check follows the app's auto-update toggle (--no-update / TUI File → Auto-Update).
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

    /// <summary>Whether a usable client is installed (index.html present).</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(ClientDir, "index.html"));

    /// <summary>The installed client release number (null when unknown/missing).</summary>
    public static int? InstalledVersion => ReadInstalledVersion();

    /// <summary>Outcome of the most recent install/update (e.g. "updated to v3"); shown by the TUI /web note.</summary>
    public static string? LastStatus { get; private set; }

    /// <summary>The startup install/update task — kicked off by Program.cs, joined by /web.</summary>
    public static readonly Task Startup = RunStartupAsync();

    private static string VersionPath => Path.Combine(ClientDir, VersionFile);

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
            LastStatus = null;
            var latest = await GetLatestVersionAsync();
            if (latest is not { } version) return; // offline or no release yet — keep what we have
            if (!IsInstalled)
            {
                await InstallAsync(version);
                return;
            }
            // Updates follow the app's auto-update toggle (--no-update / TUI File → Auto-Update).
            if (AutoUpdate.Enabled && (ReadInstalledVersion() is not { } installed || version > installed))
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
        var target = ClientDir;
        var staging = target + ".new";
        var old = target + ".old";
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var src = await http.GetStreamAsync(url))
            using (var dst = File.Create(tmpZip))
                await src.CopyToAsync(dst);

            // The release zip is FLAT (no root wrapper folder): the client files land
            // directly in the install directory. Extract straight into the staging folder
            // on the target volume so the renames below never cross a drive boundary.
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            ZipFile.ExtractToDirectory(tmpZip, staging);
            if (!File.Exists(Path.Combine(staging, "index.html")))
                throw new InvalidOperationException("the release zip did not contain index.html");

            // Swap through renames (atomic on the same volume): the current install moves
            // aside to .old, the new one takes its place, the old one is deleted best-effort
            // (cleaned at the next start if still locked). The client directory is never
            // absent; a failed swap rolls back to the previous install.
            if (Directory.Exists(old)) Directory.Delete(old, true);
            if (Directory.Exists(target)) Directory.Move(target, old);
            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                if (!Directory.Exists(target) && Directory.Exists(old)) Directory.Move(old, target);
                throw;
            }
            WriteInstalledVersion(version);
            LastStatus = $"updated to v{version}";
            Log.LogStep($"WebClient: installed v{version} at {target}");
            try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch { }
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }
}
