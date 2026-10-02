using System.Security.Cryptography;
using System.Text;

namespace SecureIntelligence.Api.Services;

public sealed record LearningAccessRequired;
public sealed record LearningFeatureRequired;

public sealed class ApiKeyAccess
{
    private readonly byte[] _diagnosis;
    private readonly byte[]? _learning;
    public bool LearningEnabled => _learning is not null;

    public ApiKeyAccess(string? diagnosisKey, string? learningKey)
    {
        if (string.IsNullOrWhiteSpace(diagnosisKey) || diagnosisKey.Length is < 32 or > 512)
            throw new InvalidOperationException("SECURE_INTELLIGENCE_API_KEY must contain 32–512 characters.");
        if (!string.IsNullOrEmpty(learningKey) && (string.IsNullOrWhiteSpace(learningKey)
            || learningKey.Length is < 32 or > 512 || learningKey == diagnosisKey))
            throw new InvalidOperationException("The learning API key must be distinct and contain 32–512 characters.");
        _diagnosis = Hash(diagnosisKey);
        _learning = string.IsNullOrEmpty(learningKey) ? null : Hash(learningKey);
    }

    public bool CanDiagnose(string? supplied) => Matches(_diagnosis, supplied) || Matches(_learning, supplied);
    public bool CanLearn(string? supplied) => Matches(_learning, supplied);
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private static bool Matches(byte[]? expected, string? supplied) =>
        expected is not null && !string.IsNullOrEmpty(supplied) && supplied.Length <= 512
        && CryptographicOperations.FixedTimeEquals(expected, Hash(supplied));
}
