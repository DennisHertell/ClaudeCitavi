"""ClaudeCitavi: Einrichtung für den angemeldeten Nutzer (Python, keine PowerShell-Skripte).

Modus "makro" (Weg C, Paket ClaudeCitavi-<Version>-Makro.zip):
  - legt ClaudeCitavi.Bridge.dll und das Citavi-Makro StartClaudeBridge.cs in %APPDATA%\\ClaudeCitavi ab
  - installiert den MCP-Server in %LOCALAPPDATA%\\ClaudeCitavi\\mcp-server (Pakete aus wheelhouse, per SHA256 geprüft)
  - trägt den MCP-Server in Claude Desktop und, falls vorhanden, Claude Code ein
Modus "nutzer" (Weg B, nach der Installation durch die IT):
  - trägt den von der IT installierten MCP-Server in Claude Desktop und Claude Code ein

Aufruf: py -3.12 einrichten.py --modus makro | --modus nutzer   [--ohne-claude]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
APPDATA = Path(os.environ["APPDATA"])
LOCALAPPDATA = Path(os.environ["LOCALAPPDATA"])
PROGRAM_FILES_MCP = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "ClaudeCitavi" / "mcp-server"


def fail(message: str) -> None:
    print(f"\nFEHLER: {message}")
    sys.exit(1)


def check_dll() -> Path:
    dll = HERE / "AddOn" / "ClaudeCitavi.Bridge.dll"
    expected = (HERE / "AddOn" / "ClaudeCitavi.Bridge.dll.sha256").read_text().split()[0].upper()
    actual = hashlib.sha256(dll.read_bytes()).hexdigest().upper()
    if actual != expected:
        fail("Die Prüfsumme der DLL stimmt nicht. Bitte das Paket nicht verwenden und neu herunterladen.")
    return dll


def install_bridge_files(dll: Path) -> Path:
    target = APPDATA / "ClaudeCitavi"
    target.mkdir(parents=True, exist_ok=True)
    shutil.copy2(dll, target / dll.name)
    shutil.copy2(HERE / "StartClaudeBridge.cs", target / "StartClaudeBridge.cs")
    print(f"Bridge und Makro abgelegt in {target}")
    return target


def install_mcp_server() -> Path:
    if sys.version_info[:2] != (3, 12) or sys.maxsize <= 2**32:
        fail("Bitte Python 3.12 in der 64-Bit-Version verwenden (python.org).")
    venv = LOCALAPPDATA / "ClaudeCitavi" / "mcp-server" / ".venv"
    if venv.exists():
        shutil.rmtree(venv)  # update: always a clean environment
    venv.parent.mkdir(parents=True, exist_ok=True)
    print("Installiere den MCP-Server (einige Sekunden) ...")
    subprocess.run([sys.executable, "-m", "venv", str(venv)], check=True)
    python = venv / "Scripts" / "python.exe"
    wheelhouse = HERE / "wheelhouse"
    result = subprocess.run(
        [str(python), "-m", "pip", "install", "--no-index", "--find-links", str(wheelhouse),
         "--require-hashes", "-r", str(wheelhouse / "requirements.txt"), "--quiet"],
        check=False,
    )
    if result.returncode != 0:
        fail("Die Installation des MCP-Servers ist fehlgeschlagen.")
    exe = venv / "Scripts" / "citavi-mcp.exe"
    print(f"MCP-Server installiert: {exe}")
    return exe


def register_claude(exe: Path) -> None:
    config = APPDATA / "Claude" / "claude_desktop_config.json"
    config.parent.mkdir(parents=True, exist_ok=True)
    data = {}
    if config.exists():
        shutil.copy2(config, config.with_suffix(".json.bak"))
        data = json.loads(config.read_text(encoding="utf-8-sig") or "{}")
    data.setdefault("mcpServers", {})["citavi"] = {"command": str(exe), "args": []}
    config.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Claude Desktop: eingetragen in {config} (Sicherung .bak). Claude Desktop bitte neu starten.")

    claude = shutil.which("claude")
    if claude:
        subprocess.run([claude, "mcp", "remove", "citavi", "--scope", "user"], capture_output=True, check=False)
        subprocess.run([claude, "mcp", "add", "--scope", "user", "citavi", "--", str(exe)], check=False)
        print("Claude Code: MCP-Server 'citavi' eingetragen.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--modus", choices=["makro", "nutzer"], required=True)
    parser.add_argument("--ohne-claude", action="store_true", help="Claude-Konfiguration nicht ändern (Test)")
    args = parser.parse_args()

    if args.modus == "makro":
        bridge_dir = install_bridge_files(check_dll())
        exe = install_mcp_server()
        if not args.ohne_claude:
            register_claude(exe)
        print("\nFertig. Bei jeder Citavi-Sitzung einmal das Makro starten:")
        print(f"  Citavi > Extras > Makro-Editor (Alt+F11) > Öffnen: {bridge_dir}\\StartClaudeBridge.cs > Ausführen (F5)")
    else:
        exe = PROGRAM_FILES_MCP / ".venv" / "Scripts" / "citavi-mcp.exe"
        if not exe.exists():
            fail(f"MCP-Server nicht gefunden ({exe}). Die Installation durch die IT fehlt noch.")
        if not args.ohne_claude:
            register_claude(exe)
        print("\nFertig. Citavi starten und in Claude fragen: 'Prüfe die Verbindung zu Citavi.'")


if __name__ == "__main__":
    main()
