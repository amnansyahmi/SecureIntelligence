using SecureIntelligence.Core.Security;

namespace SecureIntelligence.Core.Knowledge;

public static class KnowledgePolicy
{
    public static IReadOnlyList<string> Validate(KnowledgeImportRequest? request)
    {
        var errors = new List<string>();
        if (request is null) return new[] { "A document is required." };
        if (!LearningPolicy.Applications.Contains(request.Application, StringComparer.OrdinalIgnoreCase))
            errors.Add("Application is not enabled for knowledge publication.");
        if (request.IssueType != "General" && !LearningPolicy.IssueTypes.Contains(request.IssueType, StringComparer.OrdinalIgnoreCase))
            errors.Add("Issue type is not enabled for knowledge publication.");
        LearningPolicy.ValidateReviewedText(request.Title, "title", 150, errors);
        LearningPolicy.ValidateReviewedText(request.Source, "source", 300, errors);
        LearningPolicy.ValidateReviewedText(request.Content, "content", 20_000, errors, allowMultiline: true);
        if (!request.ApprovedForPublication) errors.Add("Explicit publication approval is required.");
        if (!string.IsNullOrEmpty(request.SourceUrl) && (request.SourceUrl.Length > 500
            || !Uri.TryCreate(request.SourceUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0))
            errors.Add("Source URLs must be HTTPS references without credentials, queries or fragments.");
        return errors;
    }

    public static IReadOnlyList<string> ValidateSearch(KnowledgeSearchRequest? request)
    {
        var errors = new List<string>();
        if (request is null) return new[] { "A search request is required." };
        if (!LearningPolicy.Applications.Contains(request.Application, StringComparer.OrdinalIgnoreCase))
            errors.Add("Application is not enabled for knowledge search.");
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 1000)
            errors.Add("query is required and cannot exceed 1000 characters.");
        if (request.IssueType is not null && request.IssueType != "General"
            && !LearningPolicy.IssueTypes.Contains(request.IssueType, StringComparer.OrdinalIgnoreCase))
            errors.Add("Issue type is not enabled for knowledge search.");
        if (request.Limit is < 1 or > 10) errors.Add("limit must be between 1 and 10.");
        return errors;
    }
}
