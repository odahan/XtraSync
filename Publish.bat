@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish.ps1"
set "publishExitCode=%errorlevel%"
pause
endlocal & exit /b %publishExitCode%
