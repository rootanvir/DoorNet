@echo off
setlocal
set "ROOT=%~dp0"

if /I "%~1"=="elevated" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%run.ps1" -Elevated
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%run.ps1"
)

if errorlevel 1 pause
endlocal
