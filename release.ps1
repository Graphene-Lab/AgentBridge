# release.ps1 — one command to publish an AgentBridge release:
#   1) sync-all.ps1 pushes every repo in the dependency tree (their publish.yml then
#      publishes the date-versioned NuGet packages);
#   2) waits until today's version of every dependency package is visible on nuget.org;
#   3) creates + pushes the tag v1.yy.MM.dd (release gate), which triggers release.yml.
#
# Usage: powershell -File release.ps1 [-Message "release 1.26.08.09"]

param(
    [string]$Message = "sync"
)

$ErrorActionPreference = 'Stop'
# PowerShell 7.3+ turns native-command stderr into terminating errors when
# $ErrorActionPreference=Stop, but git writes to stderr for benign cases (push
# progress, "remote: warning: Deleting a non-existent ref.", "Updated tag ...").
# $LASTEXITCODE is the source of truth for git failures — not stderr.
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$packages = 'graphene.aiorchestrator','alltomarkdown','mermaidrendering','graphene.reversemarkdown','uisupportgeneric'

# 1) Push all repos (dependencies first) — each dep repo's publish.yml fires on push.
Write-Host "=== 1/4 sync-all (push all repos) ==="
& (Join-Path $root 'sync-all.ps1') -Message $Message
if ($LASTEXITCODE -ne 0) { throw "sync-all.ps1 failed" }

# 2) Wait for today's dependency packages on nuget.org (propagation is not instant).
Write-Host "=== 2/4 wait for dependency packages ==="
$VER = (dotnet msbuild (Join-Path $root 'AgentBridge.csproj') -getProperty:Version -nologo |
    Where-Object { $_ -match '^[0-9]+(\.[0-9]+)*(-[a-z0-9]+)?$' } | Select-Object -First 1).Trim()
if (-not $VER) { throw "could not read Version from AgentBridge.csproj" }
if ($VER -like '*-prerelease') {
    Write-Warning "IsPrerelease=true in AgentBridge.csproj → version is $VER; the release CI will SKIP (no GitHub release). Set IsPrerelease=false when the version is proven to work."
}
$NVER = ((($VER -split '-')[0]) -split '\.' | ForEach-Object { [int]$_ }) -join '.'
Write-Host "version: $VER (normalized for nuget.org: $NVER)"

# Global 30-minute window from the start of the wait: each cycle checks ALL packages; the
# loop ends as soon as every one is visible, or after the window — at which point the missing
# packages are reported with a warning and the release proceeds with the latest available
# versions (only changed repos publish today's version; for an unchanged repo the latest
# available is identical to what today's version would contain).
$missing = @()
for ($i = 1; $i -le 120; $i++) {   # up to ~30 minutes total (nuget.org official propagation)
    $pending = @()
    foreach ($pkg in $packages) {
        try {
            $idx = (Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$pkg/index.json" -UseBasicParsing -TimeoutSec 20).Content
            if ($idx -match ('"' + [regex]::Escape($NVER) + '"')) {
                Write-Host "$pkg $NVER available (attempt $i)"
                continue
            }
        } catch { }
        $pending += $pkg
    }
    if (-not $pending) { $missing = @(); break }
    $missing = $pending
    Start-Sleep -Seconds 15
}
if ($missing.Count) {
    Write-Warning "after ~30 min these packages are not yet at $NVER; the release CI proceeds with the latest available version: $($missing -join ', ')"
}

# 3) Create + push the tag (force-refresh for same-day re-releases).
Write-Host "=== 3/4 tag v$VER + push ==="
Push-Location $root
try {
    # Delete the remote tag first only when it already exists: deleting a
    # non-existent ref writes a warning to stderr and is never needed.
    $existing = git ls-remote --tags origin "refs/tags/v$VER" 2>$null
    if ($LASTEXITCODE -eq 0 -and $existing) {
        git push origin ":refs/tags/v$VER" 2>$null | Out-Null
    }
    git tag -f "v$VER"
    git push origin "v$VER"
    if ($LASTEXITCODE -ne 0) { throw "tag push failed" }
} finally {
    Pop-Location
}

# 4) Done.
Write-Host "=== 4/4 done — release CI triggered on tag v$VER (see https://github.com/Graphene-Lab/AgentBridge/actions) ==="
