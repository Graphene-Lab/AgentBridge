# Verifies the STREAMING TTS path of the refactored VoiceAgentWin (base + WindowsAudioSink):
#   start → speak(3 sentences, streaming=true) → speak("", final) → recognition restarts
# Checks the sink started, no errors, and the empty-final-chunk restart works.
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

$sw = [Diagnostics.Stopwatch]::StartNew()
$proc.StandardInput.WriteLine('{"cmd":"speak","text":"Questa è una prova dello streaming. Il primo suono arriva subito. E la terza frase continua senza interruzioni.","lang":"it","streaming":true}'); $proc.StandardInput.Flush()
Start-Sleep -Seconds 3
$sw.Stop()
Write-Output ("[host] speak streaming sent, waited " + $sw.Elapsed.TotalSeconds.ToString('F1') + "s (audio should be playing on the speaker)")

$proc.StandardInput.WriteLine('{"cmd":"speak","text":"","lang":"it","streaming":false}'); $proc.StandardInput.Flush()
Start-Sleep -Seconds 4

try { $proc.StandardInput.WriteLine('{"cmd":"stop"}'); $proc.StandardInput.Flush() } catch { }
Start-Sleep -Seconds 1
try { $proc.Kill() } catch { }

$log = Get-ChildItem "C:\Users\andre\OneDrive\Sorgenti\AIOffice.VoiceAgent.Win\bin\Debug\net10.0-windows10.0.19041.0\logs" -Filter "$($proc.Id).txt" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $log) { $log = Get-ChildItem "C:\Users\andre\OneDrive\Sorgenti\AIOffice.VoiceAgent.Win\bin\Debug\net10.0-windows10.0.19041.0\logs" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
if ($log) {
    Write-Output "--- log: streaming + sink ---"
    Select-String -Path $log.FullName -Pattern 'WindowsAudioSink|Streaming TTS failed|Speak: text_len|Recognition restarted after empty|error' | ForEach-Object { $_.Line }
    $sink = Select-String -Path $log.FullName -Pattern 'WindowsAudioSink: device started'
    $err = Select-String -Path $log.FullName -Pattern 'error|failed' 
    $restart = Select-String -Path $log.FullName -Pattern 'Recognition restarted after empty final chunk'
    if ($sink -and -not $err -and $restart) { Write-Output "RESULT: PASS — streaming sink used, no errors, recognition restarted" }
    else { Write-Output "RESULT: CHECK — sink=$([bool]$sink) errors=$([bool]$err) restart=$([bool]$restart)" }
} else { Write-Output "RESULT: no log found" }
