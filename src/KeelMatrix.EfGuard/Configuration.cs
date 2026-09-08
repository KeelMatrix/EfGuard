using System.Text.Json;

namespace KeelMatrix.EfGuard;

internal sealed class GuardConfig
{
    public string Strategy { get; init; } = "rolling";
    public int MinimumCompatibleVersions { get; init; } = 1;
    public Dictionary<string, FindingSeverity> RuleSeverities { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DisabledRules { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Suppression> Suppressions { get; } = [];
}

internal sealed class Suppression
{
    public string Rule { get; init; } = "";
    public string? Migration { get; init; }
    public string Reason { get; init; } = "";
    public DateOnly? Expires { get; init; }
}

internal static class ConfigurationLoader
{
    internal static GuardConfig Load(string path)
    {
        if (!File.Exists(path))
            return new GuardConfig();

        if (new FileInfo(path).Length > 256 * 1024)
            throw new InvalidOperationException("Configuration file exceeds the 256 KB limit.");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Configuration must be a JSON object.");

        int version = ReadInt(root, "version", 1);
        if (version != 1)
            throw new InvalidOperationException("Configuration version must be 1.");

        GuardConfig result = new();
        if (TryGet(root, "deployment", out JsonElement deployment))
        {
            if (deployment.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("deployment must be an object.");

            string strategy = ReadString(deployment, "strategy", "rolling");
            if (!strategy.Equals("rolling", StringComparison.OrdinalIgnoreCase)
                && !strategy.Equals("expand-contract", StringComparison.OrdinalIgnoreCase)
                && !strategy.Equals("blue-green", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("deployment.strategy must be rolling, expand-contract, or blue-green.");

            result = new GuardConfigBuilder(strategy, ReadInt(deployment, "minimumCompatibleVersions", 1)).Build();
        }

        if (TryGet(root, "rules", out JsonElement rules))
        {
            if (rules.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("rules must be an object.");

            foreach (JsonProperty rule in rules.EnumerateObject())
            {
                string? value = rule.Value.GetString();
                if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
                    result.DisabledRules.Add(rule.Name);
                else
                    result.RuleSeverities[rule.Name] = ParseSeverity(value);
            }
        }

        if (TryGet(root, "suppressions", out JsonElement suppressions))
        {
            if (suppressions.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("suppressions must be an array.");

            foreach (JsonElement item in suppressions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Each suppression must be an object.");

                string rule = ReadRequiredString(item, "rule");
                string reason = ReadRequiredString(item, "reason");
                string? migration = ReadOptionalString(item, "migration");
                DateOnly? expires = null;
                string? expiryText = ReadOptionalString(item, "expires");
                if (expiryText is not null && (!DateOnly.TryParseExact(expiryText, "yyyy-MM-dd", out DateOnly parsed)))
                    throw new InvalidOperationException("Suppression expiry must use yyyy-MM-dd.");
                else if (expiryText is not null)
                    expires = DateOnly.ParseExact(expiryText, "yyyy-MM-dd");

                result.Suppressions.Add(new Suppression { Rule = rule, Migration = migration, Reason = reason, Expires = expires });
            }
        }

        RejectCredentialProperties(root);
        return result;
    }

    private static void RejectCredentialProperties(JsonElement element)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string name = property.Name;
            if (name.Contains("password", StringComparison.OrdinalIgnoreCase)
                || name.Contains("connectionstring", StringComparison.OrdinalIgnoreCase)
                || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || name.Contains("token", StringComparison.OrdinalIgnoreCase)
                || name.Contains("credential", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Configuration cannot contain credentials or connection settings.");

            if (property.Value.ValueKind == JsonValueKind.Object)
                RejectCredentialProperties(property.Value);
            else if (property.Value.ValueKind == JsonValueKind.Array)
                foreach (JsonElement child in property.Value.EnumerateArray())
                    if (child.ValueKind == JsonValueKind.Object)
                        RejectCredentialProperties(child);
        }
    }

    private static FindingSeverity ParseSeverity(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "error" or "block" => FindingSeverity.Block,
            "warning" or "high" => FindingSeverity.High,
            "info" or "advisory" => FindingSeverity.Advisory,
            "unverified" => FindingSeverity.Unverified,
            _ => throw new InvalidOperationException("Rule severity must be error, warning, info, unverified, or off.")
        };
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }

        value = default;
        return false;
    }

    private static string ReadString(JsonElement element, string name, string defaultValue)
    {
        if (!TryGet(element, name, out JsonElement value))
            return defaultValue;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"{name} must be a string.");
        return value.GetString() ?? defaultValue;
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        string? value = ReadOptionalString(element, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Suppression {name} is required.");
        return value.Trim();
    }

    private static string? ReadOptionalString(JsonElement element, string name)
    {
        if (!TryGet(element, name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"{name} must be a string.");
        return value.GetString();
    }

    private static int ReadInt(JsonElement element, string name, int defaultValue)
    {
        if (!TryGet(element, name, out JsonElement value))
            return defaultValue;
        if (!value.TryGetInt32(out int number) || number < 1)
            throw new InvalidOperationException($"{name} must be a positive integer.");
        return number;
    }

    private sealed class GuardConfigBuilder(string strategy, int minimumCompatibleVersions)
    {
        internal GuardConfig Build() => new() { Strategy = strategy, MinimumCompatibleVersions = minimumCompatibleVersions };
    }
}
