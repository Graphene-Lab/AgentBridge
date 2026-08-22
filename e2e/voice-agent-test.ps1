# VoiceAgent.Win dialog test — simulates the AIOffice Voice panel flow against the subprocess:
#   start → (TTS spoken to the mic) → transcript → speak → done → (TTS again) → transcript
# Reproduces the "chat stops after the first reply" bug without the GUI.
param(
    [string]$Exe = "C:\Users\andre\OneDrive\Sorgenti\AIOffice.VoiceAgent.Win\bin\Debug\net10.0-windows10.0.19041.0\AIOffice.VoiceAgent.Win.exe",
    [string]$Greeting = "C:\Users\andre\OneDrive\Sorgenti\AgentBridge\e2e\SipClientTest\bin\Debug\net10.0\greeting-it.wav",
    [string]$Lang = "it"
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Media

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = New-Object System.Diagnostics.ProcessStartInfo
$proc.StartInfo.FileName = $Exe
$proc.StartInfo.UseShellExecute = $false
$proc.StartInfo.RedirectStandardInput = $true
$proc.StartInfo.RedirectStandardOutput = $true
$proc.StartInfo.CreateNoWindow = $true
$proc.StartInfo.StandardInputEncoding = New-Object System.Text.UTF8Encoding($false)

if (-not $proc.Start()) { Write-Error "Failed to start $Exe"; exit 1 }
Write-Output ("[host] pid " + $proc.Id)

$out = $proc.StandardOutput
function ReadLineWithTimeout([int]$ms) {
    $task = $out.ReadLineAsync()
    if (-not $task.Wait($ms)) { return $null }
    return $task.Result
}

function PlayGreeting() {
    Write-Output "[host] playing greeting on the speaker (mic will hear it)..."
    $player = New-Object System.Media.SoundPlayer($Greeting)
    $player.PlaySync()
    $player.Dispose()
    Write-Output "[host] greeting done"
}

# 1. ready
$line = ReadLineWithTimeout 30000
Write-Output ("[agent] " + $line)

# 2. start recognition
$proc.StandardInput.WriteLine('{"cmd":"start","lang":"' + $Lang + '"}')
$proc.StandardInput.Flush()
Write-Output "[host] sent start; waiting for the recognizer to initialize (8s)..."
Start-Sleep -Seconds 8

# 3. first utterance: play TTS → expect a transcript
PlayGreeting
$t1 = ReadLineWithTimeout 40000
Write-Output ("[agent] first: " + $t1)
$gotFirst = $t1 -ne $null -and $t1 -match 'transcript'
Write-Output ("[host] first transcript received: " + $gotFirst)

# 4. reply: speak → done
$proc.StandardInput.WriteLine('{"cmd":"speak","text":"Questa è la mia risposta, ho capito.","lang":"' + $Lang + '","streaming":false}')
$proc.StandardInput.Flush()
Write-Output "[host] sent speak; waiting for done..."
$done = ReadLineWithTimeout 40000
Write-Output ("[agent] done: " + $done)
$gotDone = $done -ne $null -and $done -match 'done'
Write-Output ("[host] done received: " + $gotDone)

# 5. second utterance: play TTS again → expect a NEW transcript (this is where the chat usually stops)
Start-Sleep -Seconds 3   # let recognition resume after speak
PlayGreeting
$t2 = ReadLineWithTimeout 40000
Write-Output ("[agent] second: " + $t2)
$gotSecond = $t2 -ne $null -and $t2 -match 'transcript'
Write-Output ("[host] second transcript received: " + $gotSecond)

if ($gotFirst -and $gotDone -and $gotSecond) {
    Write-Output "RESULT: PASS — the dialog continues (first reply → second utterance recognized)"
} elseif ($gotFirst -and $gotDone -and -not $gotSecond) {
    Write-Output "RESULT: FAIL — second utterance NOT recognized (the chat stops after the first reply)"
} else {
    Write-Output "RESULT: INCONCLUSIVE (first=$gotFirst done=$gotDone second=$gotSecond) — check mic/speaker/volume"
}

try { $proc.StandardInput.WriteLine('{"cmd":"stop"}'); $proc.StandardInput.Flush() } catch { }
Start-Sleep -Seconds 1
try { $proc.Kill() } catch { }
