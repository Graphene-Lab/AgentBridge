# Deterministic latency test: does client-side CHUNKING reduce time-to-first-audio vs a single
# long streaming speak? Same text, two runs:
#   A) ONE speak with the whole text (streaming=true)
#   B) the text split into 5 per-sentence speak chunks (streaming=true each) + empty final
# Metric: (log "WindowsAudioSink: first PCM written") - (log first "Speak: text_len=").
param([string]$Exe = "C:\Users\andre\OneDrive\Sorgenti\AIOffice.VoiceAgent.Win\bin\Debug\net10.0-windows10.0.19041.0\AIOffice.VoiceAgent.Win.exe")

function Start-Agent {
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $proc.StartInfo.FileName = $Exe
    $proc.StartInfo.UseShellExecute = $false
    $proc.StartInfo.RedirectStandardInput = $true
    $proc.StartInfo.RedirectStandardOutput = $true
    $proc.StartInfo.CreateNoWindow = $true
    $proc.Start() | Out-Null
    $out = $proc.StandardOutput
    $r = $out.ReadLineAsync(); if (-not $r.Wait(40000)) { throw "no ready" }
    return $proc
}

function Get-Latency($procId) {
    Start-Sleep -Milliseconds 500
    $log = Get-ChildItem "C:\Users\andre\OneDrive\Sorgenti\AIOffice.VoiceAgent.Win\bin\Debug\net10.0-windows10.0.19041.0\logs" -Filter "$procId.txt" | Select-Object -First 1
    if (-not $log) { return $null }
    $speak = Select-String -Path $log.FullName -Pattern 'Speak: text_len=' | Select-Object -First 1
    $first = Select-String -Path $log.FullName -Pattern 'WindowsAudioSink: first PCM written' | Select-Object -First 1
    if (-not $speak -or -not $first) { return $null }
    # Log timestamps are "[elapsed_sec,frac]" from process start → latency = delta in ms.
    function ElapsedMs($line) {
        $m = [regex]::Match($line, '^\[(\d+),(\d+)\]')
        if (-not $m.Success) { return $null }
        return ([int]$m.Groups[1].Value * 1000) + ([int]$m.Groups[2].Value * 10)
    }
    $t1 = ElapsedMs $speak.Line
    $t2 = ElapsedMs $first.Line
    if ($null -eq $t1 -or $null -eq $t2) { return $null }
    return ($t2 - $t1)
}

$sentences = @(
  'Questa è la prima frase molto breve.',
  'Questa è la seconda frase del test di lunghezza. ',
  'Questa è la terza frase che aggiunge contenuto. ',
  'Questa è la quarta frase per il benchmark. ',
  'Questa è la quinta e ultima frase del testo.'
)
$full = ($sentences -join ' ')

# ── RUN A: single long chunk ──
Write-Output "=== RUN A: single long speak ==="
$a = Start-Agent
$a.StandardInput.WriteLine('{"cmd":"start","lang":"it"}'); $a.StandardInput.Flush()
Start-Sleep -Seconds 3
$json = '{"cmd":"speak","text":"' + $full + '","lang":"it","streaming":true}'
$a.StandardInput.WriteLine($json); $a.StandardInput.Flush()
Start-Sleep -Seconds 16
$a.StandardInput.WriteLine('{"cmd":"speak","text":"","lang":"it","streaming":false}'); $a.StandardInput.Flush()
Start-Sleep -Seconds 12
$la = Get-Latency $a.Id
Write-Output ("RUN A time-to-first-audio: " + [math]::Round($la,0) + " ms")
try { $a.StandardInput.WriteLine('{"cmd":"stop"}'); $a.StandardInput.Flush() } catch { }
Start-Sleep -Seconds 1; try { $a.Kill() } catch { }

# ── RUN B: per-sentence chunks ──
Write-Output "=== RUN B: 5 per-sentence chunks ==="
$b = Start-Agent
$b.StandardInput.WriteLine('{"cmd":"start","lang":"it"}'); $b.StandardInput.Flush()
Start-Sleep -Seconds 3
foreach ($s in $sentences) {
    $json = '{"cmd":"speak","text":"' + $s + '","lang":"it","streaming":true}'
    $b.StandardInput.WriteLine($json); $b.StandardInput.Flush()
    Start-Sleep -Milliseconds 500   # let each chunk be processed
}
$b.StandardInput.WriteLine('{"cmd":"speak","text":"","lang":"it","streaming":false}'); $b.StandardInput.Flush()
Start-Sleep -Seconds 16
$lb = Get-Latency $b.Id
Write-Output ("RUN B time-to-first-audio: " + [math]::Round($lb,0) + " ms")
try { $b.StandardInput.WriteLine('{"cmd":"stop"}'); $b.StandardInput.Flush() } catch { }
Start-Sleep -Seconds 1; try { $b.Kill() } catch { }

if ($la -ne $null -and $lb -ne $null) {
    $gain = $la - $lb
    Write-Output ("`nRESULT: A(single)=" + [math]::Round($la,0) + "ms  B(chunked)=" + [math]::Round($lb,0) + "ms  gain=" + [math]::Round($gain,0) + "ms " + $(if ($gain -gt 50) { "→ chunking HELPS first audio" } elseif ($gain -lt -50) { "→ chunking HURTS" } else { "→ no meaningful difference" }))
} else {
    Write-Output "RESULT: measurement failed (check logs)"
}
