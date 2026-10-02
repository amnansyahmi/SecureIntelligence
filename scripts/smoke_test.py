"""End-to-end API verification using Python standard library."""
import json
from pathlib import Path
import tempfile
import urllib.request
from test_support import Server, expect, DLL, REQUEST, FEEDBACK, LEARNING_KEY

if not DLL.exists():
    raise SystemExit("Build first: dotnet build SecureIntelligence.sln -c Release")

with tempfile.TemporaryDirectory(prefix="secure-intelligence-smoke-") as directory:
    with Server(directory) as server:
        expect(server, 401, "POST", "/api/v1/diagnose", raw=b"{broken", key=None)
        expect(server, 401, "POST", "/api/v1/diagnose", REQUEST, key="wrong")
        expect(server, 400, "POST", "/api/v1/diagnose", raw=b"{broken")
        expect(server, 400, "POST", "/api/v1/diagnose", {})
        expect(server, 400, "POST", "/api/v1/feedback", {"request": None}, key=LEARNING_KEY)
        expect(server, 413, "POST", "/api/v1/diagnose", raw=b" " * 65537)
        body, headers = expect(server, 200, "POST", "/api/v1/diagnose", REQUEST)
        assert len(body["findings"]) == 2 and body["similarCases"] == []
        assert body["findings"][0]["severity"] == "High"
        assert headers["Cache-Control"] == "no-store"
        expect(server, 403, "POST", "/api/v1/feedback", FEEDBACK)
        expect(server, 403, "GET", "/api/v1/cases")
        expect(server, 202, "POST", "/api/v1/feedback", {**FEEDBACK, "approvedForLearning": False}, key=LEARNING_KEY)
        body, _ = expect(server, 200, "GET", "/api/v1/cases", key=LEARNING_KEY)
        assert body == []
        contaminated = {**REQUEST, "signals": {"patientName": "Someone"}}
        expect(server, 400, "POST", "/api/v1/feedback", {**FEEDBACK, "request": contaminated}, key=LEARNING_KEY)
        expect(server, 400, "POST", "/api/v1/feedback", {**FEEDBACK, "resolution": "password=secret"}, key=LEARNING_KEY)
        learned, headers = expect(server, 201, "POST", "/api/v1/feedback", FEEDBACK, key=LEARNING_KEY)
        case_id = learned["caseId"]
        expect(server, 200, "GET", headers["Location"], key=LEARNING_KEY)
        repeated, _ = expect(server, 201, "POST", "/api/v1/feedback", FEEDBACK, key=LEARNING_KEY)
        assert repeated["caseId"] == case_id
        body, _ = expect(server, 200, "POST", "/api/v1/diagnose", REQUEST)
        assert body["similarCases"][0]["similarity"] == 1
        opposite = {**REQUEST, "signals": {**REQUEST["signals"], "systemizationConfigured": "true"}}
        body, _ = expect(server, 200, "POST", "/api/v1/diagnose", opposite)
        assert body["similarCases"] == []
        body, _ = expect(server, 200, "POST", "/api/v1/diagnose", {**REQUEST, "issueType": "Performance"})
        assert body["similarCases"] == []
        stored = Path(directory) / "validated-cases.json"
        assert REQUEST["description"] not in stored.read_text()
        assert len(json.loads(stored.read_text())["cases"]) == 1
    with Server(directory) as server:
        with urllib.request.urlopen(server.url + "/") as response:
            assert response.status == 200
            assert "SecureIntelligence" in response.read().decode()
            assert "script-src 'self'" in response.headers["Content-Security-Policy"]
            assert response.headers["Cache-Control"] == "no-store"
        expect(server, 403, "GET", "/api/v1/proposals")
        expect(server, 400, "POST", "/api/v1/proposals", {"request": None})
        proposal, _ = expect(server, 202, "POST", "/api/v1/proposals", {
            "request": REQUEST, "confirmedCause": "Reviewed queue cause", "resolution": "Run validation"})
        pid = proposal["proposalId"]
        duplicate, _ = expect(server, 202, "POST", "/api/v1/proposals", {
            "request": REQUEST, "confirmedCause": "Reviewed queue cause", "resolution": "Run validation"})
        assert duplicate["proposalId"] == pid
        body, _ = expect(server, 200, "POST", "/api/v1/diagnose", REQUEST)
        assert not any(c["confirmedCause"] == "Reviewed queue cause" for c in body["similarCases"])
        expect(server, 403, "POST", f"/api/v1/proposals/{pid}/approve", {"approvedForLearning": True})
        expect(server, 400, "POST", f"/api/v1/proposals/{pid}/approve", {"approvedForLearning": False}, key=LEARNING_KEY)
        approved, _ = expect(server, 200, "POST", f"/api/v1/proposals/{pid}/approve", {"approvedForLearning": True}, key=LEARNING_KEY)
        active_id = approved["activeCaseId"]
        expect(server, 409, "POST", f"/api/v1/proposals/{pid}/reject", {"reason": "IncorrectCause"}, key=LEARNING_KEY)
        outcome = {"incidentId": "abcdef0123456789abcdef0123456789", "resolved": True, "verified": True}
        expect(server, 403, "POST", f"/api/v1/cases/{active_id}/outcomes", outcome)
        expect(server, 400, "POST", f"/api/v1/cases/{active_id}/outcomes", {**outcome, "verified": False}, key=LEARNING_KEY)
        expect(server, 200, "POST", f"/api/v1/cases/{active_id}/outcomes", outcome, key=LEARNING_KEY)
        expect(server, 200, "POST", f"/api/v1/cases/{active_id}/outcomes", outcome, key=LEARNING_KEY)
        body, _ = expect(server, 200, "POST", "/api/v1/diagnose", REQUEST)
        matched = next(c for c in body["similarCases"] if c["caseId"] == active_id)
        assert matched["verifiedSuccesses"] == 1 and len(matched["evidence"]) == 3
        guide = {"application": "LineDesigner", "issueType": "ReadyForQuote", "title": "BOM systemization guide",
                 "source": "Reviewed runbook", "content": "If systemization is incomplete or BOM items are missing, run validation.", "approvedForPublication": True}
        expect(server, 403, "POST", "/api/v1/knowledge/documents", guide)
        expect(server, 400, "POST", "/api/v1/knowledge/documents", {**guide, "approvedForPublication": False}, key=LEARNING_KEY)
        document, _ = expect(server, 201, "POST", "/api/v1/knowledge/documents", guide, key=LEARNING_KEY)
        query = {"application": "LineDesigner", "query": "BOM systemization", "issueType": "ReadyForQuote"}
        hits, _ = expect(server, 200, "POST", "/api/v1/knowledge/search", query)
        assert hits[0]["documentId"] == document["documentId"] and "validation" in hits[0]["passage"]
        hits, _ = expect(server, 200, "POST", "/api/v1/knowledge/search", {**query, "application": "MDIX"})
        assert hits == []
        body, _ = expect(server, 200, "POST", "/api/v1/diagnose", REQUEST)
        assert body["knowledge"][0]["documentId"] == document["documentId"]
        assert REQUEST["description"] not in stored.read_text()
    with Server(directory) as server:
        queue, _ = expect(server, 200, "GET", "/api/v1/proposals", key=LEARNING_KEY)
        assert next(p for p in queue if p["proposalId"] == pid)["status"] == "Approved"
        hits, _ = expect(server, 200, "POST", "/api/v1/knowledge/search", query)
        assert hits[0]["documentId"] == document["documentId"]
        expect(server, 403, "DELETE", f"/api/v1/knowledge/documents/{document['documentId']}")
        expect(server, 204, "DELETE", f"/api/v1/knowledge/documents/{document['documentId']}", key=LEARNING_KEY)
        hits, _ = expect(server, 200, "POST", "/api/v1/knowledge/search", query)
        assert hits == []
        expect(server, 204, "DELETE", f"/api/v1/cases/{active_id}", key=LEARNING_KEY)
        queue, _ = expect(server, 200, "GET", "/api/v1/proposals", key=LEARNING_KEY)
        assert not any(p["proposalId"] == pid for p in queue)
    # A fresh process must retrieve the learned case and honor deletion.
    with Server(directory) as server:
        body, _ = expect(server, 200, "POST", "/api/v1/diagnose", REQUEST)
        assert body["similarCases"][0]["caseId"] == case_id
        expect(server, 403, "DELETE", f"/api/v1/cases/{case_id}")
        expect(server, 204, "DELETE", f"/api/v1/cases/{case_id}", key=LEARNING_KEY)
        expect(server, 404, "GET", f"/api/v1/cases/{case_id}", key=LEARNING_KEY)
        body, _ = expect(server, 200, "POST", "/api/v1/diagnose", REQUEST)
        assert body["similarCases"] == []
        stored.write_text("{broken")
        expect(server, 503, "POST", "/api/v1/diagnose", REQUEST)
        expect(server, 503, "POST", "/api/v1/feedback", FEEDBACK, key=LEARNING_KEY)
        assert stored.read_text() == "{broken"
        statuses = [server.call("GET", "/api/v1/capabilities", key=None)[0] for _ in range(65)]
        assert 429 in statuses, "Rate limit must return 429"
    stored.write_text("[]")
    with Server(directory, learning=False) as server:
        expect(server, 200, "POST", "/api/v1/diagnose", REQUEST)
        expect(server, 503, "POST", "/api/v1/feedback", FEEDBACK)
    with Server(directory, environment="Production") as server:
        expect(server, 400, "POST", "/api/v1/diagnose", REQUEST)
print("PASS API authentication, validation, learning, isolation, restart, deletion, corruption, limits and HTTPS checks")
