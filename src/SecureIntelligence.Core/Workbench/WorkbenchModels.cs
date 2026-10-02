using SecureIntelligence.Core.Cases;
using SecureIntelligence.Core.Models;

namespace SecureIntelligence.Core.Workbench;

public enum ProposalStatus { Pending, Approved, Rejected }
public enum RejectionReason { InsufficientEvidence, IncorrectCause, Duplicate, Other }
public sealed record ProposalRequest(DiagnosticRequest Request, string ConfirmedCause, string Resolution);
public sealed record ProposalReviewRequest(bool ApprovedForLearning, string? ConfirmedCause = null, string? Resolution = null);
public sealed record ProposalRejectionRequest(RejectionReason Reason);
public sealed record LearningProposal(string ProposalId, DateTimeOffset CreatedAtUtc, CaseRecord Case,
    ProposalStatus Status = ProposalStatus.Pending, DateTimeOffset? ReviewedAtUtc = null,
    RejectionReason? RejectionReason = null, string? ActiveCaseId = null);
public sealed record OutcomeRequest(string IncidentId, bool Resolved, bool Verified);
public sealed record CaseOutcome(string CaseId, string IncidentId, bool Resolved, DateTimeOffset VerifiedAtUtc);
public sealed record CaseOutcomeSummary(string CaseId, int VerifiedSuccesses, int VerifiedFailures);
public interface IReviewRepository
{
    Task<IReadOnlyList<LearningProposal>> GetProposalsAsync(CancellationToken ct = default);
    Task<LearningProposal> ProposeAsync(CaseRecord record, CancellationToken ct = default);
    Task<LearningProposal?> ApproveAsync(string proposalId, ProposalReviewRequest review, CancellationToken ct = default);
    Task<LearningProposal?> RejectAsync(string proposalId, RejectionReason reason, CancellationToken ct = default);
    Task<bool> DeleteProposalAsync(string proposalId, CancellationToken ct = default);
    Task<bool> RecordOutcomeAsync(string caseId, OutcomeRequest outcome, CancellationToken ct = default);
}
