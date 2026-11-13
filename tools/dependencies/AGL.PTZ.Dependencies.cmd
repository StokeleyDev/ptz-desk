@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Dependencies.ps1"
set "AGL_PTZ_EXIT=%errorlevel%"
pause
exit /b %AGL_PTZ_EXIT%
