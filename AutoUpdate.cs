using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AIOrchestrator;

// Automatic updates. At startup the app asks GitHub for the latest release; when it is
// newer than the running version (and auto-update is enabled) it downloads the platform
// archive, extracts it to %TEMP%, strips the protected files, spawns the NEW executable
// from the temp extract as an updater process ("--apply-update") and exits. The updater —
// a different process, never the running exe — waits for the old process to terminate,
// copies the changed files (the executable last, via a .old rename for rollback) and
// restarts the app with the original command line.
//
// Why the two-process swap: a running image cannot be overwritten on Windows, and at
// startup the app is already the process mapping its own exe, so the executable can
// never replace itself — the swap must be done by a process that is not agent(.exe).
// Verified empirically on Windows: a child process does NOT keep the parent's exe
// locked after the parent exits; the lock lasts only as long as the owning process.
public static class AutoUpdate
{
    private const string Repo = "https://github.com/Graphene-Lab/AgentBridge";
    private const string LatestUrl = Repo + "/releases/latest";
    private const string TempRootName = "agentbridge-update";
    private const string RestartArgsFile = "restart_args.json";
    private const string StateFile = "autoupdate.json";

    /// <summary>Whether the startup update check runs (default true).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Raised on update progress/state changes (the TUI shows them in the status bar).</summary>
    public static event Action<string>? OnStatus;

    // The toggle lives in the OS app-data folder (never in the app folder), so updates
    // cannot touch it — same tier as setup.json (see RELEASING.md, storage tiers).
    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent", StateFile);

    private static string TempRoot => Path.Combine(Path.GetTempPath(), TempRootName);

    // The release archives exist only for the RIDs built by release.yml.
    private static string? Rid()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64) return "win-x64";
        if (OperatingSystem.IsLinux()) return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        if (OperatingSystem.IsMacOS()) return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        return null; // e.g. win-arm64: no archive is built for this platform
    }

    // A published install runs from the apphost (single-file). Under `dotnet run` the
    // entry assembly is agent.dll and a swap would target dotnet itself — never update.
    private static bool IsPublished => !string.Equals(
        Path.GetExtension(Assembly.GetEntryAssembly()?.Location), ".dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies CLI + persisted state. Returns true when a persisted state file was applied.</summary>
    public static bool LoadState(bool noUpdateFlag)
    {
        if (noUpdateFlag) { Enabled = false; return true; }
        try
        {
            if (File.Exists(StatePath))
            {
                Enabled = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath))?.Enabled ?? true;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Persists the toggle (set by the TUI File → Auto-Update menu).</summary>
    public static void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new State { Enabled = Enabled }));
        }
        catch { }
    }

    public static void Toggle()
    {
        Enabled = !Enabled;
        SaveState();
        Status(Enabled ? "Auto-update enabled" : "Auto-update disabled");
    }

    /// <summary>Startup cleanup: the previous update's .old rollback copy is removed (this
    /// start proves the new executable works) together with any stale temp update area
    /// (fails while an updater is still running — that is fine).</summary>
    public static void CleanupOnStartup()
    {
        try
        {
            if (Environment.ProcessPath is { } p && File.Exists(p + ".old")) File.Delete(p + ".old");
            if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, true);
        }
        catch { }
    }

    /// <summary>Startup check: latest GitHub release newer than the running version → update.</summary>
    public static async Task CheckAndApplyAsync()
    {
        if (!Enabled || !IsPublished || Rid() is not { } rid) return;
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            var tag = await GetLatestTagAsync();
            if (tag is null || !Version.TryParse(tag.TrimStart('v'), out var latest) || latest <= current) return;

            Log.LogStep($"AutoUpdate: {current} → {tag}, downloading", monitor: true);
            Status($"Update {tag} available — applying, the app will restart");
            await ApplyAsync(rid, tag);
        }
        catch (Exception ex)
        {
            Log.LogStep($"AutoUpdate: check failed — {ex.Message}");
        }
    }

    // Redirect trick: /releases/latest answers with a redirect whose Location header
    // carries the tag. No API call, so the unauthenticated rate limit is never an issue.
    private static async Task<string?> GetLatestTagAsync()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        using var resp = await http.GetAsync(LatestUrl);
        if ((int)resp.StatusCode is < 300 or >= 400) return null;
        var loc = resp.Headers.Location?.OriginalString;
        var tag = string.IsNullOrEmpty(loc) ? null : Path.GetFileName(loc.TrimEnd('/'));
        return tag?.StartsWith('v') == true ? tag : null;
    }

    private static async Task ApplyAsync(string rid, string tag)
    {
        var url = $"{Repo}/releases/download/{tag}/agentbridge-{rid}.tar.gz";
        var archive = Path.Combine(TempRoot, $"agentbridge-{rid}.tar.gz");
        var extract = Path.Combine(TempRoot, "extract");
        Directory.CreateDirectory(TempRoot);

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
        using (var src = await http.GetStreamAsync(url))
        using (var dst = File.Create(archive))
            await src.CopyToAsync(dst);

        if (Directory.Exists(extract)) Directory.Delete(extract, true);
        Directory.CreateDirectory(extract);
        using (var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
            TarFile.ExtractToDirectory(gz, extract, overwriteFiles: true);

        // Protected (see RELEASING.md): the user's server config and provider list are
        // never overwritten. Everything else in the archive is distribution content.
        File.Delete(Path.Combine(extract, "appsettings.json"));
        File.Delete(Path.Combine(extract, "providers.json"));

        // The updater restarts with the original command line, minus --no-update.
        File.WriteAllText(Path.Combine(extract, RestartArgsFile), JsonSerializer.Serialize(
            Environment.GetCommandLineArgs().Skip(1).Where(a => a != "--no-update").ToArray()));

        var target = Path.GetDirectoryName(Environment.ProcessPath)!;
        var updater = new ProcessStartInfo(Path.Combine(extract, Path.GetFileName(Environment.ProcessPath)!)) { UseShellExecute = false };
        updater.ArgumentList.Add("--apply-update");
        updater.ArgumentList.Add(target);
        updater.ArgumentList.Add(extract);
        updater.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(updater);

        Log.LogStep($"AutoUpdate: updater spawned, exiting to apply {tag}", monitor: true);
        Environment.Exit(0);
    }

    /// <summary>Updater mode (--apply-update): swap the files once the old process is gone, restart.</summary>
    public static int RunUpdater(string[] args)
    {
        // args: --apply-update <targetDir> <extractDir> <oldPid>
        if (args.Length < 4 || !int.TryParse(args[3], out var oldPid)) return 1;
        var target = args[1];
        var extract = args[2];

        // The old process's exe stays locked until it fully terminates — wait for it.
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (IsAlive(oldPid) && DateTime.UtcNow < deadline) Thread.Sleep(500);
        if (IsAlive(oldPid))
        {
            Log.LogStep("AutoUpdate: old process did not exit — update aborted");
            return 1;
        }

        // Changed files first, the executable last (with a .old rollback copy).
        var exeName = Path.GetFileName(Environment.ProcessPath)!;
        foreach (var src in Directory.EnumerateFiles(extract, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(extract, src);
            if (rel == RestartArgsFile || string.Equals(rel, exeName, StringComparison.OrdinalIgnoreCase)) continue;
            var dst = Path.Combine(target, rel);
            if (!File.Exists(dst) || !SameContent(src, dst))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
            }
        }
        var exeDst = Path.Combine(target, exeName);
        var exeOld = exeDst + ".old";
        File.Delete(exeOld);
        File.Move(exeDst, exeOld);
        File.Copy(Path.Combine(extract, exeName), exeDst);

        // Restart with the original command line; the new start cleans up temp and .old.
        var restart = Array.Empty<string>();
        try { restart = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(extract, RestartArgsFile))) ?? restart; } catch { }
        var psi = new ProcessStartInfo(exeDst) { UseShellExecute = false, WorkingDirectory = target };
        foreach (var a in restart) psi.ArgumentList.Add(a);
        Process.Start(psi);

        // Best-effort: the updater's own exe is inside the extract, so on Windows the
        // deletion of the temp area only succeeds after this process exits.
        try { Directory.Delete(Path.GetDirectoryName(extract)!, true); } catch { }
        return 0;
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var ha = SHA256.Create();
        using var hb = SHA256.Create();
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return ha.ComputeHash(sa).SequenceEqual(hb.ComputeHash(sb));
    }

    private static void Status(string message) => OnStatus?.Invoke(message);

    private sealed class State
    {
        public bool Enabled { get; set; } = true;
    }
}
