# Release Checklist — AgentBridge

> **To future contributors:** when you add rules or checkpoints, keep the language minimal but
> never skip a real aspect to verify — this list must stay short, concrete and effective, not a
> verbose essay. If a rule lives elsewhere (ARCHITECTURE.md, RELEASING.md, AGENT_TOOLS_GUIDE.md),
> reference it and keep the actionable line here.

Run **before publishing the app or pushing master**. The Debug binary enforces rule 1 at
startup (it refuses to run when broken) — start it once as the final check.

## Files & layout

- [ ] No `*.json` sits **directly next to the executable** — only the SDK-generated
      `agent.deps.json`, `agent.runtimeconfig.json`, `agent.staticwebassets.endpoints.json` are
      allowed there. AppConfig's DEBUG guard (startup) fails otherwise.
- [ ] Every persistent, non-overwritable file lives under **`PersistentData\`**
      (`appsettings.json`, `providers.json`, `telegram.json`, `telegram.session`, `tools.json`)
      or in the OS app-data folder (`setup.json`, `autoupdate.json`, `crashreport.json`,
      `sipstate.json`) — see docs-dev/ARCHITECTURE.md "Where files live (storage tiers)".
- [ ] The release archive contains **no user config**: nothing under `PersistentData\` ships,
      no `appsettings.json`/`providers.json`/`telegram.json` at the archive root.
- [ ] No new file was added next to the executable by code or build targets; new runtime
      state goes to `PersistentData\` (or app data), never to the assembly folder.

## Paths & CWD

- [ ] The app works when launched from **any directory** (terminal CWD ≠ exe folder): config
      and content are resolved from the executable folder (`AppContext.BaseDirectory`), never
      from the CWD; `AppConfig.Initialize()` anchors the CWD at startup.
- [ ] Spawned child processes (voiceagent, voiceagent-stt) set their `WorkingDirectory` —
      they never inherit an arbitrary CWD.
- [ ] New code paths use `AppConfig.*File` for config files — never a bare relative path.

## Updates

- [ ] `PersistentData\` and the OS app-data folder are **never deleted or overwritten** by the
      updater; the archive is overwrite-only distribution content (see docs/autoupdate.md).
- [ ] `AutoUpdate.cs` protects by the single-directory rule (nothing user-editable is in the
      archive), not by per-file whitelists.

## Build & release

- [ ] `dotnet build -c Debug` succeeds and a Debug start prints no stray-json refusal.
- [ ] Publish output (any RID) has no root config json before the release.
- [ ] Local archive downloads use **`download-release.ps1`** (background-safe, verifies
      integrity with `tar -tzf` before extraction) — a foreground download of the ~950 MB
      archives can be killed mid-transfer and leave a silently truncated `.tar.gz`.
- [ ] Docs stay truthful: file locations in docs/ (user) and docs-dev/ (developer) match the
      layout above; `IsPrerelease` gate handled per AGENTS.md.
- [ ] Tool plugins still resolve after the change (start the app, `/v1/models` lists the
      expected agent sets).

## Push

- [ ] Checklist reviewed before `git push` / `release.ps1` — the pre-push hook reminds you;
      do not push a release while any box above is unchecked.
