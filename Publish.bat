@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish.ps1"
exit /b %errorlevel%
