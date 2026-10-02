using System.Globalization;

namespace SecureIntelligence.Core.Security;

public static class SignalSchema
{
    public static readonly IReadOnlyList<string> AllowedKeys = Array.AsReadOnly(new[]
    {
        "readyForQuote", "bomItemCount", "systemizationConfigured", "dbLatencyMs"
    });

    public static bool IsKnown(string key) => AllowedKeys.Contains(key, StringComparer.Ordinal);

    public static bool TryNormalize(string key, string? value, out string normalized)
    {
        normalized = "";
        if ((key is "readyForQuote" or "systemizationConfigured") && bool.TryParse(value, out var flag))
            normalized = flag ? "true" : "false";
        else if (key == "bomItemCount" && int.TryParse(value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var count) && count >= 0)
            normalized = count.ToString(CultureInfo.InvariantCulture);
        else if (key == "dbLatencyMs" && double.TryParse(value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var latency) && double.IsFinite(latency) && latency >= 0)
            normalized = latency.ToString("G17", CultureInfo.InvariantCulture);
        return normalized.Length > 0;
    }
}
