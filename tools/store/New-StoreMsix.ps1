<#
.SYNOPSIS
Builds the Microsoft Store MSIX package for AgentBridge from a win-x64 payload.

.DESCRIPTION
Store-ONLY artifact: the GitHub archives are not touched by this script and stay exactly as
designed. MSIX is the one Store channel where Microsoft signs for you, so no code-signing
certificate is needed here ("The Microsoft Store will automatically re-sign your MSIX/AppX
packages with a Microsoft certificate", learn.microsoft.com/apps/publish).

The layout is the payload plus:
  - AppxManifest.xml built from tools/store/msix/AppxManifest.xml (tokens substituted);
  - Assets/StoreLogo.png, Square44x44Logo.png, Square150x150Logo.png (flat placeholders:
    replace with real branding before a Store submission);
  - PersistentData\appsettings.json with AutoUpdate.Enabled=false: under MSIX the package files
    are read-only and the Store delivers updates, so the app must not try to replace itself.

NOTE: this file is intentionally ASCII-only outside comments. Windows PowerShell 5.1 parses .ps1
files as ANSI and mis-decodes non-ASCII bytes; a UTF-8 em dash inside a string literal even ends
the string (CP1252 0x94 becomes a typographic quote). Keep literals ASCII.

KNOWN LIMITATION - read docs-dev/STORE-PUBLISHING.md first: AgentBridge keeps its user
configuration under PersistentData\ INSIDE the install directory, which MSIX makes read-only.
Without a Package Support Framework FileRedirectionFixup (the remedy Microsoft documents for
exactly this case) the packaged app cannot save any setting. This script builds and validates the
package STRUCTURE; it does not add PSF, and it does not install anything.

.PARAMETER PayloadDir
Absolute path of the win-x64 payload (must contain agent.exe at its root).

.PARAMETER Version
Release version, e.g. 1.26.09.12. The MSIX version becomes 1.<yy>.<MMdd>.0 - the fourth section is
reserved by the Store and must stay 0.

.PARAMETER OutDir
Output folder (default: <parent of PayloadDir>\store-msix).

.PARAMETER IdentityName
Package identity name; for the Store it must match Partner Center "View app identity details".

.PARAMETER Publisher
Publisher DN; for the Store it must match the Partner Center publisher identity. The default is a
placeholder for local tests (a locally installed test package must be signed with a certificate
whose subject matches this value and that is trusted by the machine).

.PARAMETER Verify
Also unpack the finished package into <OutDir>\verify and check that AppxManifest.xml comes back
out (round-trip check). Off by default: `makeappx pack` already validates the manifest against the
schema, and unpacking the real 1.3 GB payload costs disk and time.

.EXAMPLE
powershell -File tools\store\New-StoreMsix.ps1 -PayloadDir D:\ab-msi-payload -Version 1.26.09.12
#>
param(
    [Parameter(Mandatory)][string]$PayloadDir,
    [Parameter(Mandatory)][string]$Version,
    [string]$OutDir,
    [string]$IdentityName = 'GrapheneLab.AgentBridge',
    [string]$Publisher = 'CN=Graphene Lab, O=Graphene Lab, C=IT',
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
# UTF-8 without BOM: PowerShell 5.1's -Encoding UTF8 would add one and the files written here are
# consumed by makeappx/the app, which expect plain UTF-8.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$root = Split-Path -Parent $MyInvocation.MyCommand.Path          # tools/store
$repo = Split-Path -Parent (Split-Path -Parent $root)            # repo root
$exe = Join-Path $PayloadDir 'agent.exe'
if (-not (Test-Path $exe)) { throw "agent.exe not found at '$exe' - is this a win-x64 AgentBridge payload?" }
if (-not $OutDir) { $OutDir = Join-Path (Split-Path -Parent $PayloadDir) 'store-msix' }

# ── Version mapping: 1.yy.MM.dd -> 1.yy.MMdd.0 ────────────────────────────
$p = ($Version.TrimStart('v') -split '\.')
if ($p.Count -lt 4) { throw "Version must look like 1.yy.MM.dd (got '$Version')." }
$build = [int]$p[2] * 100 + [int]$p[3]
if ($build -gt 65535) { throw "MSIX build section out of range for '$Version'." }
$msixVersion = "1.$([int]$p[1]).$build.0"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$layout = Join-Path $OutDir 'layout'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force -Path $layout | Out-Null

# ── Layout = payload (robocopy: exit codes 0-7 mean success) ──────────────
Write-Host "Staging payload into $layout ..."
robocopy $PayloadDir $layout /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }

# ── Manifest ─────────────────────────────────────────────────────────────
$template = Get-Content (Join-Path $root 'msix\AppxManifest.xml') -Raw
$manifest = $template.Replace('{{IDENTITY_NAME}}', $IdentityName).
                     Replace('{{PUBLISHER}}', $Publisher).
                     Replace('{{VERSION}}', $msixVersion)
[System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, $utf8NoBom)

# ── Assets (flat placeholders: real branding is a Store submission requirement) ──
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $layout 'Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
function New-AssetPng([string]$Path, [int]$Width, [int]$Height) {
    $bmp = New-Object System.Drawing.Bitmap($Width, $Height)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.Clear([System.Drawing.Color]::FromArgb(255, 24, 24, 28))
            $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0, 200, 120))
            try { $g.FillEllipse($brush, [int]($Width * 0.15), [int]($Height * 0.15), [int]($Width * 0.7), [int]($Height * 0.7)) }
            finally { $brush.Dispose() }
        }
        finally { $g.Dispose() }
        $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bmp.Dispose() }
}
New-AssetPng (Join-Path $assets 'StoreLogo.png') 50 50
New-AssetPng (Join-Path $assets 'Square44x44Logo.png') 44 44
New-AssetPng (Join-Path $assets 'Square150x150Logo.png') 150 150

# ── Store behaviour seed: no self-update inside a read-only package ──────
$seed = Join-Path $layout 'PersistentData'
New-Item -ItemType Directory -Force -Path $seed | Out-Null
$cfg = Get-Content (Join-Path $repo 'appsettings.json') -Raw | ConvertFrom-Json
$cfg.AutoUpdate.Enabled = $false
[System.IO.File]::WriteAllText((Join-Path $seed 'appsettings.json'), ($cfg | ConvertTo-Json -Depth 20), $utf8NoBom)
Write-Host 'Seeded PersistentData\appsettings.json with AutoUpdate.Enabled=false'

# ── Pack + validate ─────────────────────────────────────────────────────
$makeappx = (Get-Command makeappx.exe -ErrorAction SilentlyContinue).Source
if (-not $makeappx) {
    $sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $sdk) { throw 'makeappx.exe not found - install the Windows SDK (or the MSIX Packaging Tool).' }
    $makeappx = $sdk.FullName
}
Write-Host "Using $makeappx (MSIX version $msixVersion)"

$msix = Join-Path $OutDir ("GrapheneAgentBridge-" + $Version.TrimStart('v') + '.msix')
& $makeappx pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed (exit $LASTEXITCODE)" }

if ($Verify) {
    # Round-trip check: the package must unpack back with its manifest (makeappx has no
    # separate "validate" command in the current SDK; pack already enforced the schema).
    $verifyDir = Join-Path $OutDir 'verify'
    if (Test-Path $verifyDir) { Remove-Item $verifyDir -Recurse -Force }
    & $makeappx unpack /p $msix /d $verifyDir /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx unpack failed (exit $LASTEXITCODE)" }
    if (-not (Test-Path (Join-Path $verifyDir 'AppxManifest.xml'))) { throw 'unpacked package has no AppxManifest.xml' }
    Write-Host 'Round-trip verified (unpack + manifest)'
    Remove-Item $verifyDir -Recurse -Force
}

Write-Host ("MSIX created: {0} ({1:N1} MB)" -f $msix, ((Get-Item $msix).Length / 1MB))
Write-Host 'Store submission: upload this .msix (or a .msixbundle) in Partner Center - the Store re-signs it.'
Write-Host 'Local install test: sign it with a certificate whose subject equals the manifest Publisher, then trust that certificate (admin) - see docs-dev/STORE-PUBLISHING.md.'
Write-Host 'Reminder: without a Package Support Framework file-redirection fixup the packaged app cannot write PersistentData\ (read-only package).'
