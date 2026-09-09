using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeelMatrix.EfGuard;

internal static class ReportSerialization
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static string Serialize(Report report) => JsonSerializer.Serialize(report, Options).Replace("\r\n", "\n", StringComparison.Ordinal);
}
