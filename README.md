# SecureIntelligence

A small, model-less intelligence service for internal .NET applications.

## Goals

- No LLM required
- No GPU required
- No outbound AI provider calls
- Deterministic rules + case-based learning
- Human-approved learning only
- Application databases remain behind their owning APIs
- Minimal audit metadata; do not log raw sensitive payloads by default

## Architecture

```text
Line Designer / MDIX / other app
            |
            | HTTPS + authentication
            v
     SecureIntelligence.Api
            |
   +--------+---------+
   |                  |
Rules Engine     Case Matcher
   |                  |
   +--------+---------+
            |
        Findings

Feedback -> approved case store -> improves future case matching
```

## What "learning" means in this starter

This version uses **case-based learning**, not an LLM and not a neural network.
When an engineer confirms a diagnosis and explicitly approves it for learning,
the service stores a sanitized case. Future requests can match against those
validated cases.

It never rewrites production rules automatically.

## Requirements

- .NET 10 SDK

## Run

```bash
cd src/SecureIntelligence.Api
export SECURE_INTELLIGENCE_API_KEY="replace-with-a-long-random-secret"
dotnet run
```

On Windows PowerShell:

```powershell
$env:SECURE_INTELLIGENCE_API_KEY = "replace-with-a-long-random-secret"
dotnet run
```

The API key is only a starter authentication mechanism. For an enterprise deployment,
replace it with your organization's approved authentication (for example Windows/AD,
Entra ID, mTLS, or an internal gateway).

## Example diagnosis request

```http
POST /api/v1/diagnose
X-Internal-Api-Key: <secret>
Content-Type: application/json
```

```json
{
  "application": "LineDesigner",
  "issueType": "ReadyForQuote",
  "description": "Line is marked ready but quotation preparation is incomplete",
  "signals": {
    "readyForQuote": "true",
    "bomItemCount": "0",
    "systemizationConfigured": "false",
    "dbLatencyMs": "2200"
  }
}
```

## Example feedback

Only set `approvedForLearning` after a human has verified the cause/resolution.

```json
{
  "request": {
    "application": "LineDesigner",
    "issueType": "ReadyForQuote",
    "description": "Line is marked ready but quotation preparation is incomplete",
    "signals": {
      "readyForQuote": "true",
      "bomItemCount": "0",
      "systemizationConfigured": "false"
    }
  },
  "confirmedCause": "Systemization was not configured",
  "resolution": "Configure systemization and re-run validation",
  "approvedForLearning": true
}
```

## Important production hardening

Before production use:

1. Replace API-key auth with approved corporate authentication/authorization.
2. Store cases in an approved database instead of the local JSON starter store.
3. Apply data classification and field allow-lists per calling application.
4. Put the API behind an internal gateway/firewall and disable public ingress.
5. Add centralized audit/telemetry without logging sensitive request bodies.
6. Add application-specific authorization scopes.
7. Add retention/deletion rules for learned cases.
8. Security review all rules and learning fields before enabling production data.

## Next phase

The interfaces are intentionally small so an `ML.NET` classifier can be added later
without changing callers. Classical ML can stay CPU-only for many tabular use cases.
