using System.Globalization;
using SecureIntelligence.Core.Models;

namespace SecureIntelligence.Core.Cases;

public sealed class CaseMatcher(ICaseRepository repository)
{
    public async Task<IReadOnlyList<CaseMatch>> FindSimilarAsync(
        DiagnosticRequest request, int limit = 3, CancellationToken cancellationToken = default)
    {
        if (request.Signals is not { Count: > 0 })
            return Array.Empty<CaseMatch>();

        var cases = await repository.GetAllAsync(cancellationToken);
        var outcomes = (await repository.GetOutcomeSummariesAsync(cancellationToken)).ToDictionary(o => o.CaseId, StringComparer.Ordinal);
        return cases
            .Where(c => string.Equals(c.Application, request.Application, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(c.IssueType, request.IssueType, StringComparison.OrdinalIgnoreCase))
            .Select(c => (Case: c, Score: Similarity(request.Signals, c.Signals)))
            .Where(x => x.Score >= 0.6)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => outcomes.TryGetValue(x.Case.CaseId, out var o) ? (double)(o.VerifiedSuccesses + 1) / (o.VerifiedSuccesses + o.VerifiedFailures + 2) : 0.5)
            .ThenBy(x => x.Case.CaseId, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 10))
            .Select(x => new CaseMatch(x.Case.CaseId, x.Case.ConfirmedCause,
                x.Case.Resolution, Math.Round(x.Score, 3),
                Compare(request.Signals, x.Case.Signals),
                outcomes.GetValueOrDefault(x.Case.CaseId)?.VerifiedSuccesses ?? 0,
                outcomes.GetValueOrDefault(x.Case.CaseId)?.VerifiedFailures ?? 0))
            .ToArray();
    }

    // Signals are evidence. Free text and common words cannot manufacture a match.
    private static double Similarity(IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return 0;
        double score = 0;
        var shared = 0;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other))
                continue;
            shared++;
            if (bool.TryParse(value, out var boolean) && bool.TryParse(other, out var otherBoolean))
            {
                if (boolean != otherBoolean)
                    return 0; // Contradictory facts must not suggest a diagnosis.
                score++;
            }
            else if (TryNumber(value, out var number) && TryNumber(other, out var otherNumber))
            {
                if ((number == 0) != (otherNumber == 0))
                    return 0;
                score += 1 - Math.Abs(number - otherNumber) / Math.Max(1, Math.Max(Math.Abs(number), Math.Abs(otherNumber)));
            }
            else if (string.Equals(value, other, StringComparison.OrdinalIgnoreCase))
                score++;
        }
        return shared == 0 ? 0 : score / (left.Count + right.Count - shared);
    }

    private static IReadOnlyList<SignalComparison> Compare(IReadOnlyDictionary<string, string> requested,
        IReadOnlyDictionary<string, string> stored) => requested.Keys.Union(stored.Keys, StringComparer.Ordinal)
        .Order(StringComparer.Ordinal).Select(key =>
        {
            requested.TryGetValue(key, out var current);
            stored.TryGetValue(key, out var previous);
            var relation = current is null || previous is null ? "missing"
                : string.Equals(current, previous, StringComparison.OrdinalIgnoreCase) ? "exact" : "different";
            return new SignalComparison(key, current, previous, relation);
        }).ToArray();

    private static bool TryNumber(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
        && double.IsFinite(result) && result >= 0;
}
