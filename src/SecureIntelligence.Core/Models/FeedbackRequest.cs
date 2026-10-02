namespace SecureIntelligence.Core.Models;

public sealed record FeedbackRequest(
    DiagnosticRequest Request,
    string ConfirmedCause,
    string Resolution,
    bool ApprovedForLearning);
