@echo off
setlocal EnableDelayedExpansion
title Giraffe AI Launcher

echo ========================================
echo  GIRAFFE AI - LAUNCHER
echo ========================================
echo.

REM ---- Parse --provider <base64url of provider JSON> ----
REM The value is base64url (no padding, no '=') so it survives the cmd.exe command line:
REM a raw JSON with embedded quotes would be mangled, and '=' is an argument separator
REM for cmd. The value is decoded and URL-encoded below, then appended to the opened URL
REM as ?provider=<url-encoded JSON>; the client registers and selects the provider on
REM load (see index.html init()).
set "AUTO_PROVIDER="
:parse_args
if "%~1"=="" goto :args_done
if /i "%~1"=="--provider" (
    set "AUTO_PROVIDER=%~2"
    shift
    shift
    goto :parse_args
)
echo Parametro sconosciuto: %~1
exit /b 1
:args_done

set "BROWSER_URL=http://localhost:8000/"
if defined AUTO_PROVIDER (
    for /f "usebackq delims=" %%u in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$b='%AUTO_PROVIDER%';$b=$b.Replace('-','+').Replace('_','/');$b=$b.PadRight(($b.Length+3)-band-4,'=');$j=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b));[uri]::EscapeDataString($j)"`) do set "PROVIDER_ENC=%%u"
    if not defined PROVIDER_ENC (
        echo ERRORE: impossibile decodificare il provider.
        exit /b 1
    )
    set "BROWSER_URL=http://localhost:8000/?provider=!PROVIDER_ENC!"
    echo Provider auto-config ricevuto.
)

echo [1/4] Checking port 8000...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$porta=8000; $inUso=Get-NetTCPConnection -LocalPort $porta -ErrorAction SilentlyContinue; if($inUso){ Write-Host 'PORT 8000 ALREADY IN USE - Server already running' -ForegroundColor Green; exit 0 } else { Write-Host 'PORT 8000 FREE - Starting server...' -ForegroundColor Yellow; exit 1 }"

if errorlevel 1 goto :start_server
if errorlevel 0 goto :open_browser

:start_server
echo.
echo [2/4] Starting server...
start /B powershell -NoProfile -ExecutionPolicy Bypass -Command "Add-Type -AssemblyName System.Net.Http;$listener=[System.Net.HttpListener]::new();$listener.Prefixes.Add('http://localhost:8000/');$listener.Start();Write-Host 'SERVER STARTED ON PORT 8000' -ForegroundColor Green;$client=[System.Net.Http.HttpClient]::new();$client.Timeout=[TimeSpan]::FromMinutes(10);while($true){try{$ctx=$listener.GetContext();$path=$ctx.Request.Url.AbsolutePath;if($path -like '/v1/*'){try{$bodyBytes=$null;if($ctx.Request.HasEntityBody){$ms=[System.IO.MemoryStream]::new();$ctx.Request.InputStream.CopyTo($ms);$bodyBytes=$ms.ToArray()};$req=[System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($ctx.Request.HttpMethod),'http://127.0.0.1:8787'+$path);if($bodyBytes){$req.Content=[System.Net.Http.ByteArrayContent]::new($bodyBytes);$req.Content.Headers.ContentType=[System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/json')};$resp=$client.SendAsync($req,[System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult();$ctx.Response.StatusCode=[int]$resp.StatusCode;if($resp.Content.Headers.ContentType){$ctx.Response.ContentType=$resp.Content.Headers.ContentType.MediaType};$ctx.Response.SendChunked=$true;$stream=$resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();$buffer=New-Object byte[] 8192;while(($n=$stream.Read($buffer,0,$buffer.Length))-gt 0){$ctx.Response.OutputStream.Write($buffer,0,$n);$ctx.Response.OutputStream.Flush()};$stream.Dispose();$resp.Dispose();$req.Dispose();$ctx.Response.OutputStream.Close()}catch{$err='Proxy error: '+$_.Exception.Message;$buf=[System.Text.Encoding]::UTF8.GetBytes($err);$ctx.Response.StatusCode=502;$ctx.Response.ContentType='text/plain';$ctx.Response.ContentLength64=$buf.Length;$ctx.Response.OutputStream.Write($buf,0,$buf.Length);$ctx.Response.OutputStream.Close()}}else{$file='index.html';if($path -ne '/'){$file='.'+$path};if(Test-Path $file){$content=[System.IO.File]::ReadAllBytes($file);$ctx.Response.ContentType='text/html';$ctx.Response.ContentLength64=$content.Length;$ctx.Response.OutputStream.Write($content,0,$content.Length)}else{$ctx.Response.StatusCode=404;$err='<html><body><h1>404 - File not found</h1></body></html>';$buffer=[System.Text.Encoding]::UTF8.GetBytes($err);$ctx.Response.ContentLength64=$buffer.Length;$ctx.Response.OutputStream.Write($buffer,0,$buffer.Length)};$ctx.Response.OutputStream.Close()}}catch{break}}"

timeout /t 2 /nobreak >nul
goto :open_browser

:open_browser
echo.
echo [3/4] Detecting browser and opening in App mode...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice' -ErrorAction SilentlyContinue).Progid; if(-not $p){ $p='chrome' }; Write-Host 'Browser detected:' $p -ForegroundColor Cyan; $e=switch -Wildcard($p){ '*Edge*' { 'msedge' } '*ChromeCanary*' { 'chrome' } '*EdgeCanary*' { 'msedge' } '*TorBrowser*' { 'tor' } '*Ungoogled*' { 'chromium' } '*Naver*' { 'whale' } '*Vivaldi*' { 'vivaldi' } default { $p -replace '\..*$','' -replace 'HTML$','' -replace 'HTM$','' -replace 'Stable$','' } }; $e=$e.ToLower(); try { if($p -match 'Firefox|Waterfox|PaleMoon|Basilisk|LibreWolf|Tor'){ Start-Process $e -ArgumentList '-kiosk !BROWSER_URL!' -ErrorAction Stop; Write-Host 'Firefox started in Kiosk mode' -ForegroundColor Yellow } elseif($p -match 'Edge|Chrome|Opera|Brave|Vivaldi|Chromium|Arc|Thorium|Iron|Whale|Yandex|Samsung|Maxthon|Slimjet|Comodo|Ungoogled|Naver'){ Start-Process $e -ArgumentList '--app=!BROWSER_URL!' -ErrorAction Stop; Write-Host 'Chromium browser started in App mode' -ForegroundColor Green } else { Start-Process '!BROWSER_URL!' -ErrorAction Stop; Write-Host 'Browser opened with URL (fallback)' -ForegroundColor Yellow } } catch { Write-Host 'ERROR: Unable to start browser!' -ForegroundColor Red; Write-Host $_.Exception.Message -ForegroundColor Red; Start-Process '!BROWSER_URL!' }"

echo.
echo [4/4] Server running at !BROWSER_URL!
echo.