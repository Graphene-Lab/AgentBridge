# AgentBridge — notes for coding agents

## Release gate: IsPrerelease flag

`AgentBridge.csproj` carries `<IsPrerelease>` (default `true`). It decides whether a GitHub
release is produced:

- `true` → version is the date-based `1.yy.MM.dd` (e.g. `1.26.08.09`); the tag
  `v1.yy.MM.dd` triggers the full release build (release.yml).
- `false` → the version gets a `-prerelease` suffix and the release workflow skips
  (`check-version` job guards the build matrix).

Set the flag to `true` only when the test cycles proved the version works; keep it `false`
while iterating. Do not change the version scheme — it is date-based like UISupportBlazor.

## Release pipeline

1. `powershell -File sync-all.ps1 -Message "<commit message>"` — commits and pushes
   AgentBridge plus every repo it depends on (recursively via ProjectReference; new
   dependency repos are discovered automatically).
2. Each dependency repo's `.github/workflows/publish.yml` (trigger: push to master) packs and
   pushes its NuGet package (`1.*` floating version, `--skip-duplicate` — idempotent).
3. When the version is release-ready, tag: `git tag v1.yy.MM.dd` + push the tag →
   release.yml builds the 4 platform archives and attaches them to the GitHub release.

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

`CopyTtsAssetsToPublish` (csproj) copies `kokoro.onnx` + `voices/` + `espeak/` from
`$(OutputPath)` into the publish directory: `dotnet publish -o <dir>` does not carry them
over on its own, and the release archives ship them so TTS works out of the box.

## Known platform constraint

No linux-arm64 release: KokoroSharp 0.6.7 ships no `espeak-ng-linux-arm64` binary, so TTS
would be broken on ARM64 Linux. If that changes (new KokoroSharp or a bundled ARM64
espeak-ng), re-add the `linux-arm64` matrix cell in release.yml.
