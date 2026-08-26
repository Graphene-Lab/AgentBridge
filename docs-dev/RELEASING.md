# AgentBridge — releases, NuGet packages and automatic updates

This document describes how AgentBridge ships: the dependency NuGet packages, the version
scheme, the release gate, and what to do when adding a new dependency project. It is meant
for **developers and coding agents**. Users only need the [README](../README.md)
(installation via the GitHub Releases page).

## Overview — how an update reaches users

```
git commit + git push origin master (AgentBridge)   → pure code sync (pre-release)
   └─ pre-push hook (hooks/pre-push, installed by install-hooks.ps1)
        └─ sync-all.ps1 -SkipSelf  → pushes every dependency repo (recursive ProjectReference scan)
             └─ each repo's publish.yml triggers ONLY on v* tag pushes → a plain push publishes NOTHING
   └─ AgentBridge master pushed cleanly

release of the dependency packages (explicit): push a tag on each repo, in dependency order
   git tag v1.yy.MM.dd && git push origin v1.yy.MM.dd    (per dependency repo)
   └─ repo's publish.yml (trigger: tag v*) → packs + pushes its NuGet package (1.yy.MM.dd, --skip-duplicate)

AgentBridge release (automatic): push master with IsPrerelease=false in the committed csproj
   └─ release.yml (trigger: push to master):
        1. check-version: reads the version + the IsPrerelease gate from the csproj
           (skips when IsPrerelease=true or when today's tag v1.yy.MM.dd already exists)
        2. wait for today's dependency packages on nuget.org (GLOBAL 30-min window, see below)
        3. build 5 single-file archives (win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64)
           with the Kokoro TTS assets → create the GitHub release (tag auto-created)
```

> **General rule (see AIOrchestrator `github-push-and-release.md`):** the dependency repos
> publish **only on a `v*` tag push** — plain master pushes never publish, and no
> project-file changes are ever needed (current or future repos).

## What an update must never touch — the file storage tiers

AgentBridge and the AIOrchestrator library split persisted files into **three storage
tiers**. Every update mechanism (today: the release archives + manual replace; tomorrow:
an auto-updater) must respect them:

| Tier | Location | Purpose | Update rule |
|---|---|---|---|
| **User-editable configuration** | `<app folder>\PersistentData\` | JSON settings a user can edit by hand that must survive updates — currently `rag_settings.json` (the persisted DocumentsPath) | **Never delete or overwrite**. The legacy `rag_settings.json` next to the executable is migrated into `PersistentData` automatically on the first run after an upgrade |
| **Application data & secrets** | OS app-data folder, `<AppData>\<AppName>\` (Windows `%LocalAppData%\<AppName>`, Linux `~/.local/share/<AppName>`, macOS `~/Library/Application Support/<AppName>`) | App-owned state and credentials — currently `setup.json` (SMTP/IMAP, DPAPI-encrypted on Windows, provider name; legacy per-provider API keys as fallback only — keys live per-provider in `providers.json`) | **Never touch** — outside the app folder by construction |
| **Distribution content** | `<app folder>\` (everything the archive ships) | The runtime: `agent(.exe)`, `agent.xml`, `voices/`, `kokoro.onnx`, `assets/`, `.playwright/`, `agent.staticwebassets.endpoints.json`, the default `appsettings.json`, … | **Replace on every update**, with THREE exceptions below |

The folder name of the app-data tier is the **entry-assembly name of the running
executable** (`agent` for AgentBridge → `%LocalAppData%\agent\setup.json`), not the
product name: each host executable gets its own folder so several apps using the
AIOrchestrator library never share credentials.

**The three exceptions in the distribution tier are `appsettings.json`, `providers.json`
and `telegram.json`** — the server config (port, default LLM, voice path), the LLM
provider definitions, and the Telegram chat medium, all editable by the user. An updater
must preserve them.
Every OTHER `.json` in the archive (`.playwright/package/*.json`,
`agent.staticwebassets.endpoints.json`, …) is generated or shipped content that **must**
be overwritten: a "don't touch `.json` files" rule would break the update, not protect the
user. Protect by **whitelist** (`appsettings.json` + `providers.json` + `telegram.json` +
`PersistentData\`), never by file extension.

Both storage conventions (`PersistentData`, app-data folder) are implemented in
AIOrchestrator `Setup.cs` (`PersistentDataDir`/`SettingsFile` and `SetupFilePath`);
AgentBridge adds its own `appsettings.json`, `providers.json` and `telegram.json` to the
protected set.
The split is deliberate: user-editable JSON stays next to the executable so a portable
install keeps its configuration when the folder moves, while credentials are per-user
OS state. The automatic updater enforces these rules — see [autoupdate.md](autoupdate.md).

## How the release wait works (why you can just wait)

The wait exists ONLY to guarantee that a release ships today's version of a core dependency
that CHANGED today: the floating `1.*` restore would otherwise pick the latest available
package — i.e. the previous engine when a repo changed but its today's package is still
propagating.

**The wait is now conditional.** Before releasing, `release.ps1` runs a pre-flight check
(`Assert-CorePackagesReady`) over the 5 core repos:

- a core repo **unchanged since its last tag** (no commits, no pending changes) → nothing to
  publish, no wait needed for it;
- a core repo **changed since its last tag** → it MUST carry today's pushed tag
  `v1.yy.MM.dd` — otherwise `release.ps1` **aborts** with the exact command to run (a release
  without it would silently ship the stale engine; the wait cannot help, because a
  changed-but-untagged repo never publishes today's package);
- with the tag in place: if today's package is **already visible** on nuget.org → no wait;
  if it is **still propagating** → the `<NuGetWait>` marker is set.

The marker travels inside the gate-off commit (`<NuGetWait>true|false</NuGetWait>` in
AgentBridge.csproj): `release.yml` runs the 30-minute wait step **only when it is true**.
The push trigger and `workflow_dispatch` behave identically. A manual gate-off push without
release.ps1 leaves the marker at its conservative default (`true`).

When the wait does run, it uses a **global 30-minute window** (nuget.org's official
propagation time): every cycle (30 s) it checks **all** packages at today's version and stops
as soon as every one is visible; after the window, packages still missing are reported with a
`::warning::` and the build **proceeds** with the latest available version (for an unchanged
repo identical to today's version).

Consequence: a release on a day when **no core repo changed** skips the wait entirely
(fast); when a core repo changed and was tagged, the wait resolves in a few minutes; the only
"blocking" case is a changed core repo without a tag — a clear abort with instructions, never
a silent stale release.

## Version scheme and the prerelease flag

Every project (AgentBridge and all dependencies) versions itself as **`1.yy.MM.dd`**
(date-based, same scheme as UISupportBlazor/UISupportGeneric), computed at build time:

```xml
<Version>$([System.DateTime]::Now.ToString("1.yy.MM.dd"))</Version>
```

NuGet normalizes leading zeros: `1.26.08.09` → `1.26.8.9` (the flat-container index and the
wait step use the normalized form).

**Release gate — `IsPrerelease`** in `AgentBridge.csproj` (default `true`):

- `false` → version `1.yy.MM.dd`; the tag `v1.yy.MM.dd` triggers a full release.
- `true` → version `1.yy.MM.dd-prerelease`; the `check-version` job detects the suffix and
  **skips the build** (no assets, no GitHub release).

Set `IsPrerelease=true` while iterating and to `false` only when the test cycles proved
the version works. The gate is the switch: pushing master with `IsPrerelease=false` in the
committed csproj triggers the release automatically (release.yml); the workflow pins the tag
`v1.yy.MM.dd` to the triggering commit. No tag push needed. The status-bar button runs
`release.ps1`, which flips the gate to `false`, pushes master (the release trigger), then
flips the gate back to `true` and pushes that too — nothing stays pending locally. The
restore push's own run is skipped by the gate, and `release.yml` pins the tag to the
triggering commit (`github.sha`), so that later push cannot move it.

**A prerelease push publishes nothing.** With `IsPrerelease=true`, pushing `master` produces
no GitHub release (`release.yml` runs but the gate skips the build) and no NuGet update: the
dependency repos' `publish.yml` triggers only on `v*` tag pushes, so a plain master push does
not even run it. Real publishes only come from pushing a `v*` date tag per dependency repo
(see "The dependency packages") or from the next date's release with the gate off.

## Dependency model: dual reference

AgentBridge and AIOrchestrator reference their dependencies with the **dual-reference
pattern** (the same used by UISupportBlazor):

```xml
<ProjectReference Include="..\X\X.csproj" Condition="Exists('..\X\X.csproj')" />
<PackageReference Include="X" Version="1.*" />
```

- The local sibling project wins in solution builds (development).
- The NuGet package (`1.*` floating = always the latest published) is restored when the
  sibling source is absent (CI, standalone builds).

Consequences:

- CI never checks out the private sibling repos: it builds against the published packages.
- A release ships with today's version of every dependency that CHANGED today (the wait step
  enforces visibility within its 30-min window); unchanged repos keep their latest available
  version, which is identical to what today's version would contain.
- The `Naiad` package (transitive dependency of `Graphene.AIOrchestrator`) requires
  `<Papyrine_SponsorshipLicenseIgnored>true</Papyrine_SponsorshipLicenseIgnored>` in every
  project consuming the package — SC021 blocks Release builds otherwise.

## The dependency packages

| NuGet package | Source repo | Notes |
|---|---|---|
| `Graphene.AIOrchestrator` | Graphene-Lab/AgentHarness | the engine; pins the four packages below |
| `AllToMarkdown` | Graphene-Lab/AllToMarkdown | |
| `MermaidRendering` | Graphene-Lab/MermaidRendering | ships `assets/chart.umd.min.js` + `InstallMermaidRendering.sh` via `contentFiles` |
| `Graphene.ReverseMarkdown` | Graphene-Lab/ReverseMarkdown | fork of the MIT library → renamed id + Andrea Bruno License 1.4 |
| `UISupportGeneric` | Graphene-Lab/UISupportGeneric | predates this pipeline |

All are versioned `1.yy.MM.dd` and published on a `v*` tag push (per-repo `publish.yml`). The
**wait list** in `release.yml` ("Wait for dependency packages on NuGet") must contain every
package AgentBridge depends on, in **lowercase** — and NOTHING else (the tool plugins are not
build dependencies, see below).

### Tool plugins — GitHub Releases channel (not NuGet)

The agent-tool plugins (`DocumentTool`, `SpreadsheetTool`, `OfficeTool`, `PresentationTool`,
`OfficeSupportTool`) are **not** build dependencies and are **not** in the NuGet wait list.
They are loaded dynamically from `Tools/` by `ToolPluginHost` (byte-loaded, never referenced)
and ship in the release archives as **self-contained zips from their own GitHub Releases**:

- every plugin repo (`Graphene-Lab/<Tool>`, PUBLIC) publishes a `<Tool>-<version>.zip` on each
  `v*` tag via its standard `plugin-release.yml` (plugin dll + xml + assets + unique deps,
  minus the AIOrchestrator graph);
- `release.yml` fetches the **latest** release zip of each plugin into `publish/Tools/<Tool>/`
  and merges each payload's `assets/` into the host `assets/` (the plugins resolve host-level
  assets from `AppContext.BaseDirectory\assets`);
- the plugins' NuGet packages (`Graphene.DocumentTool`, …) exist for **third-party consumers
  only** — the AgentBridge build and release never use them.

Consequences:

- Plugin and host releases are **independent**: a new plugin version is published by tagging
  the plugin repo (`git tag v1.yy.MM.dd && git push origin v1.yy.MM.dd`), and the next
  AgentBridge release ships it automatically (no NuGet wait, no version coordination).
- A plugin with **no release yet** is skipped with a `::warning::` in the fetch step — the
  tool is simply missing from `Tools/` until its repo publishes one.
- The AgentBridge release gate (`IsPrerelease`) controls the host release only; it has no
  effect on plugin publishing.

## Adding a new dependency project

To add a project that AgentBridge (or a dependency) depends on, so it joins the automatic
update + release system:

1. **Create the project** as a sibling folder with the package metadata in its csproj:
   - date-based `<Version>` (same format as above);
   - `<PackageId>`, `<Description>`, `<PackageReadmeFile>README.md</PackageReadmeFile>`,
     `<PackageLicenseFile>LICENSE.md</PackageLicenseFile>`,
     `<PackageRequireLicenseAcceptance>False</PackageRequireLicenseAcceptance>`,
     `<Copyright>`, `<RepositoryUrl>`;
   - `LICENSE.md` (Andrea Bruno License 1.4 — copy from `UISupportGeneric`) and `README.md`,
     both packed (`<None Update="..."><Pack>True</Pack><PackagePath>\</PackagePath></None>`);
   - the pack targets `SetPackageVersion`, `CleanOldNuGetPackages`,
     `PublishPackageToNuGet` (copy from `UISupportGeneric`; the push target is skipped in CI
     with `-p:SkipNuGetPush=true`).
2. **Publish the repo on GitHub** (Graphene-Lab; private is fine) and add
   `.github/workflows/publish.yml` (copy from an existing dependency repo).
3. **Reference it from the consumer** with the dual-reference pattern (ProjectReference
   with `Condition="Exists(...)"` + `PackageReference Version="1.*"`).
4. **Check the package-id on nuget.org first** (`https://www.nuget.org/packages/<id>`):
   if the name is taken, use a `Graphene.*`-style id.
5. **Add the lowercase package id to the wait list** in `release.yml`
   ("Wait for dependency packages on NuGet").
6. **If the package depends on `Graphene.AIOrchestrator` (or Naiad)**, set
   `<Papyrine_SponsorshipLicenseIgnored>true</...>` in its consumers.
7. **Release by pushing master with `IsPrerelease=false`** (automatic — see
   "Version scheme and the prerelease flag" above).

Discovery is automatic: `sync-all.ps1` walks the ProjectReference tree from AgentBridge, so
the new repo is pushed by the pre-push hook without any script edit.

## Common pitfalls

- **Missing packages at release time**: not an error anymore — the wait step gives a 30-min
  window and then proceeds with a `::warning::` for the packages that were not published today
  (their repo had no push). Only investigate if a package you EXPECTED to publish is reported
  missing (check that repo's publish.yml run).
- **SC021 (Naiad)**: add the SponsorCheck property to the consumer (see above).
- **Package-id collision**: verify availability before the first publish.
- **TTS missing in the archives**: `CopyTtsAssetsToPublish` copies `kokoro.onnx`,
  `voices/`, `voices-zh/` into the publish directory; the native onnxruntime engine comes
  from the `Microsoft.ML.OnnxRuntime` package reference (KokoroSharp itself only pulls the
  managed wrapper — without that reference the archives ship TTS that fails at inference).
  Any other runtime content must stay next to the executable (single-file bundles only the
  managed code).
- **linux-arm64**: released since KokoroSharp 0.8.4 — the phonemizer is the pure-managed
  MisakiSharp (no `espeak-ng-linux-arm64` binary needed) and `Microsoft.ML.OnnxRuntime`
  ships `libonnxruntime.so` for linux-arm64.
- **SIP STT missing on Linux/macOS**: `CopySttAgentOutput` (the `voiceagent-stt/` folder with
  the whisper-based `AIOffice.VoiceAgent`) runs only when the sibling repo exists on the build
  machine — the CI archives never contain it. Deploy it manually next to the binary
  (see [sip.md](../docs/sip.md) → "Deploying the speech-to-text executable"); without it the SIP
  signalling and PIN gate keep working, only the speech recognition is unavailable.
