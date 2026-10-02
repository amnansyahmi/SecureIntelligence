using SecureIntelligence.Core.Knowledge;

namespace SecureIntelligence.Core.Models;

public sealed record DiagnosticResponse(
    string RequestId,
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<DiagnosticFinding> Findings,
    IReadOnlyList<CaseMatch> SimilarCases,
    IReadOnlyList<KnowledgeHit>? Knowledge = null);

public sealed record CaseMatch(
    string CaseId,
    string ConfirmedCause,
    string Resolution,
    double Similarity,
    IReadOnlyList<SignalComparison>? Evidence = null,
    int VerifiedSuccesses = 0,
    int VerifiedFailures = 0);

public sealed record SignalComparison(string Signal, string? RequestedValue, string? CaseValue, string Relation);
