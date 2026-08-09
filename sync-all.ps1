# sync-all.ps1 — commit + push AgentBridge and every repo it depends on (recursively via
# ProjectReference), so the dependency NuGet packages publish before the AgentBridge release.
# New projects added to the dependency tree are picked up automatically (no script edits).
#
# Usage: powershell -File sync-all.ps1 [-Message "release prep"] [-Branch master]
param(
    [string]$Message = "sync",
    [string]$Branch = "",
    [switch]$SkipSelf
)

# Guard: when sync-all is invoked by the pre-push hook, mark the process so the nested
# git pushes it performs (which re-trigger the hook) skip the sync — avoids recursion.
$env:SYNC_ALL_ACTIVE = '1'

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$visited = New-Object 'System.Collections.Generic.HashSet[string]'

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
            git commit -m $Message | Out-Null
            Write-Host "committed: $repoDir"
        } else {
            Write-Host "clean:     $repoDir"
        }
        if ($Branch) { git push origin $Branch } else { git push }
        Write-Host "pushed:    $repoDir"
    } catch {
        Write-Warning "push failed for $repoDir : $_"
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

foreach ($d in $order) { Sync-Repo $d }
Write-Host "Done. Dependency packages publish on nuget.org via each repo's publish.yml; then tag AgentBridge (v1.yy.MM.dd) to trigger the release."
