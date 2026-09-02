# AgentBridge — automatic updates (architecture)

How AgentBridge updates itself from the GitHub Releases page, and why it is designed
this way. Implemented in `AutoUpdate.cs` (a self-contained static class) with two hooks
in `Program.cs` and one menu item in `Tui.cs`.

## What it does

At **startup** the app asks GitHub for the latest release. When the released version is
**newer** than the running one — and auto-update is enabled — it:

0. **refreshes the tool plugins first** (`PluginUpdater`): every loaded plugin is
   checked against its repo's GitHub release and, when a newer self-contained zip
   exists, installed into `Tools/<Plugin>/` before the app archive is applied (the
   archive also carries the plugins — this covers plugin releases that landed between
   two app releases). When agents are executing the refresh is refused and the whole
   update is retried after 30 minutes (`ScheduleRetryIn`);
1. downloads the platform archive (`agentbridge-<rid>.tar.gz`) to `%TEMP%`;
2. extracts it to a temp folder;
3. spawns the **new** executable from the temp extract as an updater process
   (`--apply-update <target> <extract> <oldPid>`) and exits;
4. the updater waits for the old process to terminate, copies the **changed** files
   (the executable **last**, via a `.old` rename for rollback), restarts the app with
   the original command line and cleans up.

Any failure along the way leaves the current version running untouched — the update is
best-effort by design.

## Why the two-process swap

On Windows a running executable cannot be overwritten, and at startup the app is
already the process mapping its own exe — so the executable can **never replace
itself**, neither during execution nor at the next start. The swap must be done by a
**different process**: the new exe extracted to `%TEMP%` is a different file, so it can
replace the installed one once the old process is gone.

This was verified both against Microsoft's documentation (`CreateFile` sharing modes:
the image is opened read-only for the lifetime of the process) and empirically on
Windows: a child process does **not** keep the parent's exe locked after the parent
exits — the lock lasts only as long as the owning process. Hence the pattern "spawn the
updater and exit immediately" (never wait for the updater from the app).

## The file storage tiers (what an update may touch)

The update mechanism respects the three tiers defined in
[RELEASING.md](../docs-dev/RELEASING.md#what-an-update-must-never-touch--the-file-storage-tiers):

| Tier | Rule |
|---|---|
| `PersistentData\` (user-editable config) | never touched — not present in the archive, never written |
| OS app-data folder `<AppData>\agent\` (SMTP/IMAP credentials, `setup.json`, `autoupdate.json`; LLM API keys are NOT here — they live per-provider in `providers.json`) | never touched — outside the app folder by construction |
| Distribution content (everything else) | replaced when changed, with **two exceptions** |

The exceptions: **`appsettings.json`** (server config) and **`providers.json`** (LLM
provider definitions) are stripped from the extracted copy before the swap, so the
user's configuration is never overwritten. Everything else — including the other
`.json` files in the archive (`.playwright/package/*.json`,
`agent.staticwebassets.endpoints.json`) — is distribution content and is replaced.
Protection is by **whitelist**, never by file extension.

Files are copied only when they changed (length + SHA-256 comparison); the executable
is always replaced (never compared — a fresh build is the point of the update).

## Version check (no GitHub API)

- Current version: `Assembly.GetExecutingAssembly().GetName().Version` — the numeric
  `1.yy.MM.dd` baked into the binary by the csproj (the `-prerelease` suffix lives only
  in the informational version, so a prerelease build compares by its numeric date).
- Latest version: an HTTP `HEAD`/`GET` to
  `https://github.com/Graphene-Lab/AgentBridge/releases/latest` with
  `AllowAutoRedirect = false`; the redirect's `Location` header carries the tag
  (`v1.26.8.10`). No API call, so the unauthenticated rate limit (60 req/h/IP) is never
  an issue. A download URL is then constructed deterministically:
  `https://github.com/Graphene-Lab/AgentBridge/releases/download/<tag>/agentbridge-<rid>.tar.gz`.
- GitHub imposes no bandwidth limit on release downloads (the Acceptable Use Policies
  "Excessive Bandwidth Use" clause is a relative anti-abuse rule, not a quota); the
  client still uses bounded timeouts so a slow/offline GitHub never blocks startup.

## Platform detection

The archive name maps from the running OS + architecture (the RIDs built by
`release.yml`):

| Platform | Archive |
|---|---|
| Windows x64 | `agentbridge-win-x64.tar.gz` |
| Linux x64 / arm64 | `agentbridge-linux-x64.tar.gz` / `agentbridge-linux-arm64.tar.gz` |
| macOS x64 / arm64 | `agentbridge-osx-x64.tar.gz` / `agentbridge-osx-arm64.tar.gz` |
| anything else (e.g. Windows arm64) | no archive — the check is skipped |

The update is also skipped when the app runs from `dotnet run`/`dotnet <dll>` (dev
mode): the check looks at the **process executable** — under the dotnet host the swap
would target `dotnet` itself. Launching the published apphost (`agent.exe` / `agent`) is
what enables updates; this holds for every apphost layout, single-file or not.

## Manual update from the TUI (`/update`)

`/update` (menu **Help → Check for updates… (/update)**) forces the same GitHub check on
demand and works even when the auto-update toggle is off. The TUI shows a short note for
each outcome:

- a newer release **exists** → the app downloads it (the status bar shows the download
  percentage — the archive is large), spawns the updater and **closes itself**; the
  updater swaps the files and the app comes back on the new version. If the app stays
  open, nothing was installed and the note says why:
  - **already up to date** — the running version equals the latest release;
  - **no newer release yet** — the running build is *newer* than the latest release
    (updates install GitHub **releases**, never local commits or unpushed work);
  - **start the app with `agent(.exe)`, not `dotnet`** — the check refuses under the
    dotnet host, because the swap would target `dotnet` itself. Release installs run
    from the apphost: launch `agent.exe` (Windows) / `agent` (Linux/macOS);
  - **GitHub unreachable / agents busy / another instance is running** (see below).

## Running as a service (systemd / launchd) or with auto-start

The two-process swap assumes a plain, user-launched process. When a supervisor owns the
process the behavior adapts:

- **systemd / launchd**: the update copies the changed files **in place** and exits —
  the service manager restarts the app with the new version (the SystemExtra unit uses
  `Restart=always`). The app never restarts itself there: its own restart would race the
  supervisor (duplicate instance, or the cgroup kill cutting the swap short).
- **Windows auto-start (Task Scheduler)**: when a second instance was launched manually
  while the auto-start instance is running the same `agent.exe`, the swap cannot replace
  the locked image. `/update` then refuses with *"another AgentBridge instance is
  running"* — run `/update` from that instance, or close it first (the auto-start one
  too), then retry.

Services that manage the binary themselves should still pass `--no-update` — they are
the ones deciding when and how the files change.

## Enabling / disabling

- **Default: enabled** (`true`).
- **TUI**: menu **File → Auto-Update** toggles the check on/off and persists the choice
  to `<AppData>\agent\autoupdate.json` (the OS app-data folder, same tier as
  `setup.json` — updates never touch it).
- **`appsettings.json`**: `"AutoUpdate": { "Enabled": true }` is the shipped default.
- **Command line**: `--no-update` disables the check for that launch (for services/CI
  that manage the binary themselves — e.g. systemd units should pass it).

Precedence: `--no-update` > persisted toggle > `appsettings.json` > built-in `true`.

## Restart and rollback

- The updater restarts the app with the **original command line** (minus `--no-update`),
  so a TUI session stays a TUI and a `--headless` service stays headless.
- The old executable is kept as `agent(.exe).old` until the **next successful start**,
  which deletes it (and any stale `%TEMP%\agentbridge-update` area). A broken new
  executable can be recovered by renaming the `.old` back manually.
- If the old process does not exit within two minutes, the updater aborts and the old
  version keeps running.

## Security notes

- Downloads travel over HTTPS from GitHub's CDN; archives are not checksum-verified
  (GitHub does not publish per-asset hashes). A `sha256.txt` asset could be added to
  `release.yml` later if verification is wanted.
- The binaries are unsigned; the trust model is "GitHub + HTTPS". Fine for personal
  distribution; sign the binaries before wider roll-out.

## Testing

- Unit-testable pieces: version comparison, RID mapping, "file changed" comparison
  (length + SHA-256).
- End-to-end (Windows): run a published build from a folder, set
  `AutoUpdate.Enabled` to force a fake/lower current version (or run an old build),
  and observe the swap: temp extract → updater → `.old` → restart with the new exe.
- The updater wait loop can be observed by keeping the old process busy past its exit
  deadline — the update must abort cleanly.
