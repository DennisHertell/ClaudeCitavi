<#
.SYNOPSIS
  Builds the add-on, closes Citavi, copies the DLL into Citavi's AddOns folder and starts Citavi again.
  Meant for an unattended machine (home server) where the AddOns folder is writable for the user
  (one-time: create the folder as admin and grant the user Modify rights).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1
#>
[CmdletBinding()]
param(
    [string]$CitaviDir = "${env:ProgramFiles(x86)}\Citavi 6\bin",
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$addons = Join-Path (Split-Path -Parent $CitaviDir) 'AddOns'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH" }

dotnet build (Join-Path $repo 'bridge\src\ClaudeCitavi.Bridge') -c Release -o (Join-Path $repo 'bridge\out') "-p:CitaviDir=$CitaviDir" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen." }

$citavi = Get-Process Citavi -ErrorAction SilentlyContinue
if ($citavi) {
    Write-Host "Beende Citavi (Cloud-Projekte speichern automatisch)..."
    # Closing the last project window brings up the start window, so close repeatedly.
    for ($i = 0; $i -lt 8 -and -not $citavi.HasExited; $i++) {
        $citavi.Refresh()
        $citavi.CloseMainWindow() | Out-Null
        $citavi.WaitForExit(10000) | Out-Null
    }
    if (-not $citavi.HasExited) { throw "Citavi hat sich nicht beendet (offener Dialog?)." }
}

Copy-Item -Force (Join-Path $repo 'bridge\out\ClaudeCitavi.Bridge.dll') $addons
Write-Host "Add-on kopiert nach $addons"

if (-not $NoRestart) {
    Start-Process (Join-Path $CitaviDir 'Citavi.exe')
    Write-Host "Citavi gestartet. Log: $env:APPDATA\ClaudeCitavi\bridge.log"
}
