# AgentBridge one-line installer (Windows PowerShell).
#
#   irm https://graphene-lab.github.io/AgentBridge/install.ps1 | iex
#
# Downloads the latest Windows release archive from GitHub, extracts it into
# %LOCALAPPDATA%\AgentBridge and prints how to start the agent. The archive is
# self-contained (~460 MB): no .NET installation needed, Kokoro TTS voices included.

$ErrorActionPreference = "Stop"

$Repo = "Graphene-Lab/AgentBridge"
$Base = "https://github.com/$Repo/releases/latest/download"
$Dest = Join-Path $env:LOCALAPPDATA "AgentBridge"

# Only a win-x64 archive is published (no win-arm64 asset yet).
$Asset = "agentbridge-win-x64.tar.gz"

Write-Host "Installing AgentBridge into $Dest ..."
Write-Host "Downloading $Base/$Asset"

New-Item -ItemType Directory -Force -Path $Dest | Out-Null
$tmp = Join-Path $env:TEMP ("agentbridge-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
    $archive = Join-Path $tmp $Asset
    Invoke-WebRequest -Uri "$Base/$Asset" -OutFile $archive -UseBasicParsing
    tar -xzf $archive -C $tmp
    Remove-Item $archive
    Copy-Item -Path (Join-Path $tmp "*") -Destination $Dest -Recurse -Force
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "AgentBridge installed to $Dest."
Write-Host "Run:  $(Join-Path $Dest 'agent.exe')"
