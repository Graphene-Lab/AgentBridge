# release.ps1 — triggered by the "Push AgentBridge" status-bar menu (Release entry). It turns the
# release gate off (IsPrerelease=false), commits + pushes EVERY project with pending changes
# (sync-all.ps1, message "Update at HH:mm") and pushes master: the push itself triggers the
# wait + build + GitHub release (tag auto-created) in release.yml. Afterwards the gate is
# restored to true AND pushed, so nothing is left pending anywhere: local repos end exactly in
# sync with origin. This is safe because release.yml pins its tag to the gate-off commit
# (github.sha) and the restore commit carries [skip ci], so it creates no workflow run at all
# (no second run racing the release run in GitHub's queue — see Set-IsPrerelease).
#
# With -PreRelease it instead pushes everything keeping IsPrerelease=true: no GitHub release,
# but all pending changes are still committed and pushed, and the dependency repos still
# publish today's NuGet packages. The gate is flipped to true first when needed, so the pushed
# commit can never trigger a release. Nothing is left pending either.
#
# Any failure (sync-all error, failed push, missing core-repo tag) aborts the release,
# restores the gate to true locally (no push — a retry must start from the gate-off state)
# and rethrows the real error.
#
# Usage:  powershell -File release.ps1 [-Message "<empty-commit message>"] [-PreRelease]
#   -Message: commit message used for the empty trigger commit when the gate is already off
#             and nothing else changed (default "sync").
#   -PreRelease: commit + push everything with the gate ON (no GitHub release); flips
#             IsPrerelease to true first if the working copy has it false.
#
# Pending changes in every project (AgentBridge + all dependency repos) are committed
# automatically with "Update at HH:mm" via sync-all.ps1 in both modes.
#
# Full mechanism: docs-dev/RELEASING.md.

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
# marker shipped inside the gate-off commit (read by release.yml).
$script:coreRepos = @(
    @{ Dir = 'AIOrchestrator';   Pkg = 'graphene.aiorchestrator' },
    @{ Dir = 'UISupportGeneric'; Pkg = 'uisupportgeneric' },
    @{ Dir = 'AllToMarkdown';    Pkg = 'alltomarkdown' },
    @{ Dir = 'MermaidRendering'; Pkg = 'mermaidrendering' },
    @{ Dir = 'ReverseMarkdown';  Pkg = 'graphene.reversemarkdown' }
)

function Get-TodayVersion {
    # 1.yy.MM.dd and its NuGet-normalized form (leading zeros stripped per segment).
    $raw = '1.' + (Get-Date -Format 'yyyy.MM.dd')
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
                Write-Host "core OK (unchanged since $lastTag): $($r.Dir)"
                continue
            }
            # Changed since its last publish → today's tag is mandatory, otherwise the
            # release would silently ship the previous engine (the wait cannot help: a
            # changed-but-untagged repo never publishes today's package).
            $tagLocal = (git tag --list "v$($v.Raw)" | Out-String).Trim()
            if (-not $tagLocal) {
                throw "core repo $($r.Dir) changed since $lastTag but has no v$($v.Raw) tag — run: git -C `"$dir`" tag v$($v.Raw) && git -C `"$dir`" push origin v$($v.Raw)"
            }
            $tagRemote = git ls-remote origin "refs/tags/v$($v.Raw)" 2>$null | Select-String -Quiet "refs/tags/v$($v.Raw)"
            if (-not $tagRemote) {
                throw "core repo $($r.Dir): tag v$($v.Raw) exists locally but is NOT pushed — run: git -C `"$dir`" push origin v$($v.Raw)"
            }
            if (Test-PackageVisible $r.Pkg $v.Norm) {
                Write-Host "core OK (package $($v.Norm) visible on nuget.org): $($r.Dir)"
            } else {
                $script:needsNuGetWait = $true
                $waiting += $r.Dir
                Write-Host "core WAIT (package $($v.Norm) still propagating): $($r.Dir)"
            }
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
    # Pre-flight: every core repo changed since its last tag must carry today's tag (pushed),
    # otherwise the release would silently ship the previous engine. Also computes whether the
    # NuGet wait is needed at all (<NuGetWait> marker shipped in the gate-off commit).
    Assert-CorePackagesReady
    Set-NuGetWait $(if ($script:needsNuGetWait) { 'true' } else { 'false' })
    Set-IsPrerelease 'false'
    Invoke-SyncAll
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
