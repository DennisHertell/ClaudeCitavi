"""HTTP client for the ClaudeCitavi bridge add-on running inside Citavi."""

from __future__ import annotations

import json
import os
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import httpx

DEFAULT_PORT = 8765
STARTUP_WAIT_SECONDS = 120

CITAVI_CANDIDATES = [
    r"C:\Program Files (x86)\Citavi 6\bin\Citavi.exe",
    r"C:\Program Files\Citavi 6\bin\Citavi.exe",
    r"C:\Program Files\Citavi 7\bin\Citavi.exe",
]


def _citavi_running() -> bool:
    if os.name != "nt":
        return False
    out = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq Citavi.exe", "/NH"], capture_output=True, text=True, check=False
    ).stdout
    return "citavi.exe" in out.lower()


def start_citavi() -> bool:
    """Starts Citavi if it is installed and not running (set CITAVI_AUTOSTART=0 to disable).

    Returns True when Citavi was started or is already starting, so the caller should wait and retry.
    """
    if os.environ.get("CITAVI_AUTOSTART", "1") == "0" or os.name != "nt":
        return False
    if _citavi_running():
        return True  # running but bridge not up yet (start-up in progress)
    exe = os.environ.get("CITAVI_EXE") or next((c for c in CITAVI_CANDIDATES if Path(c).exists()), None)
    if not exe:
        return False
    subprocess.Popen([exe], close_fds=True, creationflags=getattr(subprocess, "DETACHED_PROCESS", 0))
    return True


class BridgeError(Exception):
    """Raised when the bridge is unreachable or returns an error."""

    def __init__(self, code: str, message: str):
        super().__init__(f"[{code}] {message}")
        self.code = code
        self.message = message


@dataclass(frozen=True)
class BridgeSettings:
    url: str
    token: str | None

    @staticmethod
    def config_path() -> Path:
        if override := os.environ.get("CITAVI_BRIDGE_CONFIG"):
            return Path(override)
        appdata = os.environ.get("APPDATA") or str(Path.home() / "AppData" / "Roaming")
        return Path(appdata) / "ClaudeCitavi" / "bridge.json"

    @classmethod
    def load(cls) -> BridgeSettings:
        """Environment variables win over bridge.json (written by the add-on on first start)."""
        port = DEFAULT_PORT
        token = None
        path = cls.config_path()
        if path.exists():
            data = json.loads(path.read_text(encoding="utf-8-sig"))
            port = int(data.get("port") or DEFAULT_PORT)
            token = data.get("token")

        url = os.environ.get("CITAVI_BRIDGE_URL", f"http://127.0.0.1:{port}")
        token = os.environ.get("CITAVI_BRIDGE_TOKEN", token)
        return cls(url=url.rstrip("/"), token=token)


class BridgeClient:
    def __init__(
        self,
        settings: BridgeSettings | None = None,
        transport: httpx.BaseTransport | None = None,
        timeout: float = 30.0,
    ):
        self._settings = settings
        self._transport = transport
        self._timeout = timeout
        # Name of the Citavi project to work on; None = the window that was active last.
        self.project: str | None = None

    @property
    def settings(self) -> BridgeSettings:
        # Loaded lazily so the MCP server starts even before Citavi has run once.
        if self._settings is None:
            self._settings = BridgeSettings.load()
        return self._settings

    def call(self, method: str, timeout: float | None = None, _project: str | None = None, **params: Any) -> Any:
        """_project overrides the selected project for this one call (used when copying between projects)."""
        try:
            return self._call_once(method, timeout, _project, **params)
        except BridgeError as exc:
            if exc.code not in ("citavi_not_running", "citavi_starting") or not start_citavi():
                raise
            # Citavi was not running: started it, now wait until the add-on answers.
            deadline = time.monotonic() + STARTUP_WAIT_SECONDS
            while True:
                time.sleep(3)
                try:
                    return self._call_once(method, timeout, _project, **params)
                except BridgeError as retry:
                    if retry.code not in ("citavi_not_running", "citavi_starting") or time.monotonic() > deadline:
                        raise

    def _call_once(self, method: str, timeout: float | None = None, _project: str | None = None, **params: Any) -> Any:
        settings = self.settings
        if not settings.token:
            raise BridgeError(
                "not_configured",
                f"Kein Token gefunden ({BridgeSettings.config_path()}). Ist das ClaudeCitavi-Add-on installiert "
                "und Citavi mindestens einmal gestartet worden?",
            )

        payload: dict[str, Any] = {"method": method, "params": {k: v for k, v in params.items() if v is not None}}
        if _project or self.project:
            payload["project"] = _project or self.project
        headers = {"Authorization": f"Bearer {settings.token}"}
        try:
            with httpx.Client(transport=self._transport, timeout=timeout or self._timeout) as http:
                response = http.post(f"{settings.url}/rpc", json=payload, headers=headers)
        # On Windows a closed local port often surfaces as ConnectTimeout instead of a refusal.
        except (httpx.ConnectError, httpx.ConnectTimeout) as exc:
            raise BridgeError(
                "citavi_not_running",
                "Citavi ist nicht erreichbar. Bitte Citavi starten und ein Projekt öffnen "
                f"(Bridge erwartet unter {settings.url}).",
            ) from exc
        except httpx.TimeoutException as exc:
            raise BridgeError("timeout", "Citavi hat nicht rechtzeitig geantwortet (evtl. offener Dialog?).") from exc

        try:
            body = response.json()
        except ValueError as exc:
            raise BridgeError("bad_response", f"HTTP {response.status_code}: {response.text[:200]}") from exc

        if not body.get("ok"):
            err = body.get("error") or {}
            raise BridgeError(err.get("code", "error"), err.get("message", "Unbekannter Fehler"))
        return body.get("result")
