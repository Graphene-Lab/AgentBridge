# tui_to_png.ps1 â€” render a puppet TUI ASCII capture into a high-res PNG
# Usage: powershell -File examples\_tools\tui_to_png.ps1 -In <capture.txt> -Out <out.png> [-Title "..."]
param(
  [Parameter(Mandatory=$true)][string]$In,
  [Parameter(Mandatory=$true)][string]$Out,
  [string]$Title = "AGENT - AI Chat Console",
  [int]$Scale = 2
)
$ErrorActionPreference = 'Stop'
$text = [System.IO.File]::ReadAllText((Resolve-Path $In), [System.Text.Encoding]::UTF8)
# strip trailing blank lines
$text = $text.TrimEnd("`r","`n")
$lines = $text -split "`r?`n"
# compute a reasonable width from the longest line
$maxlen = ($lines | ForEach-Object { $_.Length } | Measure-Object -Maximum).Maximum
$html = New-Object System.Text.StringBuilder
[void]$html.Append('<!doctype html><html><head><meta charset="utf-8"><style>')
[void]$html.Append('*{margin:0;padding:0;box-sizing:border-box}')
[void]$html.Append('body{background:#0b0e14;font-family:"Cascadia Mono","Consolas","Courier New",monospace}')
[void]$html.Append('.win{display:inline-block;background:#14181f;border:1px solid #2a3140;border-radius:10px;overflow:hidden;box-shadow:0 12px 40px rgba(0,0,0,.6);margin:18px}')
[void]$html.Append('.bar{display:flex;align-items:center;gap:8px;height:34px;padding:0 14px;background:#1b212c;border-bottom:1px solid #2a3140}')
[void]$html.Append('.dot{width:12px;height:12px;border-radius:50%}')
[void]$html.Append('.r{background:#ff5f57}.y{background:#febc2e}.g{background:#28c840}')
[void]$html.Append('.tt{margin-left:10px;color:#8b97ab;font-size:14px;font-family:"Segoe UI",Arial,sans-serif}')
[void]$html.Append('.scr{padding:10px 12px 14px 12px}')
[void]$html.Append('pre{color:#d7deeb;font-size:16px;line-height:1.28;letter-spacing:0;white-space:pre}')
[void]$html.Append('.hl{color:#7ee787}')
[void]$html.Append('</style></head><body><div class="win">')
[void]$html.Append('<div class="bar"><span class="dot r"></span><span class="dot y"></span><span class="dot g"></span><span class="tt">' + [System.Net.WebUtility]::HtmlEncode($Title) + '</span></div>')
[void]$html.Append('<div class="scr"><pre>')
foreach ($ln in $lines) {
  $enc = [System.Net.WebUtility]::HtmlEncode($ln)
  if ($ln -match '^\u2502?\u2502?\u25cf' -or $ln -match '^\u2502\u25cf' -or $ln -match 'tools:' -or $ln -match '^\u2502 F1 help') {
    [void]$html.Append('<span class="hl">' + $enc + '</span>')
  } else {
    [void]$html.Append($enc)
  }
  [void]$html.Append("`n")
}
[void]$html.Append('</pre></div></div></body></html>')
$tmp = [System.IO.Path]::GetFullPath([System.IO.Path]::ChangeExtension($Out, ".html"))
[System.IO.File]::WriteAllText($tmp, $html.ToString(), (New-Object System.Text.UTF8Encoding($false)))
$outAbs = [System.IO.Path]::GetFullPath($Out)
$w = [int](($maxlen * 9.7) + 60)
$h = [int](($lines.Count * 20.5) + 90)
$edge = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
$udd = Join-Path $env:TEMP ("abgen-" + $PID + "-" + [guid]::NewGuid().ToString("N").Substring(0,8))
$args = @('--headless=new','--disable-gpu','--hide-scrollbars','--no-first-run','--no-default-browser-check',
         '--user-data-dir=' + $udd,
         '--force-device-scale-factor=' + $Scale,
         '--window-size=' + $w + ',' + $h,
         '--screenshot=' + $outAbs,
         ('file:///' + ($tmp -replace '\\','/')))
& $edge @args 2>$null
Start-Sleep -Milliseconds 600
Remove-Item $tmp -ErrorAction SilentlyContinue
Remove-Item $udd -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $Out) { Write-Host ("OK " + $Out + " " + (Get-Item $Out).Length) } else { Write-Host "FAIL $Out" }

