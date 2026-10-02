# Architecture notes

## Trust boundaries

The intelligence service should not receive database credentials for Line Designer,
MDIX, Delphic AP, or other applications. Each application remains the authority for
its own data and actions.

Preferred flow:

```text
Intelligence Service -> application-owned API -> authorization -> domain service -> database
```

Avoid:

```text
Intelligence Service -> direct connection to every application database
```

## Data contract strategy

Expose only diagnostic features required by a rule or learning feature. Prefer
application-specific allow-lists over forwarding entire records.

Examples:

- `readyForQuote`
- `bomItemCount`
- `systemizationConfigured`
- `dbLatencyMs`

Avoid sending names, free-text clinical information, credentials, tokens, connection
strings, or unneeded identifiers into the learning store.

## Learning policy

The starter uses human-approved case learning:

1. Diagnose using deterministic rules and existing validated cases.
2. Engineer confirms the actual cause and resolution.
3. Engineer explicitly approves the case for learning.
4. Store the sanitized case.
5. Future incidents can match that case.

The service does **not** mutate rules automatically.

## Future ML.NET layer

A later classifier can implement a small interface such as:

```csharp
public interface IPredictionEngine
{
    Task<Prediction?> PredictAsync(DiagnosticRequest request, CancellationToken ct);
}
```

Train candidate models offline/scheduled, evaluate them, then promote an approved
version. Do not update production models directly from unreviewed user feedback.
