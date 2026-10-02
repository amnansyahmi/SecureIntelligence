namespace SecureIntelligence.Core.Knowledge;

public sealed record KnowledgeImportRequest(string Application, string IssueType, string Title,
    string Source, string Content, bool ApprovedForPublication, string? SourceUrl = null);
public sealed record KnowledgeDocument(string DocumentId, DateTimeOffset PublishedAtUtc,
    string Application, string IssueType, string Title, string Source, string Content, string? SourceUrl = null);
public sealed record KnowledgeSearchRequest(string Application, string Query, string? IssueType = null, int Limit = 5);
public sealed record KnowledgeHit(string DocumentId, string Title, string Source, string? SourceUrl,
    string Passage, double Score, IReadOnlyList<string> MatchedTerms);
public interface IKnowledgeRepository
{
    Task<IReadOnlyList<KnowledgeDocument>> GetDocumentsAsync(CancellationToken ct = default);
    Task<KnowledgeDocument> ImportAsync(KnowledgeImportRequest request, CancellationToken ct = default);
    Task<bool> DeleteDocumentAsync(string documentId, CancellationToken ct = default);
}
