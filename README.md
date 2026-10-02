# SecureIntelligence

A small internal .NET 10 diagnostic service using deterministic rules and human-approved cases. No LLM, GPU, model training or outbound AI calls are required.

It recognizes explicit diagnostic signals; it does not understand arbitrary questions like a chatbot. Learning means remembering an operator-reviewed cause and resolution for future incidents with similar evidence. It never rewrites rules or changes another application's data.

## What is included

- Rules for missing Line Designer systemization, Ready-for-Quote without BOM items, and elevated database latency.
- Evidence-based case matching scoped to application and issue type. Contradictory booleans and zero/nonzero numeric states are excluded. Similarity is **not** a probability or verified root cause.
- Separate diagnosis and learning keys, authorization before JSON body binding, a 64 KiB request limit, and 60 requests/minute per remote IP (including rejected requests).
- Allow-listed learning signals with type validation. Original descriptions are never persisted. Cause and resolution remain operator-reviewed free text with basic accidental-disclosure checks.
- Single-process JSON storage with atomic writes, duplicate handling, case deletion, 90-day retention and a 1,000-case limit by default.
- Dependency-free executable .NET tests and an HTTP smoke test. GitHub Actions runs both on Linux and Windows.

## Run locally on Windows

Install the .NET 10 SDK. From the repository root, in terminal 1:

```powershell
# These example values are for synthetic local testing only.
$env:SECURE_INTELLIGENCE_API_KEY = "local-demo-diagnosis-key-change-before-use-12345"
$env:SECURE_INTELLIGENCE_LEARNING_API_KEY = "local-demo-learning-key-change-before-use-67890"
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/SecureIntelligence.Api --no-launch-profile --urls http://localhost:5100
```

In terminal 2, set the same two keys and run:

```powershell
$env:SECURE_INTELLIGENCE_API_KEY = "local-demo-diagnosis-key-change-before-use-12345"
$env:SECURE_INTELLIGENCE_LEARNING_API_KEY = "local-demo-learning-key-change-before-use-67890"
.\scripts\demo.ps1
.\scripts\demo.ps1 -Learn
```

The first command returns three findings for synthetic Line Designer signals. `-Learn` submits an approved synthetic case and diagnoses again: `similarCases` should now include that case with similarity `1`. Repeating it reuses the same case ID. The script prints the deletion URL for cleanup.

Linux/macOS: set the equivalent environment variables with `export` and use the same `dotnet run` command. HTTP is allowed only in Development. Keep local examples on loopback; other environments reject HTTP API requests. Use an HTTPS binding and a trusted certificate for deployment.

Missing/short diagnosis keys fail startup. Keys must be distinct and 32–512 characters. Omit `SECURE_INTELLIGENCE_LEARNING_API_KEY` to disable all feedback and case-management endpoints while retaining diagnosis. Generate independent random production secrets; never commit them.

## Run the tests

From the repository root:

```powershell
dotnet build SecureIntelligence.sln -c Release
dotnet run --project tests/SecureIntelligence.Tests -c Release --no-build
# Optional local HTTP verification; requires Python 3.10+.
python scripts/smoke_test.py
```

The .NET runner returns a nonzero exit code on failure; use `dotnet run` as shown rather than `dotnet test`. It checks rules, validation, culture-independent numbers, learning restrictions, matching, key separation, concurrent persistence, duplicates, retention, deletion and corruption. The smoke test launches isolated temporary API processes; it verifies HTTP status codes, restart persistence, learning disabled, request size/rate limits and production HTTPS enforcement. It does not touch your real case store.

## API

All `/api/v1` endpoints require `X-Internal-Api-Key`.

| Endpoint | Access | Purpose |
| --- | --- | --- |
| `GET /health` | Public | Process liveness; not a storage readiness check |
| `GET /api/v1/capabilities` | Diagnosis or learning | Learning state and supported fields |
| `POST /api/v1/diagnose` | Diagnosis or learning | Deterministic findings and similar approved cases |
| `POST /api/v1/feedback` | Learning | Explicitly approved case submission |
| `GET /api/v1/cases` | Learning | Case metadata list |
| `GET /api/v1/cases/{caseId}` | Learning | Inspect a reviewed case |
| `DELETE /api/v1/cases/{caseId}` | Learning | Remove a learned case |

Example diagnosis body:

```json
{
  "application": "LineDesigner",
  "issueType": "ReadyForQuote",
  "description": "Synthetic test: ready line with incomplete preparation",
  "signals": {
    "readyForQuote": "true",
    "bomItemCount": "0",
    "systemizationConfigured": "false",
    "dbLatencyMs": "2200"
  }
}
```

Feedback wraps that body in `request`, plus `confirmedCause`, `resolution` and `approvedForLearning`. Set approval only after reviewing the diagnosis and both text fields. A key identifies a privileged caller, not proof that a human reviewed an incident; enforce your approval workflow in the caller.

Learning permits `LineDesigner` or `MDIX`, with issue type `ReadyForQuote` or `Performance`, and only these exact signal keys:

| Signal | Value |
| --- | --- |
| `readyForQuote` | String `true` or `false` |
| `systemizationConfigured` | String `true` or `false` |
| `bomItemCount` | Nonnegative integer string |
| `dbLatencyMs` | Nonnegative finite number string, using a decimal point |

Unsupported learning fields are rejected, not silently removed. Add new application/issue names and typed signal definitions through a reviewed code change. Empty findings or similar cases mean no supported evidence matched; they do not establish that a system is healthy.

Responses use named severity values (`High`, `Medium`, etc.). Invalid requests return 400, missing/invalid keys 401, diagnosis-only access to enabled learning endpoints 403, oversized requests 413, rate limits 429, and unavailable/invalid storage or disabled learning 503. Storage failures do not return raw case contents or exception details.

## Storage and deployment

`CaseStore` settings in `appsettings.json` can be overridden with `CaseStore__Directory`, `CaseStore__RetentionDays` and `CaseStore__MaxCases`. Relative directories resolve under the API content root. Expired/over-capacity cases are removed on the next store operation; a stopped or idle service does not erase them on a timer. Retention also needs to cover filesystem backups and replicas outside this service.

**Existing starter cases need a reviewed migration.** The previous format could contain raw descriptions and unrestricted signals. The new repository deliberately rejects such records instead of exposing them or overwriting them. With the service stopped, move the old `validated-cases.json` to an appropriately protected archive, review its contents, and resubmit only approved generic cases through the feedback API. Do not blindly copy the old descriptions into cause/resolution.

The JSON repository supports **one process only**. Use a transactional database before running multiple replicas or sharing storage between processes. Protect the data directory and backups with approved OS permissions and encryption. Secrets and stored cases are ignored by git.

Before production deployment, replace starter keys with approved corporate authentication and application-specific authorization, configure trusted HTTPS/gateway handling and hostnames, restrict network ingress, and review the learning fields and approval workflow. The two shared keys do not isolate applications from one another. Proxies must preserve HTTPS safely; this starter does not trust forwarded headers automatically. Rate limiting uses the direct remote IP, so callers behind a gateway share its limit.

See [architecture notes](docs/ARCHITECTURE.md) for trust boundaries and limitations.
