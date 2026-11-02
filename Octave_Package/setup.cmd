@echo off
setlocal
title OCTAVE Setup & Installer

:: Verify PowerShell is available
where powershell >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [ERROR] PowerShell is required to run OCTAVE Setup.
    pause
    exit /b 1
)

:: Run setup.ps1 with execution policy bypass
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
if %ERRORLEVEL% neq 0 (
    echo.
    echo [ERROR] Setup encountered an issue. See above output for details.
    pause
    exit /b %ERRORLEVEL%
)

exit /b 0
