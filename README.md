# SecureIntelligence

A small internal .NET 10 diagnostic service using deterministic rules and human-approved cases. No LLM, GPU, model training or outbound AI calls are required.

It recognizes explicit diagnostic signals; it does not understand arbitrary questions like a chatbot. Learning means remembering an operator-reviewed cause and resolution for future incidents with similar evidence. It never rewrites rules or changes another application's data.

## What is included

- Rules for missing Line Designer systemization, Ready-for-Quote without BOM items, and elevated database latency.
- Evidence-based case matching scoped to application and issue type. Contradictory booleans and zero/nonzero numeric states are excluded. Similarity is **not** a probability or verified root cause.
- Separate diagnosis and learning keys, authorization before JSON body binding, a 64 KiB request limit, and 60 requests/minute per remote IP (including rejected requests).
- Allow-listed learning signals with type validation. Original descriptions are never persisted. Cause and resolution remain operator-reviewed free text with basic accidental-disclosure checks.
- A local browser dashboard for diagnosis, guide search, learning reviews and verified outcomes. Keys stay in page memory and are cleared on disconnect/reload.
- A proposal queue: caller keys can propose cases; only reviewer keys can approve or reject them. Approval and case publication are one atomic transaction.
- Reviewed plain-text/Markdown guides with scoped BM25 keyword search, exact source passages and optional source links. Relevant passages are included in diagnoses.
- Verified outcome tracking, counted once per case and incident ID. Outcomes only break ties between cases with equal signal similarity.
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

Open **http://localhost:5100** to use the dashboard. Connect with the diagnosis key for diagnosis and search, or the separate learning key for review and management. The dashboard shell contains no data or embedded credentials; the APIs require a key.

A complete browser test:

1. Connect with the learning key and select **Load example → Run diagnosis**.
2. Enter a generic proposed cause and resolution, then **Submit for review**.
3. Open **Learning reviews**, inspect/edit the case, check the verification box and **Approve case**.
4. Run the same diagnosis again. The approved case and matched signal evidence should appear.
5. In **Knowledge**, expand **Publish a reviewed guide**, load the example guide, review it and approve publication. Search for `BOM` or `systemization`.
6. Open **Approved cases** and record a verified outcome. The most recent diagnosis supplies an incident ID. Reusing that ID updates the outcome instead of adding another vote.

The example scenario/guide are synthetic starter examples. Review your application documentation before publishing operational guidance. New cases and guides are not automatically created just by opening the dashboard.

For the optional PowerShell demo, in terminal 2 set the same two keys and run:

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

For optional local browser verification:

```powershell
pip install playwright
python -m playwright install chromium
python scripts/browser_test.py
```

GitHub Actions runs the real browser workflow on Linux and saves desktop/mobile preview artifacts. Playwright is a test dependency; the service and dashboard do not require Python, Node.js or browser packages at runtime.

The .NET runner returns a nonzero exit code on failure; use `dotnet run` as shown rather than `dotnet test`. It checks rules, validation, culture-independent numbers, learning restrictions, matching, key separation, concurrent persistence, duplicates, retention, deletion and corruption. Additional groups test proposal approval/rejection, outcome correction, knowledge scope/retrieval/deletion, and migration of sanitized old case arrays. The smoke test launches isolated temporary API processes; it verifies HTTP status codes, restart persistence, learning disabled, request size/rate limits and production HTTPS enforcement. It does not touch your real case store.

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
| `DELETE /api/v1/cases/{caseId}` | Learning | Remove a learned case and linked reviews/outcomes |
| `POST /api/v1/proposals` | Diagnosis or learning | Submit a sanitized case for review; learning must be enabled |
| `GET /api/v1/proposals` | Learning | Inspect pending and reviewed proposals |
| `GET /api/v1/proposals/{id}` | Learning | Inspect one proposal |
| `POST /api/v1/proposals/{id}/approve` | Learning | Approve and publish a case atomically |
| `POST /api/v1/proposals/{id}/reject` | Learning | Reject with a supported reason |
| `POST /api/v1/cases/{caseId}/outcomes` | Learning | Record/correct a verified incident outcome |
| `POST /api/v1/knowledge/search` | Diagnosis or learning | Search reviewed source passages |
| `GET /api/v1/knowledge/documents` | Diagnosis or learning | Published guide metadata |
| `GET /api/v1/knowledge/documents/{id}` | Diagnosis or learning | Read a published guide |
| `POST /api/v1/knowledge/documents` | Learning | Publish an explicitly reviewed guide |
| `DELETE /api/v1/knowledge/documents/{id}` | Learning | Remove a published guide |

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

## Learning review and knowledge contracts

`POST /proposals` accepts `request`, `confirmedCause` and `resolution`, with the same learning allow-lists as feedback. It returns 202 with `learned: false`. Pending or rejected proposals never appear in case matching. Review approval accepts `approvedForLearning: true` plus optional corrected `confirmedCause`/`resolution`. Repeated approvals are idempotent. Rejected proposals require a new corrected submission; rejected/approved decision conflicts return 409. Supported rejection reasons are `InsufficientEvidence`, `IncorrectCause`, `Duplicate` and `Other`.

The direct `/feedback` endpoint remains available for approved integration workflows using the learning key. It does not require an intermediate queue entry.

Knowledge publication accepts:

```json
{
  "application": "LineDesigner",
  "issueType": "ReadyForQuote",
  "title": "BOM validation guide",
  "source": "Reviewed runbook revision 1",
  "content": "If BOM items are missing, inspect the owning application's approved validation workflow.",
  "approvedForPublication": true
}
```

Publication is a reviewer action. Issue type can also be `General`; those guides can match all issues within the same application. `sourceUrl` is optional and must be an HTTPS reference without credentials, query or fragment. The server never fetches it. Plain text and Markdown are imported as text, with a 20,000-character limit and the existing 64 KiB request limit. Secret/personal-data checks are basic safeguards; the operator must review all content.

Search uses `application`, `query`, optional `issueType` and `limit` (1–10). Queries are limited to 1,000 characters and 64 distinct search terms. Passages are up to 1,000 characters. BM25 retrieves exact reviewed passages and source references, using Unicode-aware keywords. It does not provide semantic search, train a model, generate answers or perform RAG. No matching evidence returns an empty list. Search uses POST so queries are not placed in URL logs.

An outcome uses `incidentId` (a nonempty GUID in 32-character hexadecimal format), `resolved` and `verified: true`. One case/incident combination counts once; posting a changed outcome corrects it. Verified successes/failures are shown explicitly. Case retrieval first ranks by signal similarity; only ties use a smoothed outcome ratio. Neither score is a probability or root-cause proof.

## Storage and deployment

`CaseStore` settings in `appsettings.json` can be overridden with `CaseStore__Directory`, `CaseStore__RetentionDays` and `CaseStore__MaxCases`, `CaseStore__MaxDocuments` and `CaseStore__MaxProposals`. Relative directories resolve under the API content root. The atomic state file stores cases, proposals, outcomes and published documents together. Defaults are 1,000 cases, 1,000 proposals and 100 documents. Proposal/document capacity rejects new entries with 409 rather than silently dropping them. Approved documents remain until explicitly deleted; case/proposal/outcome retention is 90 days by default. Expired/over-capacity cases are removed on the next store operation; a stopped or idle service does not erase them on a timer. Retention also needs to cover filesystem backups and replicas outside this service.

**Already-sanitized case arrays from the previous enhancement are accepted and upgraded into the versioned state envelope on the next write.** Incomplete/corrupt envelopes are rejected and preserved.

**Raw original starter cases need a reviewed migration.** The previous format could contain raw descriptions and unrestricted signals. The new repository deliberately rejects such records instead of exposing them or overwriting them. With the service stopped, move the old `validated-cases.json` to an appropriately protected archive, review its contents, and resubmit only approved generic cases through the feedback API. Do not blindly copy the old descriptions into cause/resolution.

The JSON repository supports **one process only**. Use a transactional database before running multiple replicas or sharing storage between processes. Protect the data directory and backups with approved OS permissions and encryption. Secrets and stored cases are ignored by git.

Before production deployment, replace starter keys with approved corporate authentication and application-specific authorization, configure trusted HTTPS/gateway handling and hostnames, restrict network ingress, and review the learning fields and approval workflow. The two shared keys do not isolate applications from one another. Proxies must preserve HTTPS safely; this starter does not trust forwarded headers automatically. Rate limiting uses the direct remote IP, so callers behind a gateway share its limit.

See [architecture notes](docs/ARCHITECTURE.md) for trust boundaries and limitations.
