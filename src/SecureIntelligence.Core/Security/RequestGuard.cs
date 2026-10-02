using SecureIntelligence.Core.Models;

namespace SecureIntelligence.Core.Security;

public static class RequestGuard
{
    private const int MaxApplicationLength = 100;
    private const int MaxIssueTypeLength = 150;
    private const int MaxDescriptionLength = 4_000;
    private const int MaxSignalCount = 100;
    private const int MaxSignalKeyLength = 100;
    private const int MaxSignalValueLength = 1_000;

    public static IReadOnlyList<string> Validate(DiagnosticRequest? request)
    {
        var errors = new List<string>();
        if (request is null)
        {
            errors.Add("Request body is required.");
            return errors;
        }

        ValidateText(request.Application, "application", MaxApplicationLength, errors);
        ValidateText(request.IssueType, "issueType", MaxIssueTypeLength, errors);
        ValidateText(request.Description, "description", MaxDescriptionLength, errors);

        if (request.Signals is { Count: > MaxSignalCount })
            errors.Add($"signals cannot contain more than {MaxSignalCount} entries.");

        if (request.Signals is not null)
        {
            foreach (var (key, value) in request.Signals)
            {
                ValidateText(key, "signal key", MaxSignalKeyLength, errors);
                ValidateText(value, "signal value", MaxSignalValueLength, errors);
                if (SignalSchema.IsKnown(key) && !SignalSchema.TryNormalize(key, value, out _))
                    errors.Add("A known signal has an invalid value; booleans and nonnegative finite numbers are required.");
            }
        }

        return errors;
    }

    private static void ValidateText(string? value, string field, int maxLength, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{field} is required.");
        else if (value.Length > maxLength)
            errors.Add($"{field} cannot exceed {maxLength} characters.");
    }
}
