using System.Text.Json;
using Microsoft.Extensions.Options;
using SecureIntelligence.Core.Cases;
using SecureIntelligence.Core.Models;
using SecureIntelligence.Core.Security;

namespace SecureIntelligence.Api.Services;

// Single process only. A shared filesystem across replicas needs a transactional database.
public sealed class JsonCaseRepository : ICaseRepository
{
    private readonly string _path;
    private readonly CaseStoreOptions _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonCaseRepository(IWebHostEnvironment environment, IOptions<CaseStoreOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
        var directory = Path.GetFullPath(_options.Directory, environment.ContentRootPath);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "validated-cases.json");
    }

    public async Task<IReadOnlyList<CaseRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadAsync(cancellationToken);
            var active = Prune(records);
            if (active.Count != records.Count)
                await WriteAsync(active, cancellationToken);
            return active;
        }
        finally { _gate.Release(); }
    }

    public async Task<CaseRecord> AddAsync(CaseRecord record, CancellationToken cancellationToken = default)
    {
        ValidateStoredRecord(record);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = Prune(await ReadAsync(cancellationToken));
            var existing = records.FirstOrDefault(c => Equivalent(c, record));
            if (existing is not null)
            {
                await WriteAsync(records, cancellationToken);
                return existing;
            }
            records.Add(record);
            records = records.OrderByDescending(c => c.CreatedAtUtc).ThenBy(c => c.CaseId, StringComparer.Ordinal)
                .Take(_options.MaxCases).ToList();
            await WriteAsync(records, cancellationToken);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string caseId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = Prune(await ReadAsync(cancellationToken));
            var removed = records.RemoveAll(c => c.CaseId == caseId) > 0;
            await WriteAsync(records, cancellationToken);
            return removed;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<CaseRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new List<CaseRecord>();
        await using var stream = File.OpenRead(_path);
        var records = await JsonSerializer.DeserializeAsync<List<CaseRecord>>(stream, _jsonOptions, cancellationToken)
            ?? throw new JsonException("Case store cannot be null.");
        // Refuse legacy/invalid records rather than exposing raw fields or discarding the store.
        foreach (var record in records)
            ValidateStoredRecord(record);
        if (records.Select(c => c.CaseId).Distinct(StringComparer.Ordinal).Count() != records.Count)
            throw new JsonException("Duplicate case IDs.");
        return records;
    }

    private static void ValidateStoredRecord(CaseRecord? record)
    {
        if (record is null || !Guid.TryParseExact(record.CaseId, "N", out _)
            || record.CreatedAtUtc == default || record.Description != "Description omitted by learning policy.")
            throw new JsonException("Invalid or legacy case store; reviewed migration is required.");
        var feedback = new FeedbackRequest(new DiagnosticRequest(record.Application, record.IssueType,
            record.Description, record.Signals), record.ConfirmedCause, record.Resolution, true);
        if (LearningPolicy.Validate(feedback).Count != 0)
            throw new JsonException("Case store violates the learning policy.");
    }

    private List<CaseRecord> Prune(List<CaseRecord> records)
    {
        var cutoff = _clock.GetUtcNow().AddDays(-_options.RetentionDays);
        return records.Where(c => c.CreatedAtUtc > cutoff)
            .OrderByDescending(c => c.CreatedAtUtc).ThenBy(c => c.CaseId, StringComparer.Ordinal)
            .Take(_options.MaxCases).ToList();
    }

    private static bool Equivalent(CaseRecord a, CaseRecord b) =>
        a.Application == b.Application && a.IssueType == b.IssueType
        && a.ConfirmedCause == b.ConfirmedCause && a.Resolution == b.Resolution
        && a.Signals.Count == b.Signals.Count
        && a.Signals.All(s => b.Signals.TryGetValue(s.Key, out var value) && s.Value == value);

    private async Task WriteAsync(List<CaseRecord> records, CancellationToken cancellationToken)
    {
        var tempPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, records, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
