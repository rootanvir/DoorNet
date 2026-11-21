#requires -version 5.1
<#
.SYNOPSIS
    Builds and starts DoorNet.

.DESCRIPTION
    Builds the WPF app (Debug) and launches it. Use -Elevated to launch through UAC:
    administrator rights are required for per-app byte usage (kernel ETW) and for
    blocking applications with Windows Firewall rules.

.EXAMPLE
    .\run.ps1

.EXAMPLE
    .\run.ps1 -Elevated
#>
param(
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\DoorNet.App\DoorNet.App.csproj'
$exe = Join-Path $root 'src\DoorNet.App\bin\Debug\net10.0-windows\DoorNet.App.exe'

if (Get-Process -Name 'DoorNet.App' -ErrorAction SilentlyContinue) {
    Write-Host 'DoorNet is already running. Close it first (the running app locks bin\ binaries).' -ForegroundColor Yellow
    exit 1
}

Write-Host 'Building DoorNet...' -ForegroundColor Cyan
& dotnet build $project -v q --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Build failed.' -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ("Starting {0}" -f $exe) -ForegroundColor Cyan

if ($Elevated) {
    try {
        Start-Process -FilePath $exe -Verb RunAs | Out-Null
        Write-Host 'Launched elevated (UAC).' -ForegroundColor Green
    }
    catch {
        Write-Host "Elevation was cancelled or failed: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host 'Starting unelevated instead. Per-app bytes and firewall actions stay disabled.' -ForegroundColor Yellow
        Start-Process -FilePath $exe | Out-Null
    }
}
else {
    Start-Process -FilePath $exe | Out-Null
    Write-Host 'Started. Use the header button "Restart elevated" for per-app bytes and firewall control.' -ForegroundColor Green
}
