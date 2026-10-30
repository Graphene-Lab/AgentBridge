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
   (release.ps1 performs this tag step automatically for every core repo that changed since
    its last tag — no manual tagging needed for a release)

AgentBridge release (automatic): push master with IsPrerelease=false in the committed csproj
   └─ release.yml (trigger: push to master):
        1. check-version: reads the version + the IsPrerelease gate from the csproj
           (skips when IsPrerelease=true or when today's tag v1.yy.MM.dd already exists)
        2. wait for today's dependency packages on nuget.org (GLOBAL 30-min window, see below)
        3. build 5 single-file archives (win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64)
           with the Kokoro TTS assets → create the GitHub release (tag auto-created)
        4. store-msi job: build GrapheneAgentBridge-<v>.msi (WiX v5, tools/store) from the
           win-x64 payload → attached to the GitHub release too
        5. store-msix job: build GrapheneAgentBridge-<v>.msix (Microsoft Store MSIX, PSF included)
           from the same payload → CI artifact only (see "Microsoft Store channel" below)
        6. store-submit job: update the Microsoft Store draft package to the stable MSI URL
           and submit for certification (see "Microsoft Store channel" below)

Store users: after certification is published by Microsoft, the Store delivers the update
automatically (no action needed). GitHub-release users: unchanged (archives + now the MSI).
```

> **General rule (see AIOrchestrator `github-push-and-release.md`):** the dependency repos
> publish **only on a `v*` tag push** — plain master pushes never publish, and no
> project-file changes are ever needed (current or future repos).

## Microsoft Store channel — MSI creation & Store updates

In addition to the GitHub archives, every release also produces a Windows installer and
updates the **Microsoft Store** app ("Graphene AgentBridge", win32 EXE/MSI product).

- **MSI creation.** release.yml's `store-msi` job (windows-latest, `continue-on-error`)
  runs `tools/store/New-StoreInstaller.ps1` (WiX v5, manifest `tools/store/dotnet-tools.json`)
  over the published win-x64 payload and uploads the MSI; the `release` job attaches
  `GrapheneAgentBridge-<version>.msi` to the GitHub release. An installer problem never
  blocks the archives release.
- **Store updates.** Store EXE/MSI products reference the installer by an external
  **package URL** that must answer HTTP 200 **without redirects** — GitHub download URLs
  are rejected because they always redirect. The MSI is therefore streamed by a small
  python3 proxy on the AIOffice VPS (`tools/store/vps/mirror-msi.py`, systemd
  `agentbridge-mirror`) at **https://aitechnology.it/agentbridge/msi/<version>**, pinned to
  release tag `v<version>`; nothing is stored on the VPS disk. The submitted URL must be
  the versioned form and its binary must not change afterwards (policy 10.2.9), so the
  proxy does NOT redirect and serves `Content-Length`, ranges and `HEAD` — the bare
  `/agentbridge/msi` follows the latest release and is for manual downloads only. After
  the release is out, the `store-submit` job calls `tools/store/Submit-Store.ps1` (Store
  Submission API, `api.store.microsoft.com`): swap the draft package URL → commit →
  submit. Microsoft certifies; once PUBLISHED, Store users update automatically.
- **Signing.** Policy 10.2.9 also requires the MSI and every PE file it ships to be signed
  by a certificate chaining to a CA in the Microsoft Trusted Root Program. Set the
  `SIGN_PFX_BASE64` / `SIGN_PFX_PASSWORD` (or `SIGN_THUMBPRINT`) repo secrets so the
  `store-msi` job signs the payload and the MSI; without them the MSI is unsigned and
  certification fails.
- **Store credentials** are the repo secrets `STORE_TENANT_ID`, `STORE_CLIENT_ID`,
  `STORE_CLIENT_SECRET`, `STORE_PRODUCT_ID`, `STORE_SELLER_ID` (local runs can use
  `tools/store/store-secrets.local.json`, gitignored). The Entra ID app must hold the
  Manager role in Partner Center.

Operational details (account setup, proxy install, manual runs): `tools/store/README.md`
and `tools/store/vps/`.

## What an update must never touch — the file storage tiers

AgentBridge and the AIOrchestrator library split persisted files into **three storage
tiers**. Every update mechanism (today: the release archives + manual replace; tomorrow:
an auto-updater) must respect them:

| Tier | Location | Purpose | Update rule |
|---|---|---|---|
| **User-editable configuration** | `<app folder>\PersistentData\` | Every JSON/settings file a user can edit or the app persists that must survive updates: `appsettings.json` (server config), `providers.json` (LLM providers), `telegram.json` + `telegram.session` (Telegram medium), `tools.json` (per-tool policy), `toolset.json` (TUI agent tool-set), `rag_settings.json` (persisted DocumentsPath) | **Never delete or overwrite** — not part of the archive, never touched |
| **Application data & secrets** | OS app-data folder, `<AppData>\<AppName>\` (Windows `%LocalAppData%\<AppName>`, Linux `~/.local/share/<AppName>`, macOS `~/Library/Application Support/<AppName>`) | App-owned state and credentials — `setup.json` (SMTP/IMAP, DPAPI-encrypted on Windows, provider name; legacy per-provider API keys as fallback only — keys live per-provider in `providers.json`), `autoupdate.json`, `crashreport.json`, `sipstate.json` | **Never touch** — outside the app folder by construction |
| **Distribution content** | `<app folder>\` (everything the archive ships) | The runtime: `agent(.exe)`, `agent.xml`, `voices/`, `kokoro.onnx`, `Lingua/` (SearchPioneer.Lingua language models), `assets/`, `.playwright/`, `docs/`, `Tools/`, the SDK-generated `agent.staticwebassets.endpoints.json`, … | **Replace on every update** — no exceptions, no whitelist |

The folder name of the app-data tier is the **entry-assembly name of the running
executable** (`agent` for AgentBridge → `%LocalAppData%\agent\setup.json`), not the
product name: each host executable gets its own folder so several apps using the
AIOrchestrator library never share credentials.

**Single-directory rule (no root exceptions).** User-editable configuration lives ONLY
under `PersistentData\` — nothing user-editable is shipped or written next to the
executable anymore. The archive therefore contains no user config and the updater needs no
whitelist: replacing the distribution tier can never touch user settings. Every `.json` at
the archive root is generated or shipped content that **must** be overwritten
(`agent.staticwebassets.endpoints.json`, `.playwright/package/*.json`); a "don't touch
`.json` files" rule would break the update, not protect the user.

**Migration (one-time, automatic).** Legacy root files (`appsettings.json`,
`providers.json`, `telegram.json`, `telegram.session`, `tools.json`) are moved into
`PersistentData\` by `AppConfig.Initialize()` on the first run after an upgrade (only when
the destination does not exist yet). In DEBUG builds, the app refuses to start while any
config/state json still sits next to the executable (`AppConfig.GuardNoStrayRootJson`) —
the tripwire that keeps this layout from regressing (see RELEASE-CHECKLIST.md).

Both storage conventions are implemented in AIOrchestrator `Setup.cs`
(`PersistentDataDir`/`SettingsFile` and `SetupFilePath`) and `ProviderConfigs.ConfigDirectory`
(the default stays "next to the executable" for other hosts; AgentBridge redirects it to
`PersistentData\` and seeds the embedded factory `providers.json` there via
`ProviderConfigs.EnsureDefaultFile()`). AgentBridge's own layout is centralized in
`AppConfig.cs`.
The split is deliberate: user-editable JSON stays inside the app folder (portable install
keeps its configuration when the folder moves) but in its own directory, while credentials
are per-user OS state. The automatic updater enforces these rules — see [autoupdate.md](autoupdate.md).

## How the release wait works (why you can just wait)

The wait exists ONLY to guarantee that a release ships today's version of a core dependency
that CHANGED today: the floating `1.*` restore would otherwise pick the latest available
package — i.e. the previous engine when a repo changed but its today's package is still
propagating.

**The wait is now conditional.** Before releasing, `release.ps1` runs a pre-flight check
(`Assert-CorePackagesReady`) over the 5 core repos:

- a core repo **unchanged since its last tag** (no commits, no pending changes) → nothing to
  publish, no wait needed for it;
- a core repo **changed since its last tag** → `release.ps1` **creates and pushes today's tag
  `v1.yy.MM.dd` automatically** right after the sync, at the commit that contains the pending
  changes — a release without it would silently ship the stale engine, and the wait cannot
  help a changed-but-untagged repo (it never publishes today's package);
- after the tag is on origin: if today's package is **already visible** on nuget.org → no
  wait; if it is **still propagating** → the `<NuGetWait>` marker is set.

The marker travels inside the gate-off commit (`<NuGetWait>true|false</NuGetWait>` in
AgentBridge.csproj): `release.yml` runs the 30-minute wait step **only when it is true**.
The push trigger and `workflow_dispatch` behave identically. A manual gate-off push without
release.ps1 leaves the marker at its conservative default (`true`).

When the wait does run, it uses a **global 30-minute window** (nuget.org's official
propagation time): every cycle (30 s) it checks **all** packages at today's version and stops
as soon as every one is visible. After the window, a still-missing package is a **hard
failure**: the wait list only contains packages that should be at today's version (release.ps1
narrows `<NuGetWaitPackages>` to the repos that changed today), so expiry means that repo's
`publish.yml` run failed or nuget.org exceeded its documented window — proceeding would
restore yesterday's engine and either crash the build with misleading errors (2026-09-06:
"CrashReporter does not exist") or ship it silently. Fix the failed publish and re-run
release.ps1; the wait is skipped as soon as the package is visible.

Consequence: a release on a day when **no core repo changed** skips the wait entirely
(fast); when a core repo changed, its today-tag is created + pushed automatically and the
wait resolves in a few minutes. The only "blocking" case is a core repo whose today-tag is
**already on origin** (today's package already published and immutable) with newer changes
beyond it — that engine state can only ship with tomorrow's version, so `release.ps1` aborts
with a clear message instead of a silent stale release.

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

## Official MCP Registry — automatic publish

AgentBridge is listed on the [Official MCP Registry](https://registry.modelcontextprotocol.io)
as **`io.github.Graphene-Lab/agentbridge`**. Nothing to run by hand: a release publishes it.

### What gets published

`server.json` (repository root) is the manifest. It declares one **`mcpb` package**: the
`agentbridge-mcp.mcpb` asset attached to the matching GitHub release.

Why a package and not a `remotes` URL: the registry only accepts a `remotes` entry when that
URL is publicly reachable, and AgentBridge is a local server (`http://localhost:5290/mcp`).
The bundle is the portable answer — a ~15 KB Node stdio↔HTTP bridge from `mcp/`, no binaries,
compatible with Windows, macOS and Linux (`mcp/manifest.json` → `compatibility.platforms`).

### Version mapping (`1.yy.MM.dd` → semver)

The registry requires strict semver (`X.Y.Z`) and rejects `latest`. The release tag stays
`v1.yy.MM.dd`; the published version drops the leading `1.` and the leading zeros of each
component:

| Tag | Registry version |
|---|---|
| `v1.26.10.22` | `26.10.22` |
| `v1.26.10.05` | `26.10.5` |
| `v1.26.08.09` | `26.8.9` |

The mapping is monotonic, so the registry — which computes `is_latest` as the highest semver —
always points at the newest release. It is **not invertible** (`26.10.5` also matches
`v1.26.10.05`), so the backfill discovers the tag from the releases instead of rebuilding it.

### The CI steps

Job `mcp-bundle` (runs before `release`) builds the archive:

```bash
python3 tools/mcp/build_bundle.py --version <semver> --out dist
```

The archive is **deterministic** (sorted entries, pinned timestamps, LF-normalised sources), so
the `fileSha256` in `server.json` matches the bytes of every rebuild of the same commit. The
asset is uploaded with the archives and the MSI.

That determinism is the invariant to respect: the `fileSha256` committed in `server.json` is a
real hash, not a placeholder. After editing anything under `mcp/` (or `LICENSE`, which the
bundle ships) rebuild locally and commit the new hash:

```bash
python tools/mcp/build_bundle.py --version 26.10.22 --out dist   # prints bundle/size/sha256
```

The job then extracts that archive and runs `node tools/mcp/smoke_test.js <extracted>/index.js`
against a stub MCP server (no dependencies), so a regression in the bridge stops the registry
publish instead of shipping a bundle no host can use. Run the same two commands locally after
touching `mcp/`.

Job `mcp-oci` (runs after `mcp-bundle`) publishes the container image, with `packages: write`:

1. extracts the same `agentbridge-linux-x64.tar.gz` and builds `docker/Dockerfile` for `linux/amd64`
   with `--build-arg VERSION=<semver>`;
2. pushes `ghcr.io/graphene-lab/agentbridge:<semver>` and `:latest`;
3. tries to switch the ghcr package to public through the org API, because a new package is private
   and the registry pulls it anonymously (see 9e for the manual fallback).

Job `mcp-registry` (runs after `release` and `mcp-oci`) then, with `permissions: id-token: write` and no secret:

1. rewrites `server.json` from the tag: `version`, the `identifier` (release asset URL, which
   must contain `mcp`), `fileSha256` — the hash of the asset downloaded from the release when it is
   already attached, of the bundle built by this run otherwise — and the `oci` package when the image
   for this version is pullable;
2. probes `GET /v0.1/servers/io.github.Graphene-Lab%2Fagentbridge/versions/<semver>` — on
   `200` it stops, because published versions are immutable and republishing fails;
3. probes the image anonymously (`ghcr.io/token`, then the manifest): a missing or private image drops
   the `oci` package from the manifest instead of advertising something no client can install;
4. polls the asset URL until the registry can reach it (the registry HEAD-checks it);
5. verifies and installs the pinned `mcp-publisher` (`MCP_PUBLISHER_VERSION`, cosign +
   Sigstore bundle), then `login github-oidc` and `publish`; if the full manifest is rejected it
   retries with the `mcpb` package only, so the listing is never lost over the image.

GitHub OIDC is used instead of a PAT: no secret to store or rotate, the token is per-run, and
the registry grants exactly the `io.github.Graphene-Lab/*` namespace of this repository.

`continue-on-error: true` on both jobs: the GitHub release and the Store submission never
depend on the registry, which is still in preview.

### Backfill / repair

`workflow_dispatch` → **Build and Release AgentBridge** → `mcp_publish_version` (e.g.
`26.10.22`) publishes, or re-aligns, an already released version without rebuilding the app:
the job runs outside the normal release graph, resolves the tag from the releases, downloads
the bundle already attached to it and publishes that hash. Use it after a registry failure or a
rejected submission. Two caveats: a published `name@version` cannot be overwritten, so a wrong
manifest needs a new release rather than a second backfill of the same version; and a release
published before this feature carries no `.mcpb` asset, so it cannot be backfilled at all.

### Manual fallback

Requires a local login (`mcp-publisher login github`, device flow):

```bash
git checkout v1.26.10.22
python tools/mcp/build_bundle.py --version 26.10.22 --out dist
# update server.json by hand for that tag, then:
mcp-publisher publish
```

### Repository identity (one-time, manual)

The description and the topics are GitHub settings, not files: **Settings → General** →
description, and topics `mcp`, `mcp-server`, `model-context-protocol`, `ai-agent`,
`office-automation`, `dotnet`, `blazor`, `llm`. They drive discovery on GitHub and do **not**
replace the registry entry, which is what MCP clients read.

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
- **Lingua language models missing in the archives**: SearchPioneer.Lingua copies its
  per-language models (`Lingua/LanguageModels/…`) only into the output of a project that
  references the package directly; via the Graphene.AIOrchestrator NuGet dependency nothing
  is copied, so without `CopyLinguaModelsToOutput`/`CopyLinguaModelsToPublish` the first
  LanguageDetector call fails with "Could not find a part of the path …\Lingua\LanguageModels\
  \<lang>\…" (podcast language detection, document language-word tracking).
- **Playwright driver missing in the archives**: the package's build targets place the
  per-RID node driver under `.playwright\` at build time, but publish does not carry it
  over — released archives shipped an incomplete `.playwright` and WebTool browser launch
  failed with a bare NullReferenceException inside the single-file exe (the driver node.exe
  was absent). `CopyPlaywrightToPublish` copies the whole `.playwright` payload; each RID
  build carries its own driver (`win32_x64`/`linux-x64`/`darwin-*`), so every platform
  archive gets the right one. Check the archive for `.playwright\node\<rid>\node.exe` when
  the browser feature regresses.
- **linux-arm64**: released since KokoroSharp 0.8.4 — the phonemizer is the pure-managed
  MisakiSharp (no `espeak-ng-linux-arm64` binary needed) and `Microsoft.ML.OnnxRuntime`
  ships `libonnxruntime.so` for linux-arm64.
- **SIP STT missing on Linux/macOS**: `CopySttAgentOutput` (the `voiceagent-stt/` folder with
  the whisper-based `AIOffice.VoiceAgent`) runs only when the sibling repo exists on the build
  machine — the CI archives never contain it. Deploy it manually next to the binary
  (see [sip.md](../docs/sip.md) → "Deploying the speech-to-text executable"); without it the SIP
  signalling and PIN gate keep working, only the speech recognition is unavailable.

## 9. Checklist operativa: passi esterni per chiudere il task MCP del prompt iniziale

Il codice (server.json), l'automazione in release.yml (job mcp-bundle + mcp-oci + mcp-registry) e la sezione
MCP del README sono **completati, committati e pushati**. Per chiudere *effettivamente* il task del prompt
iniziale i tre passi esterni qui sotto sono stati **eseguiti** (vedi 9d); restano documentati come procedura ripetibile.

### 9a. Identita' MCP su GitHub (description + topics) -- SETTINGS, via gh
Description e topics vivono nelle Settings del repository, non in un file: un commit non li tocca, ma la
API di GitHub si' -- si impostano con `gh` (niente browser):

```powershell
gh repo edit Graphene-Lab/AgentBridge `
  --description "Autonomous Personal AI Assistant for office work, exposed as a Model Context Protocol (MCP) server (io.github.Graphene-Lab/agentbridge) listed on the Official MCP Registry." `
  --add-topic mcp --add-topic mcp-server --add-topic model-context-protocol
```

- Verifica: `gh repo view Graphene-Lab/AgentBridge --json description,repositoryTopics`
- Nota: description e topics non *sostituiscono* la pubblicazione sul registry; la rendono riconoscibile
  nelle ricerche e nei suggerimenti.

### 9b. Prima release reale -- attiva mcp-registry
Il job `mcp-registry` gira a **ogni** release prodotta da `release.yml` (non solo alla prima) ed e'
idempotente: se la versione e' gia' sul registry il job termina con "already published - nothing to do".

- Il tag non va creato a mano: `daily-release.yml` (cron, oppure `workflow_dispatch` con
  `ignore_time=true`) calcola la versione `1.yy.MM.dd` e chiama `release.yml`, che crea il tag
  `v<version>` con `GITHUB_TOKEN`. Per lo stesso motivo un workflow separato su `on: push: tags: v*`
  non si attiverebbe mai: la pubblicazione e' un job di `release.yml`.
- Verifica live (endpoint `/v0.1/`, nome URL-encoded):

```powershell
Invoke-RestMethod -Uri "https://registry.modelcontextprotocol.io/v0.1/servers/io.github.Graphene-Lab%2Fagentbridge/versions/latest"
```

  Atteso: `version` = semver derivata dal tag (`v1.26.10.30` -> `26.10.30`), `isLatest: true`,
  `status: active`. Ripubblicare la *stessa* versione e' un errore (republish non permesso); il
  registry e' in *preview* (reset possibili) -- il backfill `mcp_publish_version` e il fallback
  manuale `mcp-publisher` coprono entrambi i casi.

### 9c. Badge del registry + visibilita' su github.com/mcp
- **Badge**: fatto. Due badge `modelcontextprotocol.io` nel README (header + sezione MCP).
- **Inclusione in https://github.com/mcp**: *non* e' una submission manuale. `github.com/mcp` e' un
  **aggregatore downstream** che consuma l'API del registry ufficiale con pull periodici (le docs
  parlano di "regular but infrequent", es. una volta all'ora) e applica una curatela propria; non
  esiste API di sottomissione. Una volta che il server e' `active` sul registry, la comparsa su
  `github.com/mcp` dipende solo dalla sincronizzazione lato GitHub: nessuna azione ulteriore nostra e
  nessun automatismo possibile da parte nostra. Se dopo qualche giorno non compare, l'unico canale e'
  il feedback nelle community discussions di GitHub.

### 9d. Stato del primo publish (release v1.26.10.30)
- 9a: description aggiornata e 15 topics, inclusi `mcp`, `mcp-server`, `model-context-protocol`
  (impostati via `gh repo edit`).
- 9b: release `v1.26.10.30` -> asset `agentbridge-mcp.mcpb` (15.109 byte), job `mcp-registry` verde,
  server `26.10.30` con `isLatest: true` e `status: active`; lo sha256 dell'asset scaricato coincide
  con il `fileSha256` pubblicato (`08352ff8...ade8d77`).
- 9c: badge nel README; `github.com/mcp` in attesa della sincronizzazione dell'aggregatore.
### 9e. Immagine container (job `mcp-oci`) -- il prerequisito per github.com/mcp

Il registry ufficiale accetta anche pacchetti `oci`, e un'immagine pubblica e' la ragione per cui un
server locale puo' entrare in un catalogo che non installa bundle desktop. Il job `mcp-oci` di
`release.yml` costruisce e pubblica l'immagine a ogni release, prima di `mcp-registry`.

1. **Cosa costruisce**: build-context = l'archivio `agentbridge-linux-x64.tar.gz` gia' allegato alla
   release (va estratto, non ricompilato). `docker/Dockerfile` parte da
   `mcr.microsoft.com/dotnet/runtime-deps:10.0-noble` (la build e' self-contained), copia tutto in
   `/app`, dichiara `VOLUME /app/PersistentData`, `EXPOSE 5290` ed entra con
   `--headless --no-update --Urls http://0.0.0.0:5290`. Serve anche `fonts-dejavu-core`: la base non
   ha font e gli strumenti di office generano PDF/DOCX.
2. **Tag**: `ghcr.io/graphene-lab/agentbridge:<semver>` (lo stesso semver del registry, per esempio
   `26.10.30`) piu' `:latest`. Il path del package deve essere minuscolo (`${GITHUB_REPOSITORY,,}`).
3. **Invariante da non rompere**: la label `io.modelcontextprotocol.server.name` deve valere
   esattamente `io.github.Graphene-Lab/agentbridge`, cioe' il `name` di `server.json`. Il validatore
   del registry (`internal/validators/registries/oci.go`) fa un pull **anonimo** e confronta la label:
   label diversa o package privato significa publish rifiutato. Le due stringhe si modificano insieme.
4. **Visibilita' del package**: il primo push crea il package **privato**. Il job tenta da solo
   `PATCH /orgs/{owner}/packages/container/agentbridge {"visibility":"public"}` con `GITHUB_TOKEN`; se
   l'API rifiuta, la correzione e' manuale: org -> Packages -> `agentbridge` -> Package settings ->
   Change visibility -> Public. Finche' il package e' privato il probe anonimo dell'immagine fallisce e
   `mcp-registry` pubblica il manifest **senza** la voce `oci`: fallback automatico, nessun rosso.
5. **Verifica dopo una release** (qualsiasi host con Docker):

   ```bash
   docker run -d --name ab -p 127.0.0.1:5290:5290 ghcr.io/graphene-lab/agentbridge:<semver>
   curl -s localhost:5290/health
   curl -s -X POST localhost:5290/mcp -H 'content-type: application/json' \
     -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}'
   docker rm -f ab
   ```

   Poi `GET /v0.1/servers/io.github.Graphene-Lab%2Fagentbridge/versions/latest` deve mostrare **due**
   voci in `packages` (`mcpb` e `oci`).
6. **Se la voce `oci` non compare** anche col package pubblico: leggere lo step "Publish to the
   registry". Se il manifest completo viene rifiutato, il job ripubblica la stessa versione con il solo
   `mcpb` (le versioni del registry diventano immutabili solo dopo il publish, quindi il retry e'
   sicuro) e il motivo resta nel log. Rimedio manuale: `mcp-publisher publish` con un `server.json`
   corretto.
7. **Dimensione**: circa 2,7 GB spacchettata (agent 729 MB, `kokoro.onnx` 310 MB, 5 plugin). Non e' un
   errore: e' l'archivio Linux completo con runtime, modelli e strumenti. La build richiede qualche
   minuto e il job ha `continue-on-error`, quindi un problema di ghcr non blocca `release` ne'
   `store-submit`.
8. **`latest`**: il tag `:latest` dell'immagine e' un comodo alias mobile, non un dato del registry. In
   `server.json` la versione e' sempre semver esplicita: lo schema vieta la stringa `"latest"`.