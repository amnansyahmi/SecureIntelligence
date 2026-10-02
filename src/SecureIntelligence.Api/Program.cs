using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SecureIntelligence.Api.Services;
using SecureIntelligence.Core.Cases;
using SecureIntelligence.Core.Models;
using SecureIntelligence.Core.Rules;
using SecureIntelligence.Core.Security;

var builder = WebApplication.CreateBuilder(args);
var access = new ApiKeyAccess(Environment.GetEnvironmentVariable("SECURE_INTELLIGENCE_API_KEY"),
    Environment.GetEnvironmentVariable("SECURE_INTELLIGENCE_LEARNING_API_KEY"));
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOptions<CaseStoreOptions>().BindConfiguration("CaseStore")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Directory) && o.RetentionDays is >= 1 and <= 3650
        && o.MaxCases is >= 1 and <= 10000, "Invalid case store configuration.").ValidateOnStart();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<ICaseRepository, JsonCaseRepository>();
builder.Services.AddSingleton<CaseMatcher>();
builder.Services.AddSingleton<IIntelligenceRule, MissingSystemizationRule>();
builder.Services.AddSingleton<IIntelligenceRule, ReadyForQuoteWithoutBomRule>();
builder.Services.AddSingleton<IIntelligenceRule, HighDatabaseLatencyRule>();
builder.Services.AddSingleton<RuleEngine>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("internal-api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
});

var app = builder.Build();
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseRouting();
app.UseRateLimiter();
// Middleware authorizes before endpoint binding reads a JSON body.
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api/v1"))
    {
        await next(context);
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (!app.Environment.IsDevelopment() && !context.Request.IsHttps)
    {
        await Results.Problem("HTTPS is required.", statusCode: 400).ExecuteAsync(context);
        return;
    }
    var values = context.Request.Headers["X-Internal-Api-Key"];
    var key = values.Count == 1 ? values[0] : null;
    if (!access.CanDiagnose(key))
    {
        await Results.Unauthorized().ExecuteAsync(context);
        return;
    }
    if (context.GetEndpoint()?.Metadata.GetMetadata<LearningAccessRequired>() is not null)
    {
        if (!access.LearningEnabled)
        {
            await Results.Problem("Learning access is disabled.", statusCode: 503).ExecuteAsync(context);
            return;
        }
        if (!access.CanLearn(key))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    try { await next(context); }
    catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
    {
        // Avoid exception messages, raw request fields, keys and case contents in telemetry.
        app.Logger.LogError("Case storage operation failed. TraceId: {TraceId}", context.TraceIdentifier);
        await Results.Problem("Case storage is unavailable; no learning change was confirmed.", statusCode: 503)
            .ExecuteAsync(context);
    }
    finally
    {
        app.Logger.LogInformation("API request {Method} {StatusCode} TraceId: {TraceId}",
            context.Request.Method, context.Response.StatusCode, context.TraceIdentifier);
    }
});

app.MapGet("/health", () => Results.Ok(new { ok = true, mode = "rules-plus-case-based-learning", llm = false, gpuRequired = false }));
var api = app.MapGroup("/api/v1").RequireRateLimiting("internal-api");
api.MapGet("/capabilities", () => Results.Ok(new
{
    learningEnabled = access.LearningEnabled, learningApplications = LearningPolicy.Applications,
    learningIssueTypes = LearningPolicy.IssueTypes, learningSignalKeys = SignalSchema.AllowedKeys,
    similarityIsProbability = false
}));

api.MapPost("/diagnose", async (DiagnosticRequest? request, RuleEngine rules, CaseMatcher matcher, CancellationToken ct) =>
{
    var errors = RequestGuard.Validate(request);
    if (errors.Count > 0) return Validation(errors);
    var findings = rules.Evaluate(request!);
    var similarCases = await matcher.FindSimilarAsync(request!, cancellationToken: ct);
    return Results.Ok(new DiagnosticResponse(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, findings, similarCases));
});

api.MapPost("/feedback", async (FeedbackRequest? feedback, ICaseRepository cases, CancellationToken ct) =>
{
    var errors = RequestGuard.Validate(feedback?.Request);
    if (errors.Count > 0) return Validation(errors);
    if (!feedback!.ApprovedForLearning)
        return Results.Accepted(value: new { learned = false, reason = "Human approval was not provided." });
    errors = LearningPolicy.Validate(feedback);
    if (errors.Count > 0) return Validation(errors);
    var record = await cases.AddAsync(LearningPolicy.CreateRecord(feedback, DateTimeOffset.UtcNow), ct);
    return Results.Created($"/api/v1/cases/{record.CaseId}", new { learned = true, record.CaseId });
}).WithMetadata(new LearningAccessRequired());

var management = api.MapGroup("/cases").WithMetadata(new LearningAccessRequired());
management.MapGet("/", async (ICaseRepository cases, CancellationToken ct) =>
    Results.Ok((await cases.GetAllAsync(ct)).Select(c => new { c.CaseId, c.CreatedAtUtc, c.Application, c.IssueType })));
management.MapGet("/{caseId}", async (string caseId, ICaseRepository cases, CancellationToken ct) =>
{
    var record = (await cases.GetAllAsync(ct)).FirstOrDefault(c => c.CaseId == caseId);
    return record is null ? Results.NotFound() : Results.Ok(record);
});
management.MapDelete("/{caseId}", async (string caseId, ICaseRepository cases, CancellationToken ct) =>
    await cases.DeleteAsync(caseId, ct) ? Results.NoContent() : Results.NotFound());

app.Run();

static IResult Validation(IReadOnlyList<string> errors) =>
    Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = errors.ToArray() });
