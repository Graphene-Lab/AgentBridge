<#
release.ps1 — the "Release" entry point of the "Push AgentBridge" status-bar menu. It turns
the release gate off, commits + pushes every project with pending changes (AgentBridge and
all dependency repos, via sync-all.ps1), makes sure every changed core repo carries today's
pushed v-tag, and pushes master so release.yml produces the GitHub release. Afterwards it
restores the gate and pushes it too: nothing is left pending anywhere, and local repos end
exactly in sync with origin.

== VERSION SCHEME AND THE GATE ==

Every project (AgentBridge + dependencies) versions itself as 1.yy.MM.dd, computed from the
build date at pack time. NuGet normalizes leading zeros (1.26.08.09 -> 1.26.8.9): the
flat-container index, the package-visibility checks and the release.yml wait step all use
the normalized form, while git tags use the raw form (v1.26.08.09).

The release gate is the <IsPrerelease> element in AgentBridge.csproj:
  false -> version 1.yy.MM.dd; a master push runs release.yml, which builds the archives,
           creates the GitHub release and pins the tag v1.yy.MM.dd to the triggering commit
           (github.sha).
  true  -> version 1.yy.MM.dd-prerelease; release.yml's check-version job sees the suffix,
           skips the build and creates no release.
The gate is the only switch: no tag push is needed for the AgentBridge release itself.

== THE DEPENDENCY MODEL ==

AgentBridge and AIOrchestrator reference their dependencies with the dual-reference pattern:
  <ProjectReference Include="..\X\X.csproj" Condition="Exists('..\X\X.csproj')" />
  <PackageReference Include="X" Version="1.*" />
The local sibling project wins in solution builds; the CI (public) never checks out the
private sibling repos, so it restores the published packages (1.* floating = latest).

The core repos (the dependency tree hard-coded in $script:coreRepos) publish their packages
ONLY on a v* tag push of their own repo (per-repo publish.yml). A plain master push never
publishes. Their packages are date-versioned: tag v1.26.08.28 produces 1.26.8.28 on
nuget.org. Consequence: a release restores today's version of every dependency that CHANGED
today, and the latest available version for the ones that did not (identical to today's for
an unchanged repo).

== PRE-FLIGHT: Assert-CorePackagesReady ==

Before touching anything, the script runs a read-only pre-flight over the 5 core repos to
guarantee a release can never silently ship the previous engine (the 2026-08-26 incident:
AIOrchestrator had 14 commits and AllToMarkdown 1 commit since v1.26.08.21 that were NOT in
the release until they got tagged). For each repo it computes "changed" = commits since the
last reachable tag, or a dirty worktree:

  unchanged -> OK. Exception: when the last tag IS today's tag, a just-published package may
               still be propagating (the 2026-08-27 CS0117 incident: the floating 1.*
               restore resolved the previous version); today's package visibility on
               nuget.org then decides the NuGet wait marker.
  changed   -> today's tag must be on origin.
                 - NOT on origin: the repo is recorded in $script:tagActions (auto-tagged
                   later by Publish-CoreTags) and the NuGet wait is forced on — the package
                   does not exist yet, it will be published by the tag push.
                 - ALREADY on origin: today's package is (or is being) published from that
                   tag and is immutable on nuget.org, so any commits or pending changes
                   beyond it cannot ship today. The script ABORTS with a clear message:
                   re-tagging the same version is impossible, those changes can only ship
                   with tomorrow's version. (This also covers the case where HEAD sits on
                   the tag but the worktree is dirty: sync-all would commit those files
                   AFTER this check, moving HEAD past the immutable tag.)

The pre-flight also computes $script:needsNuGetWait, which becomes the <NuGetWait> marker
(true|false) written into AgentBridge.csproj. release.yml runs its 30-minute
dependency-wait step ONLY when the marker is true. The marker's conservative default is
true, so a manual gate-off push that bypasses release.ps1 still waits.

== RELEASE MODE — STEP BY STEP ==

1. Assert-CorePackagesReady — read-only pre-flight (above). Records which core repos need
   today's tag and computes the NuGet wait.
2. Set-NuGetWait            — writes <NuGetWait>true|false> into the csproj.
3. Set-IsPrerelease 'false' — commits ONLY AgentBridge.csproj (git commit -- <path>, never
   a bare commit, so unrelated staged changes stay in the sync commit): the gate-off commit.
   It carries the NuGetWait marker and is the commit release.yml pins the tag to.
4. Invoke-SyncAll           — commits ("Update at HH:mm") + pushes every project with
   pending changes, dependency repos first, AgentBridge last. The AgentBridge master push
   (containing the gate-off commit) is what triggers release.yml. sync-all exits non-zero
   on any failure and aborts the release: a partial sync is never silent.
5. Publish-CoreTags         — for every repo recorded in step 1: creates today's tag at the
   FULLY-SYNCED HEAD (git tag -f, so a stale local tag is force-moved — it was never on
   origin, nothing published depends on it) and pushes it. The tag push fires the repo's
   publish.yml, which publishes today's package. The tag MUST be created AFTER sync-all,
   otherwise it would point at a commit missing the pending changes and the package would
   be built from stale code while master carries the changes. This is the auto-tag fix
   (2026-08-28): previously the script aborted and told the user to run `git tag && git
   push origin <tag>` by hand, repo after repo.
6. Empty-commit fallback    — if NOTHING was pushed so far (gate was already off AND no
   pending changes anywhere), origin/master did not move and release.yml stayed
   untriggered: push an empty commit (message -Message, default "sync") to fire it.
   Comparing origin/master before vs after is the correct "was it triggered?" check — a
   rev-list count is always 0 once sync-all has pushed, which caused a duplicate trigger
   commit + a double push (two release builds).
7. Run-wait + dispatch      — GitHub has intermittently failed to create push-triggered
   runs of release.yml after a workflow-file change (2026-08-26: five clean pushes produced
   no run at all, while workflow_dispatch always worked). The script polls `gh run list`
   for the gate-off commit's run for ~2 minutes and, if absent, falls back to
   `gh workflow run release.yml` — the same release path, provably reliable. The dispatch
   reads the gate from master HEAD, still the gate-off commit at this point (the restore
   push comes afterwards).
8. Restore                   — Set-NuGetWait back to true (conservative default) and
   Set-IsPrerelease 'true' -Push -SkipCi: the restore commit carries [skip ci], so it
   creates NO workflow run. This is deliberate: a second run created seconds later raced
   GitHub's queue on 2026-08-26 — the runs were created in inverted order and the gate-off
   run was failed while queued, so the release never happened. The [skip ci] restore leaves
   the gate-off run as the ONLY one in the queue (release.yml also has a concurrency group
   with cancel-in-progress:false). release.yml pins its tag to github.sha, so the restore
   push cannot move it either.

== PRERELEASE MODE (-PreRelease) ==

Everything is committed + pushed with the gate ON (flipped to true first if the working copy
has it false): no GitHub release. Because publish.yml triggers only on v* tag pushes, this
path publishes NO dependency package either — packages are published by the next real
release, whose pre-flight tags the changed repos automatically. Nothing is left pending.

== FAILURE HANDLING ==

Any failure (sync-all error, failed push, core-repo tag push, immutable-tag abort) falls
into the catch block: the gate and the NuGetWait marker are restored locally ONLY (no push —
a retry must re-run the whole flow from the gate-off state) and the REAL error is rethrown,
so the terminal shows the actual cause instead of a generic wrapper.

== USAGE ==

powershell -File release.ps1 [-Message "<empty-commit message>"] [-PreRelease]
  -Message    commit message for the empty trigger commit (step 6), default "sync".
  -PreRelease commit + push everything with the gate ON (no GitHub release).

Full mechanism, storage tiers and pitfalls: docs-dev/RELEASING.md.
#>

param(
    [string]$Message = "sync",
    [switch]$PreRelease
)

$ErrorActionPreference = 'Stop'
# PowerShell 7.3+ turns native-command stderr into terminating errors when
# $ErrorActionPreference=Stop, but git writes to stderr for benign cases (push
# progress, "remote: warning: Deleting a non-existent ref.", "Updated tag ...").
# $LASTEXITCODE is the source of truth for git failures — not stderr.
$PSNativeCommandUseErrorActionPreference = $false
# Every push made by this script is "internal": sync-all has already synced the dependency
# tree (or it was clean), so the pre-push hook's nested sync would be redundant and could
# fail the push. The hook keeps running for plain manual pushes, which is its purpose.
$env:SYNC_ALL_ACTIVE = '1'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csprojPath = Join-Path $root 'AgentBridge.csproj'
$gateRegex = '<IsPrerelease>\s*(true|false)\s*</IsPrerelease>'
$nugetWaitRegex = '<NuGetWait>\s*(true|false)\s*</NuGetWait>'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$syncMsg = "Update at $(Get-Date -Format HH:mm)"

function Set-IsPrerelease([string]$value, [switch]$Push, [switch]$SkipCi) {
    $content = [regex]::Replace([System.IO.File]::ReadAllText($csprojPath), $gateRegex, "<IsPrerelease>$value</IsPrerelease>", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    [System.IO.File]::WriteAllText($csprojPath, $content, $utf8NoBom)
    Push-Location $root
    try {
        git add AgentBridge.csproj
        if ($LASTEXITCODE -ne 0) { throw "git add failed (exit $LASTEXITCODE)" }
        git diff --cached --quiet HEAD -- AgentBridge.csproj
        if ($LASTEXITCODE -ne 0) {
            # Commit ONLY the csproj (-- <path>): a bare "git commit" would sweep unrelated
            # staged changes into the gate commit — they belong in the sync commit instead.
            # The RESTORE commit (gate back on) carries [skip ci]: the release.yml run of the
            # gate-off commit must be the ONLY run in the queue. A second run created seconds
            # later (the restore push) raced GitHub's queue on 2026-08-26 — the runs were
            # created in inverted order and the gate-off run was failed while queued, so the
            # release never happened. [skip ci] makes the restore push create NO run at all.
            $msg = "chore: IsPrerelease=$value"
            if ($SkipCi) { $msg += " [skip ci]" }
            git commit -m $msg -- AgentBridge.csproj | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)" }
        }
        if ($Push) {
            git push origin master
            if ($LASTEXITCODE -ne 0) { throw "git push failed (exit $LASTEXITCODE)" }
        }
    } finally {
        Pop-Location
    }
}

# Commit + push every project with pending changes (AgentBridge + dependency repos).
function Invoke-SyncAll {
    Push-Location $root
    try {
        & (Join-Path $root 'sync-all.ps1') -Message $syncMsg
        if ($LASTEXITCODE -ne 0) { throw "sync-all failed — fix the FAILED repos listed above and re-run" }
    } finally {
        Pop-Location
    }
}

# The AgentBridge build restores Graphene.AIOrchestrator + its transitive deps from NuGet
# (the CI is public, the sibling repos are private). Those packages are date-versioned and
# published ONLY on a v* tag push of their repo (publish.yml). The 30-min NuGet wait in
# release.yml is therefore needed ONLY when a core repo changed since its last tag AND its
# today's package is still propagating on nuget.org. Assert-CorePackagesReady computes that:
# a changed core repo REQUIRES a pushed today-tag — otherwise the release would silently ship
# the previous engine — and the resulting $script:needsNuGetWait becomes the <NuGetWait>
# marker shipped inside the gate-off commit (read by release.yml). Missing today-tags are
# created + pushed automatically after the sync (Publish-CoreTags), at the commit that
# contains the pending changes.
$script:coreRepos = @(
    @{ Dir = 'AIOrchestrator';   Pkg = 'graphene.aiorchestrator' },
    @{ Dir = 'UISupportGeneric'; Pkg = 'uisupportgeneric' },
    @{ Dir = 'AllToMarkdown';    Pkg = 'alltomarkdown' },
    @{ Dir = 'MermaidRendering'; Pkg = 'mermaidrendering' },
    @{ Dir = 'ReverseMarkdown';  Pkg = 'graphene.reversemarkdown' }
)

function Get-TodayVersion {
    # 1.yy.MM.dd and its NuGet-normalized form (leading zeros stripped per segment).
    $raw = '1.' + (Get-Date -Format 'yy.MM.dd')
    $norm = (($raw -split '\.') | ForEach-Object { [string][int]$_ }) -join '.'
    return @{ Raw = $raw; Norm = $norm }
}

function Test-PackageVisible([string]$pkg, [string]$ver) {
    try {
        $idx = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 20 "https://api.nuget.org/v3-flatcontainer/$pkg/index.json").Content
        return $idx.Contains('"' + $ver + '"')
    } catch {
        return $false
    }
}

function Assert-CorePackagesReady {
    $v = Get-TodayVersion
    $script:needsNuGetWait = $false
    $waiting = @()
    $script:tagActions = @()
    foreach ($r in $script:coreRepos) {
        $dir = Join-Path $root "..\$($r.Dir)"
        Push-Location $dir
        try {
            $lastTag = (git describe --tags --abbrev=0 2>$null | Out-String).Trim()
            $changed = $false
            if ($lastTag) {
                $ahead = (git rev-list --count "$lastTag..HEAD" 2>$null | Out-String).Trim()
                if ($ahead -match '^\d+$' -and [int]$ahead -gt 0) { $changed = $true }
            } else {
                $changed = $true
            }
            if (-not $changed) {
                $dirty = @(git status --porcelain)
                if ($dirty.Count -gt 0) { $changed = $true }
            }
            if (-not $changed) {
                # A today-tag at HEAD means today's package was just published and may still be
                # propagating (2026-08-27: the floating 1.* restore resolved the previous
                # version → CS0117). Verify visibility even though the repo is unchanged.
                if ($lastTag -eq "v$($v.Raw)") {
                    if (Test-PackageVisible $r.Pkg $v.Norm) {
                        Write-Host "core OK (package $($v.Norm) visible on nuget.org): $($r.Dir)"
                    } else {
                        $script:needsNuGetWait = $true
                        $waiting += $r.Dir
                        Write-Host "core WAIT (package $($v.Norm) still propagating): $($r.Dir)"
                    }
                } else {
                    Write-Host "core OK (unchanged since $lastTag): $($r.Dir)"
                }
                continue
            }
            # Changed since its last publish → today's tag is mandatory, otherwise the
            # release would silently ship the previous engine (the wait cannot help: a
            # changed-but-untagged repo never publishes today's package).
            $todayTag = "v$($v.Raw)"
            $tagOnOrigin = git ls-remote origin "refs/tags/$todayTag" 2>$null | Select-String -Quiet "refs/tags/$todayTag"
            if ($tagOnOrigin) {
                # Already pushed: today's package was (or is being) published from it and is
                # immutable on nuget.org, yet the repo still has changes beyond it — either
                # HEAD is ahead, or sync-all will commit pending files after this check. The
                # release would silently ship the engine WITHOUT those changes; they can only
                # ship with tomorrow's version (the same version cannot be republished).
                throw "core repo $($r.Dir): tag $todayTag is already on origin but the repo has commits/pending changes beyond it — today's package $($v.Norm) is already published and immutable, so this release would silently ship the engine WITHOUT them (they can only ship with tomorrow's version). Aborting."
            }
            # Not on origin yet: create + push it AFTER the sync (Publish-CoreTags), so the
            # tag points at the exact commit the release restores (pending changes included).
            # A stale local tag is force-moved — it was never published, nothing depends on it.
            $script:tagActions += @{ Dir = $r.Dir; Tag = $todayTag }
            $script:needsNuGetWait = $true
            $waiting += $r.Dir
            Write-Host "core TAG (tag $todayTag created + pushed after the sync): $($r.Dir)"
        } finally {
            Pop-Location
        }
    }
    if ($script:needsNuGetWait) {
        Write-Host "NuGet wait needed for: $($waiting -join ', ')"
    } else {
        Write-Host "No NuGet wait needed — release will skip the 30-min dependency wait."
    }
}

# Create + push today's tag on every core repo that changed since its last publish (recorded
# by Assert-CorePackagesReady). Runs AFTER Invoke-SyncAll: the tag must point at the commit
# that contains the pending changes (sync-all committed + pushed them), otherwise today's
# package would be built from a stale commit while master carries the changes. A stale local
# tag is force-moved — it was never on origin, so nothing published depends on it.
function Publish-CoreTags {
    foreach ($a in $script:tagActions) {
        $dir = Join-Path $root "..\$($a.Dir)"
        Push-Location $dir
        try {
            git tag -f $a.Tag
            if ($LASTEXITCODE -ne 0) { throw "git tag -f $($a.Tag) failed in $($a.Dir) (exit $LASTEXITCODE)" }
            git push origin $a.Tag
            if ($LASTEXITCODE -ne 0) { throw "git push origin $($a.Tag) failed in $($a.Dir) (exit $LASTEXITCODE)" }
            Write-Host "core tagged + pushed $($a.Tag): $($a.Dir)"
        } finally {
            Pop-Location
        }
    }
}

function Set-NuGetWait([string]$value) {
    $content = [regex]::Replace([System.IO.File]::ReadAllText($csprojPath), $nugetWaitRegex, "<NuGetWait>$value</NuGetWait>", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    [System.IO.File]::WriteAllText($csprojPath, $content, $utf8NoBom)
}

# PreRelease: push the current state WITHOUT releasing. The pushed commit must keep
# IsPrerelease=true (release.yml would skip the GitHub release) — flip it first if the working
# copy has it false, then commit + push everything (sync-all: deps publish today's NuGet).
if ($PreRelease) {
    Write-Host "=== PreRelease: IsPrerelease=true + commit/push all pending changes (no GitHub release) ==="
    Set-IsPrerelease 'true'
    Invoke-SyncAll
    Write-Host "Done. No GitHub release: everything pushed with the prerelease gate on."
    exit 0
}

# Release: turn the gate off, commit every pending change and push → release.yml runs.
try {
    Write-Host "=== Release: IsPrerelease=false + commit/push all pending changes (release trigger) ==="
    $originBefore = (git rev-parse origin/master).Trim()
    # Pre-flight: every core repo changed since its last tag must carry today's tag (pushed) —
    # missing tags are created + pushed automatically after the sync (Publish-CoreTags) —
    # otherwise the release would silently ship the previous engine. Also computes whether the
    # NuGet wait is needed at all (<NuGetWait> marker shipped in the gate-off commit).
    Assert-CorePackagesReady
    Set-NuGetWait $(if ($script:needsNuGetWait) { 'true' } else { 'false' })
    Set-IsPrerelease 'false'
    Invoke-SyncAll
    # Core repos changed since their last publish: create + push today's tag now, at the
    # fully-synced HEAD (sync-all committed + pushed the pending changes). The tag push
    # triggers the repo's publish.yml; the <NuGetWait> marker set above makes release.yml
    # wait for the package to propagate.
    Publish-CoreTags
    # sync-all pushed master whenever anything changed (the "chore: IsPrerelease=false" commit
    # or pending work) — that push already triggered release.yml. Only when NOTHING was pushed
    # (gate already off + no pending changes) does no ref move and the workflow stay
    # untriggered: push an empty commit to fire it. Comparing origin/master before vs after is
    # the correct "was it triggered?" check — a rev-list count is always 0 once sync-all has
    # pushed, which caused a duplicate trigger commit + a double push (two release builds).
    Push-Location $root
    try {
        if ((git rev-parse origin/master).Trim() -eq $originBefore) {
            git commit --allow-empty -m $Message | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)" }
            git push origin master
            if ($LASTEXITCODE -ne 0) { throw "git push failed (exit $LASTEXITCODE)" }
        }
    } finally {
        Pop-Location
    }
    # GitHub has intermittently failed to create push-triggered runs of release.yml after a
    # workflow-file change (2026-08-26: five clean pushes produced no run at all, while
    # workflow_dispatch always worked). Wait for the release run of the gate-off commit to
    # materialize; if it does not within ~2 minutes, trigger it via workflow_dispatch — the
    # same release path, provably reliable. The dispatch reads the gate from master HEAD,
    # still the gate-off commit at this point (the restore push below comes afterwards).
    $triggerSha = (git rev-parse origin/master).Trim()
    $runSeen = $false
    for ($i = 0; $i -lt 20 -and -not $runSeen; $i++) {
        try {
            $runs = gh run list --repo Graphene-Lab/AgentBridge --workflow=release.yml --json headSha 2>$null | ConvertFrom-Json
            $runSeen = [bool]($runs | Where-Object { $_.headSha -eq $triggerSha })
        } catch { }
        if (-not $runSeen) { Start-Sleep -Seconds 6 }
    }
    if (-not $runSeen) {
        Write-Host "release run not created by the push within 2 min — falling back to workflow_dispatch"
        gh workflow run release.yml --repo Graphene-Lab/AgentBridge
        if ($LASTEXITCODE -ne 0) { throw "gh workflow run failed (exit $LASTEXITCODE) — the release may not have started" }
    }
    # Success: restore the gate and push it too — the button must leave nothing pending.
    # The restore commit carries [skip ci] so it creates NO workflow run: the release.yml run
    # of the gate-off commit stays the only one in the queue (a second run seconds later
    # raced GitHub's queue on 2026-08-26 — the runs were created in inverted order and the
    # gate-off run was failed while queued, so the release never happened). The NuGetWait
    # marker is reset to its conservative default (true) so a manual gate-off push without
    # release.ps1 still waits.
    Set-NuGetWait 'true'
    Set-IsPrerelease 'true' -Push -SkipCi
} catch {
    # Failure: restore the gate locally only (no push — a retry must start from the gate-off
    # state) and rethrow the real error so the terminal shows the actual cause.
    try { Set-NuGetWait 'true'; Set-IsPrerelease 'true' } catch { Write-Warning "could not restore IsPrerelease=true — set it manually: $_" }
    throw
}
Write-Host "Done. Release runs in GitHub Actions (https://github.com/Graphene-Lab/AgentBridge/actions)."
