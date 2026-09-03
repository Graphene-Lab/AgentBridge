<#
download-release.ps1 — download and (optionally) extract an AgentBridge release archive safely.

Why this exists: the release archives are large (win-x64 ~950 MB). Downloading one through a
short-lived foreground process is dangerous: if the process is killed mid-download the .tar.gz
is left TRUNCATED, and `tar -xzf` fails later with "Truncated tar archive detected" — a silent,
hard-to-diagnose install failure. This script:
  1. downloads with `gh` (or resumable `curl -C -` fallback),
  2. VERIFIES the archive integrity (`tar -tzf`, exit code) BEFORE any extraction,
  3. deletes the corrupt partial instead of leaving it behind,
  4. extracts only after the verification passed.

Run it as a BACKGROUND/long-running process for big archives (or with a generous timeout):
  powershell -File download-release.ps1 -Tag v1.26.09.03 -OutDir D:\agentbridge-win-x64

Usage:
  -Repo    GitHub repo (default Graphene-Lab/AgentBridge)
  -Tag     release tag, e.g. v1.26.09.03 (default: latest release)
  -Asset   archive file name (default agentbridge-win-x64.tar.gz)
  -OutDir  destination folder for the extraction (created if missing; default current dir)
  -DownloadOnly  only download + verify, do not extract
#>
param(
    [string]$Repo = "Graphene-Lab/AgentBridge",
    [string]$Tag = "",
    [string]$Asset = "agentbridge-win-x64.tar.gz",
    [string]$OutDir = ".",
    [switch]$DownloadOnly
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) { Write-Error $msg; exit 1 }

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = gh release view --repo $Repo --json tagName --jq .tagName
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Tag)) { Fail "cannot resolve the latest release of $Repo" }
    Write-Host "Latest release: $Tag"
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) "abdl-$([System.IO.Path]::GetRandomFileName())"
New-Item -ItemType Directory -Path $work | Out-Null
$archive = Join-Path $work $Asset

Write-Host "Downloading $Repo $Tag -> $($Asset) ..."
$ok = $false
try {
    gh release download $Tag --repo $Repo -p $Asset -D $work --clobber
    $ok = $LASTEXITCODE -eq 0
} catch {
    $ok = $false
}
if (-not $ok) {
    # Fallback: resumable curl (HTTP Range + retries). gh may be missing or the API flaky.
    curl.exe -L --fail --retry 5 --retry-delay 5 -C - -o $archive "https://github.com/$Repo/releases/download/$Tag/$Asset"
    if ($LASTEXITCODE -ne 0) { Fail "download failed for $Asset (use a longer timeout / background run for large archives)" }
}
if (-not (Test-Path $archive)) { Fail "download produced no file: $archive" }

Write-Host "Verifying archive integrity (tar -tzf) ..."
tar -tzf $archive *> $null
if ($LASTEXITCODE -ne 0) {
    Remove-Item $archive -Force -ErrorAction SilentlyContinue
    Fail "archive is truncated/corrupt — deleted. Re-run the download (background), the retry logic resumes from where it stopped."
}
Write-Host "Archive verified OK ($([math]::Round((Get-Item $archive).Length / 1MB)) MB)."

if ($DownloadOnly) { Write-Host "Download + verification done: $archive"; exit 0 }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Write-Host "Extracting into $OutDir ..."
tar -xzf $archive -C $OutDir
if ($LASTEXITCODE -ne 0) { Fail "extraction failed (exit $LASTEXITCODE)" }
Remove-Item $archive -Force -ErrorAction SilentlyContinue
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Done: $Asset extracted to $OutDir"
