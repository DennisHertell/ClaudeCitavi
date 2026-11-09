<#
.SYNOPSIS
  Renders a Markdown file from docs\ as a typeset PDF: Pandoc (Markdown to HTML with scripts\pdf.css),
  then headless Chrome or Edge (HTML to PDF).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\md2pdf.ps1 -In docs\IT-INSTALLATION.md -Out D:\IT-Anleitung.pdf -Meta "Version 0.4.0"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$In,
    [Parameter(Mandatory)][string]$Out,
    [string]$Meta = ""
)

$ErrorActionPreference = 'Stop'
$css = Join-Path $PSScriptRoot 'pdf.css'
$browser = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
             "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw "Chrome oder Edge nicht gefunden." }
if (-not (Get-Command pandoc -ErrorAction SilentlyContinue)) { throw "Pandoc nicht gefunden." }

$tmp = Join-Path $env:TEMP ("md2pdf-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$html = Join-Path $tmp 'doc.html'
$title = (Get-Content $In -Encoding UTF8 | Where-Object { $_ -match '^# ' } | Select-Object -First 1) -replace '^# ', ''
pandoc $In -f markdown -t html5 --standalone --embed-resources --resource-path (Split-Path -Parent ([IO.Path]::GetFullPath($In))) --css $css --metadata "pagetitle=$title" -o $html
if ($LASTEXITCODE -ne 0) { throw "Pandoc fehlgeschlagen." }
if ($Meta) {
    $content = Get-Content $html -Raw -Encoding UTF8
    $content = [regex]::Replace($content, '(</h1>)', "`$1<div class=""meta"">$Meta</div>", 1)
    [IO.File]::WriteAllText($html, $content, (New-Object Text.UTF8Encoding $false))
}
$outFull = [IO.Path]::GetFullPath($Out)
# Start-Process: Chrome writes harmless log lines to stderr, which Windows PowerShell would treat as errors.
$args = @('--headless=new', '--disable-gpu', '--no-pdf-header-footer', "--print-to-pdf=""$outFull""", ([Uri]$html).AbsoluteUri)
Start-Process -FilePath $browser -ArgumentList $args -Wait -WindowStyle Hidden
if (-not (Test-Path $outFull)) { throw "PDF wurde nicht erzeugt." }
Remove-Item -Recurse -Force $tmp
Write-Host "PDF: $outFull"
