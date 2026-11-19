# AgentBridge — guardrails

Rules that exist because breaking them broke a release, an update, or a user's installation.
Verified against the working tree on **2026-11-19**. The detail of each rule lives in the document
named in the last column; this file is the index you read first.

> **When this file and the code disagree, neither side wins by default.** First decide what the
> document *is*:
>
> | The document is… | How to recognise it | If the code differs |
> |---|---|---|
> | **Prescriptive** — a rule contributors must follow (contract, checklist, guardrail) | it says *must / never / always*, it has a checklist, the code or a build step refers to it | **the code is the defect**: fix the code, keep the rule |
> | **Descriptive** — written after the fact, recording what the code does | it says *is / how it works*, it carries measurements and mechanism | the text is **stale**: update the text |
> | **Mixed** — a rule plus the explanation of the mechanism (most real documents) | both of the above in the same file | split it: keep the rule, correct the description |
>
> Then confirm with a decision record: `git log -S "<symbol or rule>" --oneline`. A deliberate,
> recorded change means the older text is stale; a silent drift means the code is the defect.
>
> A rule rewritten to match violating code teaches the next agent to violate it. When the verdict is
> not obvious from the record, leave the finding open and say so — do not pick a side silently.

## 1. Read before you edit

| If you touch… | Read first |
|---|---|
| the release pipeline, NuGet, tags, archives | [RELEASING.md](RELEASING.md) and [RELEASE-CHECKLIST.md](RELEASE-CHECKLIST.md) |
| an installer or a launcher | [INSTALLER-CONTRACT.md](INSTALLER-CONTRACT.md) |
| the TUI | [TUI-DEVELOPMENT.md](TUI-DEVELOPMENT.md) |
| anything shipped to the user | `docs/` vs `docs-dev/` — see §4 below |
| the plugin deployment | AIOrchestrator `API/AGENT_TOOLS_GUIDE.md` §6–§9 |

## 2. The rules

| Don't | Why it breaks | Do instead |
|---|---|---|
| Put a config/state `.json` next to the executable | Updates replace the distribution content; a Debug build of AgentBridge **refuses to start** when a stray json sits next to the exe (`AppConfig`) — treat that refusal as a blocker, not a nuisance. | Only SDK-generated `agent.*.json` next to the exe; user-editable state in `PersistentData\`; credentials in the OS app-data folder. |
| Assume the current directory is the app folder | The app must run when launched from any directory (shortcut, service, `dotnet run`, IDE). | Resolve paths from `AppContext.BaseDirectory`. |
| Condition both references in a *plugin* csproj | The plugin publishes a package: the dependency disappears from the nuspec. | In AgentBridge (an app, no package) **both** are conditional — `ProjectReference` on `Exists(...)`, `PackageReference` on `!Exists(...)`. The opposite in a plugin. |
| Add a plugin to the NuGet wait list in `release.yml` | Plugins are not build dependencies: they are fetched as GitHub Release zips. | Keep the wait list to lowercase **build** dependencies only. |
| Hand-edit the GitHub wiki | It is generated from `docs/` by `sync-wiki.yml` on every push; manual edits are overwritten. | Edit `docs/`, push, let the workflow sync. |
| Put a contributor rule in `docs/` | `docs/` ships inside every release archive and mirrors to the public wiki — a developer note there becomes a user instruction. | Contributor material goes in `docs-dev/`, which is never shipped. |
| Ship `AIOrchestrator.dll` (or its graph) inside a plugin folder | The host provides it; the release zip strips that graph mechanically. | Keep `plugin-release.yml` untouched — it computes the exclusion from `project.assets.json`. |
| Replace plugin files while the host is running | File-based loading locks the DLLs on Windows. | A plugin refresh rides the app update; `PluginUpdater` refuses while agents are executing (`AgentBusyException`) and retries later. |
| Change an installer's launcher for a single request | The contract is "AgentBridge + plugin + target application, then the assistant chat" — the issue #7 regression opened the login page instead of the chat. | Check `INSTALLER-CONTRACT.md` before touching a launcher. |
| Edit `mcp/` or `LICENSE` without rebuilding the bundle | `server.json` carries a real `fileSha256`; a stale hash breaks the MCP listing. | `python tools/mcp/build_bundle.py --version <semver> --out dist`, then commit the new hash. |
| Flip `IsPrerelease` to `false` while iterating | It is the release gate: `false` + master push produces a GitHub release. | Keep it `true` until the test cycle proved the version. |
| Re-implement conversation logic in a new audio medium | The engines live in the `AIOffice.VoiceAgent` subprocess; a medium is I/O only (`IAudioMedia`). | Implement `IAudioMedia` and reuse `VoiceConversation` (see AIOrchestrator `docs-dev/ARCHITECTURE.md`). |
| Parse a slash command as free text | TUI commands are deterministic entry points. | Register the command (e.g. `/taskscheduler`) in the command table. |

## 3. Where a change lands

| You change | It also reaches |
|---|---|
| `Program.cs` endpoints | every OpenAI-compatible client (GiraffeAI, the MCP bundle, third-party apps) |
| the release workflow or `sync-all.ps1` | every dependency repo (the pre-push hook commits **and pushes** them) |
| `docs/` | the shipped archive **and** the public wiki |
| the plugin fetch step in `release.yml` | what every user gets in `Tools/<Tool>/` on the next update |
| the voice subprocess protocol (JSON Lines) | the SIP bridge and the Windows voice agent — both inherit `VoiceAgentBase` |

## 4. The two documentation sets (never mix them)

| Folder | Audience | Shipped? |
|---|---|---|
| `docs/` | the person who **installs** the app (manual, TUI/API, telegram, sip, autoupdate) | **yes** — copied to the build output and every release archive; mirrored to the wiki |
| `docs-dev/` | the person who **maintains** the code (architecture, release pipeline, TUI internals) | no — repository only |
| `media/` | README showcase only | no |

`docs/` is copied by `AgentBridge.csproj` (the `None` items with `CopyToOutputDirectory` at the
bottom of the file): a guide that is not in `docs/` never reaches the people who install the app.
