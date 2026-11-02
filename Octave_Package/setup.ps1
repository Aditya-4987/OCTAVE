<#
.SYNOPSIS
    OCTAVE Music Player - Automated Setup & Installer for Windows 10 & 11
.DESCRIPTION
    Installs OCTAVE into %LOCALAPPDATA%\Programs\Octave (standard per-user location,
    requiring zero admin/UAC elevation). Creates Start Menu & Desktop shortcuts,
    and registers OCTAVE in Windows Settings > Installed apps with full uninstall support.
.PARAMETER Silent
    Performs silent, unattended installation without user prompts.
.PARAMETER Launch
    Automatically launches OCTAVE after installation completes.
#>
[CmdletBinding()]
param (
    [switch]$Silent,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Text)
    Write-Host "`n[+] " -ForegroundColor Cyan -NoNewline
    Write-Host $Text -ForegroundColor White
}

function Write-Success {
    param([string]$Text)
    Write-Host "    [OK] " -ForegroundColor Green -NoNewline
    Write-Host $Text -ForegroundColor Gray
}

function Write-Info {
    param([string]$Text)
    Write-Host "    [i]  " -ForegroundColor Yellow -NoNewline
    Write-Host $Text -ForegroundColor Gray
}

function Write-Failure {
    param([string]$Text)
    Write-Host "`n[!] " -ForegroundColor Red -NoNewline
    Write-Host $Text -ForegroundColor White
}

Clear-Host
Write-Host "=================================================================" -ForegroundColor DarkCyan
Write-Host "                OCTAVE Music Player - Setup                      " -ForegroundColor Cyan
Write-Host "    High-Performance, High-Fidelity Music Player for Windows    " -ForegroundColor Gray
Write-Host "=================================================================" -ForegroundColor DarkCyan

# 1. Determine Source Directory containing the packaged binaries
$scriptDir = $PSScriptRoot
$sourceDir = $null

if (Test-Path (Join-Path $scriptDir "Octave.Desktop.exe")) {
    $sourceDir = $scriptDir
} elseif (Test-Path (Join-Path $scriptDir "Octave_Package\Octave.Desktop.exe")) {
    $sourceDir = Join-Path $scriptDir "Octave_Package"
} elseif (Test-Path (Join-Path $scriptDir "Octave.Desktop\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\Octave.Desktop.exe")) {
    $sourceDir = Join-Path $scriptDir "Octave.Desktop\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"
} else {
    Write-Failure "Could not locate packaged OCTAVE binaries (Octave.Desktop.exe)."
    Write-Host "Please ensure 'Octave_Package' folder is present or run 'dotnet publish' first." -ForegroundColor Yellow
    exit 1
}

Write-Step "Checking source package..."
Write-Success "Found package at: $sourceDir"

# 2. Check for currently running instances of OCTAVE
$runningProcesses = Get-Process -Name "Octave.Desktop" -ErrorAction SilentlyContinue
if ($runningProcesses) {
    Write-Step "Closing active OCTAVE instance..."
    foreach ($p in $runningProcesses) {
        try {
            $p.CloseMainWindow() | Out-Null
            Start-Sleep -Milliseconds 800
            if (-not $p.HasExited) {
                $p.Kill()
            }
            Write-Success "Closed running OCTAVE process ($($p.Id))."
        } catch {
            Write-Info "Could not gracefully close process: $($_.Exception.Message)"
        }
    }
}

# 3. Setup Target Installation Path
$targetDir = Join-Path $env:LOCALAPPDATA "Programs\Octave"
Write-Step "Setting up installation directory..."
Write-Info "Destination: $targetDir"

if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

# 4. Copy Application Files
Write-Step "Deploying OCTAVE files..."
# Exclude setup/install scripts themselves from recursive copy if run from source
$excludeNames = @("setup.cmd", "setup.bat", "setup.ps1", "setup.iss")

Get-ChildItem -Path $sourceDir -Force | Where-Object { $_.Name -notin $excludeNames } | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination $targetDir -Recurse -Force
}
Write-Success "Application files deployed successfully."

# 5. Create Shortcuts (Start Menu & Desktop)
Write-Step "Creating system shortcuts..."
$wscript = New-Object -ComObject WScript.Shell
$mainExePath = Join-Path $targetDir "Octave.Desktop.exe"
$iconPath = Join-Path $targetDir "Assets\AppIcon.ico"
if (-not (Test-Path $iconPath)) {
    $iconPath = $mainExePath
}

# A. Start Menu
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
if (Test-Path $startMenuDir) {
    $startShortcutPath = Join-Path $startMenuDir "OCTAVE.lnk"
    $startShortcut = $wscript.CreateShortcut($startShortcutPath)
    $startShortcut.TargetPath = $mainExePath
    $startShortcut.WorkingDirectory = $targetDir
    $startShortcut.IconLocation = "$iconPath,0"
    $startShortcut.Description = "OCTAVE - High-Fidelity Music Player for Windows"
    $startShortcut.Save()
    Write-Success "Start Menu shortcut created: $startShortcutPath"
}

# B. Desktop
$desktopDir = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Desktop)
if (Test-Path $desktopDir) {
    $desktopShortcutPath = Join-Path $desktopDir "OCTAVE.lnk"
    $desktopShortcut = $wscript.CreateShortcut($desktopShortcutPath)
    $desktopShortcut.TargetPath = $mainExePath
    $desktopShortcut.WorkingDirectory = $targetDir
    $desktopShortcut.IconLocation = "$iconPath,0"
    $desktopShortcut.Description = "OCTAVE - High-Fidelity Music Player for Windows"
    $desktopShortcut.Save()
    Write-Success "Desktop shortcut created: $desktopShortcutPath"
}

# 6. Generate Uninstaller Scripts inside target directory
Write-Step "Generating uninstaller..."
$uninstallPs1Content = @'
[CmdletBinding()]
param ([switch]$Quiet)

$ErrorActionPreference = 'SilentlyContinue'

$running = Get-Process -Name "Octave.Desktop" -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
}

$startMenuShortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\OCTAVE.lnk"
if (Test-Path $startMenuShortcut) { Remove-Item -Path $startMenuShortcut -Force }

$desktopShortcut = Join-Path ([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::Desktop)) "OCTAVE.lnk"
if (Test-Path $desktopShortcut) { Remove-Item -Path $desktopShortcut -Force }

$regPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OCTAVE"
if (Test-Path $regPath) { Remove-Item -Path $regPath -Recurse -Force }

$installDir = $PSScriptRoot
# Self-deletion cleanup via background cmd
Start-Process -WindowStyle Hidden -FilePath "cmd.exe" -ArgumentList "/c timeout /t 2 /nobreak > NUL & rmdir /s /q `"$installDir`""
if (-not $Quiet) {
    [System.Windows.Forms.MessageBox]::Show("OCTAVE has been successfully uninstalled from your system.", "OCTAVE Uninstalled", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
}
'@

$uninstallPs1Path = Join-Path $targetDir "uninstall.ps1"
Set-Content -Path $uninstallPs1Path -Value $uninstallPs1Content -Encoding UTF8

$uninstallCmdContent = @"
@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
"@
$uninstallCmdPath = Join-Path $targetDir "uninstall.cmd"
Set-Content -Path $uninstallCmdPath -Value $uninstallCmdContent -Encoding ASCII
Write-Success "Uninstaller registered at: $uninstallCmdPath"

# 7. Register in Windows Settings > Installed Apps (Add/Remove Programs)
Write-Step "Registering in Windows Settings..."
$uninstallRegKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OCTAVE"
if (-not (Test-Path $uninstallRegKey)) {
    New-Item -Path $uninstallRegKey -Force | Out-Null
}

$installSizeKB = 0
try {
    $installSizeKB = [int]((Get-ChildItem -Path $targetDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1KB)
} catch { }

Set-ItemProperty -Path $uninstallRegKey -Name "DisplayName" -Value "OCTAVE"
Set-ItemProperty -Path $uninstallRegKey -Name "DisplayVersion" -Value "1.0.0"
Set-ItemProperty -Path $uninstallRegKey -Name "Publisher" -Value "OCTAVE Team"
Set-ItemProperty -Path $uninstallRegKey -Name "DisplayIcon" -Value "$iconPath,0"
Set-ItemProperty -Path $uninstallRegKey -Name "InstallLocation" -Value $targetDir
Set-ItemProperty -Path $uninstallRegKey -Name "UninstallString" -Value "`"$uninstallCmdPath`""
Set-ItemProperty -Path $uninstallRegKey -Name "QuietUninstallString" -Value "`"$uninstallCmdPath`" -Quiet"
Set-ItemProperty -Path $uninstallRegKey -Name "EstimatedSize" -Value $installSizeKB
Set-ItemProperty -Path $uninstallRegKey -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallRegKey -Name "NoRepair" -Value 1 -Type DWord
Write-Success "Registered in Windows Installed Apps control panel."

# 8. Finished
Write-Host "`n=================================================================" -ForegroundColor DarkGreen
Write-Host "                OCTAVE Installation Completed!                   " -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor DarkGreen
Write-Host "OCTAVE is now ready to use!" -ForegroundColor White
Write-Host "  * Start Menu: Search 'OCTAVE'" -ForegroundColor Gray
Write-Host "  * Desktop: Shortcut created" -ForegroundColor Gray
Write-Host "  * Installed Location: $targetDir" -ForegroundColor Gray
Write-Host "  * User Library & Cache: $env:LOCALAPPDATA\Octave" -ForegroundColor Gray

if ($Launch -or (-not $Silent)) {
    if ($Launch) {
        $shouldRun = $true
    } else {
        $response = Read-Host "`nDo you want to launch OCTAVE now? (Y/n)"
        $shouldRun = ($response -eq '' -or $response -match '^[Yy]')
    }

    if ($shouldRun) {
        Write-Info "Launching OCTAVE..."
        Start-Process -FilePath $mainExePath
    }
}
