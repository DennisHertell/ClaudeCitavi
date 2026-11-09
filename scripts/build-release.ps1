<#
.SYNOPSIS
  Builds both release packages:
    ClaudeCitavi-<version>-AddOn.zip  ways A and B: add-on in Citavi's AddOns folder (admin once), it-install.ps1
    ClaudeCitavi-<version>-Makro.zip  way C: no admin rights, bridge started by a Citavi macro, makro-einrichten.ps1
  Both contain the add-on DLL with SHA256, an offline wheelhouse of the MCP server (Python 3.12, Windows x64)
  and the documentation.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -OutDir D:\release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutDir,
    [string]$CitaviDir = "${env:ProgramFiles(x86)}\Citavi 6\bin"
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH" }
$version = ([xml](Get-Content (Join-Path $repo 'bridge\src\ClaudeCitavi.Bridge\ClaudeCitavi.Bridge.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$common = Join-Path $env:TEMP ("ClaudeCitavi-common-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path "$common\AddOn", "$common\wheelhouse", "$common\docs" | Out-Null
dotnet build (Join-Path $repo 'bridge\src\ClaudeCitavi.Bridge') -c Release -o "$common\build" "-p:CitaviDir=$CitaviDir" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen." }
Copy-Item "$common\build\ClaudeCitavi.Bridge.dll" "$common\AddOn\"
Remove-Item -Recurse -Force "$common\build"
$hash = (Get-FileHash "$common\AddOn\ClaudeCitavi.Bridge.dll" -Algorithm SHA256).Hash
"$hash  ClaudeCitavi.Bridge.dll" | Set-Content "$common\AddOn\ClaudeCitavi.Bridge.dll.sha256" -Encoding ASCII
& py -3.12 -m pip wheel (Join-Path $repo 'mcp-server') -w "$common\wheelhouse" --quiet
if ($LASTEXITCODE -ne 0) { throw "Wheelhouse fehlgeschlagen." }
# Pinned requirements with SHA256 for every wheel: pip install --require-hashes refuses any modified file.
$req = foreach ($w in Get-ChildItem "$common\wheelhouse" -Filter *.whl | Sort-Object Name) {
    $parts = $w.BaseName.Split('-')
    "{0}=={1} --hash=sha256:{2}" -f $parts[0], $parts[1], (Get-FileHash $w.FullName -Algorithm SHA256).Hash.ToLower()
}
$req | Set-Content "$common\wheelhouse\requirements.txt" -Encoding ASCII
Copy-Item (Join-Path $repo 'scripts\nutzer-einrichten.ps1') $common
Copy-Item (Join-Path $repo 'docs\ZITIERREGELN.md'), (Join-Path $repo 'docs\NUTZUNG.md') "$common\docs\"
# Source of exactly this build, so IT can review it or rebuild the DLL.
Push-Location $repo
git archive --format=zip -o "$common\source.zip" HEAD
Pop-Location
& (Join-Path $PSScriptRoot 'md2pdf.ps1') -In (Join-Path $repo 'docs\IT-INSTALLATION.md') -Out "$common\docs\IT-Anleitung.pdf" -Meta "Version $version"
& (Join-Path $PSScriptRoot 'md2pdf.ps1') -In (Join-Path $repo 'docs\anleitung-makro\ANLEITUNG-MAKRO.md') -Out "$common\docs\Anleitung-Makro.pdf" -Meta "Version $version"

function Pack($name, [string[]]$files, [string[]]$docs) {
    $stage = "$common-$name"
    Copy-Item -Recurse $common $stage
    foreach ($f in $files) { Copy-Item (Join-Path $repo $f) $stage }
    foreach ($d in $docs) { Copy-Item (Join-Path $repo $d) "$stage\docs\" }
    $zip = Join-Path $OutDir "ClaudeCitavi-$version-$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$stage\*" -DestinationPath $zip
    Remove-Item -Recurse -Force $stage
    Write-Host "Paket: $zip"
}

Pack 'AddOn' @('scripts\it-install.ps1') @('docs\IT-INSTALLATION.md', 'docs\INSTALLATION.md')
Pack 'Makro' @('scripts\Einrichten.cmd', 'scripts\makro-einrichten.ps1', 'bridge\macro\StartClaudeBridge.cs') @('docs\INSTALLATION.md')
Remove-Item -Recurse -Force $common
$sums = Get-ChildItem $OutDir -Filter "ClaudeCitavi-$version-*.zip" | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name }
$sums += "$hash  ClaudeCitavi.Bridge.dll"
$sums | Set-Content (Join-Path $OutDir "SHA256SUMS-$version.txt") -Encoding ASCII
$sums | ForEach-Object { Write-Host $_ }
