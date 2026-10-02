using SecureIntelligence.Api.Services;
using SecureIntelligence.Core.Cases;
using SecureIntelligence.Core.Knowledge;
using SecureIntelligence.Core.Models;
using SecureIntelligence.Core.Security;
using SecureIntelligence.Core.Workbench;

namespace SecureIntelligence.Api.Endpoints;

public static class WorkbenchEndpoints
{
    public static void MapWorkbenchEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/proposals", async (ProposalRequest? input, IReviewRepository reviews, CancellationToken ct) =>
        {
            var feedback = input is null ? null : new FeedbackRequest(input.Request, input.ConfirmedCause, input.Resolution, true);
            var errors = LearningPolicy.Validate(feedback);
            if (errors.Count > 0) return Validation(errors);
            var proposal = await reviews.ProposeAsync(LearningPolicy.CreateRecord(feedback!, DateTimeOffset.UtcNow), ct);
            return Results.Accepted($"/api/v1/proposals/{proposal.ProposalId}",
                new { queued = true, learned = false, proposal.ProposalId, proposal.Status });
        }).WithMetadata(new LearningFeatureRequired());

        var review = api.MapGroup("/proposals").WithMetadata(new LearningAccessRequired());
        review.MapGet("/", async (IReviewRepository repository, CancellationToken ct) => Results.Ok(await repository.GetProposalsAsync(ct)));
        review.MapGet("/{id}", async (string id, IReviewRepository repository, CancellationToken ct) =>
        {
            var proposal = (await repository.GetProposalsAsync(ct)).FirstOrDefault(p => p.ProposalId == id);
            return proposal is null ? Results.NotFound() : Results.Ok(proposal);
        });
        review.MapDelete("/{id}", async (string id, IReviewRepository repository, CancellationToken ct) =>
            await repository.DeleteProposalAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        review.MapPost("/{id}/approve", async (string id, ProposalReviewRequest? input, IReviewRepository repository, CancellationToken ct) =>
        {
            var errors = new List<string>();
            if (input is null || !input.ApprovedForLearning) errors.Add("Explicit human approval is required.");
            if (input?.ConfirmedCause is not null) LearningPolicy.ValidateReviewedText(input.ConfirmedCause, "confirmedCause", 2000, errors);
            if (input?.Resolution is not null) LearningPolicy.ValidateReviewedText(input.Resolution, "resolution", 4000, errors);
            if (errors.Count > 0) return Validation(errors);
            var approved = await repository.ApproveAsync(id, input!, ct);
            return approved is null ? Results.NotFound() : Results.Ok(approved);
        });
        review.MapPost("/{id}/reject", async (string id, ProposalRejectionRequest? input, IReviewRepository repository, CancellationToken ct) =>
        {
            if (input is null || !Enum.IsDefined(input.Reason)) return Validation(new[] { "A supported rejection reason is required." });
            var rejected = await repository.RejectAsync(id, input.Reason, ct);
            return rejected is null ? Results.NotFound() : Results.Ok(rejected);
        });

        api.MapPost("/cases/{caseId}/outcomes", async (string caseId, OutcomeRequest? input, IReviewRepository repository, CancellationToken ct) =>
        {
            if (input is null || !input.Verified || !Guid.TryParseExact(input.IncidentId, "N", out var incident) || incident == Guid.Empty)
                return Validation(new[] { "A verified outcome with a valid incident ID is required." });
            return await repository.RecordOutcomeAsync(caseId, input, ct) ? Results.Ok(new { recorded = true }) : Results.NotFound();
        }).WithMetadata(new LearningAccessRequired());

        var knowledge = api.MapGroup("/knowledge");
        knowledge.MapPost("/search", async (KnowledgeSearchRequest? input, KnowledgeSearch search, CancellationToken ct) =>
        {
            var errors = KnowledgePolicy.ValidateSearch(input);
            return errors.Count > 0 ? Validation(errors) : Results.Ok(await search.SearchAsync(input!, ct));
        });
        knowledge.MapGet("/documents", async (IKnowledgeRepository repository, CancellationToken ct) =>
            Results.Ok((await repository.GetDocumentsAsync(ct)).Select(d => new
            { d.DocumentId, d.Application, d.IssueType, d.Title, d.Source, d.SourceUrl, d.PublishedAtUtc })));
        knowledge.MapGet("/documents/{id}", async (string id, IKnowledgeRepository repository, CancellationToken ct) =>
        {
            var document = (await repository.GetDocumentsAsync(ct)).FirstOrDefault(d => d.DocumentId == id);
            return document is null ? Results.NotFound() : Results.Ok(document);
        });
        knowledge.MapPost("/documents", async (KnowledgeImportRequest? input, IKnowledgeRepository repository, CancellationToken ct) =>
        {
            var errors = KnowledgePolicy.Validate(input);
            if (errors.Count > 0) return Validation(errors);
            var document = await repository.ImportAsync(input!, ct);
            return Results.Created($"/api/v1/knowledge/documents/{document.DocumentId}", document);
        }).WithMetadata(new LearningAccessRequired());
        knowledge.MapDelete("/documents/{id}", async (string id, IKnowledgeRepository repository, CancellationToken ct) =>
            await repository.DeleteDocumentAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithMetadata(new LearningAccessRequired());
    }

    private static IResult Validation(IReadOnlyList<string> errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = errors.ToArray() });
}
