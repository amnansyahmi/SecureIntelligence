using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using SecureIntelligence.Api.Services;
using SecureIntelligence.Api.Endpoints;
using SecureIntelligence.Core.Knowledge;
using SecureIntelligence.Core.Workbench;
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
        && o.MaxCases is >= 1 and <= 10000 && o.MaxDocuments is >= 1 and <= 1000
        && o.MaxProposals is >= 1 and <= 10000, "Invalid case store configuration.").ValidateOnStart();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<JsonCaseRepository>();
builder.Services.AddSingleton<ICaseRepository>(sp => sp.GetRequiredService<JsonCaseRepository>());
builder.Services.AddSingleton<IReviewRepository>(sp => sp.GetRequiredService<JsonCaseRepository>());
builder.Services.AddSingleton<IKnowledgeRepository>(sp => sp.GetRequiredService<JsonCaseRepository>());
builder.Services.AddSingleton<KnowledgeSearch>();
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
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    if (!app.Environment.IsDevelopment() && !context.Request.IsHttps && context.Request.Path != "/health")
    {
        await Results.Problem("HTTPS is required.", statusCode: 400).ExecuteAsync(context);
        return;
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-store" });
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
    if (!access.LearningEnabled && context.GetEndpoint()?.Metadata.GetMetadata<LearningFeatureRequired>() is not null)
    {
        await Results.Problem("Learning access is disabled.", statusCode: 503).ExecuteAsync(context);
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
    catch (BadHttpRequestException exception)
    {
        await Results.Problem("The request body is invalid or exceeds the permitted size.", statusCode: exception.StatusCode)
            .ExecuteAsync(context);
    }
    catch (ReviewConflictException exception)
    {
        await Results.Problem(exception.Message, statusCode: 409).ExecuteAsync(context);
    }
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
api.MapGet("/capabilities", (HttpContext context) => Results.Ok(new
{
    learningEnabled = access.LearningEnabled, learningApplications = LearningPolicy.Applications,
    learningIssueTypes = LearningPolicy.IssueTypes, learningSignalKeys = SignalSchema.AllowedKeys,
    accessLevel = access.CanLearn(context.Request.Headers["X-Internal-Api-Key"].ToString()) ? "reviewer" : "diagnosis",
    knowledgeSearch = "keyword-bm25", reviewQueue = true, outcomeTracking = true,
    similarityIsProbability = false
}));

api.MapPost("/diagnose", async (DiagnosticRequest? request, RuleEngine rules, CaseMatcher matcher, KnowledgeSearch search, CancellationToken ct) =>
{
    var errors = RequestGuard.Validate(request);
    if (errors.Count > 0) return Validation(errors);
    var findings = rules.Evaluate(request!);
    var similarCases = await matcher.FindSimilarAsync(request!, cancellationToken: ct);
    var terms = string.Join(' ', new[] { request!.IssueType }.Concat(findings.Select(f => f.Summary)).Append(request.Description));
    var knowledge = LearningPolicy.Applications.Contains(request.Application, StringComparer.OrdinalIgnoreCase)
        ? await search.SearchAsync(new KnowledgeSearchRequest(request.Application, terms[..Math.Min(1000, terms.Length)], request.IssueType), ct)
        : Array.Empty<KnowledgeHit>();
    return Results.Ok(new DiagnosticResponse(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, findings, similarCases, knowledge));
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
{
    var records = await cases.GetAllAsync(ct);
    var outcomes = (await cases.GetOutcomeSummariesAsync(ct)).ToDictionary(o => o.CaseId);
    return Results.Ok(records.Select(c => new { c.CaseId, c.CreatedAtUtc, c.Application, c.IssueType,
        c.ConfirmedCause, c.Resolution, verifiedSuccesses = outcomes.GetValueOrDefault(c.CaseId)?.VerifiedSuccesses ?? 0,
        verifiedFailures = outcomes.GetValueOrDefault(c.CaseId)?.VerifiedFailures ?? 0 }));
});
management.MapGet("/{caseId}", async (string caseId, ICaseRepository cases, CancellationToken ct) =>
{
    var record = (await cases.GetAllAsync(ct)).FirstOrDefault(c => c.CaseId == caseId);
    return record is null ? Results.NotFound() : Results.Ok(record);
});
management.MapDelete("/{caseId}", async (string caseId, ICaseRepository cases, CancellationToken ct) =>
    await cases.DeleteAsync(caseId, ct) ? Results.NoContent() : Results.NotFound());

api.MapWorkbenchEndpoints();

app.Run();

static IResult Validation(IReadOnlyList<string> errors) =>
    Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = errors.ToArray() });
