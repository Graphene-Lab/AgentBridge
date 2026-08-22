@echo off
rem Interactive SIP configuration generator for AgentBridge (Windows).
rem Wrapper for sip-config.ps1 — writes ./sip.json and merges the Sip section into
rem appsettings.json when found (same configuration the TUI /sip config uses).
rem Usage: sip-config.bat [path\to\appsettings.json]
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0sip-config.ps1" -AppSettings "%1"
endlocal
