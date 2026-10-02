using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using SecureIntelligence.Api.Services;
using SecureIntelligence.Core.Cases;
using SecureIntelligence.Core.Models;
using SecureIntelligence.Core.Rules;
using SecureIntelligence.Core.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ICaseRepository, JsonCaseRepository>();
builder.Services.AddSingleton<CaseMatcher>();
builder.Services.AddSingleton<IIntelligenceRule, MissingSystemizationRule>();
builder.Services.AddSingleton<IIntelligenceRule, ReadyForQuoteWithoutBomRule>();
builder.Services.AddSingleton<IIntelligenceRule, HighDatabaseLatencyRule>();
builder.Services.AddSingleton<RuleEngine>();

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("internal-api", limiter =>
    {
        limiter.PermitLimit = 60;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    mode = "rules-plus-case-based-learning",
    llm = false,
    gpuRequired = false
}));

var api = app.MapGroup("/api/v1")
    .RequireRateLimiting("internal-api")
    .AddEndpointFilter(async (context, next) =>
    {
        var expected = Environment.GetEnvironmentVariable("SECURE_INTELLIGENCE_API_KEY");
        if (string.IsNullOrWhiteSpace(expected))
            return Results.Problem("SECURE_INTELLIGENCE_API_KEY is not configured.", statusCode: 503);

        var httpContext = context.HttpContext;
        if (!httpContext.Request.Headers.TryGetValue("X-Internal-Api-Key", out var provided))
            return Results.Unauthorized();

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided.ToString());
        if (expectedBytes.Length != providedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    });

api.MapPost("/diagnose", async (
    DiagnosticRequest request,
    RuleEngine rules,
    CaseMatcher matcher,
    CancellationToken cancellationToken) =>
{
    var errors = RequestGuard.Validate(request);
    if (errors.Count > 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = errors.ToArray() });

    var requestId = Guid.NewGuid().ToString("N");
    var findings = rules.Evaluate(request);
    var similarCases = await matcher.FindSimilarAsync(request, cancellationToken: cancellationToken);

    return Results.Ok(new DiagnosticResponse(
        requestId,
        DateTimeOffset.UtcNow,
        findings,
        similarCases));
});

api.MapPost("/feedback", async (
    FeedbackRequest feedback,
    ICaseRepository cases,
    CancellationToken cancellationToken) =>
{
    var errors = RequestGuard.Validate(feedback.Request);
    if (errors.Count > 0)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = errors.ToArray() });

    if (!feedback.ApprovedForLearning)
        return Results.Accepted(value: new { learned = false, reason = "Human approval was not provided." });

    if (string.IsNullOrWhiteSpace(feedback.ConfirmedCause) || feedback.ConfirmedCause.Length > 2_000 ||
        string.IsNullOrWhiteSpace(feedback.Resolution) || feedback.Resolution.Length > 4_000)
    {
        return Results.BadRequest(new { error = "confirmedCause and resolution are required and must be within limits." });
    }

    // Production note: add application-specific field allow-lists/redaction before persistence.
    var record = new CaseRecord(
        Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow,
        feedback.Request.Application,
        feedback.Request.IssueType,
        feedback.Request.Description,
        feedback.Request.Signals ?? new Dictionary<string, string>(),
        feedback.ConfirmedCause,
        feedback.Resolution);

    await cases.AddAsync(record, cancellationToken);
    return Results.Created($"/api/v1/cases/{record.CaseId}", new { learned = true, record.CaseId });
});

app.Run();
