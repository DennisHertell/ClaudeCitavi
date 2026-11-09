#!/usr/bin/env bash
# Builds fake Citavi assemblies into $1 (default: ./out) so the bridge can be compiled in CI.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="${1:-$here/out}"
mkdir -p "$out"
dotnet build "$here/CitaviStubs.csproj" -c Release -o "$here/bin-stub" --nologo -v q
cp "$here/bin-stub/Citavi.dll" "$out/Citavi.exe"
cp "$here/bin-stub/Newtonsoft.Json.dll" "$out/"
# Empty placeholder assemblies (referenced by the bridge project, unused at compile time).
for name in SwissAcademic SwissAcademic.Citavi SwissAcademic.Controls; do
  cp "$here/bin-stub/Citavi.dll" "$out/$name.dll"
done
echo "Stubs written to $out"
