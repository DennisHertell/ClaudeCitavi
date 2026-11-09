import pytest
from mcp.server.mcpserver.exceptions import ToolError

from citavi_mcp import bibio, citability, server

PAGES = [
    {"page": 1, "label": None, "text": "Journal cover page\nTo cite this article: Doe (2020)\n"},
    {
        "page": 2,
        "label": None,
        "text": "Manufacturing cost deployment\n"
        "Obviously, one of the major problems in manufacturing is to reduce cost.\n"
        "1. Introduction\nEach company must continue to make a reasonable profit. Losses are classified into causal "
        "losses and resultant losses in this paper. Variety raises cost [12]. Nakajima (1988) defined OEE.\n",
    },
    {
        "page": 3,
        "label": None,
        "text": "4078\nH. Yamashina\nWe derive a cost-reduction programme from the loss "
        "matrix.\nReferences\nNakajima, S., 1988. Introduction to TPM.\n",
    },
]


def test_abstract_without_heading_is_rejected():
    v = citability.check(PAGES, "one of the major problems in manufacturing is to reduce cost")
    assert v["citable"] is False and "Einleitung" in v["violations"][0]


def test_own_finding_in_main_text_is_citable_with_printed_page():
    v = citability.check(PAGES, "We derive a cost-reduction programme from the loss matrix")
    assert v["citable"] is True and v["pdfPage"] == 3 and v["printedPage"] == "4078"


def test_numeric_and_author_year_citations_are_rejected():
    v1 = citability.check(PAGES, "Variety raises cost")
    assert v1["citable"] is False and any("numeric" in c for c in v1["citationsInSentence"])
    v2 = citability.check(PAGES, "Nakajima (1988) defined OEE")
    assert v2["citable"] is False and any("author (year)" in c for c in v2["citationsInSentence"])


def test_reference_list_is_rejected():
    v = citability.check(PAGES, "Introduction to TPM")
    assert v["citable"] is False and any("Literaturverzeichnis" in x for x in v["violations"])


async def test_create_knowledge_item_refuses_cited_sentence(fake_bridge):
    from test_server import structured

    ref = structured(await server.mcp.call_tool("create_reference", {"title": "X"}))["reference"]
    with pytest.raises(ToolError, match="Sekundärzitat"):
        await server.mcp.call_tool(
            "create_knowledge_item",
            {
                "reference_id": ref["id"],
                "text": "ClaudeCitavi Cited",
                "knowledge_type": "indirect",
                "highlight_in_pdf": True,
            },
        )
    with pytest.raises(ToolError, match="Abstract"):
        await server.mcp.call_tool(
            "create_knowledge_item",
            {"reference_id": ref["id"], "text": "ClaudeCitavi Abstract claim", "highlight_in_pdf": True},
        )
    assert not any(m == "knowledgeItems.create" for m, _ in fake_bridge.calls)


RIS = """TY  - JOUR
AU  - Fisher, Marshall L.
AU  - Ittner, Christopher D.
TI  - The impact of product variety on automobile assembly operations
JO  - Management Science
PY  - 1999
VL  - 45
IS  - 6
SP  - 771
EP  - 786
DO  - 10.1287/mnsc.45.6.771
KW  - Produktvielfalt
ER  -
"""

BIB = """@book{Schuh2005,
  author = {Schuh, G{\\"u}nther and Schwenk, Uwe},
  title = {Produktkomplexit{\\"a}t managen},
  publisher = {Hanser},
  address = {M{\\"u}nchen},
  year = {2005},
  isbn = {3-446-40111-5}
}
@article{Doe2020, author = "Jane Doe", title = "Costs", journal = {IJPR}, year = 2020, pages = {1--10}}
"""


def test_parse_ris():
    (rec,) = bibio.parse_ris(RIS, {"JournalArticle", "Book"})
    assert rec["type"] == "JournalArticle"
    assert rec["periodical"] == "Management Science"
    assert rec["fields"]["PageRange"] == "771-786" and rec["fields"]["Doi"] == "10.1287/mnsc.45.6.771"
    assert rec["authors"][1] == {"lastName": "Ittner", "firstName": "Christopher D."}
    assert rec["keywords"] == ["Produktvielfalt"]


def test_parse_bibtex():
    book, article = bibio.parse_bibtex(BIB, {"JournalArticle", "Book"})
    assert book["type"] == "Book" and book["publishers"] == ["Hanser"] and book["fields"]["BibTeXKey"] == "Schuh2005"
    assert [a["lastName"] for a in book["authors"]] == ["Schuh", "Schwenk"]
    assert article["authors"] == [{"lastName": "Doe", "firstName": "Jane"}]
    assert article["fields"]["PageRange"] == "1-10" and article["periodical"] == "IJPR"


def test_to_bibtex_roundtrip():
    out = bibio.to_bibtex(
        [
            {
                "typeKey": "JournalArticle",
                "title": "Costs",
                "year": "2020",
                "periodical": "IJPR",
                "authors": [{"lastName": "Doe", "firstName": "Jane"}],
                "fields": {"PageRange": "1-10"},
            }
        ]
    )
    assert out.startswith("@article{Doe2020,") and "pages = {1--10}" in out
    (back,) = bibio.parse_bibtex(out, {"JournalArticle"})
    assert back["fields"]["Title"] == "Costs" and back["fields"]["PageRange"] == "1-10"


def test_rules_are_loaded_into_instructions():
    assert "Was zitierfähig ist" in server.INSTRUCTIONS and "Primärquelle" in server.INSTRUCTIONS
