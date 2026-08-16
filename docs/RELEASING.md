# AgentBridge — releases, NuGet packages and automatic updates

This document describes how AgentBridge ships: the dependency NuGet packages, the version
scheme, the release gate, and what to do when adding a new dependency project. It is meant
for **developers and coding agents**. Users only need the [README](../README.md)
(installation via the GitHub Releases page).

## Overview — how an update reaches users

```
git commit + git push origin master (AgentBridge)
   └─ pre-push hook (hooks/pre-push, installed by install-hooks.ps1)
        └─ sync-all.ps1 -SkipSelf  → pushes every dependency repo (recursive ProjectReference scan)
             └─ each repo's publish.yml (push to master) → packs + pushes its NuGet package
                (1.yy.MM.dd, --skip-duplicate → idempotent)
   └─ AgentBridge master pushed cleanly

release (automatic):  push master with IsPrerelease=false in the committed csproj
   └─ release.yml (trigger: push to master):
        1. check-version: reads the version + the IsPrerelease gate from the csproj
           (skips when IsPrerelease=true or when today's tag v1.yy.MM.dd already exists)
        2. wait for today's dependency packages on nuget.org (GLOBAL 30-min window, see below)
        3. build 5 single-file archives (win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64)
           with the Kokoro TTS assets → create the GitHub release (tag auto-created)
```

## What an update must never touch — the file storage tiers

AgentBridge and the AIOrchestrator library split persisted files into **three storage
tiers**. Every update mechanism (today: the release archives + manual replace; tomorrow:
an auto-updater) must respect them:

| Tier | Location | Purpose | Update rule |
|---|---|---|---|
| **User-editable configuration** | `<app folder>\PersistentData\` | JSON settings a user can edit by hand that must survive updates — currently `rag_settings.json` (the persisted DocumentsPath) | **Never delete or overwrite**. The legacy `rag_settings.json` next to the executable is migrated into `PersistentData` automatically on the first run after an upgrade |
| **Application data & secrets** | OS app-data folder, `<AppData>\<AppName>\` (Windows `%LocalAppData%\<AppName>`, Linux `~/.local/share/<AppName>`, macOS `~/Library/Application Support/<AppName>`) | App-owned state and credentials — currently `setup.json` (API keys, DPAPI-encrypted on Windows, SMTP/IMAP, provider name) | **Never touch** — outside the app folder by construction |
| **Distribution content** | `<app folder>\` (everything the archive ships) | The runtime: `agent(.exe)`, `agent.xml`, `voices/`, `kokoro.onnx`, `assets/`, `.playwright/`, `agent.staticwebassets.endpoints.json`, the default `appsettings.json`, … | **Replace on every update**, with TWO exceptions below |

The folder name of the app-data tier is the **entry-assembly name of the running
executable** (`agent` for AgentBridge → `%LocalAppData%\agent\setup.json`), not the
product name: each host executable gets its own folder so several apps using the
AIOrchestrator library never share credentials.

**The two exceptions in the distribution tier are `appsettings.json` and
`providers.json`** — the server config (port, default LLM, voice path) and the LLM
provider definitions, both editable by the user. An updater must preserve them.
Every OTHER `.json` in the archive (`.playwright/package/*.json`,
`agent.staticwebassets.endpoints.json`, …) is generated or shipped content that **must**
be overwritten: a "don't touch `.json` files" rule would break the update, not protect the
user. Protect by **whitelist** (`appsettings.json` + `providers.json` + `PersistentData\`),
never by file extension.

Both storage conventions (`PersistentData`, app-data folder) are implemented in
AIOrchestrator `Setup.cs` (`PersistentDataDir`/`SettingsFile` and `SetupFilePath`);
AgentBridge adds its own `appsettings.json` and `providers.json` to the protected set.
The split is deliberate: user-editable JSON stays next to the executable so a portable
install keeps its configuration when the folder moves, while credentials are per-user
OS state. The automatic updater enforces these rules — see [autoupdate.md](autoupdate.md).

## How the release wait works (why you can just wait)

Only the dependency repos that were actually **pushed** publish today's version: `sync-all.ps1`
commits a repo only when it has changes, so an unchanged repo keeps its previous day's package
(the content is identical — a date-versioned re-publish would not differ).

The wait step in `release.yml` (mirrored by `release.ps1`) therefore uses a **global 30-minute
window** (nuget.org's official propagation time), not a per-package timeout:

- every cycle (30 s in the workflow) it checks **all** packages at today's version;
- it stops as soon as every one is visible — usually a few minutes, often less;
- after the 30-minute window, any package still missing means *that repo had no publish
  today*: it is reported with a `::warning::` and the build **proceeds** with the latest
  available version (the floating `1.*` restore picks it; for an unchanged repo it is
  identical to today's version).

Consequence: after tagging `v1.yy.MM.dd` there is **nothing to monitor** — either all
packages are visible and the build starts immediately, or it waits at most 30 minutes and
then builds anyway. The release cannot fail on missing packages anymore.

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
`release.ps1`, which only flips the gate to `false`, pushes master and restores the gate to
`true` afterwards (equivalent to doing it by hand).

**A prerelease push publishes nothing.** With `IsPrerelease=true`, pushing `master` produces
no GitHub release (`release.yml` runs but the gate skips the build) and no NuGet update: the
dependency repos' `publish.yml` still runs on the push and attempts the push, but
`--skip-duplicate` skips the already-published date version
(same-day freeze), ending with "Package ... already exists at feed". Real publishes only
come from the next date's push with the gate off.

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

All are versioned `1.yy.MM.dd` and published on every master push. The **wait list** in
`release.yml` ("Wait for dependency packages on NuGet") must contain every package
AgentBridge depends on, in **lowercase**.

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
  (see [sip.md](sip.md) → "Deploying the speech-to-text executable"); without it the SIP
  signalling and PIN gate keep working, only the speech recognition is unavailable.
