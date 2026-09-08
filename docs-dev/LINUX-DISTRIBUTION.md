# Linux distribution — how AgentBridge reaches Linux users (technical reference)

Status: 2026-09-08. This is the developer-facing documentation for the Linux packaging
that surrounds the AgentBridge release pipeline. The engine code itself is unchanged;
all packaging lives in **separate repositories/workflows** and is driven automatically by
the normal GitHub release.

Two distribution channels exist:

1. **Self-hosted Flatpak bundles** on GitHub (`Graphene-Lab/AgentBridge-Linux`) — built
   automatically at every release, x86_64 + aarch64.
2. **Flathub store submission** (`flathub/flathub` PR) — same product, but the engine is
   **compiled from source** on the Flathub infra (their policy: no prebuilt-archive
   wrapping). PR #10129, under review.

The user-facing docs live in `docs/` and in the repos' READMEs; everything below is the
"how it actually works" reference for maintenance.

---

## Repositories / files

| Where | What |
|---|---|
| `Graphene-Lab/AgentBridge-Linux` (public) | Flatpak packaging + auto-publish pipeline |
| `…/AgentBridge-Linux/.github/workflows/linux-release.yml` | Release pipeline (x86_64 job + aarch64 job) |
| `…/AgentBridge-Linux/.github/workflows/sourcebuild-proof.yml` | Experimental engine-from-source build (validates the Flathub model) |
| `…/AgentBridge-Linux/packaging/agentbridge.yml.tmpl` | Manifest template for the release pipeline (archive-based, per-arch) |
| `…/AgentBridge-Linux/packaging/branding/` | `agent-run` (TUI launcher), `agent-desktop` (WebKitGTK desktop wrapper), `.desktop` entries, metainfo, giraffe logo |
| `…/AgentBridge-Linux/packaging/flathub/` | Drop-in directory + generators for the Flathub submission |
| `Graphene-Lab/AgentBridge` docs-dev/ | this document |

Both repos are public; the sibling private repos (AIOrchestrator etc.) are never needed
by the Linux build — the engine restores them from the public NuGet packages.

---

## Channel 1 — self-hosted Flatpak (release pipeline)

### Trigger / automation (zero secrets)

`linux-release.yml` runs **hourly** (`cron '23 * * * *'`) plus `workflow_dispatch`
(`version` input, `force` input). No PAT/cross-repo dispatch is used anywhere: the
resolve job reads `Graphene-Lab/AgentBridge` releases **anonymously** (sending the
repo-scoped `GITHUB_TOKEN` to another repo returns 404 — never do that).

Skip logic is **completeness-based**: the run is skipped only when the existing release
tag already carries **both** architecture assets (`agentbridge-linux-x86_64.flatpak` +
`agentbridge-linux-aarch64.flatpak`). A partial release (e.g. the aarch64 job failed
after x86_64 published) is completed by the next poll instead of being skipped forever.

### Jobs

- **resolve** — latest upstream tag vs our tag + asset check; client (GiraffeAI) version.
- **build (x86_64)** — runs in the official container
  `ghcr.io/flathub-infra/flatpak-github-actions:gnome-48` (`--privileged`), which ships
  flatpak/flatpak-builder/rsvg-convert/xvfb-run. Steps: engine+client archive **sha
  digests** from the GitHub API `digest` field (no ~400 MB double downloads) → render
  manifest from the template → generate giraffe icons → flatpak-builder action → doctor
  smoke → **creates the GitHub Release** and uploads x86_64 assets (asset names are
  unique per release → pre-existing assets are deleted before re-upload on force runs).
- **build-aarch64** — host runner (`needs: build`), flatpak via apt, aarch64 binfmt via
  `docker/setup-qemu-action@v3`, aarch64 GNOME runtime/SDK installed system-wide,
  `flatpak-builder --arch=aarch64` via sudo; appends its assets to the release created by
  the x86_64 job.

Container-job gotchas that bit us (keep them in mind when editing):
`${{ github.workspace }}` is the HOST path → use relative paths; `GITHUB_TOKEN` is NOT
injected → export `GH_TOKEN: ${{ github.token }}` in the workflow env; the
flatpak-builder action input names are `manifest-path`/`repo-dir` (v6), not
`manifest`/`repo`.

### Runtime model (both channels)

- `/app/lib/agentbridge` is the read-only payload image; `/app` is read-only in a
  Flatpak, but the engine persists its user config under `PersistentData/` **next to the
  executable** (AppConfig). The launchers (`agent-run` sh, `agent-desktop` python)
  therefore **materialize** the image once into
  `$XDG_DATA_HOME/agentbridge/engine` (per-app data dir) and **overlay-refresh**
  binaries/docs when the `agentbridge-version.txt` marker changes — never touching
  `PersistentData` (it is not part of any archive).
- The engine is **version-pinned**: the launchers append `--no-update` when invoked with
  no arguments. The new version arrives with the next Flatpak build, not via the engine's
  in-app auto-update.
- `agent-desktop` starts the engine `--headless` if `/health` on the configured port is
  not answering, serves the bundled GiraffeAI client on a random localhost port and opens
  it in a WebKitGTK window auto-connected via the same `?provider=` handshake the TUI
  `/web` command uses. `agent-desktop --doctor` is the headless CI smoke entry point
  (probes run in isolated subprocesses because `gi.require_version` is process-global).
- WebKitGTK is present in `org.gnome.Platform//48` (verified); the desktop window works
  under WSLg, which is how real-machine verification is done from a Windows dev box
  (install flatpak in the WSL distro, `flatpak install` the bundle, run
  `agent-desktop --doctor`, launch headless + `agent-desktop` and check processes/ports).

### Metainfo / AppStream

- Icons: white giraffe on the brand gradient (`#6c5ce7 → #3b2f9e`), rendered from
  `giraffe.svg` at 64/128/256/512 (release CI uses `rsvg-convert`).
- Demo screenshot is a **WebM/VP9 video** (`media/office-manager-demo.webm`, transcoded
  from the mp4 — not the gif): AppStream `<video>` accepts only WebM/Matroska with
  VP9/AV1, never mp4, and a video screenshot cannot be the default one.

---

## Channel 2 — Flathub submission (source build)

Flathub builds from source only. The model below was validated end-to-end by the
`sourcebuild-proof.yml` workflow (bundle ≈662 MB, engine compiled from source, NuGet
offline, doctor OK) before the submission PR was opened.

Submission: **flathub/flathub#10129** (branch `new-pr`, app id
`io.github.graphene_lab.agentbridge`). Update path afterwards: the manifest git source
carries `x-checker-data` (git tag pattern `^v([\d.]+)$`), so flathubbot opens the update
PRs automatically; a maintainer then re-runs the pinned-hash generators (see
`packaging/flathub/README.md` in AgentBridge-Linux) and bumps the payload/feed hashes.

### Manifest design decisions (do not change casually)

- **Runtime `org.gnome.Platform//50`** (store submission; 48 is EOL per the Flathub
  linter). The self-hosted channel still uses 48 — both need the WebKitGTK GUI that only
  the GNOME runtime provides (verified in the doctor smoke test). 
- **Official .NET SDK 10.0.400 tarball as a module source**: the
  `org.freedesktop.Sdk.Extension.dotnet10` extension belongs to the freedesktop SDK, not
  the GNOME one; the tarball works with the GNOME runtime and makes the build SDK
  identical to the SDK that generated the NuGet feed.
- **Offline NuGet**: 114 packages inlined in the manifest (one `file` source per nupkg,
  sha512 in **hex**). flatpak-builder 1.2 (Debian) has **no build-time network**, no
  `--allow-network-access`, and **cannot expand a nested sources JSON** — the feed must
  be merged into the manifest at generation time.
- **Feed must be generated on Linux with the same SDK (10.0.400)**: a Windows restore
  produces a different, broken feed (missing `Microsoft.ML.OnnxRuntime.Gpu.Linux` and
  `Microsoft.NET.ILLink.Tasks`).
- **`kokoro.onnx`** (325 MB) is a `file` source dropped at the repo root so the csproj
  `DownloadKokoroModel` target copies it (build commands have no network).
- **`agentbridge-payload` module**: `Tools/` plugins and the `.playwright` driver are
  prebuilt data overlaid from the same version's official release archive; only data
  folders are copied, never the prebuilt `agent`.
- **Sandbox differs per channel**: the store submission uses the narrowed finish-args the
  Flathub linter requires (wayland + fallback-x11, xdg-documents/xdg-download only; no
  `--filesystem=home`, no explicit X11, no xdg-cache RW) while the self-hosted release
  bundle keeps the broader sandbox. `flatpak-builder-lint` (manifest + appstream) is run
  in CI before submissions (`flathub-lint.yml`, image `ghcr.io/flathub/flatpak-builder-lint`).
- `flathub.json` skips aarch64 until an arm64 feed is pinned too.

### Updating a new version on Flathub

1. AgentBridge tag `v1.yy.MM.dd` published.
2. Update the pins in `packaging/flathub/build-submission.py`
   (`--version`, `--commit` from `git ls-remote`, `--engine-sha256` from the release
   asset digest, client version/hash) — see `packaging/flathub/README.md`.
3. Regenerate the feed on Linux (same SDK), regenerate the manifest, commit into the
   `flathub/flathub` checkout (branch from `new-pr`), push, PR.

---

## Regression checklist after touching packaging

- `agent-desktop --doctor` still reports `WebKit (GTK4): OK` and `engine image: OK`
  inside the built bundle.
- Release run produces **both** arch assets (skip logic depends on it).
- Force rebuild of an existing version replaces assets (delete-before-upload).
- Flatpak install + desktop entries + icons verified on a real Linux/WSLg desktop.
- Metainfo passes appstream validation (icons present, video screenshot non-default).
