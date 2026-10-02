"""End-to-end checks using only Python's standard library and a built .NET API."""
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
API = ROOT / "src/SecureIntelligence.Api"
DLL = API / "bin/Release/net10.0/SecureIntelligence.Api.dll"
DIAGNOSIS_KEY = secrets.token_hex(32)
LEARNING_KEY = secrets.token_hex(32)
REQUEST = {
    "application": "LineDesigner", "issueType": "ReadyForQuote",
    "description": "PRIVATE-DESCRIPTION-MUST-NOT-BE-PERSISTED",
    "signals": {"readyForQuote": "true", "bomItemCount": "0", "systemizationConfigured": "false"},
}
FEEDBACK = {"request": REQUEST, "confirmedCause": "Systemization was not configured",
            "resolution": "Configure systemization and run validation", "approvedForLearning": True}


class Server:
    def __init__(self, directory, learning=True, environment="Development"):
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        self.url = f"http://127.0.0.1:{port}"
        env = os.environ.copy()
        env.update({"ASPNETCORE_ENVIRONMENT": environment, "DOTNET_ENVIRONMENT": environment,
                    "ASPNETCORE_URLS": self.url, "CaseStore__Directory": str(directory),
                    "SECURE_INTELLIGENCE_API_KEY": DIAGNOSIS_KEY})
        env.pop("SECURE_INTELLIGENCE_LEARNING_API_KEY", None)
        if learning:
            env["SECURE_INTELLIGENCE_LEARNING_API_KEY"] = LEARNING_KEY
        self.log = tempfile.TemporaryFile(mode="w+b")
        self.process = subprocess.Popen(["dotnet", str(DLL)], cwd=API, env=env,
                                        stdout=self.log, stderr=subprocess.STDOUT)

    def __enter__(self):
        for _ in range(100):
            if self.process.poll() is not None:
                self.log.seek(0)
                raise RuntimeError(self.log.read().decode())
            try:
                if self.call("GET", "/health", key=None)[0] == 200:
                    return self
            except urllib.error.URLError:
                pass
            time.sleep(0.1)
        self.__exit__(None, None, None)
        raise RuntimeError("API did not become healthy")

    def __exit__(self, *_):
        self.process.terminate()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
        self.log.seek(0)
        assert REQUEST["description"].encode() not in self.log.read(), "Raw description was logged"
        self.log.close()

    def call(self, method, path, payload=None, key=DIAGNOSIS_KEY, raw=None, extra_headers=None):
        body = raw if raw is not None else (json.dumps(payload).encode() if payload is not None else None)
        headers = {"Content-Type": "application/json"}
        if key is not None:
            headers["X-Internal-Api-Key"] = key
        headers.update(extra_headers or {})
        request = urllib.request.Request(self.url + path, data=body, method=method, headers=headers)
        try:
            response = urllib.request.urlopen(request, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            content = response.read()
            return response.status, json.loads(content) if content else None, response.headers


def expect(server, status, *args, **kwargs):
    actual, body, headers = server.call(*args, **kwargs)
    assert actual == status, f"{args[:2]}: expected {status}, got {actual}: {body}"
    return body, headers


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
        assert len(json.loads(stored.read_text())) == 1
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
