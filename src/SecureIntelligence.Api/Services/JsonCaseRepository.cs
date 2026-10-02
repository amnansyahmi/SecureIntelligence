using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SecureIntelligence.Core.Cases;
using SecureIntelligence.Core.Knowledge;
using SecureIntelligence.Core.Models;
using SecureIntelligence.Core.Security;
using SecureIntelligence.Core.Workbench;

namespace SecureIntelligence.Api.Services;

public sealed class ReviewConflictException(string message) : Exception(message);

// One process, one atomic state file: approving a proposal and publishing its case commit together.
public sealed class JsonCaseRepository : ICaseRepository, IReviewRepository, IKnowledgeRepository
{
    private readonly string _path;
    private readonly CaseStoreOptions _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };

    public JsonCaseRepository(IWebHostEnvironment environment, IOptions<CaseStoreOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
        var directory = Path.GetFullPath(_options.Directory, environment.ContentRootPath);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "validated-cases.json");
    }

    public Task<IReadOnlyList<CaseRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync<IReadOnlyList<CaseRecord>>(s => (s.Cases.ToArray(), false), cancellationToken);

    public Task<CaseRecord> AddAsync(CaseRecord record, CancellationToken cancellationToken = default)
    {
        ValidateCase(record);
        return ExecuteAsync(s =>
        {
            var existing = s.Cases.FirstOrDefault(c => Equivalent(c, record));
            if (existing is not null) return (existing, false);
            s.Cases.Add(record);
            return (record, true);
        }, cancellationToken);
    }

    public Task<bool> DeleteAsync(string caseId, CancellationToken cancellationToken = default) => ExecuteAsync(s =>
    {
        var removed = s.Cases.RemoveAll(c => c.CaseId == caseId) > 0;
        if (removed)
        {
            s.Outcomes.RemoveAll(o => o.CaseId == caseId);
            s.Proposals.RemoveAll(p => p.ActiveCaseId == caseId);
        }
        return (removed, removed);
    }, cancellationToken);

    public Task<IReadOnlyList<CaseOutcomeSummary>> GetOutcomeSummariesAsync(CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<CaseOutcomeSummary>>(s => (s.Outcomes.GroupBy(o => o.CaseId)
            .Select(g => new CaseOutcomeSummary(g.Key, g.Count(o => o.Resolved), g.Count(o => !o.Resolved))).ToArray(), false), ct);

    public Task<IReadOnlyList<LearningProposal>> GetProposalsAsync(CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<LearningProposal>>(s => (s.Proposals.OrderByDescending(p => p.CreatedAtUtc).ToArray(), false), ct);

    public Task<LearningProposal> ProposeAsync(CaseRecord record, CancellationToken ct = default)
    {
        ValidateCase(record);
        return ExecuteAsync(s =>
        {
            var existing = s.Proposals.FirstOrDefault(p => p.Status == ProposalStatus.Pending && Equivalent(p.Case, record));
            if (existing is not null) return (existing, false);
            if (s.Proposals.Count >= _options.MaxProposals) throw new ReviewConflictException("The review queue is full; remove obsolete review records before submitting more proposals.");
            var proposal = new LearningProposal(record.CaseId, record.CreatedAtUtc, record);
            s.Proposals.Add(proposal);
            return (proposal, true);
        }, ct);
    }

    public Task<LearningProposal?> ApproveAsync(string proposalId, ProposalReviewRequest review, CancellationToken ct = default) =>
        ExecuteAsync<LearningProposal?>(s =>
        {
            var index = s.Proposals.FindIndex(p => p.ProposalId == proposalId);
            if (index < 0) return (null, false);
            var proposal = s.Proposals[index];
            if (!review.ApprovedForLearning) throw new ReviewConflictException("Explicit human approval is required.");
            if (proposal.Status == ProposalStatus.Approved) return (proposal, false);
            if (proposal.Status != ProposalStatus.Pending) throw new ReviewConflictException("A rejected proposal cannot be approved; submit a corrected proposal.");
            var record = proposal.Case with
            {
                CreatedAtUtc = _clock.GetUtcNow(),
                ConfirmedCause = review.ConfirmedCause?.Trim() ?? proposal.Case.ConfirmedCause,
                Resolution = review.Resolution?.Trim() ?? proposal.Case.Resolution
            };
            ValidateCase(record);
            var active = s.Cases.FirstOrDefault(c => Equivalent(c, record));
            if (active is null) { s.Cases.Add(record); active = record; }
            var approved = proposal with { Case = record, Status = ProposalStatus.Approved,
                ReviewedAtUtc = _clock.GetUtcNow(), ActiveCaseId = active.CaseId };
            s.Proposals[index] = approved;
            return (approved, true);
        }, ct);

    public Task<LearningProposal?> RejectAsync(string proposalId, RejectionReason reason, CancellationToken ct = default) =>
        ExecuteAsync<LearningProposal?>(s =>
        {
            var index = s.Proposals.FindIndex(p => p.ProposalId == proposalId);
            if (index < 0) return (null, false);
            var proposal = s.Proposals[index];
            if (proposal.Status == ProposalStatus.Rejected) return (proposal, false);
            if (proposal.Status != ProposalStatus.Pending) throw new ReviewConflictException("An approved proposal cannot be rejected; remove its active case if necessary.");
            var rejected = proposal with { Status = ProposalStatus.Rejected, ReviewedAtUtc = _clock.GetUtcNow(), RejectionReason = reason };
            s.Proposals[index] = rejected;
            return (rejected, true);
        }, ct);

    public Task<bool> DeleteProposalAsync(string proposalId, CancellationToken ct = default) => ExecuteAsync(s =>
    {
        var removed = s.Proposals.RemoveAll(p => p.ProposalId == proposalId) > 0;
        return (removed, removed); // Removing history does not unpublish an approved case.
    }, ct);

    public Task<bool> RecordOutcomeAsync(string caseId, OutcomeRequest outcome, CancellationToken ct = default) => ExecuteAsync(s =>
    {
        if (!s.Cases.Any(c => c.CaseId == caseId)) return (false, false);
        if (!outcome.Verified || !Guid.TryParseExact(outcome.IncidentId, "N", out var incident) || incident == Guid.Empty)
            throw new ReviewConflictException("A verified outcome with a valid incident ID is required.");
        var canonicalId = incident.ToString("N");
        var index = s.Outcomes.FindIndex(o => o.CaseId == caseId && o.IncidentId == canonicalId);
        var recorded = new CaseOutcome(caseId, canonicalId, outcome.Resolved, _clock.GetUtcNow());
        if (index >= 0)
        {
            if (s.Outcomes[index].Resolved == outcome.Resolved) return (true, false);
            s.Outcomes[index] = recorded; // Correction, not another vote for the same incident.
        }
        else
        {
            if (s.Outcomes.Count >= 10_000) throw new ReviewConflictException("Outcome capacity has been reached.");
            s.Outcomes.Add(recorded);
        }
        return (true, true);
    }, ct);

    public Task<IReadOnlyList<KnowledgeDocument>> GetDocumentsAsync(CancellationToken ct = default) =>
        ExecuteAsync<IReadOnlyList<KnowledgeDocument>>(s => (s.Documents.OrderByDescending(d => d.PublishedAtUtc).ToArray(), false), ct);

    public Task<KnowledgeDocument> ImportAsync(KnowledgeImportRequest request, CancellationToken ct = default)
    {
        if (KnowledgePolicy.Validate(request).Count > 0) throw new ArgumentException("Invalid reviewed document.", nameof(request));
        return ExecuteAsync(s =>
        {
            var application = LearningPolicy.Applications.Single(a => a.Equals(request.Application, StringComparison.OrdinalIgnoreCase));
            var issue = request.IssueType == "General" ? "General" : LearningPolicy.IssueTypes.Single(i => i.Equals(request.IssueType, StringComparison.OrdinalIgnoreCase));
            var existing = s.Documents.FirstOrDefault(d => d.Application == application && d.IssueType == issue
                && d.Title == request.Title.Trim() && d.Source == request.Source.Trim()
                && d.Content == request.Content.Trim() && d.SourceUrl == request.SourceUrl);
            if (existing is not null) return (existing, false);
            if (s.Documents.Count >= _options.MaxDocuments) throw new ReviewConflictException("Document capacity has been reached; remove obsolete guides.");
            var document = new KnowledgeDocument(Guid.NewGuid().ToString("N"), _clock.GetUtcNow(), application,
                issue, request.Title.Trim(), request.Source.Trim(), request.Content.Trim(), request.SourceUrl);
            s.Documents.Add(document);
            return (document, true);
        }, ct);
    }

    public Task<bool> DeleteDocumentAsync(string documentId, CancellationToken ct = default) =>
        ExecuteAsync(s => { var removed = s.Documents.RemoveAll(d => d.DocumentId == documentId) > 0; return (removed, removed); }, ct);

    private async Task<T> ExecuteAsync<T>(Func<StoreState, (T Value, bool Changed)> operation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await ReadAsync(ct);
            var pruned = Prune(state);
            var (value, changed) = operation(state);
            pruned |= Prune(state);
            if (changed || pruned) await WriteAsync(state, ct);
            return value;
        }
        finally { _gate.Release(); }
    }

    private async Task<StoreState> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new StoreState();
        await using var stream = File.OpenRead(_path);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        // Accept only already-sanitized arrays from the previous release; raw starter cases still fail closed.
        if (json.RootElement.ValueKind != JsonValueKind.Array && (json.RootElement.ValueKind != JsonValueKind.Object
            || !json.RootElement.TryGetProperty("version", out _) || !json.RootElement.TryGetProperty("cases", out _)
            || !json.RootElement.TryGetProperty("proposals", out _) || !json.RootElement.TryGetProperty("documents", out _)
            || !json.RootElement.TryGetProperty("outcomes", out _)))
            throw new JsonException("State envelope is incomplete.");
        var state = json.RootElement.ValueKind == JsonValueKind.Array
            ? new StoreState { Cases = json.RootElement.Deserialize<List<CaseRecord>>(_json) ?? throw new JsonException("Invalid case array.") }
            : json.RootElement.Deserialize<StoreState>(_json) ?? throw new JsonException("Invalid store.");
        ValidateState(state);
        return state;
    }

    private static void ValidateState(StoreState state)
    {
        if (state.Version != 1 || state.Cases is null || state.Proposals is null || state.Documents is null || state.Outcomes is null)
            throw new JsonException("Invalid state version or collections.");
        foreach (var record in state.Cases) ValidateCase(record);
        foreach (var proposal in state.Proposals)
        {
            if (proposal is null || !Guid.TryParseExact(proposal.ProposalId, "N", out _)
                || proposal.CreatedAtUtc == default || !Enum.IsDefined(proposal.Status)
                || (proposal.Status != ProposalStatus.Pending && proposal.ReviewedAtUtc is null)
                || (proposal.Status == ProposalStatus.Approved && !state.Cases.Any(c => c.CaseId == proposal.ActiveCaseId))
                || (proposal.Status == ProposalStatus.Rejected && (proposal.RejectionReason is null || !Enum.IsDefined(proposal.RejectionReason.Value))))
                throw new JsonException("Invalid proposal.");
            ValidateCase(proposal.Case);
        }
        foreach (var document in state.Documents)
            if (document is null || !Guid.TryParseExact(document.DocumentId, "N", out _) || document.PublishedAtUtc == default
                || KnowledgePolicy.Validate(new(document.Application, document.IssueType, document.Title,
                    document.Source, document.Content, true, document.SourceUrl)).Count > 0)
                throw new JsonException("Invalid document.");
        foreach (var outcome in state.Outcomes)
            if (outcome is null || !Guid.TryParseExact(outcome.IncidentId, "N", out var incident) || incident == Guid.Empty
                || outcome.VerifiedAtUtc == default || !state.Cases.Any(c => c.CaseId == outcome.CaseId))
                throw new JsonException("Invalid outcome.");
        if (state.Cases.Select(c => c.CaseId).Distinct().Count() != state.Cases.Count
            || state.Proposals.Select(p => p.ProposalId).Distinct().Count() != state.Proposals.Count
            || state.Documents.Select(d => d.DocumentId).Distinct().Count() != state.Documents.Count
            || state.Outcomes.Select(o => (o.CaseId, o.IncidentId)).Distinct().Count() != state.Outcomes.Count)
            throw new JsonException("Duplicate store identities.");
    }

    private static void ValidateCase(CaseRecord? record)
    {
        if (record is null || !Guid.TryParseExact(record.CaseId, "N", out _) || record.CreatedAtUtc == default
            || record.Description != "Description omitted by learning policy.")
            throw new JsonException("Invalid or legacy case; reviewed migration is required.");
        if (LearningPolicy.Validate(new FeedbackRequest(new(record.Application, record.IssueType,
            record.Description, record.Signals), record.ConfirmedCause, record.Resolution, true)).Count > 0)
            throw new JsonException("Case violates learning policy.");
    }

    private bool Prune(StoreState state)
    {
        var count = state.Cases.Count + state.Proposals.Count + state.Outcomes.Count;
        var cutoff = _clock.GetUtcNow().AddDays(-_options.RetentionDays);
        state.Cases = state.Cases.Where(c => c.CreatedAtUtc > cutoff).OrderByDescending(c => c.CreatedAtUtc)
            .ThenBy(c => c.CaseId, StringComparer.Ordinal).Take(_options.MaxCases).ToList();
        var ids = state.Cases.Select(c => c.CaseId).ToHashSet(StringComparer.Ordinal);
        state.Proposals.RemoveAll(p => p.CreatedAtUtc <= cutoff || (p.Status == ProposalStatus.Approved && !ids.Contains(p.ActiveCaseId!)));
        state.Outcomes.RemoveAll(o => o.VerifiedAtUtc <= cutoff || !ids.Contains(o.CaseId));
        return count != state.Cases.Count + state.Proposals.Count + state.Outcomes.Count;
    }
    private static bool Equivalent(CaseRecord a, CaseRecord b) => a.Application == b.Application
        && a.IssueType == b.IssueType && a.ConfirmedCause == b.ConfirmedCause && a.Resolution == b.Resolution
        && a.Signals.Count == b.Signals.Count && a.Signals.All(s => b.Signals.TryGetValue(s.Key, out var value) && s.Value == value);

    private async Task WriteAsync(StoreState state, CancellationToken ct)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, state, _json, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class StoreState
    {
        public StoreState() { }
        public int Version { get; set; } = 1;
        public List<CaseRecord> Cases { get; set; } = new();
        public List<LearningProposal> Proposals { get; set; } = new();
        public List<KnowledgeDocument> Documents { get; set; } = new();
        public List<CaseOutcome> Outcomes { get; set; } = new();
    }
}
