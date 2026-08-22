# Interactive SIP configuration generator for AgentBridge (Windows, PowerShell).
# Asks a few questions in English and produces the "Sip" configuration — the SAME
# structure the TUI /sip config commands read and write (appsettings.json -> Sip).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File sip-config.ps1                 # writes ./sip.json
#   powershell -ExecutionPolicy Bypass -File sip-config.ps1 -AppSettings path\to\appsettings.json
#   (or just run sip-config.bat — the wrapper below)
param([string]$AppSettings = "")

function Ask([string]$prompt, [string]$default) {
    $answer = Read-Host -Prompt "$prompt [$default]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
    return $answer.Trim()
}

$enabled = Ask "Enable the SIP server? (y/n)" "y"
$enabled = $enabled -match '^(y|yes|true|1)$'
$registrar = Ask "Registrar / SIP entry point (e.g. sip:195.20.235.5:5060; empty = direct dial only)" ""
$username = Ask "Username used to REGISTER at the entry point (e.g. agent)" "agent"
$password = Ask "Password / shared secret for the REGISTER" ""
$listenPort = [int](Ask "Local SIP listen port (non-standard port if your ISP drops inbound UDP 5060)" "6070")
$registerExpiry = [int](Ask "REGISTER refresh interval in seconds (60 keeps home-NAT mappings alive)" "60")
$answerMode = Ask "Incoming-call gate (pin | allowlist | none)" "pin"
$pin = Ask "DTMF PIN" "12345"
$lang = Ask "STT/TTS language, two-letter ISO (it, en, ...)" "it"

$sip = [ordered]@{
    Enabled          = $enabled
    ListenPort       = $listenPort
    Registrar        = $registrar
    Username         = $username
    Password         = $password
    AnswerMode       = $answerMode
    Pin              = $pin
    MaxPinAttempts   = 3
    LockoutHours     = 24
    RegisterExpiry   = $registerExpiry
    AllowedCallers   = @()
    Agent            = "default-agent"
    Lang             = $lang
    SttExePath       = ""
    RtpPortRange     = ""
}

$outPath = Join-Path (Get-Location) "sip.json"
$sipJson = $sip | ConvertTo-Json
[System.IO.File]::WriteAllText($outPath, $sipJson, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ""
Write-Host "Sip section written to $outPath"

if ([string]::IsNullOrWhiteSpace($AppSettings)) {
    $AppSettings = Join-Path (Get-Location) "appsettings.json"
}
if (Test-Path $AppSettings) {
    try {
        $cfg = Get-Content $AppSettings -Raw | ConvertFrom-Json
        $cfg.Sip = $sip
        $merged = $cfg | ConvertTo-Json -Depth 10
        [System.IO.File]::WriteAllText($AppSettings, $merged, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Merged the Sip section into $AppSettings"
    } catch {
        Write-Host "Could not merge into $AppSettings : $($_.Exception.Message)"
        Write-Host "Apply the same keys with the TUI: /sip config set <key> <value>"
    }
} else {
    Write-Host "No appsettings.json found: copy the Sip section into your appsettings.json, or"
    Write-Host "apply the same keys with the TUI: /sip config set <key> <value>"
}
