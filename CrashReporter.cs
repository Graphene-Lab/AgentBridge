using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIOrchestrator;

// ═══════════════════════════════════════════════════════════════════════
//  CrashReporter — best-effort crash diagnostics to the GitHub repository
//
//  When AgentBridge dies with an unhandled exception, a SANITIZED diagnostic
//  is offered to the project's GitHub issues so the crash can be fixed without
//  chasing logs:
//
//    WHAT IS SENT — only the exception type chain and the stack-trace frames
//    that belong to THIS project's assemblies (AgentBridge, AIOrchestrator,
//    UISupportGeneric, Graphene.* plugins): method names, file names and line
//    numbers of OUR code. That is the "description obtained with
//    Exception.ToString" limited to its code-reference part.
//
//    WHAT IS NEVER SENT — exception MESSAGES (the most likely place for user
//    content: prompts, file paths, provider errors), memory dumps, request
//    bodies, session data, configuration, API keys, IPs, timestamps — nothing
//    else. The report is built ONLY from the <see cref="Exception"/> metadata
//    of the crash, in-process, and is a static text payload.
//
//  Delivery (best-effort, bounded so the crash exit is never delayed long):
//    1. when a GitHub token is configured (appsettings CrashReport:Token) the
//       report is POSTed to the repo's issues API (CrashReport:Repo);
//    2. otherwise, when the `gh` CLI is installed and authenticated on the machine
//       (the user's own GitHub login — no token stored in appsettings), the issue
//       is created automatically with `gh issue create`;
//    3. otherwise, on a desktop session, the pre-filled "new issue" page of the
//       repo opens in the OS default browser — the user REVIEWS the exact text
//       (privacy by construction) and submits it with one click;
//    4. headless servers without token/gh simply log the crash (existing
//       behaviour) — nothing is sent.
//
//  The user can disable sending from the TUI (Help → Crash report, or
//  /crashreport); the toggle is persisted in the OS app-data folder like the
//  auto-update toggle, so updates never touch it. Disabling stops only the
//  SENDING — the local crash log (logs/<pid>.txt) is unchanged.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Sanitized crash reporting to the project's GitHub issues (see the class comment).</summary>
public static class CrashReporter
{
    private const string StateFile = "crashreport.json";
    private const string DefaultRepo = "Graphene-Lab/AgentBridge";

    /// <summary>Whether crash diagnostics are sent (default true; the TUI toggle persists this).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The repository the report goes to, "owner/name".</summary>
    public static string Repo { get; set; } = DefaultRepo;

    /// <summary>Optional GitHub token for automatic issue creation; empty → pre-filled browser form.</summary>
    public static string? Token { get; set; }

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent", StateFile);

    /// <summary>Applies the persisted toggle (set by the TUI). Returns true when a state file was applied.</summary>
    public static bool LoadState()
    {
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

    /// <summary>Persists the toggle (set by the TUI Help → Crash report menu).</summary>
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
        Log.LogStep($"CrashReporter: sending {(Enabled ? "enabled" : "disabled")} by the user", monitor: true);
    }

    /// <summary>Builds and delivers the crash report. Never throws — the crash handler that
    /// calls this must not fail further. Sending is skipped when disabled by the user.</summary>
    public static void Report(Exception? ex)
    {
        if (!Enabled || ex == null) return;
        try
        {
            var (title, body) = BuildReport(ex);
            // The exact payload is recorded in the local log: the verifiable record of what
            // was (or would have been) sent — the same content the browser pre-fills.
            Log.LogStep($"CrashReporter: crash report ready — {title}");
            Log.LogStep("CrashReporter payload:\n" + body);
            if (!string.IsNullOrWhiteSpace(Token))
                PostIssue(title, body);
            else if (!SendViaGhCli(title, body) && HasDesktopSession())
                OpenPrefilledIssue(title, body);
            // else: gh unavailable/not authenticated on a headless server — the crash is
            // already in the local log (logs/<pid>.txt)
        }
        catch (Exception sendEx)
        {
            Log.LogStep($"CrashReporter: send failed — {sendEx.Message}");
        }
    }

    /// <summary>Sanitized report: exception type chain + our stack frames. Messages are
    /// deliberately NOT included (they can carry user content).</summary>
    private static (string Title, string Body) BuildReport(Exception ex)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        var types = new List<string>();
        for (var e = ex; e != null; e = e.InnerException)
            types.Add(e.GetType().FullName ?? e.GetType().Name);

        var frames = new List<string>();
        foreach (var line in (ex.StackTrace ?? "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("at ", StringComparison.Ordinal)) continue;
            // The assembly is the first namespace segment of the method name; keep only ours.
            var ns = trimmed[(trimmed.IndexOf(' ') + 1)..].Split('.')[0];
            if (IsOurAssembly(ns)) frames.Add(trimmed);
        }

        var body = new StringBuilder();
        body.AppendLine($"**AgentBridge {version} crashed** — automatic crash report.");
        body.AppendLine();
        body.AppendLine("Exception chain: `" + string.Join(" → ", types) + "`");
        body.AppendLine();
        body.AppendLine("Stack trace (project code only):");
        body.AppendLine();
        body.AppendLine("```");
        if (frames.Count == 0)
            body.AppendLine("(no frames in project assemblies — full trace in the local log logs/<pid>.txt)");
        else
            foreach (var f in frames) body.AppendLine(f);
        body.AppendLine("```");
        body.AppendLine();
        body.AppendLine("_Privacy: this report contains only the exception type and stack frames of AgentBridge code — no user data, no memory dumps, no messages._");
        return ($"Crash {version}: {types[^1]}", body.ToString());
    }

    private static bool IsOurAssembly(string firstNamespaceSegment) =>
        firstNamespaceSegment is "AgentBridge" or "AIOrchestrator" or "UISupportGeneric"
        || firstNamespaceSegment.StartsWith("Graphene.", StringComparison.OrdinalIgnoreCase);

    // Automatic delivery: POST to the GitHub issues API (needs CrashReport:Token). Synchronous
    // and bounded — the process is dying, a background task might never run.
    private static void PostIssue(string title, string body)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/repos/{Repo}/issues")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { title, body }), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        req.Headers.TryAddWithoutValidation("User-Agent", "AgentBridge-CrashReporter/1.0");
        using var resp = http.Send(req);
        Log.LogStep($"CrashReporter: GitHub issue create → {(int)resp.StatusCode}");
    }

    // Automatic delivery through the machine's `gh` CLI (no token stored in appsettings): uses
    // the user's own GitHub login (gh auth). Returns false when gh is missing, not
    // authenticated or the issue could not be created — the caller falls back to the browser.
    private static bool SendViaGhCli(string title, string body)
    {
        try
        {
            var psi = new ProcessStartInfo("gh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("issue");
            psi.ArgumentList.Add("create");
            psi.ArgumentList.Add("--repo");
            psi.ArgumentList.Add(Repo);
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add("--body");
            psi.ArgumentList.Add(body);
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            if (!proc.WaitForExit(10000))
            {
                try { proc.Kill(); } catch { }
                return false;
            }
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            var stderr = proc.StandardError.ReadToEnd().Trim();
            Log.LogStep($"CrashReporter: gh issue create → exit {proc.ExitCode} {stdout} {stderr}".TrimEnd());
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;   // gh not installed or not usable
        }
    }

    // Desktop delivery: open the pre-filled "new issue" page — the user reviews the exact
    // text before submitting (privacy by construction).
    private static void OpenPrefilledIssue(string title, string body)
    {
        var url = $"https://github.com/{Repo}/issues/new"
            + $"?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // Same desktop-session rule as the TUI /officemanager command and
    // AgentHarness.IsInteractiveDesktopSession: a browser exists only on a graphical session.
    private static bool HasDesktopSession()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return Environment.UserInteractive;
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    private sealed class State
    {
        public bool Enabled { get; set; } = true;
    }
}
