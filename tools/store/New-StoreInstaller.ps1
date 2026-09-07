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
    [ValidateSet('low', 'high')][string]$Compression = 'low'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $PayloadDir 'agent.exe'
if (-not (Test-Path $exe)) { throw "agent.exe not found at '$exe' — is this a win-x64 AgentBridge payload?" }
if (-not $OutDir) { $OutDir = Join-Path (Split-Path -Parent $PayloadDir) 'store-msi' }
$ver = ($Version.TrimStart('v') -split '\.')[0..2] -join '.'

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
        Write-Host ("MSI created: {0} ({1:N1} MB)" -f $msi, ((Get-Item $msi).Length / 1MB))
    }
    finally { $env:TMP = $oldTmp; $env:TEMP = $oldTemp }
}
finally { Pop-Location }
