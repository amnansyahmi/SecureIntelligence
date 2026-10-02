namespace SecureIntelligence.Core.Models;

public enum FindingSeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical
}

public sealed record DiagnosticFinding(
    string Code,
    FindingSeverity Severity,
    string Summary,
    string Explanation,
    string? SuggestedAction = null,
    double? Confidence = null,
    string Source = "rule");
