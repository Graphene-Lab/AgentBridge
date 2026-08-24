# sync-all.ps1 — commit + push AgentBridge and every repo it depends on (recursively via
# ProjectReference). A plain push is a code-only sync: the dependency repos' publish.yml
# triggers ONLY on v* tag pushes, so no NuGet package is published by this script. For a
# real release, push a v1.yy.MM.dd tag on each dependency repo (in dependency order) and
# then push AgentBridge with IsPrerelease=false — see docs-dev/RELEASING.md.
# New projects added to the dependency tree are picked up automatically (no script edits).
#
# Exit code: 0 when every repo was committed + pushed, 1 when any repo failed (each failing
# repo is printed as a "FAILED:" line and "FAILED repos:" lists them). Callers (release.ps1,
# the pre-push hook) abort on a non-zero exit so a partial sync is never mistaken for a
# clean one.
#
# Usage: powershell -File sync-all.ps1 [-Message "release prep"] [-Branch master] [-SkipSelf]
param(
    [string]$Message = "sync",
    [string]$Branch = "",
    [switch]$SkipSelf
)

# Guard: when sync-all is invoked by the pre-push hook, mark the process so the nested
# git pushes it performs (which re-trigger the hook) skip the sync — avoids recursion.
$env:SYNC_ALL_ACTIVE = '1'

$ErrorActionPreference = 'Stop'
# git writes benign progress/warnings to stderr; only $LASTEXITCODE decides failure.
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$visited = New-Object 'System.Collections.Generic.HashSet[string]'
# Safety net for 'git add -A': a commit touching far more files than the whole source tree
# is build output that escaped .gitignore (e.g. a publish/ folder). Abort that repo instead
# of committing it — the caller must add the directory to .gitignore first.
$MaxStagedFiles = 1000

function Get-ChildProjects([string]$csproj) {
    $results = @()
    $dir = Split-Path -Parent $csproj
    foreach ($line in [System.IO.File]::ReadAllLines($csproj)) {
        if ($line -match 'ProjectReference Include="([^"]+)"') {
            $full = [System.IO.Path]::GetFullPath((Join-Path $dir $Matches[1]))
            if ([System.IO.File]::Exists($full)) {
                $results += $full
                $results += Get-ChildProjects $full
            }
        }
    }
    return $results
}

function Sync-Repo([string]$repoDir) {
    Push-Location $repoDir
    try {
        $status = git status --porcelain
        if ($status) {
            git add -A
            if ($LASTEXITCODE -ne 0) { throw "git add failed (exit $LASTEXITCODE)" }
            $stagedCount = @(git diff --cached --name-only).Count
            if ($stagedCount -gt $MaxStagedFiles) {
                Write-Host "FAILED: $repoDir — $stagedCount files staged by 'git add -A' (limit $MaxStagedFiles). Build output not covered by .gitignore? Add it and re-run." -ForegroundColor Red
                return $false
            }
            git commit -m $Message | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE)" }
            Write-Host "committed: $repoDir"
        } else {
            Write-Host "clean:     $repoDir"
        }
        if ($Branch) { git push origin $Branch } else { git push }
        if ($LASTEXITCODE -ne 0) { throw "git push failed (exit $LASTEXITCODE)" }
        Write-Host "pushed:    $repoDir"
        return $true
    } catch {
        Write-Host "FAILED: $repoDir — $_" -ForegroundColor Red
        return $false
    } finally {
        Pop-Location
    }
}

$agent = Join-Path $root 'AgentBridge.csproj'
if (-not (Test-Path $agent)) { throw "AgentBridge.csproj not found at $agent" }

# Dependency-first order (children before parents) so package publishes happen in order.
$order = @()
foreach ($p in (Get-ChildProjects $agent)) {
    $dir = Split-Path -Parent $p
    if ($visited.Add($dir)) { $order += $dir }
}
[array]::Reverse($order)
if (-not $SkipSelf) { $order += $root }

Write-Host "Repos to sync ($($order.Count)):"
foreach ($d in $order) { Write-Host "  - $d" }

$failures = @()
foreach ($d in $order) {
    if (-not (Sync-Repo $d)) { $failures += $d }
}
if ($failures.Count -gt 0) {
    Write-Host "FAILED repos: $($failures -join ', ') — the sync is NOT complete, fix and re-run." -ForegroundColor Red
    exit 1
}
Write-Host "Done. This was a code-only sync (no NuGet publish: the dependency repos' publish.yml runs only on v* tag pushes). For a release, push a tag per dependency repo, then push AgentBridge with IsPrerelease=false — see docs-dev/RELEASING.md."
exit 0
