@echo off
REM One-click launcher for FT-MONITOR.
REM Updates from git, builds, runs, and opens the home page in your browser.
REM Prefers PowerShell 7 (pwsh) and falls back to Windows PowerShell.

setlocal
set "SCRIPT_DIR=%~dp0"

where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%launch-ft-monitor.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%launch-ft-monitor.ps1" %*
)

if errorlevel 1 (
    echo.
    echo Launch failed. Press any key to close this window.
    pause >nul
)
endlocal
