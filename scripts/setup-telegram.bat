@echo off
REM Interactive Telegram configuration generator for AgentBridge (Windows).
REM Asks a few questions in English and produces/updates telegram.json — the SAME
REM file the TUI /telegram config commands and the TelegramBridge read and write.
REM
REM Usage:
REM   setup-telegram.bat                    writes .\telegram.json
REM   setup-telegram.bat path\to\file.json  updates an existing file
REM
REM After configuring, start the bridge from the TUI (/telegram status, then set
REM Enabled true) and complete the first login with /telegram login-code <code>.
setlocal EnableDelayedExpansion

set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=telegram.json"

set /p ENABLED=Enable the Telegram bridge at startup? (y/n) [y]: 
if "%ENABLED%"=="" set "ENABLED=y"
if /i "%ENABLED%"=="y" (set "ENABLED=true") else if "%ENABLED%"=="1" (set "ENABLED=true") else (set "ENABLED=false")

set /p PHONE=Account phone number, international format (e.g. +393331234567):
set /p SESSION=Session file name (auth keys persist here after the first login) [telegram.session]:
if "%SESSION%"=="" set "SESSION=telegram.session"
set /p ALLOWED=Allowed users, comma separated (numeric ids or @usernames; empty = all private chats):

REM The app credentials (ApiId/ApiHash) are built into AgentBridge - no need to ask.
REM Override them in telegram.json only to use a per-install app identity.
(
echo {
echo   "Enabled": %ENABLED%,
echo   "PhoneNumber": "%PHONE%",
echo   "SessionPath": "%SESSION%",
echo   "AllowedUsers": [
for /f "tokens=1,* delims=," %%a in ("%ALLOWED%") do (
  set "FIRST=%%a"
  set "REST=%%b"
)
if defined REST (
  echo     "!FIRST!",
  echo     "!REST!"
) else (
  echo     "!FIRST!"
)
echo   ],
echo   "Agent": "default-agent"
echo }
) > "%TARGET%.tmp"

REM Keep JSON valid: an empty AllowedUsers answer produces a bare empty array.
if "%ALLOWED%"=="" (
  (
  echo {
  echo   "Enabled": %ENABLED%,
  echo   "PhoneNumber": "%PHONE%",
  echo   "SessionPath": "%SESSION%",
  echo   "AllowedUsers": [],
  echo   "Agent": "default-agent"
  echo }
  ) > "%TARGET%.tmp"
)

move /y "%TARGET%.tmp" "%TARGET%" >nul

echo.
echo Telegram configuration written to %TARGET%
echo.
echo Next steps:
echo   1. Run AgentBridge and open the TUI.
echo   2. /telegram status                  - see the bridge state.
echo   3. /telegram config set Enabled true - start the bridge (first login requires
echo      a verification code from Telegram; send it with /telegram login-code ^<code^>).
echo   4. /telegram allow ^<user^>          - optionally restrict who can talk to the agent.
echo More: docs/telegram.md
endlocal
