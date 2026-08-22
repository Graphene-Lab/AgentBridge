# Verifies: (1) the Kokoro synthesizer pre-warm at startup, (2) streaming with a LONG reply
# (buffer no longer truncates the tail), (3) the empty-final-chunk restart still works.
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
$r1 = $out.ReadLineAsync(); if (-not $r1.Wait(40000)) { Write-Output "no ready"; $proc.Kill(); exit 1 }
Write-Output ("[agent] " + $r1.Result)

# LONG reply (~12 s of speech) streamed as ONE chunk — the old 5 s buffer + 4 s drain cap
# would truncate the tail; the new 120 s buffer + full drain must play it all.
$long = "Questa è una frase molto lunga per verificare che il buffer non tronchi la coda dell'audio. " +
        "Contiene abbastanza parole da produrre circa dodici secondi di parlato continuo. " +
        "Se il problema del troncamento fosse ancora presente, sentireste solo l'inizio e poi il silenzio. " +
        "Invece con il nuovo buffer l'intera risposta dovrebbe essere ascoltata fino alla fine. " +
        "Questa è l'ultima frase del test di durata."
$proc.StandardInput.WriteLine('{"cmd":"start","lang":"it"}'); $proc.StandardInput.Flush()
Start-Sleep -Seconds 3
$sw = [Diagnostics.Stopwatch]::StartNew()
$json = '{"cmd":"speak","text":"' + $long + '","lang":"it","streaming":true}'
$proc.StandardInput.WriteLine($json); $proc.StandardInput.Flush()
Start-Sleep -Seconds 16
$sw.Stop()
Write-Output ("[host] long streaming speak sent (waited " + $sw.Elapsed.TotalSeconds.ToString('F1') + "s for ~12s audio)")

$proc.StandardInput.WriteLine('{"cmd":"speak","text":"","lang":"it","streaming":false}'); $proc.StandardInput.Flush()
Start-Sleep -Seconds 13   # let the ~12s tail drain before checking the restart

try { $proc.StandardInput.WriteLine('{"cmd":"stop"}'); $proc.StandardInput.Flush() } catch { }
Start-Sleep -Seconds 1
try { $proc.Kill() } catch { }

$log = Get-ChildItem "C:\Users\andre\OneDrive\Sorgenti\AIOffice.VoiceAgent.Win\bin\Debug\net10.0-windows10.0.19041.0\logs" -Filter "$($proc.Id).txt" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $log) { $log = Get-ChildItem "C:\Users\andre\OneDrive\Sorgenti\AIOffice.VoiceAgent.Win\bin\Debug\net10.0-windows10.0.19041.0\logs" | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
if ($log) {
    Write-Output "--- log: prewarm + streaming ---"
    Select-String -Path $log.FullName -Pattern 'pre-warming|synthesizer ready|WindowsAudioSink|Streaming TTS failed|error|Speak: text_len' | ForEach-Object { $_.Line }
    $prewarm = Select-String -Path $log.FullName -Pattern 'Kokoro streaming synthesizer ready'
    $sink = Select-String -Path $log.FullName -Pattern 'WindowsAudioSink: device started'
    $err = Select-String -Path $log.FullName -Pattern 'Streaming TTS failed|error'
    if ($prewarm -and $sink -and -not $err) { Write-Output "RESULT: PASS — prewarm OK, sink started, no streaming errors" }
    else { Write-Output "RESULT: CHECK — prewarm=$([bool]$prewarm) sink=$([bool]$sink) errors=$([bool]$err)" }
} else { Write-Output "RESULT: no log found" }
