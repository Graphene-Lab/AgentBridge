# fix-temp-junctions.ps1 — developer tooling: recreates the dangling bin/obj junction
# targets under %TEMP% that break MSBuild on this machine.
#
# The repos redirect their bin/obj folders to %TEMP%\<Repo> (junctions, so OneDrive never
# syncs build junk). When the temp folder is cleaned, the junctions dangle and MSBuild's
# `**/*.resx` default glob fails with "MSB3552: the resource file '**/*.resx' was not found"
# (the glob returns the literal pattern for un-enumerable directories).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\fix-temp-junctions.ps1 [SorgentiRoot]
# Default root: the Sorgenti folder containing this repo (the script's grandparent).
$ErrorActionPreference = "SilentlyContinue"
$root = if ($args.Count -gt 0) { $args[0] } else { Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$created = 0
Get-ChildItem $root -Directory | Where-Object { Test-Path (Join-Path $_.FullName "*.csproj") } | ForEach-Object {
    Get-ChildItem $_.FullName -Directory -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | ForEach-Object {
        $target = ($_.Target -join "")
        if ($target -and -not (Test-Path $target)) {
            New-Item -ItemType Directory -Force -Path $target | Out-Null
            $created++
            Write-Host "recreated: $target"
        }
    }
}
Write-Host "Done. Recreated $created junction target(s)."
