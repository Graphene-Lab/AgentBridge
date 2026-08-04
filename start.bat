@echo off
title MinimalChatApi Launcher

echo ========================================
echo  MINIMAL CHAT API - LAUNCHER
echo ========================================
echo.

cd /d "%~dp0"

echo [1/2] Checking port 5290...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$porta=5290; $inUso=Get-NetTCPConnection -LocalPort $porta -ErrorAction SilentlyContinue; if($inUso){ Write-Host 'PORT 5290 ALREADY IN USE - Server already running' -ForegroundColor Green; exit 0 } else { Write-Host 'PORT 5290 FREE - Starting server...' -ForegroundColor Yellow; exit 1 }"

if errorlevel 1 goto :start_server
if errorlevel 0 goto :done

:start_server
echo.
echo [2/2] Starting MinimalChatApi (dotnet run)...
echo   OpenAI-compatible API:  http://localhost:5290
echo   Health check:           http://localhost:5290/health
echo   Press Ctrl+C to stop.
echo.
dotnet run --project MinimalChatApi.csproj
goto :done

:done
echo.
echo Server stopped.
pause
