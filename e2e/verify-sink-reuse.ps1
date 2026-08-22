# Verifies SINK REUSE: within one agent process, turn 2 must skip the WaveOutEvent setup cost
# (turn-1 first-audio includes it; turn 2 should be ~300 ms faster).
param([string]$Exe = "C:\Users\andre\OneDrive\Sorgenti\AIOffice.VoiceAgent.Win\bin\Debug\net10.0-windows10.0.19041.0\AIOffice.VoiceAgent.Win.exe")

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = New-Object System.Diagnostics.ProcessStartInfo
$proc.StartInfo.FileName = $Exe
$proc.StartInfo.UseShellExecute = $false
$proc.StartInfo.RedirectStandardInput = $true
$proc.StartInfo.RedirectStandardOutput = $true
$proc.StartInfo.CreateNoWindow = $true
if (-not $proc.Start()) { Write-Error "start failed"; exit 1 }
$out = $proc.StandardOutput
$r = $out.ReadLineAsync(); if (-not $r.Wait(40000)) { Write-Output "no ready"; $proc.Kill(); exit 1 }
Write-Output ("[host] pid " + $proc.Id)
$proc.StandardInput.WriteLine('{"cmd":"start","lang":"it"}'); $proc.StandardInput.Flush()
Start-Sleep -Seconds 3

function FirstAudioMs($procId) {
    Start-Sleep -Milliseconds 400
    $log = Get-ChildItem "C:\Users\andre\OneDrive\Sorgenti\AIOffice.VoiceAgent.Win\bin\Debug\net10.0-windows10.0.19041.0\logs" -Filter "$procId.txt" | Select-Object -First 1
    $speak = Select-String -Path $log.FullName -Pattern 'Speak: text_len=' | Select-Object -Last 1
    $first = Select-String -Path $log.FullName -Pattern 'WindowsAudioSink: first PCM written' | Select-Object -Last 1
    function Em($line) { $m = [regex]::Match($line, '^\[(\d+),(\d+)\]'); if (-not $m.Success) { return $null }; return ([int]$m.Groups[1].Value * 1000) + ([int]$m.Groups[2].Value * 10) }
    $t1 = Em $speak.Line; $t2 = Em $first.Line
    if ($null -eq $t1 -or $null -eq $t2) { return $null }
    return ($t2 - $t1)
}

# Turn 1
$t1 = 'Questa è la prima risposta del test sul riuso del dispositivo audio.'
$proc.StandardInput.WriteLine('{"cmd":"speak","text":"' + $t1 + '","lang":"it","streaming":true}'); $proc.StandardInput.Flush()
Start-Sleep -Seconds 8
$proc.StandardInput.WriteLine('{"cmd":"speak","text":"","lang":"it","streaming":false}'); $proc.StandardInput.Flush()
Start-Sleep -Seconds 6
$l1 = FirstAudioMs $proc.Id
Write-Output ("turn 1 time-to-first-audio: " + [math]::Round($l1,0) + " ms")

# Turn 2 (sink reused)
$t2 = 'Questa è la seconda risposta che riusa il dispositivo audio già avviato.'
$proc.StandardInput.WriteLine('{"cmd":"speak","text":"' + $t2 + '","lang":"it","streaming":true}'); $proc.StandardInput.Flush()
Start-Sleep -Seconds 8
$proc.StandardInput.WriteLine('{"cmd":"speak","text":"","lang":"it","streaming":false}'); $proc.StandardInput.Flush()
Start-Sleep -Seconds 6
$l2 = FirstAudioMs $proc.Id
Write-Output ("turn 2 time-to-first-audio: " + [math]::Round($l2,0) + " ms")

try { $proc.StandardInput.WriteLine('{"cmd":"stop"}'); $proc.StandardInput.Flush() } catch { }
Start-Sleep -Seconds 1; try { $proc.Kill() } catch { }

if ($l1 -ne $null -and $l2 -ne $null) {
    $saved = $l1 - $l2
    Write-Output ("`nRESULT: turn1=" + [math]::Round($l1,0) + "ms  turn2=" + [math]::Round($l2,0) + "ms  saved=" + [math]::Round($saved,0) + "ms " + $(if ($saved -gt 100) { "→ sink reuse WORKS" } else { "→ no clear reuse gain (check)" }))
} else { Write-Output "RESULT: measurement failed" }
