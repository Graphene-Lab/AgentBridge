<#
.SYNOPSIS
Builds the Windows Store MSI installer for AgentBridge from a win-x64 publish payload.

.DESCRIPTION
Harvests every file under -PayloadDir into a per-machine MSI using the repo-local WiX v5
tool (tools/store/.config manifest, `dotnet tool restore`). Installs to
"%ProgramFiles%\Graphene Lab\AgentBridge" mirroring the payload tree (agent.exe,
kokoro.onnx, voices/, Tools/, assets/), adds Start-menu + desktop shortcuts to agent.exe
and the standard uninstall entry. The MSI is the package uploaded to the Microsoft Store
("EXE or MSI app" product). The .wxs is generated with System.Xml.Linq (safe escaping).
Store policy 10.2.9 also requires the MSI AND every PE file it ships to be signed with a
certificate chaining to a Microsoft Trusted Root CA: pass a certificate (-SignPfx /
-SignThumbprint, or the SIGN_* env vars), otherwise the MSI is built unsigned.

.PARAMETER PayloadDir
Absolute path of the win-x64 payload (must contain agent.exe at its root).

.PARAMETER Version
Version for the product, e.g. 1.26.09.07 (MSI uses the first 3 numeric parts).

.PARAMETER OutDir
Folder for the produced .msi (default: <parent of PayloadDir>\store-msi).

.EXAMPLE
powershell -File tools\store\New-StoreInstaller.ps1 -PayloadDir D:\win-x64 -Version 1.26.09.07
#>
param(
    [Parameter(Mandatory)][string]$PayloadDir,
    [Parameter(Mandatory)][string]$Version,
    [string]$OutDir,
    # Cabinet compression: 'high' (LZX, smaller) is fine for small payloads; 'low'
    # (mszip) is more robust for very large payloads (WiX wixnative cabbing of ~1 GB+
    # trees has failed with "failed to compress cabinet" under 'high').
    [ValidateSet('low', 'high')][string]$Compression = 'low',
    # Code signing (Store 10.2.9), either:
    #   -SignPfx <file.pfx> [-SignPfxPassword <pw>]  : PFX on disk
    #   -SignThumbprint <sha1>                       : cert in LocalMachine\My (token/HSM)
    # or the env vars SIGN_PFX / SIGN_PFX_PASSWORD / SIGN_THUMBPRINT / SIGN_TIMESTAMP_URL.
    # Without any of them the MSI is built UNSIGNED (Store certification fails).
    [string]$SignPfx,
    [string]$SignPfxPassword,
    [string]$SignThumbprint,
    [string]$TimestampUrl
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $PayloadDir 'agent.exe'
if (-not (Test-Path $exe)) { throw "agent.exe not found at '$exe' — is this a win-x64 AgentBridge payload?" }
if (-not $OutDir) { $OutDir = Join-Path (Split-Path -Parent $PayloadDir) 'store-msi' }
# MSI ProductVersion has only three sections (major.minor.build), so the release's date-based
# fourth section must be folded into the build field: 1.26.09.06 -> 1.26.906, 1.26.09.11 ->
# 1.26.911. Taking just the first three sections made every release within one month share a
# single ProductVersion, and MajorUpgrade does not detect same-version products, so installing
# a newer build over an older one left duplicate Add/Remove Programs entries and orphaned
# components instead of upgrading. MM*100+DD stays well under the 65535 build limit.
$vp = $Version.TrimStart('v') -split '\.'
if ($vp.Count -ge 4 -and $vp[2] -match '^\d+$' -and $vp[3] -match '^\d+$') {
    $ver = '{0}.{1}.{2}' -f [int]$vp[0], [int]$vp[1], ([int]$vp[2] * 100 + [int]$vp[3])
} else {
    $ver = ($vp[0..([Math]::Min(2, $vp.Count - 1))]) -join '.'
}

# ── Code signing configuration ────────────────────────────────────────────
if (-not $SignPfx) { $SignPfx = $env:SIGN_PFX }
if (-not $SignPfxPassword) { $SignPfxPassword = $env:SIGN_PFX_PASSWORD }
if (-not $SignThumbprint) { $SignThumbprint = $env:SIGN_THUMBPRINT }
if (-not $TimestampUrl) { $TimestampUrl = 'http://timestamp.digicert.com' }
if ($env:SIGN_TIMESTAMP_URL) { $TimestampUrl = $env:SIGN_TIMESTAMP_URL }

$signTool = $null
if ($SignPfx -or $SignThumbprint) {
    $signTool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $signTool) {
        $sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $sdk) { throw 'Signing requested but signtool.exe was not found (install the Windows SDK or add it to PATH).' }
        $signTool = $sdk.FullName
    }
    Write-Host "Signing with $signTool, timestamped by $TimestampUrl"
}

function Invoke-Sign([string]$Path) {
    $signArgs = @('sign', '/fd', 'sha256', '/tr', $TimestampUrl, '/td', 'sha256')
    if ($SignPfx) {
        $signArgs += @('/f', $SignPfx)
        if ($SignPfxPassword) { $signArgs += @('/p', $SignPfxPassword) }
    }
    else { $signArgs += @('/sha1', $SignThumbprint, '/sm') }
    & $signTool @signArgs $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for '$Path' (exit $LASTEXITCODE)" }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$wxs = Join-Path $root 'AgentBridge.wxs'
$rootFull = [System.IO.Path]::GetFullPath($PayloadDir)

Add-Type -AssemblyName System.Xml.Linq
$X = [System.Xml.Linq.XNamespace]::Get('http://wixtoolset.org/schemas/v4/wxs')

# ── Build the folder-id map ───────────────────────────────────────────────
$dirIds = @{}                        # full path -> id
$dirIds[$rootFull] = 'INSTALLFOLDER'
$nextDir = 0
$allDirs = @(Get-ChildItem $rootFull -Directory -Recurse | Sort-Object FullName)
foreach ($d in $allDirs) { $dirIds[$d.FullName] = 'D' + (++$nextDir) }

# ── Recursive directory element builder (nested under a parent Directory) ──
function New-WixDirectory([System.IO.DirectoryInfo]$dir, [string]$id) {
    $el = [System.Xml.Linq.XElement]::new($X + 'Directory',
        [System.Xml.Linq.XAttribute]::new('Id', $id),
        [System.Xml.Linq.XAttribute]::new('Name', $dir.Name))
    foreach ($child in ($dir.GetDirectories() | Sort-Object Name)) {
        $el.Add((New-WixDirectory $child $dirIds[$child.FullName]))
    }
    return $el
}

# ── Document ──────────────────────────────────────────────────────────────
$xw = [System.Xml.Linq.XDocument]::new()
$xw.Declaration = [System.Xml.Linq.XDeclaration]::new('1.0', 'utf-8', $null)
$wix = [System.Xml.Linq.XElement]::new($X + 'Wix')

$pkg = [System.Xml.Linq.XElement]::new($X + 'Package',
    [System.Xml.Linq.XAttribute]::new('Name', 'Graphene AgentBridge'),
    [System.Xml.Linq.XAttribute]::new('Manufacturer', 'Graphene Lab'),
    [System.Xml.Linq.XAttribute]::new('Version', $ver),
    [System.Xml.Linq.XAttribute]::new('UpgradeCode', '45f6a1b2-3c4d-4e5f-9a8b-7c6d5e4f3a2b'),
    [System.Xml.Linq.XAttribute]::new('Scope', 'perMachine'),
    [System.Xml.Linq.XAttribute]::new('Compressed', 'yes'))
$pkg.Add([System.Xml.Linq.XElement]::new($X + 'MajorUpgrade',
    [System.Xml.Linq.XAttribute]::new('DowngradeErrorMessage', 'A newer version of Graphene AgentBridge is already installed.')))
$pkg.Add([System.Xml.Linq.XElement]::new($X + 'MediaTemplate',
    [System.Xml.Linq.XAttribute]::new('EmbedCab', 'yes'),
    [System.Xml.Linq.XAttribute]::new('CompressionLevel', $Compression)))
$pkg.Add([System.Xml.Linq.XElement]::new($X + 'StandardDirectory', [System.Xml.Linq.XAttribute]::new('Id', 'ProgramFiles64Folder')))
$pkg.Add([System.Xml.Linq.XElement]::new($X + 'StandardDirectory', [System.Xml.Linq.XAttribute]::new('Id', 'ProgramMenuFolder')))
$pkg.Add([System.Xml.Linq.XElement]::new($X + 'StandardDirectory', [System.Xml.Linq.XAttribute]::new('Id', 'DesktopFolder')))

# Shortcuts
$sc = [System.Xml.Linq.XElement]::new($X + 'ComponentGroup', [System.Xml.Linq.XAttribute]::new('Id', 'ShortcutComponents'))
$scStart = [System.Xml.Linq.XElement]::new($X + 'Component',
    [System.Xml.Linq.XAttribute]::new('Id', 'StartShortcut'),
    [System.Xml.Linq.XAttribute]::new('Directory', 'ABPROGMENU'),
    [System.Xml.Linq.XAttribute]::new('Guid', '7b2d2e39-2a3b-4c5d-8e6f-9a1b2c3d4e5f'))
$scStart.Add([System.Xml.Linq.XElement]::new($X + 'Shortcut',
    [System.Xml.Linq.XAttribute]::new('Id', 'AgentBridgeStartMenu'),
    [System.Xml.Linq.XAttribute]::new('Name', 'Graphene AgentBridge'),
    [System.Xml.Linq.XAttribute]::new('Description', 'Local-first AI assistant'),
    [System.Xml.Linq.XAttribute]::new('Target', '[INSTALLFOLDER]agent.exe'),
    [System.Xml.Linq.XAttribute]::new('WorkingDirectory', 'INSTALLFOLDER')))
$scStart.Add([System.Xml.Linq.XElement]::new($X + 'RemoveFolder', [System.Xml.Linq.XAttribute]::new('Id', 'RemoveStartMenuFolder'), [System.Xml.Linq.XAttribute]::new('On', 'uninstall')))
$sc.Add($scStart)
$scDesktop = [System.Xml.Linq.XElement]::new($X + 'Component',
    [System.Xml.Linq.XAttribute]::new('Id', 'DesktopShortcut'),
    [System.Xml.Linq.XAttribute]::new('Directory', 'DesktopFolder'),
    [System.Xml.Linq.XAttribute]::new('Guid', '8c3e3f4a-3b4c-4d6e-9f7a-0b2c3d4e5f60'))
$scDesktop.Add([System.Xml.Linq.XElement]::new($X + 'Shortcut',
    [System.Xml.Linq.XAttribute]::new('Id', 'AgentBridgeDesktop'),
    [System.Xml.Linq.XAttribute]::new('Name', 'Graphene AgentBridge'),
    [System.Xml.Linq.XAttribute]::new('Description', 'Local-first AI assistant'),
    [System.Xml.Linq.XAttribute]::new('Target', '[INSTALLFOLDER]agent.exe'),
    [System.Xml.Linq.XAttribute]::new('WorkingDirectory', 'INSTALLFOLDER')))
$sc.Add($scDesktop)
$pkg.Add($sc)

# Harvested file components
$pc = [System.Xml.Linq.XElement]::new($X + 'ComponentGroup', [System.Xml.Linq.XAttribute]::new('Id', 'ProductComponents'))
$cid = 0
foreach ($f in (Get-ChildItem $rootFull -Recurse -File | Sort-Object FullName)) {
    $cid++
    $dir = Split-Path -Parent $f.FullName
    $c = [System.Xml.Linq.XElement]::new($X + 'Component',
        [System.Xml.Linq.XAttribute]::new('Id', ('C' + $cid)),
        [System.Xml.Linq.XAttribute]::new('Directory', $dirIds[$dir]))
    $c.Add([System.Xml.Linq.XElement]::new($X + 'File',
        [System.Xml.Linq.XAttribute]::new('Id', ('F' + $cid)),
        [System.Xml.Linq.XAttribute]::new('Source', $f.FullName)))
    $pc.Add($c)
}
$pkg.Add($pc)

$feat = [System.Xml.Linq.XElement]::new($X + 'Feature',
    [System.Xml.Linq.XAttribute]::new('Id', 'Main'),
    [System.Xml.Linq.XAttribute]::new('Title', 'Graphene AgentBridge'),
    [System.Xml.Linq.XAttribute]::new('Level', '1'))
$feat.Add([System.Xml.Linq.XElement]::new($X + 'ComponentGroupRef', [System.Xml.Linq.XAttribute]::new('Id', 'ProductComponents')))
$feat.Add([System.Xml.Linq.XElement]::new($X + 'ComponentGroupRef', [System.Xml.Linq.XAttribute]::new('Id', 'ShortcutComponents')))
$pkg.Add($feat)
$wix.Add($pkg)

# Directory fragment (WiX v5: custom directories nest under StandardDirectory)
$dirFrag = [System.Xml.Linq.XElement]::new($X + 'Fragment')
$pfStd = [System.Xml.Linq.XElement]::new($X + 'StandardDirectory', [System.Xml.Linq.XAttribute]::new('Id', 'ProgramFiles64Folder'))
$install = [System.Xml.Linq.XElement]::new($X + 'Directory',
    [System.Xml.Linq.XAttribute]::new('Id', 'INSTALLFOLDER'),
    [System.Xml.Linq.XAttribute]::new('Name', 'Graphene Lab\AgentBridge'))
# top-level payload folders become INSTALLFOLDER children (recursion emits the rest)
$tops = @(Get-ChildItem $rootFull -Directory | Sort-Object Name)
foreach ($t in $tops) { $install.Add((New-WixDirectory $t $dirIds[$t.FullName])) }
$pfStd.Add($install)
$dirFrag.Add($pfStd)
$pmStd = [System.Xml.Linq.XElement]::new($X + 'StandardDirectory', [System.Xml.Linq.XAttribute]::new('Id', 'ProgramMenuFolder'))
$pmStd.Add([System.Xml.Linq.XElement]::new($X + 'Directory',
    [System.Xml.Linq.XAttribute]::new('Id', 'ABPROGMENU'),
    [System.Xml.Linq.XAttribute]::new('Name', 'Graphene AgentBridge')))
$dirFrag.Add($pmStd)
$wix.Add($dirFrag)

$xw.Add($wix)
$xw.Save($wxs)

# ── Sign the payload (the MSI embeds these bytes: sign before the cabinet) ──
if ($signTool) {
    $peFiles = @(Get-ChildItem $rootFull -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll', '.sys' })
    Write-Host ("Signing {0} payload PE files" -f $peFiles.Count)
    foreach ($f in $peFiles) {
        # Skip files whose signature is already valid: re-signing would strip a
        # Microsoft-signed runtime DLL and gain nothing.
        if ((Get-AuthenticodeSignature $f.FullName).Status -ne 'Valid') { Invoke-Sign $f.FullName }
    }
}
else {
    Write-Warning 'No signing certificate (-SignPfx / -SignThumbprint / SIGN_* env vars): the MSI is UNSIGNED and Store certification fails (policy 10.2.9).'
}

# ── Build the MSI ─────────────────────────────────────────────────────────
Push-Location $root
try {
    dotnet tool restore | Out-Null
    # WiX stages the cabinet in %TEMP%; a short staging path avoids long-path and
    # wixnative "failed to compress cabinet" failures on very large payloads.
    $oldTmp = $env:TMP; $oldTemp = $env:TEMP
    $wixTmp = Join-Path (Split-Path -Parent $root) '.wix-tmp'
    New-Item -ItemType Directory -Force -Path $wixTmp | Out-Null
    $env:TMP = $wixTmp; $env:TEMP = $wixTmp
    try {
        $msi = Join-Path $OutDir ("GrapheneAgentBridge-" + $Version.TrimStart('v') + '.msi')
        & dotnet tool run wix build $wxs -o $msi -arch x64
        if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)" }
        if ($signTool) { Invoke-Sign $msi }
        Write-Host ("MSI created: {0} ({1:N1} MB)" -f $msi, ((Get-Item $msi).Length / 1MB))
    }
    finally { $env:TMP = $oldTmp; $env:TEMP = $oldTemp }
}
finally { Pop-Location }
