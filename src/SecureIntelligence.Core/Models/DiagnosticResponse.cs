namespace SecureIntelligence.Core.Models;

public sealed record DiagnosticResponse(
    string RequestId,
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<DiagnosticFinding> Findings,
    IReadOnlyList<CaseMatch> SimilarCases);

public sealed record CaseMatch(
    string CaseId,
    string ConfirmedCause,
    string Resolution,
    double Similarity);
