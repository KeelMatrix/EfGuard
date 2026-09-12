using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeelMatrix.EfGuard;

/// <summary>
/// A single provider-behavior claim that the repository verifies against a real database engine.
/// </summary>
internal sealed class ProviderEngineClaim
{
    public string Rule { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Engine { get; set; } = "";
    public string Behavior { get; set; } = "";
    public string VerifiedBy { get; set; } = "";
}

/// <summary>
/// Engine verification evidence for provider-specific findings. A provider finding may only be
/// reported with high confidence when this evidence covers the finding's rule and provider.
/// </summary>
internal sealed class ProviderEngineEvidence
{
    internal const string EngineSqlServer = "sqlserver";
    internal const string EnginePostgreSql = "postgresql";
    internal const string ResourceName = "KeelMatrix.EfGuard.ProviderEngineEvidence.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    internal static ProviderEngineEvidence None { get; } = new();

    internal static ProviderEngineEvidence Shipped { get; } = LoadShipped();

    public int Version { get; set; } = 1;
    public List<ProviderEngineClaim> Claims { get; set; } = [];

    internal bool IsEngineVerified(string ruleId, string? provider)
        => provider is not null
            && Claims.Any(claim =>
                claim.Rule.Equals(ruleId, StringComparison.OrdinalIgnoreCase)
                && claim.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));

    internal ProviderEngineEvidence ForEngine(string engine)
        => new()
        {
            Version = Version,
            Claims = Claims.Where(claim => claim.Engine.Equals(engine, StringComparison.OrdinalIgnoreCase)).ToList()
        };

    internal string Serialize() => JsonSerializer.Serialize(this, Options).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

    internal static ProviderEngineEvidence Parse(string json)
        => JsonSerializer.Deserialize<ProviderEngineEvidence>(json, Options) ?? throw new JsonException("The provider engine evidence is empty.");

    private static ProviderEngineEvidence LoadShipped()
    {
        try
        {
            using Stream? stream = typeof(ProviderEngineEvidence).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
                return new ProviderEngineEvidence();
            using StreamReader reader = new(stream);
            return Parse(reader.ReadToEnd());
        }
        catch
        {
            return new ProviderEngineEvidence();
        }
    }
}
