using SecureIntelligence.Core.Models;
using SecureIntelligence.Core.Workbench;

namespace SecureIntelligence.Core.Cases;

public sealed record CaseRecord(
    string CaseId,
    DateTimeOffset CreatedAtUtc,
    string Application,
    string IssueType,
    string Description,
    IReadOnlyDictionary<string, string> Signals,
    string ConfirmedCause,
    string Resolution);

public interface ICaseRepository
{
    Task<IReadOnlyList<CaseRecord>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<CaseRecord> AddAsync(CaseRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CaseOutcomeSummary>> GetOutcomeSummariesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CaseOutcomeSummary>>(Array.Empty<CaseOutcomeSummary>());
    Task<bool> DeleteAsync(string caseId, CancellationToken cancellationToken = default);
}
