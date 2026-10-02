using System.Text.RegularExpressions;
using SecureIntelligence.Core.Cases;
using SecureIntelligence.Core.Models;

namespace SecureIntelligence.Core.Security;

public static partial class LearningPolicy
{
    public static readonly IReadOnlyList<string> Applications = Array.AsReadOnly(new[] { "LineDesigner", "MDIX" });
    public static readonly IReadOnlyList<string> IssueTypes = Array.AsReadOnly(new[] { "ReadyForQuote", "Performance" });

    public static IReadOnlyList<string> Validate(FeedbackRequest? feedback)
    {
        var errors = RequestGuard.Validate(feedback?.Request).ToList();
        if (feedback?.Request is not { } request)
            return errors;
        if (!Applications.Contains(request.Application, StringComparer.OrdinalIgnoreCase))
            errors.Add("Application is not enabled for learning.");
        if (!IssueTypes.Contains(request.IssueType, StringComparer.OrdinalIgnoreCase))
            errors.Add("Issue type is not enabled for learning.");
        if (request.Signals is not { Count: > 0 })
            errors.Add("At least one approved diagnostic signal is required for learning.");
        else if (request.Signals.Keys.Any(k => !SignalSchema.IsKnown(k)))
            errors.Add("Learning accepts only allow-listed diagnostic signals.");
        ValidateReviewedText(feedback.ConfirmedCause, "confirmedCause", 2_000, errors);
        ValidateReviewedText(feedback.Resolution, "resolution", 4_000, errors);
        return errors;
    }

    public static CaseRecord CreateRecord(FeedbackRequest feedback, DateTimeOffset now)
    {
        var errors = Validate(feedback);
        if (errors.Count > 0 || !feedback.ApprovedForLearning)
            throw new ArgumentException("Validated human approval is required.", nameof(feedback));
        var request = feedback.Request;
        var signals = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in request.Signals!)
        {
            SignalSchema.TryNormalize(key, value, out var normalized);
            signals[key] = normalized;
        }
        return new CaseRecord(Guid.NewGuid().ToString("N"), now,
            Applications.Single(a => a.Equals(request.Application, StringComparison.OrdinalIgnoreCase)),
            IssueTypes.Single(i => i.Equals(request.IssueType, StringComparison.OrdinalIgnoreCase)),
            "Description omitted by learning policy.", signals,
            feedback.ConfirmedCause.Trim(), feedback.Resolution.Trim());
    }

    // This catches common accidental disclosures, not every form of personal data.
    // The learning operator must still review both text fields before submission.
    public static void ValidateReviewedText(string? value, string field, int limit, List<string> errors, bool allowMultiline = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit)
            errors.Add($"{field} is required and cannot exceed {limit} characters.");
        else if (SensitiveText().IsMatch(value) || value.Any(c => char.IsControl(c) && !(allowMultiline && (c == '\n' || c == '\r' || c == '\t'))))
            errors.Add($"{field} contains potentially sensitive or unsupported content; submit reviewed generic text.");
    }

    [GeneratedRegex(@"(?i)(\b(password|passwd|pwd|token|secret|api[_-]?key|connectionstring)\s*[:=]|\bbearer\s+\S+|[\w.+-]+@[\w.-]+\.[a-z]{2,}|\b\d{6}-\d{2}-\d{4}\b)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveText();
}
