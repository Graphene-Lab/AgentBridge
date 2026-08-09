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

release:  powershell -File release.ps1        (or manually: sync-all → git tag v1.yy.MM.dd → push)
   └─ release.yml on tag v*:
        1. check-version: reads the version + the IsPrerelease gate from the csproj
        2. wait for every dependency package (today's version) to be visible on nuget.org
        3. build 4 single-file archives (win-x64, linux-x64, osx-x64, osx-arm64)
           with the Kokoro TTS assets → attach to the GitHub release
```

## Version scheme and the prerelease flag

Every project (AgentBridge and all dependencies) versions itself as **`1.yy.MM.dd`**
(date-based, same scheme as UISupportBlazor/UISupportGeneric), computed at build time:

```xml
<Version>$([System.DateTime]::Now.ToString("1.yy.MM.dd"))</Version>
```

NuGet normalizes leading zeros: `1.26.08.09` → `1.26.8.9` (the flat-container index and the
wait step use the normalized form).

**Release gate — `IsPrerelease`** in `AgentBridge.csproj` (default `true`):

- `true` → version `1.yy.MM.dd`; the tag `v1.yy.MM.dd` triggers a full release.
- `false` → version `1.yy.MM.dd-prerelease`; the `check-version` job detects the suffix and
  **skips the build** (no assets, no GitHub release).

Set `IsPrerelease=false` while iterating and back to `true` only when the test cycles proved
the version works. `release.ps1` warns when the gate is off.

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
- A release always ships with the newest dependency versions published before it ran (the
  wait step enforces visibility, so a release can never silently use yesterday's deps).
- The `Naiad` package (transitive dependency of `Graphene.AIOrchestrator`) requires
  `<Papyrine_SponsorshipLicenseIgnored>true</Papyrine_SponsorshipLicenseIgnored>` in every
  project consuming the package — SC021 blocks Release builds otherwise.

## The dependency packages

| NuGet package | Source repo | Notes |
|---|---|---|
| `Graphene.AIOrchestrator` | Graphene-Lab/AIOrchestrator | the engine; pins the four packages below |
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
7. **Run the release** (`powershell -File release.ps1`, or the manual steps below).

Discovery is automatic: `sync-all.ps1` walks the ProjectReference tree from AgentBridge, so
the new repo is pushed by the pre-push hook without any script edit.

## Common pitfalls

- **Stale release**: tagging without running sync-all (or before the packages propagated) →
  the wait step fails with a clear message; run `sync-all.ps1` (or `release.ps1`) first.
- **SC021 (Naiad)**: add the SponsorCheck property to the consumer (see above).
- **Package-id collision**: verify availability before the first publish.
- **TTS missing in the archives**: `CopyTtsAssetsToPublish` copies `kokoro.onnx`,
  `voices/`, `espeak/` into the publish directory; any other runtime content must stay next
  to the executable (single-file bundles only the managed code).
- **linux-arm64**: intentionally not released — KokoroSharp 0.6.7 ships no
  `espeak-ng-linux-arm64` binary (TTS would be broken). Re-add the matrix cell only when
  that changes (new KokoroSharp or a bundled ARM64 espeak-ng).
