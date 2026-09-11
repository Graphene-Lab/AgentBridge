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

PACKAGE SUPPORT FRAMEWORK (on by default, -SkipPsf to disable): the app keeps its configuration
inside the install directory (PersistentData\, attachments\, tui-screenshots\,
GiraffeAIWebClient\); an MSIX package is read-only, so Microsoft's documented remedy is a PSF
FileRedirectionFixup. The manifest entry point becomes PSFLauncher64.exe and the fixup redirects
those writes into the per-user VFS under %LOCALAPPDATA%. The x64 PSF binaries (MIT) come from the
pinned NuGet package Microsoft.PackageSupportFramework, cached in %LOCALAPPDATA%\AgentBridge\psf.
Store eligibility of a PSF-bearing package is not documented either way: confirm with Partner
Center (docs-dev/STORE-PUBLISHING.md section 8). This script installs nothing.

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

.PARAMETER PsfBinDir
Directory holding the three x64 PSF binaries already built from source (PsfLauncher64.exe,
PsfRuntime64.dll, FileRedirectionFixup64.dll). When set they are used as-is and NuGet is not
contacted. This is the preferred path: Microsoft ties PSF telemetry collection to the binaries
taken from the NuGet package (those carry Microsoft's telemetry provider GUID), while a build from
the repo leaves the provider id in include/Telemetry.h as the zeroed placeholder. The CI
build-psf job produces this directory. Without -PsfBinDir the script falls back to the NuGet
package and emits a warning.

.PARAMETER Verify
Also unpack the finished package into <OutDir>\verify and check that AppxManifest.xml comes back
out (round-trip check). Off by default: `makeappx pack` already validates the manifest against the
schema, and unpacking the real 1.3 GB payload costs disk and time.

.PARAMETER SkipPsf
Build without the Package Support Framework: the manifest entry point stays agent.exe and no
config.json is written. The package then cannot save any setting (read-only install directory) -
use it only to inspect the bare structure.

.EXAMPLE
powershell -File tools\store\New-StoreMsix.ps1 -PayloadDir D:\ab-msi-payload -Version 1.26.09.12
#>
param(
    [Parameter(Mandatory)][string]$PayloadDir,
    [Parameter(Mandatory)][string]$Version,
    [string]$OutDir,
    [string]$IdentityName = 'GrapheneLab.AgentBridge',
    [string]$Publisher = 'CN=Graphene Lab, O=Graphene Lab, C=IT',
    [string]$PsfBinDir,
    [switch]$Verify,
    [switch]$SkipPsf
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

# ── Package Support Framework: file redirection for the in-package writes ──
# PersistentData\ (AppConfig.cs), attachments\ and tui-screenshots\ (Tui.cs),
# GiraffeAIWebClient\ (WebClientUpdater.cs) all live inside the package, which MSIX mounts
# read-only. The launcher starts agent.exe and FileRedirectionFixup redirects those writes to the
# per-user VFS, so the app keeps working unchanged.
$psfVersion = '1.0.240212.1'
$psfNeeded = @('PsfLauncher64.exe', 'PsfRuntime64.dll', 'FileRedirectionFixup64.dll')
$exeName = 'agent.exe'
if (-not $SkipPsf) {
    $exeName = 'PSFLauncher64.exe'
    if ($PsfBinDir) {
        # Binaries built from source (see the build-psf job in release.yml). Preferred: the NuGet
        # copy is the one Microsoft's own docs tie telemetry collection to, because the shipped
        # binaries carry Microsoft's telemetry provider GUID. Built from the repo the provider id in
        # include/Telemetry.h stays the zeroed placeholder, so there is nowhere to send anything.
        $psfDir = (Resolve-Path $PsfBinDir).Path
        $missing = @($psfNeeded | Where-Object { -not (Test-Path (Join-Path $psfDir $_)) })
        if ($missing.Count -gt 0) {
            throw "-PsfBinDir '$psfDir' is missing: $($missing -join ', ') (expected the x64 Release build output)"
        }
        Write-Host "Using PSF binaries built from source in $psfDir"
    } else {
        $psfDir = Join-Path $env:LOCALAPPDATA "AgentBridge\psf\$psfVersion"
        if (@($psfNeeded | Where-Object { -not (Test-Path (Join-Path $psfDir $_)) }).Count -gt 0) {
            New-Item -ItemType Directory -Force -Path $psfDir | Out-Null
            $nupkg = Join-Path $OutDir 'psf.nupkg'
            $url = "https://api.nuget.org/v3-flatcontainer/microsoft.packagesupportframework/$psfVersion/microsoft.packagesupportframework.$psfVersion.nupkg"
            Write-Host "Fetching the Package Support Framework $psfVersion (NuGet, MIT) ..."
            & curl.exe -fsSL -o $nupkg $url
            if ($LASTEXITCODE -ne 0) { throw "cannot download the PSF package from $url" }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
            try {
                foreach ($n in $psfNeeded) {
                    $entry = $zip.Entries | Where-Object { $_.FullName -eq "bin/$n" }
                    if (-not $entry) { throw "PSF package does not contain bin/$n" }
                    $target = Join-Path $psfDir $n
                    if (Test-Path $target) { Remove-Item $target -Force }
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target)
                }
            }
            finally { $zip.Dispose() }
            Remove-Item $nupkg -Force -ErrorAction SilentlyContinue
        }
        Write-Warning "Using NuGet-sourced PSF binaries: these carry Microsoft's telemetry provider id. Pass -PsfBinDir with a from-source build to avoid it (docs-dev/STORE-PUBLISHING.md, PSF telemetry)."
    }
    foreach ($n in $psfNeeded) { Copy-Item (Join-Path $psfDir $n) (Join-Path $layout $n) -Force }
    $psfConfig = Get-Content (Join-Path $root 'msix\config.json') -Raw
    [System.IO.File]::WriteAllText((Join-Path $layout 'config.json'), $psfConfig, $utf8NoBom)
    Write-Host "PSF file redirection enabled (entry point $exeName)"
}
else {
    Write-Warning 'Building WITHOUT the PSF fixup: the packaged app cannot write PersistentData\ (structure test only).'
}

# ── Manifest ─────────────────────────────────────────────────────────────
$template = Get-Content (Join-Path $root 'msix\AppxManifest.xml') -Raw
$manifest = $template.Replace('{{IDENTITY_NAME}}', $IdentityName).
                     Replace('{{PUBLISHER}}', $Publisher).
                     Replace('{{VERSION}}', $msixVersion).
                     Replace('{{EXECUTABLE}}', $exeName)
[System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, $utf8NoBom)

# ── Assets (flat placeholders: real branding is a Store submission requirement) ──
# StoreAssets, NOT Assets: the payload already carries its own assets\ folder and NTFS is
# case-insensitive, so "Assets" would silently merge into it (the manifest would then reference a
# path that only resolves by case-insensitivity).
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $layout 'StoreAssets'
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
if ($SkipPsf) { Write-Host 'WARNING: no PSF in this package - the app cannot save configuration (structure test only).' }
else { Write-Host 'PSF included: writes to PersistentData\, attachments\, tui-screenshots\, GiraffeAIWebClient\ go to the per-user VFS.' }
