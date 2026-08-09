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
    Write-Warning "IsPrerelease=false in AgentBridge.csproj → version is $VER; the release CI will SKIP (no GitHub release). Set IsPrerelease=true when the version is proven to work."
}
$NVER = ((($VER -split '-')[0]) -split '\.' | ForEach-Object { [int]$_ }) -join '.'
Write-Host "version: $VER (normalized for nuget.org: $NVER)"

foreach ($pkg in $packages) {
    $ok = $false
    for ($i = 1; $i -le 40; $i++) {   # up to ~10 minutes
        try {
            $idx = (Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/$pkg/index.json" -UseBasicParsing -TimeoutSec 20).Content
            if ($idx -match ('"' + [regex]::Escape($NVER) + '"')) {
                Write-Host "$pkg $NVER available (attempt $i)"
                $ok = $true
                break
            }
        } catch { }
        Start-Sleep -Seconds 15
    }
    if (-not $ok) {
        Write-Warning "$pkg $NVER not visible after ~10 min — the release CI will fail; check the publish workflow of Graphene-Lab/$($pkg -replace '^graphene\.','') ."
    }
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
