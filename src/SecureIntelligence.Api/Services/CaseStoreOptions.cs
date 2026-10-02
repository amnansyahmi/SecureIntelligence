namespace SecureIntelligence.Api.Services;

public sealed class CaseStoreOptions
{
    public string Directory { get; set; } = "Data";
    public int RetentionDays { get; set; } = 90;
    public int MaxDocuments { get; set; } = 100;
    public int MaxProposals { get; set; } = 1000;
    public int MaxCases { get; set; } = 1_000;
}
