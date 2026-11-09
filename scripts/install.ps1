<#
.SYNOPSIS
  Installs ClaudeCitavi on Windows: builds the Citavi add-on, copies it into
  Citavi's AddOns folder, installs the MCP server and registers it with Claude.

.EXAMPLE
  # From the repository root, in PowerShell (Citavi must be closed):
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1

.EXAMPLE
  # Way C: start the bridge via a Citavi macro instead of the AddOns folder
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Macro

.EXAMPLE
  # Non-default Citavi location:
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -CitaviDir "D:\Citavi 6\bin"
#>
[CmdletBinding()]
param(
    [string]$CitaviDir,
    [switch]$SkipAddon,
    [switch]$Macro,
    [switch]$SkipMcp,
    [switch]$SkipClaudeDesktop
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

# --- 1. Locate Citavi ------------------------------------------------------------------------------
if (-not $CitaviDir) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Citavi 6\bin",
        "$env:ProgramFiles\Citavi 6\bin",
        "$env:ProgramFiles\Citavi 7\bin",
        "${env:ProgramFiles(x86)}\Citavi 7\bin"
    )
    $CitaviDir = $candidates | Where-Object { Test-Path (Join-Path $_ 'Citavi.exe') } | Select-Object -First 1
}
if (-not $CitaviDir -or -not (Test-Path (Join-Path $CitaviDir 'Citavi.exe'))) {
    throw "Citavi.exe nicht gefunden. Bitte -CitaviDir angeben (Ordner, der Citavi.exe enthaelt)."
}
$addonsDir = Join-Path (Split-Path -Parent $CitaviDir) 'AddOns'
Write-Host "Citavi:  $CitaviDir"
Write-Host "AddOns:  $addonsDir"

# --- 2. Build and install the add-on ---------------------------------------------------------------
if (-not $SkipAddon) {
    if (Get-Process -Name Citavi -ErrorAction SilentlyContinue) {
        throw "Citavi laeuft noch. Bitte Citavi schliessen und das Skript erneut starten."
    }
    # .NET may be installed per user (dotnet-install.ps1) and not be on PATH.
    $userDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
    if (Test-Path (Join-Path $userDotnet 'dotnet.exe')) { $env:PATH = "$userDotnet;$env:PATH" }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw ".NET SDK fehlt. Siehe docs/ENTWICKLUNG.md."
    }

    Step "Baue Citavi-Add-on"
    $project = Join-Path $repo 'bridge\src\ClaudeCitavi.Bridge\ClaudeCitavi.Bridge.csproj'
    $out = Join-Path $repo 'bridge\out'
    dotnet build $project -c Release -o $out "-p:CitaviDir=$CitaviDir" --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen." }

    $dll = Join-Path $out 'ClaudeCitavi.Bridge.dll'
    if ($Macro) {
        # Way C: DLL + macro go to the user profile; the macro starts the bridge.
        $target = Join-Path $env:APPDATA 'ClaudeCitavi'
        Step "Kopiere Bridge und Start-Makro nach $target (Benutzerprofil)"
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item -Force $dll $target
        Copy-Item -Force (Join-Path $repo 'bridge\macro\StartClaudeBridge.cs') $target
        Write-Host "Nach jedem Citavi-Start: Extras > Makro-Editor (Alt+F11), Datei $target\StartClaudeBridge.cs oeffnen, Ausfuehren (F5)."
    }
    else {
        Step "Kopiere Add-on nach $addonsDir (Administratorrechte erforderlich)"
        $copy = "New-Item -ItemType Directory -Force -Path '$addonsDir' | Out-Null; Copy-Item -Force '$dll' '$addonsDir'"
        Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile", "-Command", $copy
        if (-not (Test-Path (Join-Path $addonsDir 'ClaudeCitavi.Bridge.dll'))) {
            throw "Kopieren in den AddOns-Ordner fehlgeschlagen (Administratorrechte noetig). Alternative: Weg C mit -Macro."
        }
    }
}

# --- 3. Install the MCP server ---------------------------------------------------------------------
if (-not $SkipMcp) {
    Step "Installiere MCP-Server (Python)"
    # A running MCP server (started by Claude Desktop / Claude Code) locks citavi-mcp.exe and python.exe.
    $running = Get-Process -Name citavi-mcp -ErrorAction SilentlyContinue
    if ($running) {
        throw "Der MCP-Server laeuft noch (Claude Desktop/Claude Code ist geoeffnet). Bitte Claude ganz beenden (auch im Infobereich neben der Uhr) und das Skript erneut starten."
    }
    $py = Get-Command py -ErrorAction SilentlyContinue
    if (-not $py) { $py = Get-Command python -ErrorAction SilentlyContinue }
    if (-not $py) { throw "Python 3.10+ fehlt. Download: https://www.python.org/downloads/ (Haken bei 'Add python.exe to PATH')" }

    $venv = Join-Path $repo 'mcp-server\.venv'
    & $py.Source -m venv $venv
    & "$venv\Scripts\python.exe" -m pip install --upgrade pip --quiet
    & "$venv\Scripts\python.exe" -m pip install -e (Join-Path $repo 'mcp-server') --quiet
    $mcpExe = Join-Path $venv 'Scripts\citavi-mcp.exe'
    Write-Host "MCP-Server: $mcpExe"

    # --- 4. Register with Claude Desktop ----------------------------------------------------------
    if (-not $SkipClaudeDesktop) {
        Step "Trage MCP-Server in Claude Desktop ein"
        $cfgPath = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
        New-Item -ItemType Directory -Force -Path (Split-Path $cfgPath) | Out-Null
        $cfg = if (Test-Path $cfgPath) { Get-Content $cfgPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
        if (-not $cfg.PSObject.Properties['mcpServers']) {
            $cfg | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{})
        }
        $entry = [pscustomobject]@{ command = $mcpExe; args = @() }
        $cfg.mcpServers | Add-Member -Force -NotePropertyName citavi -NotePropertyValue $entry
        if (Test-Path $cfgPath) { Copy-Item $cfgPath "$cfgPath.bak" -Force }
        $cfg | ConvertTo-Json -Depth 20 | Set-Content -Path $cfgPath -Encoding UTF8
        Write-Host "Eingetragen in $cfgPath (Sicherung: .bak). Claude Desktop bitte neu starten."
    }

    if (Get-Command claude -ErrorAction SilentlyContinue) {
        Write-Host "`nClaude Code gefunden. Registrierung (optional):"
        Write-Host "  claude mcp add --scope user citavi -- `"$mcpExe`""
    }
}

Step "Fertig"
Write-Host "1. Citavi starten und ein Projekt oeffnen."
Write-Host "2. In Claude fragen: 'Pruefe die Verbindung zu Citavi.'"
Write-Host "Log der Bridge: $env:APPDATA\ClaudeCitavi\bridge.log"
