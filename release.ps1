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
# Any failure (sync-all error, failed push) aborts the release, restores the gate to true
# locally (no push — a retry must start from the gate-off state) and rethrows the real error.
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
    # Success: restore the gate and push it too — the button must leave nothing pending.
    # The restore commit carries [skip ci] so it creates NO workflow run: the release.yml run
    # of the gate-off commit stays the only one in the queue (a second run seconds later
    # raced GitHub's queue on 2026-08-26 — the runs were created in inverted order and the
    # gate-off run was failed while queued, so the release never happened).
    Set-IsPrerelease 'true' -Push -SkipCi
} catch {
    # Failure: restore the gate locally only (no push — a retry must start from the gate-off
    # state) and rethrow the real error so the terminal shows the actual cause.
    try { Set-IsPrerelease 'true' } catch { Write-Warning "could not restore IsPrerelease=true — set it manually: $_" }
    throw
}
Write-Host "Done. Release runs in GitHub Actions (https://github.com/Graphene-Lab/AgentBridge/actions)."
