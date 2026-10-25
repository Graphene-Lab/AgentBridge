using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Resources;
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

    /// <summary>Last progress text ("download 42%"), empty when no transfer is running.
    /// The TUI shows it in the "update already in progress" answer so the user can tell a
    /// slow download from a stuck one.</summary>
    public static string Progress { get; private set; } = "";

    // Serializes the whole update pipeline — the startup check (Program.cs), the 30-minute
    // retry (ScheduleRetryIn) and the TUI /update command all funnel here. Without it two
    // flows could download in parallel and collide on the same temp archive: the second
    // File.Create failed with a sharing violation ("...being used by another process") that
    // surfaced as a bogus "update failed" while the first download kept running unseen.
    // Same single-flight pattern as WebClientUpdater.Gate.
    private static readonly SemaphoreSlim UpdateGate = new(1, 1);

    // The toggle lives in the OS app-data folder (never in the app folder), so updates
    // cannot touch it — same tier as setup.json (see docs-dev/RELEASING.md, storage tiers).
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

    // A published install runs from the apphost (agent.exe on Windows, agent on
    // POSIX); `dotnet run` and `dotnet agent.dll` run under the dotnet host instead.
    // The PROCESS executable is the reliable test — checking the ENTRY assembly would
    // treat every apphost-published (non single-file) install as `dotnet run`, because
    // the managed entry assembly is agent.dll even when launched through agent.exe
    // (that is exactly how the release archives are laid out).
    private static bool IsPublished
    {
        get
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(processPath)) return false;
            return !string.Equals(
                Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        }
    }

    // Debug configuration (the SDK bakes AssemblyConfiguration into the entry assembly):
    // auto-update must never run on a Debug build — not even a published one. Combined
    // with IsPublished (which already blocks `dotnet run`), a Debug apphost is excluded too.
    private static bool IsDebugBuild => string.Equals(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
        "Debug", StringComparison.OrdinalIgnoreCase);

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
    /// (fails while an updater is still running — that is fine). Partially downloaded
    /// archives are KEPT: a 1.5 GB transfer on a slow link often outlives one TUI session,
    /// so the next start resumes it instead of fetching it again from zero.</summary>
    public static void CleanupOnStartup()
    {
        try
        {
            if (Environment.ProcessPath is { } p && File.Exists(p + ".old")) File.Delete(p + ".old");
            if (!Directory.Exists(TempRoot)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(TempRoot))
            {
                if (Path.GetFileName(entry).StartsWith("partial-", StringComparison.Ordinal)) continue;
                try { if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry); } catch { }
            }
        }
        catch { }
    }

    // Best-effort removal of stale update leftovers before a fresh attempt: orphaned
    // per-attempt folders (crashed runs), pre-1.26.09.10 archives that sat at the root and
    // partial downloads of OTHER releases. Files still held by a live updater fail to delete
    // and are skipped — the next app start removes them via CleanupOnStartup.
    private static void CleanupStaleUpdateArea(string keep)
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(TempRoot))
                try { Directory.Delete(dir, true); } catch { }
            foreach (var file in Directory.EnumerateFiles(TempRoot))
                if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(file); } catch { }
        }
        catch { }
    }

    // Opens the downloaded archive for extraction, retrying on transient sharing violations
    // (an antivirus real-time scan of the freshly written file). Bounded: ~2 s worst case.
    private static FileStream OpenArchiveForRead(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (IOException) when (attempt < 4) { Thread.Sleep(400); }
        }
    }

    /// <summary>Startup check: latest GitHub release newer than the running version → update.
    /// Skipped entirely on Debug builds and under <c>dotnet run</c> (see
    /// <see cref="IsPublished"/>/<see cref="IsDebugBuild"/>).</summary>
    public static async Task CheckAndApplyAsync()
    {
        if (!Enabled || !IsPublished || IsDebugBuild || Rid() is not { } rid) return;
        // The TUI /update command (or a previous retry) is already downloading — it will
        // apply the update and restart the app, so there is nothing left to do here.
        if (!await UpdateGate.WaitAsync(0))
        {
            Log.LogStep("AutoUpdate: an update is already in progress — skipping");
            return;
        }
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            var tag = await GetLatestTagAsync();
            if (tag is null || !Version.TryParse(tag.TrimStart('v'), out var latest) || latest <= current) return;

            // A second agent.exe instance (e.g. the Windows auto-start task plus a manual
            // launch) would keep the executable locked during the swap — the other
            // instance applies the update instead.
            if (OtherAgentInstanceRunning())
            {
                Log.LogStep("AutoUpdate: another agent instance is running — skipping (it will apply the update)");
                return;
            }

            Log.LogStep($"AutoUpdate: {current} → {tag}, downloading", monitor: true);
            Status(string.Format(Dictionary.UpdateDownloading, tag));

            // Plugin refresh first: the plugin repos publish self-contained zips to their
            // GitHub releases; refresh the plugins BEFORE the app archive is applied. The
            // archive also carries the plugins (belt-and-braces) — this covers plugin
            // releases that landed between two app releases. When agents are executing the
            // refresh is refused (AgentBusyException): postpone the whole update and retry
            // later — the running process keeps the old byte-loaded types until the restart
            // anyway, and the updater would wait for this process to exit regardless.
            try
            {
                var pluginUpdates = await PluginUpdater.UpdatePluginsAsync(AgentBridge.ToolPlugins.Host);
                if (pluginUpdates.Count > 0)
                    Status(string.Format(Dictionary.UpdatePlugins, pluginUpdates.Count));
            }
            catch (PluginUpdater.AgentBusyException)
            {
                Log.LogStep("AutoUpdate: agents are executing — postponing the update");
                ScheduleRetryIn(TimeSpan.FromMinutes(30));
                return;
            }
            catch (Exception ex)
            {
                Log.LogStep($"AutoUpdate: plugin refresh failed — {ex.Message}");
            }

            await ApplyAsync(rid, tag);
        }
        catch (Exception ex)
        {
            Log.LogStep($"AutoUpdate: check failed — {ex.Message}");
        }
        finally
        {
            UpdateGate.Release();
        }
    }

    /// <summary>Manual update check (TUI /update): unlike <see cref="CheckAndApplyAsync"/>
    /// it runs regardless of <see cref="Enabled"/> — the user asked for it explicitly —
    /// but still refuses when running under the dotnet host and on Debug builds (see
    /// <see cref="IsPublished"/>/<see cref="IsDebugBuild"/>). When a newer release exists
    /// the updater is spawned and the process exits, so a returned result always means
    /// "nothing was installed"; the TUI localizes each status for the user.</summary>
    public static async Task<ManualUpdateResult> CheckAndApplyManualAsync()
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        if (!IsPublished) return new(ManualUpdateStatus.NotPublished, current.ToString(), null, null);
        if (IsDebugBuild) return new(ManualUpdateStatus.DebugBuild, current.ToString(), null, null);
        if (Rid() is not { } rid) return new(ManualUpdateStatus.NoArchive, current.ToString(), null, null);
        // The startup check or an earlier /update is already downloading: report it instead
        // of opening the same temp archive a second time (sharing violation). The running
        // flow applies the update and restarts the app when it finishes.
        if (!await UpdateGate.WaitAsync(0))
            return new(ManualUpdateStatus.UpdateInProgress, current.ToString(), null, null);
        try
        {
            var tag = await GetLatestTagAsync();
            if (tag is null || !Version.TryParse(tag.TrimStart('v'), out var latest))
                return new(ManualUpdateStatus.Unreachable, current.ToString(), tag, null);
            if (latest <= current)
                return new(latest == current ? ManualUpdateStatus.UpToDate : ManualUpdateStatus.NewerThanLatest,
                    current.ToString(), tag, null);
            if (OtherAgentInstanceRunning())
                return new(ManualUpdateStatus.AnotherInstance, current.ToString(), tag, null);

            Log.LogStep($"AutoUpdate (/update): {current} → {tag}, downloading", monitor: true);
            Status(string.Format(Dictionary.UpdateDownloading, tag));

            try
            {
                var pluginUpdates = await PluginUpdater.UpdatePluginsAsync(AgentBridge.ToolPlugins.Host);
                if (pluginUpdates.Count > 0)
                    Status(string.Format(Dictionary.UpdatePlugins, pluginUpdates.Count));
            }
            catch (PluginUpdater.AgentBusyException)
            {
                return new(ManualUpdateStatus.AgentsBusy, current.ToString(), tag, null);
            }
            catch (Exception ex)
            {
                Log.LogStep($"AutoUpdate: plugin refresh failed — {ex.Message}");
            }

            // The plugin refresh refused busy agents before the download, but a run can also
            // start during it — refuse here too (no apply, no exit) instead of killing the run.
            if (AgentHarness.ActiveExecutions > 0 || AgentTaskRegistry.RunningCount > 0)
                return new(ManualUpdateStatus.AgentsBusy, current.ToString(), tag, null);

            await ApplyAsync(rid, tag);
            // ApplyAsync spawns the updater and exits the process when an update applies;
            // reaching this line means the process is still alive (defensive fallback).
            return new(ManualUpdateStatus.Failed, current.ToString(), tag, "the updater did not start");
        }
        catch (Exception ex)
        {
            return new(ManualUpdateStatus.Failed, current.ToString(), null, ex.Message);
        }
        finally
        {
            UpdateGate.Release();
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

    // Resumable download. The win-x64 archive is ~1.5 GB and a slow link can need more than
    // one TUI session to fetch it, so the partial file is kept across runs and every attempt
    // continues with a Range request. There is no total timeout: a transfer that keeps
    // delivering must be allowed to finish (the old 20-minute cap aborted large archives on
    // slow links and the next start began again from zero). A read that delivers nothing for
    // two minutes is treated as stalled and retried, resuming from the bytes already written.
    private static async Task DownloadAsync(string url, string path)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var have = File.Exists(path) ? new FileInfo(path).Length : 0;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                // 416: the partial already holds the whole file (a previous attempt finished
                // the transfer but failed later, e.g. during extraction).
                if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) return;
                resp.EnsureSuccessStatusCode();
                var resuming = have > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (!resuming) have = 0;
                var total = (resp.Content.Headers.ContentLength ?? 0) + have;
                if (have > 0)
                {
                    Log.LogStep($"AutoUpdate: resuming the download at {have:N0} bytes", monitor: true);
                    Progress = string.Format(Dictionary.UpdateProgress, total > 0 ? (int)(have * 100 / total) : 0);
                    Status(Progress);
                }
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(path, resuming ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long read = have;
                int lastPct = -1;
                while (true)
                {
                    var n = await src.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromMinutes(2));
                    if (n == 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    if (total > 0)
                    {
                        var pct = (int)(read * 100 / total);
                        if (pct >= lastPct + 5)
                        {
                            lastPct = pct;
                            Progress = string.Format(Dictionary.UpdateProgress, pct);
                            Status(Progress);
                        }
                    }
                }
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                Log.LogStep($"AutoUpdate: download interrupted ({ex.GetType().Name}: {ex.Message}) — retry {attempt + 1}/{maxAttempts}", monitor: true);
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
            }
        }
    }

    private static async Task ApplyAsync(string rid, string tag)
    {
        var url = $"{Repo}/releases/download/{tag}/agentbridge-{rid}.tar.gz";

        // Partial downloads live at the root of the temp area, one per rid+tag: they survive
        // an app restart (CleanupOnStartup keeps them) and the next attempt continues from
        // the last byte instead of fetching ~1.5 GB again. The per-attempt work folder still
        // isolates the extraction, and the updater deletes it when done.
        var partial = Path.Combine(TempRoot, $"partial-{rid}-{tag}.tar.gz");
        CleanupStaleUpdateArea(partial);
        var work = Path.Combine(TempRoot, $"update-{Guid.NewGuid():N}");
        var extract = Path.Combine(work, "extract");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(TempRoot);

        // Download with visible progress (the archive is large — runtime, kokoro.onnx,
        // plugins, OfficeManager). Status events reach the TUI status bar.
        await DownloadAsync(url, partial);

        Directory.CreateDirectory(extract);
        // Antivirus real-time scans can hold the freshly written archive for a moment after
        // the download closes; retry the open instead of failing the update on a transient lock.
        try
        {
            using (var gz = new GZipStream(OpenArchiveForRead(partial), CompressionMode.Decompress))
                TarFile.ExtractToDirectory(gz, extract, overwriteFiles: true);
        }
        catch
        {
            // A partial archive that does not extract is unusable: drop it so the next
            // attempt downloads clean instead of resuming a corrupt file forever.
            try { File.Delete(partial); } catch { }
            throw;
        }
        try { File.Delete(partial); } catch { }
        Progress = "";

        // Legacy archives (pre-PersistentData layout) still carried the root config json
        // (appsettings/providers/telegram): strip them from the extract so an upgrade never
        // places user config next to the executable. Since the single-directory layout
        // (AppConfig), user-editable config lives in PersistentData\ — never part of an
        // archive — so nothing else needs protecting.
        File.Delete(Path.Combine(extract, "appsettings.json"));
        File.Delete(Path.Combine(extract, "providers.json"));
        File.Delete(Path.Combine(extract, "telegram.json"));

        var target = Path.GetDirectoryName(Environment.ProcessPath)!;

        // A service manager (systemd / launchd) owns the process lifecycle: restarting the
        // app from here would race the supervisor (its own restart can kill the updater via
        // the cgroup, or a second unmanaged instance would start). On POSIX the running
        // image can be replaced in place, so copy the changed files and exit — the
        // manager's restart policy (Restart=always in the SystemExtra unit) brings the new
        // version up.
        if (IsServiceSupervised)
        {
            if (DeferIfAgentsBusy()) return;
            Log.LogStep($"AutoUpdate: service-managed run — applying {tag} in place", monitor: true);
            Status(string.Format(Dictionary.UpdateServiceRestart, tag));
            foreach (var src in Directory.EnumerateFiles(extract, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(extract, src);
                if (rel == RestartArgsFile) continue;
                var dst = Path.Combine(target, rel);
                if (!File.Exists(dst) || !SameContent(src, dst))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, true);
                }
            }
            try { Directory.Delete(work, true); } catch { }
            Environment.Exit(0);
        }

        // Desktop / interactive runs: the updater (the NEW executable, extracted to the
        // temp area) swaps the files once this process is gone — the executable last, via
        // a .old rename for rollback — and restarts with the original command line,
        // minus --no-update.
        if (DeferIfAgentsBusy()) return;
        File.WriteAllText(Path.Combine(extract, RestartArgsFile), JsonSerializer.Serialize(
            Environment.GetCommandLineArgs().Skip(1).Where(a => a != "--no-update").ToArray()));

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

        // This process runs from the transient temp extract: LogStep would write next to
        // THIS executable (→ %TEMP%…) and AIOrchestrator.Log is disabled here anyway, so a
        // failure used to vanish together with the temp area. Report into the install's own
        // logs folder instead — the same place the app's logs live.
        var updateLog = Path.Combine(target, "logs", "autoupdate-updater.log");
        void UpdaterLog(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(updateLog)!);
                File.AppendAllText(updateLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        try
        {
            // The old process's exe stays locked until it fully terminates — wait for it.
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (IsAlive(oldPid) && DateTime.UtcNow < deadline) Thread.Sleep(500);
            if (IsAlive(oldPid))
            {
                UpdaterLog($"old process {oldPid} did not exit within 120 s — update aborted");
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
            UpdaterLog($"files swapped into {target} (old executable kept as {Path.GetFileName(exeOld)} for rollback)");

            // Restart with the original command line; the new start cleans up temp and .old.
            var restart = Array.Empty<string>();
            try { restart = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(extract, RestartArgsFile))) ?? restart; } catch { }
            var psi = new ProcessStartInfo(exeDst) { UseShellExecute = false, WorkingDirectory = target };
            foreach (var a in restart) psi.ArgumentList.Add(a);
            Process.Start(psi);
            UpdaterLog($"restarted {exeDst} — update complete");

            // Best-effort: the updater's own exe is inside the extract, so on Windows the
            // deletion of the temp area only succeeds after this process exits.
            try { Directory.Delete(Path.GetDirectoryName(extract)!, true); } catch { }
            return 0;
        }
        catch (Exception ex)
        {
            UpdaterLog($"update FAILED: {ex}");
            return 1;
        }
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    private static bool _retryScheduled;

    // Long agent/tool runs (e.g. a CPU-TTS podcast) can start or outlive the download phase,
    // so exiting the process now would kill the run mid-work. Defer and retry in 30 minutes —
    // the same busy signal and cadence as the plugin-refresh refusal in CheckAndApplyAsync.
    // Returns true when the apply was deferred (the caller must not exit).
    private static bool DeferIfAgentsBusy()
    {
        if (AgentHarness.ActiveExecutions == 0 && AgentTaskRegistry.RunningCount == 0) return false;
        Log.LogStep("AutoUpdate: agents are executing — postponing the update");
        ScheduleRetryIn(TimeSpan.FromMinutes(30));
        return true;
    }

    // Postpones the whole update: runs the check again after the delay (agents may still be
    // busy — then it defers again, but concurrent retries never stack). The process usually
    // restarts before the delay elapses and the startup check takes over.
    private static void ScheduleRetryIn(TimeSpan delay)
    {
        if (_retryScheduled) return;
        _retryScheduled = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                _retryScheduled = false;
                await CheckAndApplyAsync();
            }
            catch (Exception ex)
            {
                Log.LogStep($"AutoUpdate: retry failed — {ex.Message}");
            }
        });
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

    // Windows only: another process running the same executable from the same folder would
    // hold the image lock during the swap (e.g. the Task Scheduler auto-start instance plus
    // a second manual launch). Refuse the update up-front instead of failing the swap after
    // the requesting UI already exited — the running server instance is the one to update.
    private static bool OtherAgentInstanceRunning()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(processPath)) return false;
            var myPid = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processPath)))
            {
                try
                {
                    if (p.Id != myPid && string.Equals(p.MainModule?.FileName, processPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { /* process of another user — cannot read its image, not ours */ }
            }
        }
        catch { }
        return false;
    }

    // True when a service manager (systemd / launchd) owns this process and will restart it
    // after the files are swapped. systemd marks its units with INVOCATION_ID /
    // JOURNAL_STREAM; launchd services are children of pid 1 (also true for systemd units).
    private static bool IsServiceSupervised
    {
        get
        {
            if (OperatingSystem.IsWindows()) return false;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID"))
                || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JOURNAL_STREAM"))) return true;
            try
            {
                if (!OperatingSystem.IsLinux()) return false;
                var stat = File.ReadAllText($"/proc/{Environment.ProcessId}/stat");
                var close = stat.LastIndexOf(')');
                if (close < 0) return false;
                var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return fields.Length > 0
                    && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ppid)
                    && ppid == 1;
            }
            catch { return false; }
        }
    }

    private static void Status(string message) => OnStatus?.Invoke(message);

    // ── Manual update result (TUI /update) ─────────────────────────────────────────
    /// <summary>Why a manual update check did not install anything. The TUI localizes each
    /// status; when an update IS applied the process exits, so a returned value always
    /// means the user keeps the running version.</summary>
    public enum ManualUpdateStatus
    {
        /// <summary>Running under the dotnet host — launch agent(.exe) instead.</summary>
        NotPublished,
        /// <summary>Debug build — updates never run there.</summary>
        DebugBuild,
        /// <summary>No release archive exists for this OS/architecture.</summary>
        NoArchive,
        /// <summary>GitHub could not be reached.</summary>
        Unreachable,
        /// <summary>Running version is the latest published release.</summary>
        UpToDate,
        /// <summary>Running build is newer than any published release.</summary>
        NewerThanLatest,
        /// <summary>Agents are executing — retry later.</summary>
        AgentsBusy,
        /// <summary>Another agent instance holds the app folder.</summary>
        AnotherInstance,
        /// <summary>An update flow (the startup check or a previous /update) is already running.</summary>
        UpdateInProgress,
        /// <summary>Unexpected failure (see Detail).</summary>
        Failed,
    }

    /// <summary>Outcome of <see cref="CheckAndApplyManualAsync"/> with the versions involved.</summary>
    public sealed record ManualUpdateResult(
        ManualUpdateStatus Status, string? CurrentVersion, string? LatestVersion, string? Detail);

    private sealed class State
    {
        public bool Enabled { get; set; } = true;
    }
}
