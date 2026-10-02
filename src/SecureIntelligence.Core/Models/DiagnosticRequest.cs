namespace SecureIntelligence.Core.Models;

public sealed record DiagnosticRequest(
    string Application,
    string IssueType,
    string Description,
    IReadOnlyDictionary<string, string>? Signals = null);
