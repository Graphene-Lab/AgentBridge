# Puppet TCP client helper: sends one JSON command to localhost:5292 and prints the response.
# Usage: puppet.ps1 <json-body>  (e.g. .\puppet.ps1 '{"type":"capture"}')
param([Parameter(Mandatory=$true)][string]$Body)
$ErrorActionPreference = 'Stop'
$client = New-Object System.Net.Sockets.TcpClient('127.0.0.1', 5292)
try {
    $stream = $client.GetStream()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
    $client.Client.Shutdown([System.Net.Sockets.SocketShutdown]::Send)  # signal EOF
    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
    $result = $reader.ReadToEnd()
    Write-Output $result
} finally {
    $client.Close()
}
