# Verifies the "empty final chunk" fix in VoiceAgent.Win:
#   start → speak(streaming) → speak("", final) → recognition must restart
param([string]$Exe = "C:\Users\andre\OneDrive\Sorgenti\AIOffice.VoiceAgent.Win\bin\Debug\net10.0-windows10.0.19041.0\AIOffice.VoiceAgent.Win.exe")

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = New-Object System.Diagnostics.ProcessStartInfo
$proc.StartInfo.FileName = $Exe
$proc.StartInfo.UseShellExecute = $false
$proc.StartInfo.RedirectStandardInput = $true
$proc.StartInfo.RedirectStandardOutput = $true
$proc.StartInfo.CreateNoWindow = $true
if (-not $proc.Start()) { Write-Error "start failed"; exit 1 }
Write-Output ("[host] pid " + $proc.Id)

$out = $proc.StandardOutput
$r1 = $out.ReadLineAsync(); if (-not $r1.Wait(30000)) { Write-Output "no ready"; $proc.Kill(); exit 1 }
Write-Output ("[agent] " + $r1.Result)

$proc.StandardInput.WriteLine('{"cmd":"start","lang":"it"}'); $proc.StandardInput.Flush()
Start-Sleep -Seconds 3

# Streaming chunk (pauses recognition) then EMPTY final chunk (must restart recognition)
$proc.StandardInput.WriteLine('{"cmd":"speak","text":"Prima frase di prova.","lang":"it","streaming":true}'); $proc.StandardInput.Flush()
Start-Sleep -Seconds 2
$proc.StandardInput.WriteLine('{"cmd":"speak","text":"","lang":"it","streaming":false}'); $proc.StandardInput.Flush()
Start-Sleep -Seconds 4

try { $proc.StandardInput.WriteLine('{"cmd":"stop"}'); $proc.StandardInput.Flush() } catch { }
Start-Sleep -Seconds 1
try { $proc.Kill() } catch { }

# Check the log
$log = Get-ChildItem "C:\Users\andre\OneDrive\Sorgenti\AIOffice.VoiceAgent.Win\bin\Debug\net10.0-windows10.0.19041.0\logs" -Filter "$($proc.Id).txt" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $log) { $log = Get-ChildItem "C:\Users\andre\OneDrive\Sorgenti\AIOffice.VoiceAgent.Win\bin\Debug\net10.0-windows10.0.19041.0\logs" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
if ($log) {
    Write-Output "--- log tail ---"
    Get-Content $log.FullName -Tail 12
    $restarted = Select-String -Path $log.FullName -Pattern 'Recognition restarted after empty final chunk'
    if ($restarted) { Write-Output "RESULT: PASS — recognition restarted after the empty final chunk (fix works)" }
    else { Write-Output "RESULT: FAIL — recognition did NOT restart (fix ineffective)" }
} else {
    Write-Output "RESULT: no log found"
}
