@echo off
echo sync Git...
cd /d "%~dp0"

git add -A
git commit -m "Syncronization repository at %date% %time%"
git pull origin main --no-edit
git push origin main

echo Operation completed!
pause