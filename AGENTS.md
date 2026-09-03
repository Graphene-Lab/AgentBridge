# AgentBridge — notes for coding agents

> The full release/NuGet mechanism (diagrams, pitfalls, integration checklist) is in
> **[docs-dev/RELEASING.md](docs-dev/RELEASING.md)** — read it before touching the release pipeline.

## ⚠️ Before ANY push or release — developer checklist

**Read and satisfy [docs-dev/RELEASE-CHECKLIST.md](docs-dev/RELEASE-CHECKLIST.md) before every
push or release** (the pre-push hook prints the reminder). It is short on purpose: the layout
rules the code enforces — no config/state json next to the executable (only SDK-generated
`agent.*.json`), persistent files under `PersistentData\` or the OS app-data folder, the app
must run when launched from any directory — must never regress. A Debug build of agent refuses
to start when a stray json sits next to the exe (AppConfig); treat that refusal as a blocker.
Do not push while any box is unchecked.

## Documentation: two types (READ BEFORE WRITING A GUIDE)

AgentBridge has **two distinct documentation sets** — they are physically separated and
treated differently by the build:

| Folder | Audience | Shipped? |
|---|---|---|
| `docs/` | **End users** of the distributed app (manual, TUI/API references, telegram, sip, autoupdate, installers, `sip-entry/`) | **YES** — copied to the build/publish output and into every release archive (`docs/` next to the executable) |
| `docs-dev/` | **Developers** working on the repository (architecture, release pipeline, TUI internals) | **NO** — repository only, never shipped |

Rules:

- **A guide read by the end user of the distributed app goes in `docs/`.** It is copied to
  the destination automatically by `AgentBridge.csproj` (the `None` items with
  `CopyToOutputDirectory` at the bottom of the file) — no extra step needed. That is the
  whole point: **if a guide is not in `docs/`, it never reaches the people who install the
  app.**
- **A document for developers only goes in `docs-dev/`.** Architecture, release pipeline,
  TUI-internals, tooling. It stays in the repository and must NOT be referenced by shipped
  user guides as a required read (a shipped `docs/` guide may link to a `docs-dev/` guide
  only as an optional "(developers, not shipped)" note).
- **`media/` is README showcase only** (demo gifs/mp4, screenshots used by the repository
  README) — never shipped.
- Before writing any guide, ask: *who reads this — the person who installs the app, or the
  developer who maintains the code?* User → `docs/`. Developer → `docs-dev/`.

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
docs-dev/RELEASING.md "How the release wait works".

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
(pushing the release trigger) and pushes the gate restore to `true` afterwards — nothing is
left pending: the local repos end exactly in sync with origin (release.yml pins its tag to the
gate-off commit, and the restore push's own run is skipped by the gate). **PreRelease** keeps
the gate on (flipping it first if needed), so all changes are committed and pushed and no
GitHub release is created. Both modes fail loudly: `sync-all.ps1` lists the failing repos as
`FAILED:` lines and aborts, so a partial sync is never mistaken for a clean one.

**The push itself (always runs):** a `pre-push` hook (installed via `install-hooks.ps1`, template
in `hooks/pre-push`) runs `sync-all.ps1` automatically whenever AgentBridge master is pushed to
origin — so a plain `git push` syncs the dependency repos (commits + pushes their pending
changes) before/while the release workflow waits for their packages. The hook is skipped for
tags, other branches/remotes, and re-entrant pushes (guard: `SYNC_ALL_ACTIVE`); a failing
nested sync aborts the push.

**Manually (equivalent steps):**
1. `powershell -File sync-all.ps1 -Message "<commit message>"` — commits and pushes
   AgentBridge plus every repo it depends on (recursively via ProjectReference; new
   dependency repos are discovered automatically).
2. Each dependency repo's `.github/workflows/publish.yml` (trigger: `v*` tag push) packs and
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
automatic update + release system (full details in docs-dev/RELEASING.md):

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
