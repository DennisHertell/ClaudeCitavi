<#
.SYNOPSIS
  Per-user step after it-install.ps1 (no admin rights): registers the Citavi MCP server with
  Claude Desktop and, if present, Claude Code. Changes only the user's own Claude configuration.
#>
[CmdletBinding()]
param([string]$McpExe = "$env:ProgramFiles\ClaudeCitavi\mcp-server\.venv\Scripts\citavi-mcp.exe")

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $McpExe)) { throw "MCP-Server nicht gefunden: $McpExe. Bitte zuerst die IT-Installation ausfuehren lassen." }

# Claude Desktop
$cfgPath = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
New-Item -ItemType Directory -Force -Path (Split-Path $cfgPath) | Out-Null
$cfg = if (Test-Path $cfgPath) { Get-Content $cfgPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
if (-not $cfg.PSObject.Properties['mcpServers']) { $cfg | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{}) }
$cfg.mcpServers | Add-Member -Force -NotePropertyName citavi -NotePropertyValue ([pscustomobject]@{ command = $McpExe; args = @() })
if (Test-Path $cfgPath) { Copy-Item $cfgPath "$cfgPath.bak" -Force }
$cfg | ConvertTo-Json -Depth 20 | Set-Content -Path $cfgPath -Encoding UTF8
Write-Host "Claude Desktop: eingetragen in $cfgPath (Sicherung .bak). Claude Desktop neu starten."

# Claude Code (optional)
if (Get-Command claude -ErrorAction SilentlyContinue) {
    claude mcp add --scope user citavi -- "$McpExe"
    Write-Host "Claude Code: MCP-Server 'citavi' eingetragen."
}

Write-Host "Test: Citavi starten, Projekt oeffnen, in Claude fragen: 'Pruefe die Verbindung zu Citavi.'"
