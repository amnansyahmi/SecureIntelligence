using System.Text.RegularExpressions;
using SecureIntelligence.Core.Models;

namespace SecureIntelligence.Core.Cases;

public sealed class CaseMatcher(ICaseRepository repository)
{
    public async Task<IReadOnlyList<CaseMatch>> FindSimilarAsync(
        DiagnosticRequest request,
        int limit = 3,
        CancellationToken cancellationToken = default)
    {
        var cases = await repository.GetAllAsync(cancellationToken);
        var requestTokens = Tokenize(BuildSearchText(request));

        return cases
            .Where(c => string.Equals(c.Application, request.Application, StringComparison.OrdinalIgnoreCase))
            .Select(c => new
            {
                Case = c,
                Score = Jaccard(requestTokens, Tokenize(BuildSearchText(c)))
            })
            .Where(x => x.Score >= 0.15)
            .OrderByDescending(x => x.Score)
            .Take(Math.Clamp(limit, 1, 10))
            .Select(x => new CaseMatch(
                x.Case.CaseId,
                x.Case.ConfirmedCause,
                x.Case.Resolution,
                Math.Round(x.Score, 3)))
            .ToArray();
    }

    private static string BuildSearchText(DiagnosticRequest request) =>
        string.Join(' ', new[]
        {
            request.IssueType,
            request.Description,
            string.Join(' ', (request.Signals ?? new Dictionary<string, string>())
                .Select(kv => $"{kv.Key} {kv.Value}"))
        });

    private static string BuildSearchText(CaseRecord record) =>
        string.Join(' ', new[]
        {
            record.IssueType,
            record.Description,
            string.Join(' ', record.Signals.Select(kv => $"{kv.Key} {kv.Value}")),
            record.ConfirmedCause
        });

    private static HashSet<string> Tokenize(string value) =>
        Regex.Matches(value.ToLowerInvariant(), "[a-z0-9_]+")
            .Select(m => m.Value)
            .Where(token => token.Length > 1)
            .ToHashSet(StringComparer.Ordinal);

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return 0;

        var intersection = left.Count(right.Contains);
        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
