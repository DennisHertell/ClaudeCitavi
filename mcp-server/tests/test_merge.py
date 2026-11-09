from citavi_mcp import merge, server

SOURCE = {
    "project": "Hiwi",
    "categories": [{"name": "Grundlagen", "children": []}],
    "references": [
        {
            "id": "s1",
            "shortTitle": "Doe 2020",
            "title": "A long title about variant costs",
            "year": "2020",
            "doi": "https://doi.org/10.1000/ABC.1",
            "categories": ["Grundlagen"],
            "attachments": [{"fileName": "doe.pdf"}],
            "knowledgeItems": [
                {
                    "id": "k1",
                    "text": "Varianten erhöhen Kosten.",
                    "coreStatement": "Kosten",
                    "pageRange": "3",
                    "categories": ["Grundlagen"],
                },
                {
                    "id": "k2",
                    "text": "Neue Aussage des Hiwis.",
                    "coreStatement": "Neu",
                    "pageRange": "5",
                    "categories": ["Grundlagen", "Sonstiges"],
                },
            ],
        },
        {
            "id": "s2",
            "shortTitle": "Roe 2019",
            "title": "Completely different book title",
            "year": "2019",
            "isbn": "978-3-540-23779-0",
            "categories": [],
            "attachments": [],
            "knowledgeItems": [{"id": "k3", "text": "x", "categories": ["Grundlagen"]}],
        },
        {
            "id": "s3",
            "shortTitle": "New 2024",
            "title": "Something nobody has yet",
            "year": "2024",
            "categories": ["Grundlagen"],
            "attachments": [],
            "knowledgeItems": [],
        },
    ],
}

TARGET = {
    "project": "Diss",
    "categories": [{"name": "Kostenwirkung", "children": [{"name": "Verlustarten", "children": []}]}],
    "references": [
        {
            "id": "t1",
            "title": "Other",
            "doi": "10.1000/abc.1",
            "attachments": [],
            "knowledgeItems": [{"id": "tk1", "text": "Varianten erhoehen Kosten", "coreStatement": "x"}],
        },
        {
            "id": "t2",
            "title": "Completely different book title",
            "year": "2019",
            "isbn": "9783540237790",
            "attachments": [{"fileName": "roe.pdf"}],
            "knowledgeItems": [],
        },
    ],
}


def test_identifiers_are_normalized():
    assert merge.norm_doi("https://doi.org/10.1000/ABC.1.") == "10.1000/abc.1"
    assert merge.norm_isbn("0-306-40615-2") == "9780306406157"
    assert merge.norm_isbn("978-3-540-23779-0") == "9783540237790"


def test_compare_finds_duplicates_and_new_knowledge_items():
    plan = merge.compare(SOURCE, TARGET)
    assert plan["summary"]["newReferences"] == 1
    assert [r["sourceId"] for r in plan["newReferences"]] == ["s3"]
    by_source = {m["sourceId"]: m for m in plan["matchedReferences"]}
    assert by_source["s1"]["matchedBy"] == "doi"
    assert [k["id"] for k in by_source["s1"]["newKnowledgeItems"]] == ["k2"]  # k1 exists (umlaut-normalized text)
    assert by_source["s2"]["matchedBy"] == "isbn"
    assert by_source["s2"]["targetHasAttachment"] is True
    assert "Kostenwirkung > Verlustarten" in plan["targetCategoryPaths"]
    assert plan["sourceCategoriesInUse"]["Grundlagen"] == 3


def test_map_categories_reports_unmapped_and_allows_drop():
    mapped, unmapped = merge.map_categories(
        ["Grundlagen", "Sonstiges", "Weg"],
        {"Grundlagen": ["Kostenwirkung", "Kostenwirkung > Verlustarten"], "Weg": None},
    )
    assert mapped == ["Kostenwirkung", "Kostenwirkung > Verlustarten"]
    assert unmapped == ["Sonstiges"]


def test_merge_project_dry_run_and_execution(monkeypatch):
    calls = []

    def fake_call(method, timeout=None, _project=None, **params):
        calls.append((method, _project, params))
        if method == "projects.list":
            return {"open": [{"name": "Hiwi"}, {"name": "Diss"}], "known": []}
        if method == "project.export":
            return SOURCE if _project == "Hiwi" else TARGET
        if method == "references.copyToProject":
            return {"copied": [{"targetId": "new-s3", "knowledgeItems": []}]}
        if method == "knowledgeItems.copyTo":
            return {
                "knowledgeItems": [{"sourceId": k, "targetId": "new-" + k} for k in params["knowledgeItemIds"]],
                "report": [],
            }
        return {}

    monkeypatch.setattr(server, "_call", fake_call)
    mapping = {"Grundlagen": "Kostenwirkung > Verlustarten"}

    dry = server.merge_project("Hiwi", "Diss", mapping, dry_run=True)
    assert not any(c[0] in ("references.copyToProject", "knowledgeItems.copyTo", "categories.set") for c in calls)
    assert dry["unmappedCategories"] == ["Sonstiges"]
    assert len(dry["newReferences"]) == 1 and len(dry["knowledgeItemsAdded"]) == 2

    calls.clear()
    server.merge_project("Hiwi", "Diss", mapping, dry_run=False)
    copy_ki = [c for c in calls if c[0] == "knowledgeItems.copyTo"]
    assert {tuple(c[2]["knowledgeItemIds"]) for c in copy_ki} == {("k2",), ("k3",)}
    # target t1 had no PDF -> attachment copied along; t2 had one -> not copied
    assert {c[2]["targetReferenceId"]: c[2]["copyAttachments"] for c in copy_ki} == {"t1": True, "t2": False}
    sets = [c for c in calls if c[0] == "categories.set"]
    assert all(c[1] == "Diss" for c in sets)
    assert {"new-s3"} == {r for c in sets for r in c[2].get("referenceIds", [])}
    assert all(c[2]["categories"] == ["Kostenwirkung > Verlustarten"] for c in sets)
