using SecureIntelligence.Core.Models;

namespace SecureIntelligence.Core.Rules;

public interface IIntelligenceRule
{
    string Code { get; }
    DiagnosticFinding? Evaluate(DiagnosticRequest request);
}
