# Sends the content of a JSON file as a single puppet command (avoids cmd.exe escaping).
# Usage: puppet-body.ps1 <json-file>
param([Parameter(Mandatory=$true)][string]$JsonFile)
$ErrorActionPreference = 'Stop'
$body = Get-Content $JsonFile -Raw -Encoding UTF8
$client = New-Object System.Net.Sockets.TcpClient('127.0.0.1', 5292)
try {
    $stream = $client.GetStream()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
    $client.Client.Shutdown([System.Net.Sockets.SocketShutdown]::Send)
    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
    $result = $reader.ReadToEnd()
    if ($result.Length -gt 2000) { Write-Output ($result.Substring(0, 2000) + '…') } else { Write-Output $result }
} finally {
    $client.Close()
}
