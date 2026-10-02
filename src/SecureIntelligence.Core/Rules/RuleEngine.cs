using SecureIntelligence.Core.Models;

namespace SecureIntelligence.Core.Rules;

public sealed class RuleEngine(IEnumerable<IIntelligenceRule> rules)
{
    private readonly IReadOnlyList<IIntelligenceRule> _rules = rules.ToArray();

    public IReadOnlyList<DiagnosticFinding> Evaluate(DiagnosticRequest request) =>
        _rules
            .Select(rule => rule.Evaluate(request))
            .Where(finding => finding is not null)
            .Cast<DiagnosticFinding>()
            .OrderByDescending(f => f.Severity)
            .ToArray();
}
