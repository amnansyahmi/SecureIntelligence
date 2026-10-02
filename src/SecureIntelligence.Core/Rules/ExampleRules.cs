using SecureIntelligence.Core.Models;

namespace SecureIntelligence.Core.Rules;

public sealed class MissingSystemizationRule : IIntelligenceRule
{
    public string Code => "LD-SYSTEMIZATION-MISSING";

    public DiagnosticFinding? Evaluate(DiagnosticRequest request)
    {
        if (!IsLineDesigner(request) || !TryGetBool(request, "systemizationConfigured", out var configured) || configured)
            return null;

        return new DiagnosticFinding(
            Code,
            FindingSeverity.High,
            "Systemization is not configured",
            "The request indicates that systemization is currently not configured.",
            "Configure systemization, then run the application's normal validation again.");
    }

    private static bool IsLineDesigner(DiagnosticRequest request) =>
        string.Equals(request.Application, "LineDesigner", StringComparison.OrdinalIgnoreCase);

    internal static bool TryGetBool(DiagnosticRequest request, string key, out bool value)
    {
        value = false;
        return request.Signals is not null &&
               request.Signals.TryGetValue(key, out var raw) &&
               bool.TryParse(raw, out value);
    }
}

public sealed class ReadyForQuoteWithoutBomRule : IIntelligenceRule
{
    public string Code => "LD-RFQ-BOM-MISSING";

    public DiagnosticFinding? Evaluate(DiagnosticRequest request)
    {
        if (!string.Equals(request.Application, "LineDesigner", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!MissingSystemizationRule.TryGetBool(request, "readyForQuote", out var ready) || !ready)
            return null;

        if (!TryGetInt(request, "bomItemCount", out var count) || count > 0)
            return null;

        return new DiagnosticFinding(
            Code,
            FindingSeverity.High,
            "Ready-for-Quote line has no BOM items",
            "The line is marked Ready for Quote but the supplied BOM item count is zero.",
            "Use the owning application's normal BOM validation/generation workflow before quotation processing.");
    }

    private static bool TryGetInt(DiagnosticRequest request, string key, out int value)
    {
        value = 0;
        return request.Signals is not null &&
               request.Signals.TryGetValue(key, out var raw) &&
               int.TryParse(raw, out value);
    }
}

public sealed class HighDatabaseLatencyRule : IIntelligenceRule
{
    public string Code => "COMMON-DB-LATENCY";

    public DiagnosticFinding? Evaluate(DiagnosticRequest request)
    {
        if (request.Signals is null ||
            !request.Signals.TryGetValue("dbLatencyMs", out var raw) ||
            !double.TryParse(raw, out var latency) ||
            latency < 1500)
        {
            return null;
        }

        return new DiagnosticFinding(
            Code,
            latency >= 5000 ? FindingSeverity.High : FindingSeverity.Medium,
            "Database latency is elevated",
            $"The supplied database latency is {latency:0} ms.",
            "Inspect the application's query timings and execution plan before attributing the issue to the intelligence service.");
    }
}
