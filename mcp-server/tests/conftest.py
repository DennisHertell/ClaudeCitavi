"""Test fixtures: an in-memory fake of the Citavi bridge, so tests run without Windows/Citavi."""

from __future__ import annotations

import json
import uuid
from typing import Any

import httpx
import pytest

from citavi_mcp import server
from citavi_mcp.bridge_client import BridgeClient, BridgeSettings

TOKEN = "test-token"


class FakeBridge:
    """Implements a subset of the bridge protocol (docs/BRIDGE-PROTOKOLL.md) in memory."""

    def __init__(self) -> None:
        self.opened: list[str] = []
        self.references: dict[str, dict[str, Any]] = {}
        self.knowledge_items: dict[str, dict[str, Any]] = {}
        self.calls: list[tuple[str, dict[str, Any]]] = []
        self.projects = [{"name": "Diss", "isCloud": False}, {"name": "Diss Cloud", "isCloud": True}]
        self.last_project: str | None = None
        # One local and one cloud-only PDF, as in a shared cloud project.
        self.attachments = [
            {
                "locationId": "L1",
                "referenceId": "R1",
                "fileName": "a.pdf",
                "storage": "local",
                "availableLocally": True,
            },
            {
                "locationId": "L2",
                "referenceId": "R2",
                "fileName": "b.pdf",
                "storage": "cloud",
                "availableLocally": False,
            },
        ]

    def handle(self, request: httpx.Request) -> httpx.Response:
        if request.headers.get("Authorization") != f"Bearer {TOKEN}":
            return self._error("unauthorized", "Missing or invalid token.", 401)
        body = json.loads(request.content)
        method, params = body["method"], body.get("params", {})
        self.last_project = body.get("project")
        self.calls.append((method, params))
        handler = getattr(self, "m_" + method.replace(".", "_"), None)
        if handler is None:
            return self._error("unknown_method", method, 404)
        try:
            return httpx.Response(200, json={"ok": True, "result": handler(params)})
        except KeyError as exc:
            return self._error("not_found", str(exc), 404)

    @staticmethod
    def _error(code: str, message: str, status: int) -> httpx.Response:
        return httpx.Response(status, json={"ok": False, "error": {"code": code, "message": message}})

    # --- methods ---
    def m_system_ping(self, p):
        return {"bridgeVersion": "0.1.0", "project": {"name": "Diss", "referenceCount": len(self.references)}}

    def m_projects_list(self, p):
        known = self.projects + [{"name": "Dissertation", "type": "DesktopCloud"}]
        return {"open": self.projects, "known": known}

    def m_projects_open(self, p):
        self.opened.append(p["name"])
        return {"name": p["name"], "opened": True, "alreadyOpen": False}

    def m_attachments_list(self, p):
        items = [a for a in self.attachments if not p.get("onlyMissing") or not a["availableLocally"]]
        return {"projectIsCloud": True, "total": len(items), "items": items}

    def m_attachments_download(self, p):
        folder = p.get("targetFolder", "C:/Downloads")
        items = [
            {"status": "copied" if a["availableLocally"] else "downloaded", "path": f"{folder}/{a['fileName']}"}
            for a in self.attachments
        ]
        return {"targetFolder": folder, "total": len(items), "ok": len(items), "items": items}

    def m_references_types(self, p):
        return [{"key": k, "name": k} for k in ("JournalArticle", "Book", "Contribution", "UnknownType")]

    def m_references_create(self, p):
        rid = str(uuid.uuid4())
        ref = {"id": rid, "type": p.get("type"), "doi": p.get("fields", {}).get("Doi"), **p}
        self.references[rid] = ref
        return {"reference": ref, "warnings": []}

    def m_references_search(self, p):
        q = (p.get("query") or "").lower()
        items = [r for r in self.references.values() if q in json.dumps(r).lower()]
        return {"total": len(items), "items": items[: p.get("limit", 25)]}

    def m_references_get(self, p):
        return self.references[p["id"]]

    def m_knowledgeItems_create(self, p):
        self.references[p["referenceId"]]  # raises KeyError if unknown
        kid = str(uuid.uuid4())
        self.knowledge_items[kid] = {"id": kid, **p}
        return {"knowledgeItem": self.knowledge_items[kid], "warnings": []}

    def m_pdf_findText(self, p):
        found = "ClaudeCitavi" in p["text"]
        hits = [{"page": 1, "text": p["text"], "context": p["text"], "rects": 1}] if found else []
        return {"locationId": "L1", "total": len(hits), "hits": hits}

    PDF_PAGES = [
        {
            "page": 1,
            "label": None,
            "text": "A Title\nAbstract\nThis paper shows ClaudeCitavi Abstract claim.\n1. Introduction\n"
            "ClaudeCitavi Test is our own finding. "
            "Costs rise with variety (Fisher and Ittner, 1999) ClaudeCitavi Cited.\n",
        },
    ]

    def m_pdf_text(self, p):
        return {"fileName": "x.pdf", "pageCount": 1, "truncated": False, "pages": self.PDF_PAGES}

    def m_pdf_highlight(self, p):
        self.knowledge_items[p["knowledgeItemId"]]  # raises KeyError if unknown
        return {"highlight": {"annotationId": "A1", "page": 1, "rects": 1}, "matchedText": p.get("text")}

    def m_categories_assign(self, p):
        return {"targets": len(p.get("referenceIds", [])) + len(p.get("knowledgeItemIds", [])), "changes": 1}


@pytest.fixture
def fake_bridge(monkeypatch) -> FakeBridge:
    fake = FakeBridge()
    client = BridgeClient(
        BridgeSettings(url="http://127.0.0.1:8765", token=TOKEN),
        transport=httpx.MockTransport(fake.handle),
    )
    monkeypatch.setattr(server, "bridge", client)
    return fake


@pytest.fixture(autouse=True)
def _no_citavi_autostart(monkeypatch):
    """Tests must never launch the real Citavi."""
    monkeypatch.setenv("CITAVI_AUTOSTART", "0")
