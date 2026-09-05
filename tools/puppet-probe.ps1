# Diagnostic puppet probe with socket timeouts (no infinite hang possible).
param([string]$Body = '{"type":"capture"}', [int]$TimeoutMs = 5000)
$client = New-Object System.Net.Sockets.TcpClient
$client.ReceiveTimeout = $TimeoutMs
$client.SendTimeout = $TimeoutMs
$client.Connect('127.0.0.1', 5292)
$stream = $client.GetStream()
$bytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
$stream.Write($bytes, 0, $bytes.Length)
$stream.Flush()
$client.Client.Shutdown([System.Net.Sockets.SocketShutdown]::Send)
$buffer = New-Object byte[] 65536
$sb = New-Object System.Text.StringBuilder
try {
    while ($true) {
        $n = $stream.Read($buffer, 0, $buffer.Length)
        if ($n -le 0) { break }
        [void]$sb.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $n))
    }
} catch {
    Write-Host ("READ-FAIL: " + $_.Exception.Message)
}
Write-Host ("LEN=" + $sb.Length)
if ($sb.Length -gt 0) { Write-Output $sb.ToString() }
$client.Close()
