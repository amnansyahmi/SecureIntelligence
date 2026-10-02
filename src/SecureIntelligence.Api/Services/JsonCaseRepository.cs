using System.Text.Json;
using SecureIntelligence.Core.Cases;

namespace SecureIntelligence.Api.Services;

public sealed class JsonCaseRepository : ICaseRepository
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public JsonCaseRepository(IWebHostEnvironment environment)
    {
        var dataDirectory = Path.Combine(environment.ContentRootPath, "Data");
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "validated-cases.json");
    }

    public async Task<IReadOnlyList<CaseRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
                return Array.Empty<CaseRecord>();

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<CaseRecord>>(stream, _jsonOptions, cancellationToken)
                   ?? Array.Empty<CaseRecord>();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(CaseRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            List<CaseRecord> records;
            if (File.Exists(_path))
            {
                await using var readStream = File.OpenRead(_path);
                records = await JsonSerializer.DeserializeAsync<List<CaseRecord>>(readStream, _jsonOptions, cancellationToken)
                          ?? new List<CaseRecord>();
            }
            else
            {
                records = new List<CaseRecord>();
            }

            records.Add(record);

            var tempPath = _path + ".tmp";
            await using (var writeStream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(writeStream, records, _jsonOptions, cancellationToken);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
