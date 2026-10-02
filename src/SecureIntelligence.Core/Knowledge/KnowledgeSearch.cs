using System.Text.RegularExpressions;

namespace SecureIntelligence.Core.Knowledge;

// Keyword BM25 retrieval. It returns source passages, never generated prose.
public sealed partial class KnowledgeSearch(IKnowledgeRepository repository)
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    { "the", "a", "an", "to", "of", "is", "in", "and", "for", "with", "yang", "dan", "di", "ini", "itu", "untuk" };

    public async Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeSearchRequest request, CancellationToken ct = default)
    {
        var terms = Tokens(request.Query).Distinct(StringComparer.Ordinal).Take(64).ToArray();
        if (terms.Length == 0) return Array.Empty<KnowledgeHit>();
        var documents = await repository.GetDocumentsAsync(ct);
        var chunks = documents.Where(d => d.Application.Equals(request.Application, StringComparison.OrdinalIgnoreCase)
            && (request.IssueType is null || d.IssueType == "General" || d.IssueType.Equals(request.IssueType, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(d => Chunk(d.Content).Select(text => new Passage(d, text, Tokens(d.Title + " " + text))))
            .ToArray();
        if (chunks.Length == 0) return Array.Empty<KnowledgeHit>();
        var averageLength = Math.Max(1, chunks.Average(c => c.Tokens.Length));
        var frequencies = terms.ToDictionary(t => t, t => chunks.Count(c => c.Frequencies.ContainsKey(t)), StringComparer.Ordinal);
        return chunks.Select(chunk =>
        {
            var matched = terms.Where(t => chunk.Frequencies.ContainsKey(t)).ToArray();
            double score = 0;
            foreach (var term in matched)
            {
                var frequency = chunk.Frequencies[term];
                var idf = Math.Log(1 + (chunks.Length - frequencies[term] + 0.5) / (frequencies[term] + 0.5));
                score += idf * frequency * 2.2 / (frequency + 1.2 * (0.25 + 0.75 * chunk.Tokens.Length / averageLength));
            }
            return new KnowledgeHit(chunk.Document.DocumentId, chunk.Document.Title, chunk.Document.Source,
                chunk.Document.SourceUrl, chunk.Text, Math.Round(score, 4), matched);
        }).Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score).ThenBy(hit => hit.DocumentId, StringComparer.Ordinal)
            .ThenBy(hit => hit.Passage, StringComparer.Ordinal).Take(Math.Clamp(request.Limit, 1, 10)).ToArray();
    }

    private static string[] Tokens(string text) => Words().Matches(text.ToLowerInvariant())
        .Select(m => m.Value).Where(t => t.Length > 1 && !StopWords.Contains(t)).Take(10_000).ToArray();
    private static IEnumerable<string> Chunk(string content)
    {
        for (var start = 0; start < content.Length;)
        {
            var length = Math.Min(1000, content.Length - start);
            if (start + length < content.Length)
            {
                var boundary = content.LastIndexOfAny(new[] { ' ', '\n' }, start + length - 1, length);
                if (boundary > start + length / 2) length = boundary - start + 1;
            }
            var passage = content.Substring(start, length).Trim();
            if (passage.Length > 0) yield return passage;
            start += length;
        }
    }
    private sealed record Passage(KnowledgeDocument Document, string Text, string[] Tokens)
    {
        public Dictionary<string, int> Frequencies { get; } = Tokens.GroupBy(t => t, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }
    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
