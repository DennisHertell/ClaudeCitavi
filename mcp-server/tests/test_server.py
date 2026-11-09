from __future__ import annotations

import json

import pytest
from mcp.server.mcpserver.exceptions import ToolError

from citavi_mcp import crossref, server
from citavi_mcp.bridge_client import BridgeClient, BridgeError, BridgeSettings


def structured(result):
    assert not result.is_error, result.content
    if result.structured_content is not None:
        return result.structured_content.get("result", result.structured_content)
    return json.loads(result.content[0].text)


async def test_all_tools_registered():
    names = {t.name for t in await server.mcp.list_tools()}
    expected = {
        "citavi_status",
        "search_references",
        "get_reference",
        "create_reference",
        "update_reference",
        "import_reference_by_doi",
        "list_categories",
        "create_category",
        "assign_categories",
        "list_keywords",
        "assign_keywords",
        "list_knowledge_items",
        "create_knowledge_item",
        "update_knowledge_item",
        "attach_file",
        "get_citation_style",
        "list_citation_styles",
        "set_citation_style",
        "describe_citavi_type",
        "list_open_projects",
        "select_project",
        "open_project",
        "list_attachments",
        "download_attachments",
        "find_citavi_members",
        "inspect_citavi_object",
        "highlight_in_pdf",
        "find_text_in_pdf",
        "list_reference_types",
    }
    assert expected <= names


async def test_read_tools_are_annotated_read_only():
    tools = {t.name: t for t in await server.mcp.list_tools()}
    assert tools["search_references"].annotations.read_only_hint is True
    assert tools["create_reference"].annotations.read_only_hint is False


async def test_status(fake_bridge):
    result = structured(await server.mcp.call_tool("citavi_status", {}))
    assert result["project"]["name"] == "Diss"


async def test_create_reference_maps_title_and_year(fake_bridge):
    result = structured(
        await server.mcp.call_tool(
            "create_reference",
            {
                "title": "Variantenmanagement",
                "reference_type": "Book",
                "year": "2020",
                "authors": ["Schuh, Günther"],
                "categories": ["Kapitel 2 > Grundlagen"],
            },
        )
    )
    method, params = fake_bridge.calls[-1]
    assert method == "references.create"
    assert params["fields"] == {"Title": "Variantenmanagement", "Year": "2020"}
    assert params["type"] == "Book"
    assert params["categories"] == ["Kapitel 2 > Grundlagen"]
    assert "periodical" not in params  # None values are not sent
    assert result["reference"]["id"]


async def test_knowledge_item_and_category_flow(fake_bridge):
    ref = structured(await server.mcp.call_tool("create_reference", {"title": "X"}))["reference"]
    ki = structured(
        await server.mcp.call_tool(
            "create_knowledge_item",
            {"reference_id": ref["id"], "text": "Zitat", "knowledge_type": "indirect", "page_range": "12"},
        )
    )["knowledgeItem"]
    assert fake_bridge.calls[-1][1]["type"] == "indirect"
    assign = structured(
        await server.mcp.call_tool(
            "assign_categories",
            {"categories": ["A > B"], "knowledge_item_ids": [ki["id"]]},
        )
    )
    assert assign["targets"] == 1


async def test_bridge_errors_become_tool_errors(fake_bridge):
    with pytest.raises(ToolError, match="not_found"):
        await server.mcp.call_tool(
            "create_knowledge_item",
            {"reference_id": "00000000-0000-0000-0000-000000000000", "text": "x"},
        )


def test_missing_token_gives_helpful_message():
    client = BridgeClient(BridgeSettings(url="http://127.0.0.1:1", token=None))
    with pytest.raises(BridgeError) as exc:
        client.call("system.ping")
    assert exc.value.code == "not_configured"


def test_citavi_not_running_message():
    client = BridgeClient(BridgeSettings(url="http://127.0.0.1:1", token="t"), timeout=2)
    with pytest.raises(BridgeError) as exc:
        client.call("system.ping")
    assert exc.value.code == "citavi_not_running"


def test_settings_from_config_file(tmp_path, monkeypatch):
    cfg = tmp_path / "bridge.json"
    cfg.write_text('{"port": 9999, "token": "abc"}', encoding="utf-8")
    monkeypatch.setenv("CITAVI_BRIDGE_CONFIG", str(cfg))
    monkeypatch.delenv("CITAVI_BRIDGE_URL", raising=False)
    monkeypatch.delenv("CITAVI_BRIDGE_TOKEN", raising=False)
    s = BridgeSettings.load()
    assert s.url == "http://127.0.0.1:9999" and s.token == "abc"


# --- Crossref ---------------------------------------------------------------------------------

WORK = {
    "DOI": "10.1000/xyz123",
    "type": "journal-article",
    "title": ["Deep learning"],
    "author": [{"given": "Yann", "family": "LeCun"}, {"given": "Yoshua", "family": "Bengio"}],
    "container-title": ["Nature"],
    "volume": "521",
    "issue": "7553",
    "page": "436-444",
    "published-print": {"date-parts": [[2015, 5, 28]]},
    "abstract": "<jats:p>Deep learning allows   models</jats:p>",
    "URL": "https://doi.org/10.1000/xyz123",
}


def test_normalize_doi():
    assert crossref.normalize_doi("https://doi.org/10.1/ABC") == "10.1/ABC"
    assert crossref.normalize_doi("doi: 10.1/abc ") == "10.1/abc"


def test_crossref_mapping():
    p = crossref.to_reference_params(WORK, available_types={"JournalArticle", "Book"})
    assert p["type"] == "JournalArticle"
    assert p["periodical"] == "Nature"
    assert p["fields"]["Year"] == "2015"
    assert p["fields"]["PageRange"] == "436-444"
    assert p["authors"][0] == {"lastName": "LeCun", "firstName": "Yann"}
    assert p["abstract"] == "Deep learning allows models"


def test_type_fallback_when_not_available():
    assert crossref.choose_type("proceedings-article", {"Contribution", "Book"}) == "Contribution"
    assert crossref.choose_type("something-new", {"JournalArticle"}) == "JournalArticle"


async def test_import_by_doi_skips_duplicates(fake_bridge, monkeypatch):
    monkeypatch.setattr(crossref, "fetch", lambda doi: WORK)
    first = structured(await server.mcp.call_tool("import_reference_by_doi", {"doi": "10.1000/xyz123"}))
    assert first["status"] == "created"
    second = structured(await server.mcp.call_tool("import_reference_by_doi", {"doi": "doi:10.1000/XYZ123"}))
    assert second["status"] == "exists"


# --- Projects & cloud attachments -------------------------------------------------------------


async def test_select_project_sends_project_with_every_call(fake_bridge):
    result = structured(await server.mcp.call_tool("select_project", {"name": "diss cloud"}))
    assert result["selectedProject"] == "Diss Cloud"
    await server.mcp.call_tool("citavi_status", {})
    assert fake_bridge.last_project == "Diss Cloud"

    structured(await server.mcp.call_tool("select_project", {}))
    await server.mcp.call_tool("citavi_status", {})
    assert fake_bridge.last_project is None


async def test_select_unknown_project_fails(fake_bridge):
    with pytest.raises(ToolError, match="weder geöffnet"):
        await server.mcp.call_tool("select_project", {"name": "Gibt es nicht"})


async def test_list_missing_attachments(fake_bridge):
    result = structured(await server.mcp.call_tool("list_attachments", {"only_missing": True}))
    assert [a["locationId"] for a in result["items"]] == ["L2"]
    assert fake_bridge.calls[-1][1]["onlyMissing"] is True


async def test_download_attachments_passes_options(fake_bridge):
    result = structured(
        await server.mcp.call_tool(
            "download_attachments",
            {"category": "Kapitel 2", "target_folder": "D:/Diss/PDFs", "naming": "original"},
        )
    )
    method, params = fake_bridge.calls[-1]
    assert method == "attachments.download"
    assert params["categoryPath"] == ["Kapitel 2"]
    assert params["targetFolder"] == "D:/Diss/PDFs"
    assert params["naming"] == "original"
    assert params["onlyPdf"] is True
    assert {i["status"] for i in result["items"]} == {"copied", "downloaded"}


# --- PDF highlights ---------------------------------------------------------------------------


async def test_create_knowledge_item_with_highlight(fake_bridge):
    ref = structured(await server.mcp.call_tool("create_reference", {"title": "X"}))["reference"]
    result = structured(
        await server.mcp.call_tool(
            "create_knowledge_item",
            {"reference_id": ref["id"], "text": "ClaudeCitavi Test", "highlight_in_pdf": True},
        )
    )
    assert result["highlight"]["highlight"]["annotationId"] == "A1"
    methods = [m for m, _ in fake_bridge.calls]
    assert methods[-4:] == ["pdf.findText", "pdf.text", "knowledgeItems.create", "pdf.highlight"]


async def test_highlight_text_not_found_creates_nothing(fake_bridge):
    ref = structured(await server.mcp.call_tool("create_reference", {"title": "X"}))["reference"]
    with pytest.raises(ToolError, match="nicht gefunden"):
        await server.mcp.call_tool(
            "create_knowledge_item",
            {"reference_id": ref["id"], "text": "steht nicht im PDF", "highlight_in_pdf": True},
        )
    assert "knowledgeItems.create" not in [m for m, _ in fake_bridge.calls]


async def test_open_project_selects_it(fake_bridge):
    result = structured(await server.mcp.call_tool("open_project", {"name": "Dissertation"}))
    assert result["opened"] is True
    assert result["selectedProject"] == "Dissertation"
    assert fake_bridge.opened == ["Dissertation"]


async def test_select_known_but_closed_project_opens_it(fake_bridge):
    result = structured(await server.mcp.call_tool("select_project", {"name": "dissertation"}))
    assert result["selectedProject"] == "Dissertation"
    assert fake_bridge.opened == ["Dissertation"]
