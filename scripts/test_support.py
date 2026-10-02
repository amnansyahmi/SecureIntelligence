"""Shared isolated API process helpers for verification scripts."""
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


