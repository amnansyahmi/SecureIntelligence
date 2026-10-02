using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using SecureIntelligence.Api.Services;
using SecureIntelligence.Core.Cases;
using SecureIntelligence.Core.Models;
using SecureIntelligence.Core.Rules;
using SecureIntelligence.Core.Security;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Rules diagnose RFQ and performance from explicit evidence", Rules),
    ("Validation rejects missing, null, oversized and invalid signals", Validation),
    ("Numeric parsing is invariant and rejects NaN/Infinity", NumericValidation),
    ("Learning omits descriptions and only permits approved fields", Learning),
    ("Matching abstains on contradictions, missing evidence and other scopes", Matching),
    ("Keys separate diagnosis and learning; missing/weak keys fail closed", Keys),
    ("Case store handles concurrent writes, duplicates and reload", Storage),
    ("Case deletion, retention and capacity persist", Retention),
    ("Corrupt and legacy case stores are preserved and rejected", Corruption)
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static DiagnosticRequest Request(IReadOnlyDictionary<string, string>? signals = null) => new(
    "LineDesigner", "ReadyForQuote", "Example description that must never be stored",
    signals ?? new Dictionary<string, string> { ["readyForQuote"] = "true", ["bomItemCount"] = "0", ["systemizationConfigured"] = "false" });
static FeedbackRequest Feedback(DiagnosticRequest? request = null) => new(request ?? Request(), "Systemization was not configured", "Configure systemization and run validation", true);
static CaseRecord Record(DateTimeOffset? now = null) => LearningPolicy.CreateRecord(Feedback(), now ?? DateTimeOffset.UtcNow);
static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static Task Rules()
{
    var engine = new RuleEngine(new IIntelligenceRule[] { new MissingSystemizationRule(), new ReadyForQuoteWithoutBomRule(), new HighDatabaseLatencyRule() });
    Check(engine.Evaluate(Request()).Select(f => f.Code).SequenceEqual(new[] { "LD-SYSTEMIZATION-MISSING", "LD-RFQ-BOM-MISSING" }));
    Check(engine.Evaluate(Request(new Dictionary<string, string> { ["dbLatencyMs"] = "5000" })).Single().Severity == FindingSeverity.High);
    Check(engine.Evaluate(Request(new Dictionary<string, string>())).Count == 0);
    Check(engine.Evaluate(Request() with { Application = "Other" }).Count == 0);
    return Task.CompletedTask;
}
static Task Validation()
{
    Check(RequestGuard.Validate(null).Count > 0);
    Check(RequestGuard.Validate(Request() with { Application = null! }).Count > 0);
    Check(RequestGuard.Validate(Request() with { Description = new string('x', 4001) }).Count > 0);
    foreach (var value in new[] { "-1", "abc", "1.5", "2147483648" })
        Check(RequestGuard.Validate(Request(new Dictionary<string, string> { ["bomItemCount"] = value })).Count > 0);
    Check(RequestGuard.Validate(Request(new Dictionary<string, string> { ["readyForQuote"] = null! })).Count > 0);
    Check(RequestGuard.Validate(Request(new Dictionary<string, string> { ["PRIVATE-SECRET"] = "" })).All(e => !e.Contains("PRIVATE-SECRET")));
    Check(RequestGuard.Validate(Request(Enumerable.Range(0,101).ToDictionary(i => $"signal{i}", _ => "1"))).Count > 0);
    return Task.CompletedTask;
}
static Task NumericValidation()
{
    var before = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        Check(new HighDatabaseLatencyRule().Evaluate(Request(new Dictionary<string, string> { ["dbLatencyMs"] = "1500.5" })) is not null);
        foreach (var value in new[] { "NaN", "Infinity", "-1", "1,500", "1e999" })
            Check(RequestGuard.Validate(Request(new Dictionary<string, string> { ["dbLatencyMs"] = value })).Count > 0);
    }
    finally { CultureInfo.CurrentCulture = before; }
    return Task.CompletedTask;
}
static Task Learning()
{
    var record = Record();
    Check(!JsonSerializer.Serialize(record).Contains("Example description"));
    Check(record.Signals["readyForQuote"] == "true");
    Check(LearningPolicy.Validate(Feedback(Request(new Dictionary<string, string> { ["patientName"] = "Someone" }))).Count > 0);
    Check(LearningPolicy.Validate(Feedback(Request(new Dictionary<string, string> { ["dbLatencyMs"] = "password=secret" }))).Count > 0);
    Check(LearningPolicy.Validate(Feedback(Request() with { Application = "Unknown" })).Count > 0);
    Check(LearningPolicy.Validate(Feedback(Request() with { IssueType = "Unknown" })).Count > 0);
    Check(LearningPolicy.Validate(Feedback(Request(new Dictionary<string, string>()))).Count > 0);
    foreach (var value in new[] { "password=abc", "Bearer abc", "contact person@example.com", "900101-01-1234", "api_key=abc" })
        Check(LearningPolicy.Validate(Feedback() with { ConfirmedCause = value }).Count > 0);
    Throws<ArgumentException>(() => LearningPolicy.CreateRecord(Feedback() with { ApprovedForLearning = false }, DateTimeOffset.UtcNow));
    return Task.CompletedTask;
}
static async Task Matching()
{
    var memory = new MemoryRepository();
    await memory.AddAsync(Record());
    var matcher = new CaseMatcher(memory);
    Check((await matcher.FindSimilarAsync(Request())).Single().Similarity == 1);
    var opposite = new Dictionary<string, string>(Request().Signals!) { ["systemizationConfigured"] = "true" };
    Check((await matcher.FindSimilarAsync(Request(opposite))).Count == 0);
    opposite = new Dictionary<string, string>(Request().Signals!) { ["bomItemCount"] = "1" };
    Check((await matcher.FindSimilarAsync(Request(opposite))).Count == 0);
    Check((await matcher.FindSimilarAsync(Request() with { Application = "MDIX" })).Count == 0);
    Check((await matcher.FindSimilarAsync(Request() with { IssueType = "Performance" })).Count == 0);
    Check((await matcher.FindSimilarAsync(Request(new Dictionary<string, string>()))).Count == 0);
    Check((await matcher.FindSimilarAsync(Request(new Dictionary<string, string> { ["other"] = "true" }))).Count == 0);
    Check((await matcher.FindSimilarAsync(Request() with { Description = "Entirely unrelated prose" })).Count == 1);
}
static Task Keys()
{
    var diagnosis = new string('d', 32); var learning = new string('l', 32);
    var access = new ApiKeyAccess(diagnosis, learning);
    Check(access.CanDiagnose(diagnosis) && !access.CanLearn(diagnosis));
    Check(access.CanDiagnose(learning) && access.CanLearn(learning));
    Check(!access.CanDiagnose(null) && !access.CanLearn("wrong"));
    Check(!new ApiKeyAccess(diagnosis, null).LearningEnabled);
    Throws<InvalidOperationException>(() => new ApiKeyAccess(null, learning));
    Throws<InvalidOperationException>(() => new ApiKeyAccess("weak", learning));
    Throws<InvalidOperationException>(() => new ApiKeyAccess(diagnosis, diagnosis));
    return Task.CompletedTask;
}
static async Task Storage()
{
    using var fixture = new StoreFixture();
    var records = Enumerable.Range(0, 12).Select(i => Record() with { ConfirmedCause = $"Reviewed cause {i}" }).ToArray();
    await Task.WhenAll(records.Select(c => fixture.Store.AddAsync(c)));
    Check((await fixture.Store.GetAllAsync()).Count == 12);
    var original = await fixture.Store.AddAsync(Record());
    var repeated = await fixture.Store.AddAsync(Record());
    Check(original.CaseId == repeated.CaseId);
    Check((await fixture.Reload().GetAllAsync()).Count == 13);
    Check(!File.ReadAllText(fixture.File).Contains("Example description"));
    Check(Directory.GetFiles(fixture.Root, "*.tmp").Length == 0);
}
static async Task Retention()
{
    using var fixture = new StoreFixture(maxCases: 2);
    var first = await fixture.Store.AddAsync(Record(fixture.Clock.GetUtcNow()));
    fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
    var second = await fixture.Store.AddAsync(Record(fixture.Clock.GetUtcNow()) with { ConfirmedCause = "Cause two" });
    fixture.Clock.Now = fixture.Clock.Now.AddHours(1);
    await fixture.Store.AddAsync(Record(fixture.Clock.GetUtcNow()) with { ConfirmedCause = "Cause three" });
    Check((await fixture.Store.GetAllAsync()).Count == 2);
    Check(!(await fixture.Store.GetAllAsync()).Any(c => c.CaseId == first.CaseId));
    Check(await fixture.Store.DeleteAsync(second.CaseId));
    Check(!await fixture.Store.DeleteAsync(second.CaseId));
    Check((await fixture.Reload().GetAllAsync()).Count == 1);
    fixture.Clock.Now = fixture.Clock.Now.AddDays(91);
    Check((await fixture.Store.GetAllAsync()).Count == 0);
    Check(JsonSerializer.Deserialize<CaseRecord[]>(File.ReadAllText(fixture.File))!.Length == 0);
}
static async Task Corruption()
{
    using var fixture = new StoreFixture();
    foreach (var text in new[] { "{broken", "null", JsonSerializer.Serialize(new[] { Record() with { Description = "Legacy sensitive description" } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) })
    {
        File.WriteAllText(fixture.File, text);
        await ThrowsAsync<JsonException>(async () => { await fixture.Store.GetAllAsync(); });
        await ThrowsAsync<JsonException>(async () => { await fixture.Store.AddAsync(Record()); });
        Check(File.ReadAllText(fixture.File) == text);
    }
}

sealed class MemoryRepository : ICaseRepository
{
    private readonly List<CaseRecord> _cases = new();
    public Task<IReadOnlyList<CaseRecord>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CaseRecord>>(_cases);
    public Task<CaseRecord> AddAsync(CaseRecord record, CancellationToken cancellationToken = default) { _cases.Add(record); return Task.FromResult(record); }
    public Task<bool> DeleteAsync(string caseId, CancellationToken cancellationToken = default) => Task.FromResult(_cases.RemoveAll(c => c.CaseId == caseId) > 0);
}
sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class StoreFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "secure-intelligence-" + Guid.NewGuid().ToString("N"));
    public string File => Path.Combine(Root, "validated-cases.json");
    public TestClock Clock { get; } = new();
    private readonly CaseStoreOptions _options;
    public JsonCaseRepository Store { get; }
    public StoreFixture(int maxCases = 1000)
    {
        _options = new CaseStoreOptions { Directory = Root, MaxCases = maxCases };
        Store = Reload();
    }
    public JsonCaseRepository Reload() => new(new TestEnvironment { ContentRootPath = Root }, Options.Create(_options), Clock);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
