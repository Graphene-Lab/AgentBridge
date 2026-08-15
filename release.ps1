# release.ps1 — triggered by the "Push AgentBridge Release" status-bar button. It turns the
# release gate off (IsPrerelease=false) and pushes master: the push itself triggers everything
# else — the pre-push hook syncs the dependency repos (NuGet) and release.yml runs the wait +
# build + GitHub release (tag auto-created). Afterwards the gate is restored to true locally
# (NOT pushed: the pushed commit must keep IsPrerelease=false so the release tag points at it).
#
# Running it is exactly equivalent to: set IsPrerelease=false, commit, push master — so the
# button works from VS Code and a plain git push works from anywhere. No confirmation dialog:
# the button executes this script right away.
#
# Usage:  powershell -File release.ps1 [-Message "<commit message>"]
#   -Message: commit message used for the empty trigger commit when the gate is already off
#             (default "sync").
#
# Full mechanism: docs/RELEASING.md.

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
$csprojPath = Join-Path $root 'AgentBridge.csproj'
$gateRegex = '<IsPrerelease>\s*(true|false)\s*</IsPrerelease>'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Set-IsPrerelease([string]$value, [switch]$Push) {
    $content = [regex]::Replace([System.IO.File]::ReadAllText($csprojPath), $gateRegex, "<IsPrerelease>$value</IsPrerelease>", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    [System.IO.File]::WriteAllText($csprojPath, $content, $utf8NoBom)
    Push-Location $root
    try {
        git add AgentBridge.csproj
        git diff --cached --quiet HEAD -- AgentBridge.csproj
        if ($LASTEXITCODE -ne 0) {
            git commit -m "chore: IsPrerelease=$value" | Out-Null
        } elseif ($Push) {
            # Gate already off in HEAD: push an empty commit so the workflow still triggers.
            git commit --allow-empty -m $Message | Out-Null
        }
        if ($Push) {
            git push origin master
            if ($LASTEXITCODE -ne 0) { throw "git push failed" }
        }
    } finally {
        Pop-Location
    }
}

# Turn the gate off and push → release.yml runs + the pre-push hook syncs the dependencies.
try {
    Write-Host "=== IsPrerelease=false + push master (release trigger) ==="
    Set-IsPrerelease 'false' -Push
} finally {
    # Restore the gate afterwards — locally only, so the pushed commit keeps IsPrerelease=false.
    try {
        Set-IsPrerelease 'true'
    } catch {
        Write-Warning "could not restore IsPrerelease=true — set it manually: $_"
    }
}
Write-Host "Done. Release runs in GitHub Actions (https://github.com/Graphene-Lab/AgentBridge/actions)."
