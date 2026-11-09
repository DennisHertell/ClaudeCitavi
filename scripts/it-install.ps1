<#
.SYNOPSIS
  One-time installation of ClaudeCitavi by IT (admin rights), fully offline from the release package.

  1. Copies the Citavi add-on ClaudeCitavi.Bridge.dll into Citavi's AddOns folder (SHA256 is checked).
  2. Installs the MCP server for Claude into C:\Program Files\ClaudeCitavi\mcp-server (own Python venv,
     packages only from the bundled wheelhouse folder, no internet access).

  Prerequisites: Citavi 6 (desktop), Python 3.12 for all users (python.org installer). Citavi must be closed.

.EXAMPLE
  # In an elevated PowerShell, inside the unpacked release folder:
  powershell -ExecutionPolicy Bypass -File .\it-install.ps1
#>
[CmdletBinding()]
param(
    [string]$CitaviDir,
    [string]$TargetDir = "$env:ProgramFiles\ClaudeCitavi",
    [switch]$SkipMcp
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Bitte in einer PowerShell mit Administratorrechten ausfuehren." }

if (-not $CitaviDir) {
    $CitaviDir = @("${env:ProgramFiles(x86)}\Citavi 6\bin", "$env:ProgramFiles\Citavi 6\bin") |
        Where-Object { Test-Path (Join-Path $_ 'Citavi.exe') } | Select-Object -First 1
}
if (-not $CitaviDir) { throw "Citavi 6 nicht gefunden. Pfad mit -CitaviDir angeben (Ordner mit Citavi.exe)." }
if (Get-Process Citavi -ErrorAction SilentlyContinue) { throw "Citavi laeuft noch. Bitte schliessen und erneut starten." }

# --- 1. Add-on ------------------------------------------------------------------------------------
$dll = Join-Path $here 'AddOn\ClaudeCitavi.Bridge.dll'
$expected = (Get-Content (Join-Path $here 'AddOn\ClaudeCitavi.Bridge.dll.sha256') -Raw).Trim().Split(' ')[0]
$actual = (Get-FileHash $dll -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw "Pruefsumme stimmt nicht ($actual statt $expected). Paket nicht verwenden." }

$addons = Join-Path (Split-Path -Parent $CitaviDir) 'AddOns'
New-Item -ItemType Directory -Force -Path $addons | Out-Null
Copy-Item -Force $dll $addons
Write-Host "Add-on installiert: $addons\ClaudeCitavi.Bridge.dll (SHA256 $actual)"

# --- 2. MCP server ----------------------------------------------------------------------------------
if (-not $SkipMcp) {
    $py = Get-Command py -ErrorAction SilentlyContinue
    if (-not $py) { throw "Python 3.12 fehlt (python.org, Installation fuer alle Benutzer)." }
    $bits = & py -3.12 -c "import struct; print(struct.calcsize('P') * 8)" 2>$null
    if (-not $bits) { throw "Python 3.12 fehlt (die Pakete im Ordner wheelhouse sind fuer 3.12 gebaut)." }
    if ("$bits".Trim() -ne '64') { throw "Python 3.12 ist $bits Bit. Benoetigt wird die 64 Bit Version." }

    $venv = Join-Path $TargetDir 'mcp-server\.venv'
    New-Item -ItemType Directory -Force -Path (Split-Path $venv) | Out-Null
    if (Test-Path $venv) { Remove-Item -Recurse -Force $venv }  # update: always a clean environment
    & py -3.12 -m venv $venv
    # Every package is checked against the SHA256 in wheelhouse\requirements.txt.
    & "$venv\Scripts\python.exe" -m pip install --no-index --find-links (Join-Path $here 'wheelhouse') --require-hashes -r (Join-Path $here 'wheelhouse\requirements.txt') --quiet
    if ($LASTEXITCODE -ne 0) { throw "Installation des MCP-Servers fehlgeschlagen." }
    Copy-Item -Force (Join-Path $here 'einrichten.py'), (Join-Path $here 'Claude-verbinden.cmd') $TargetDir
    Write-Host "MCP-Server installiert: $venv\Scripts\citavi-mcp.exe"
}

Write-Host ""
Write-Host "Fertig. Jede Nutzerin und jeder Nutzer verbindet Claude danach einmal selbst per Doppelklick auf:"
Write-Host "  $TargetDir\Claude-verbinden.cmd"
