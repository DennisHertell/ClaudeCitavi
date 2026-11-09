<#
.SYNOPSIS
  Way C: set up ClaudeCitavi WITHOUT admin rights. Everything goes into the user profile:
  - %APPDATA%\ClaudeCitavi: ClaudeCitavi.Bridge.dll and the Citavi macro StartClaudeBridge.cs
  - %LOCALAPPDATA%\ClaudeCitavi\mcp-server: MCP server (own Python venv, packages from the bundled wheelhouse)
  - registration in the user's Claude configuration
  After every Citavi start the macro is run once (Citavi > Extras > Makro-Editor, open the file, F5).

  Prerequisite: Python 3.12 (python.org installer "for me only" works without admin rights).
#>
[CmdletBinding()]
param([switch]$SkipRegister)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

$dll = Join-Path $here 'AddOn\ClaudeCitavi.Bridge.dll'
$expected = (Get-Content (Join-Path $here 'AddOn\ClaudeCitavi.Bridge.dll.sha256') -Raw).Trim().Split(' ')[0]
if ((Get-FileHash $dll -Algorithm SHA256).Hash -ne $expected) { throw "Pruefsumme der DLL stimmt nicht. Paket nicht verwenden." }

$bridgeDir = Join-Path $env:APPDATA 'ClaudeCitavi'
New-Item -ItemType Directory -Force -Path $bridgeDir | Out-Null
Copy-Item -Force $dll $bridgeDir
Copy-Item -Force (Join-Path $here 'StartClaudeBridge.cs') $bridgeDir
Write-Host "Bridge und Makro abgelegt in $bridgeDir"

$python = $null
if (Get-Command py -ErrorAction SilentlyContinue) {
    if (& py -3.12 -c "print(1)" 2>$null) { $python = @('py', '-3.12') }
}
if (-not $python) {
    $candidate = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'
    if (Test-Path $candidate) { $python = @($candidate) }
}
if (-not $python) { throw "Python 3.12 fehlt. Von python.org installieren (ohne Adminrechte: 'Install for me only')." }

$venv = Join-Path $env:LOCALAPPDATA 'ClaudeCitavi\mcp-server\.venv'
New-Item -ItemType Directory -Force -Path (Split-Path $venv) | Out-Null
if (Test-Path $venv) { Remove-Item -Recurse -Force $venv }  # update: always a clean environment
& $python[0] @($python | Select-Object -Skip 1) -m venv $venv
& "$venv\Scripts\python.exe" -m pip install --no-index --find-links (Join-Path $here 'wheelhouse') --require-hashes -r (Join-Path $here 'wheelhouse\requirements.txt') --quiet
if ($LASTEXITCODE -ne 0) { throw "Installation des MCP-Servers fehlgeschlagen." }

& (Join-Path $here 'nutzer-einrichten.ps1') -McpExe "$venv\Scripts\citavi-mcp.exe"

Write-Host ""
Write-Host "Fertig. Nach jedem Citavi-Start einmal:"
Write-Host "  Citavi > Extras > Makro-Editor (Alt+F11) > Oeffnen: $bridgeDir\StartClaudeBridge.cs > Ausfuehren (F5)"
