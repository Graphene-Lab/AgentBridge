# install-hooks.ps1 — copies the tracked pre-push hook template into .git/hooks.
# Run once after cloning (or after a fresh checkout): powershell -File install-hooks.ps1
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$gitDir = git -C $root rev-parse --git-dir
$hooksDir = Join-Path $gitDir 'hooks'
if (-not (Test-Path $hooksDir)) { New-Item -ItemType Directory $hooksDir | Out-Null }
Copy-Item (Join-Path $root 'hooks\pre-push') (Join-Path $hooksDir 'pre-push') -Force
Write-Host "pre-push hook installed at $hooksDir\pre-push"
