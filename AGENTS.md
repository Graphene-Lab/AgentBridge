# AgentBridge — notes for coding agents

> The full release/NuGet mechanism (diagrams, pitfalls, integration checklist) is in
> **[docs/RELEASING.md](docs/RELEASING.md)** — read it before touching the release pipeline.

## Release gate: IsPrerelease flag

`AgentBridge.csproj` carries `<IsPrerelease>` (default `true`). It decides whether a GitHub
release is produced:

- `false` → version is the date-based `1.yy.MM.dd` (e.g. `1.26.08.09`); the tag
  `v1.yy.MM.dd` triggers the full release build (release.yml).
- `true` → the version gets a `-prerelease` suffix and the release workflow skips
  (`check-version` job guards the build matrix).

Set the flag to `false` only when the test cycles proved the version works; keep it `true`
while iterating. Do not change the version scheme — it is date-based like UISupportBlazor.

**Release wait — no monitoring needed.** After tagging `v1.yy.MM.dd`, the workflow waits for
today's dependency packages on nuget.org within a **global 30-minute window** (nuget.org's
official propagation time), then builds anyway with a warning for any package not published
today (that repo simply had no push — only changed repos publish; for an unchanged repo the
latest available version is identical to today's). There is nothing to check during the wait:
either the build starts immediately or it starts after at most 30 minutes. See
docs/RELEASING.md "How the release wait works".

## Release pipeline

**Automatic (recommended):** the release runs by itself in GitHub Actions on every master push
whose commit has `IsPrerelease=false` in `AgentBridge.csproj` (see `release.yml`): it waits for
the dependency packages on nuget.org, builds the 5 platform archives and creates the GitHub
release — the tag `v1.yy.MM.dd` is created on the fly. Nothing else to run; works from any git
client. With `IsPrerelease=true`, or when today's tag already exists, the run is skipped.
The "Push AgentBridge" status-bar button does exactly this with one click (no confirmation):
it opens a Release / PreRelease menu. In both modes `release.ps1` first commits **every
project with pending changes** (AgentBridge + all dependency repos, via `sync-all.ps1`, commit
message `"Update at HH:mm"`) and pushes them. **Release** then flips the gate to `false`
(pushing the release trigger) and restores the gate to `true` locally afterwards.
**PreRelease** instead keeps the gate on (flipping it first if needed), so all changes are
committed and pushed and the dependency NuGet packages publish, but no GitHub release is
created.

**The push itself (always runs):** a `pre-push` hook (installed via `install-hooks.ps1`, template
in `hooks/pre-push`) runs `sync-all.ps1` automatically whenever AgentBridge master is pushed to
origin — so a plain `git push` updates the dependency NuGet packages before/while the release
workflow waits for them. The hook is skipped for tags, other branches/remotes, and re-entrant
pushes (guard: `SYNC_ALL_ACTIVE`).

**Manually (equivalent steps):**
1. `powershell -File sync-all.ps1 -Message "<commit message>"` — commits and pushes
   AgentBridge plus every repo it depends on (recursively via ProjectReference; new
   dependency repos are discovered automatically).
2. Each dependency repo's `.github/workflows/publish.yml` (trigger: push to master) packs and
   pushes its NuGet package (`1.*` floating version, `--skip-duplicate` — idempotent).
3. Push master with `IsPrerelease=false` in the committed csproj → `release.yml` releases
   automatically (it first waits until today's version of every dependency package is visible
   on nuget.org, then builds the archives and creates the GitHub release).

## Dependencies: dual-reference pattern

AgentBridge references its engine (`Graphene.AIOrchestrator`) with both:

- `ProjectReference` to the local sibling, `Condition="Exists(...)"` — wins in solution builds;
- `PackageReference Version="1.*"` — restored when the sibling source is absent (CI).

The CI never checks out the private sibling repos: it builds against the published NuGet
packages. The dependency packages are `AllToMarkdown`, `MermaidRendering`,
`Graphene.ReverseMarkdown`, `UISupportGeneric` (pulled in transitively by
`Graphene.AIOrchestrator`). All are date-versioned `1.yy.MM.dd` and published on every
master push.

## TTS assets in publish output

`CopyTtsAssetsToPublish` (csproj) copies `kokoro.onnx` + `voices/` + `voices-zh/` from
`$(OutputPath)` into the publish directory: `dotnet publish -o <dir>` does not carry them
over on its own, and the release archives ship them so TTS works out of the box. The native
ONNX Runtime engine is included per-RID by the `Microsoft.ML.OnnxRuntime` package reference
(KokoroSharp only pulls the managed wrapper, which has no native library).

## Platform support

Released RIDs: win-x64, linux-x64, **linux-arm64**, osx-x64, osx-arm64. linux-arm64 became
possible with KokoroSharp 0.8.4 (phonemizer is the pure-managed MisakiSharp — no espeak-ng
binary needed) plus the linux-arm64 `libonnxruntime.so` from `Microsoft.ML.OnnxRuntime`.

## Adding a new dependency project (quick checklist)

When AgentBridge (or a dependency) gains a new project dependency, make it part of the
automatic update + release system (full details in docs/RELEASING.md):

1. csproj: date-based `<Version>`, package metadata (`PackageId`, `Description`,
   `PackageReadmeFile`, `PackageLicenseFile`, `PackageRequireLicenseAcceptance`,
   `Copyright`, `RepositoryUrl`), packed `LICENSE.md` + `README.md`, and the pack targets
   (`SetPackageVersion`, `CleanOldNuGetPackages`, `PublishPackageToNuGet`).
2. Repo on GitHub + `.github/workflows/publish.yml` (push to master → pack + push,
   `-p:SkipNuGetPush=true` in the pack step).
3. Consumer uses the dual-reference pattern (`ProjectReference` with
   `Condition="Exists(...)"` + `PackageReference Version="1.*"`).
4. Check the package id on nuget.org first; use a `Graphene.*` id if taken.
5. Add the lowercase package id to the "Wait for dependency packages on NuGet" list in
   `release.yml`.
6. If it depends on `Graphene.AIOrchestrator`/`Naiad`, add
   `<Papyrine_SponsorshipLicenseIgnored>true</...>` to its consumers (SC021).
7. Release by pushing master with `IsPrerelease=false` (automatic, see "Release pipeline").

`sync-all.ps1` discovers the new repo automatically via the ProjectReference scan — no
script edits needed.
