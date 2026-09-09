namespace KeelMatrix.EfGuard.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void ValidConfigurationLoadsRuleOverrideAndSuppression()
    {
        string path = Write("{\"version\":1,\"deployment\":{\"strategy\":\"rolling\",\"minimumCompatibleVersions\":2},\"rules\":{\"EFG302\":\"error\"},\"suppressions\":[{\"rule\":\"EFG101\",\"reason\":\"retired\"}]}");
        try
        {
            GuardConfig config = ConfigurationLoader.Load(path);
            Assert.Equal("rolling", config.Strategy);
            Assert.Equal(2, config.MinimumCompatibleVersions);
            Assert.Equal(FindingSeverity.Block, config.RuleSeverities["EFG302"]);
            Assert.Equal("retired", config.Suppressions.Single().Reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CredentialShapedConfigurationIsRejected()
    {
        string path = Write("{\"version\":1,\"databaseConnectionString\":\"not-a-secret\"}");
        try { Assert.Throws<InvalidOperationException>(() => ConfigurationLoader.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OffRuleIsDisabledRatherThanDowngraded()
    {
        string path = Write("{\"version\":1,\"rules\":{\"EFG399\":\"off\"}}");
        try
        {
            GuardConfig config = ConfigurationLoader.Load(path);
            Report report = Analyzer.Analyze(new ExtractionResult
            {
                Success = true,
                Provider = "Microsoft.EntityFrameworkCore.SqlServer",
                ProviderSupported = true,
                Operations = [new NormalizedOperation { Kind = "custom-operation" }]
            }, null, config, null);

            Assert.Contains(report.Diagnostics, diagnostic => diagnostic.RuleId == "EFG399" && diagnostic.Suppressed);
            Assert.Equal(0, report.Summary.ExitCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MalformedPropertyTypeIsRejected()
    {
        string path = Write("{\"version\":1,\"deployment\":{\"strategy\":42}}");
        try { Assert.Throws<InvalidOperationException>(() => ConfigurationLoader.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownTopLevelPropertyIsRejectedWithActionableMessage()
    {
        string path = Write("{\"version\":1,\"deplyoment\":{\"strategy\":\"rolling\"}}");
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ConfigurationLoader.Load(path));
            Assert.Contains("deplyoment", exception.Message);
            Assert.Contains("deployment", exception.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownRuleOverrideIsRejected()
    {
        string path = Write("{\"version\":1,\"rules\":{\"EFG301 typo\":\"error\"}}");
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ConfigurationLoader.Load(path));
            Assert.Contains("Unknown rule ID", exception.Message);
            Assert.Contains("EFG301 typo", exception.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SuppressionForUnknownRuleIsRejected()
    {
        string path = Write("{\"version\":1,\"suppressions\":[{\"rule\":\"EFG999\",\"reason\":\"temporary\"}]}");
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ConfigurationLoader.Load(path));
            Assert.Contains("EFG999", exception.Message);
            Assert.Contains("suppression", exception.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MalformedJsonIsRejectedWithConfigurationContext()
    {
        string path = Write("{\"version\":1");
        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ConfigurationLoader.Load(path));
            Assert.StartsWith("Configuration is not valid JSON:", exception.Message);
        }
        finally { File.Delete(path); }
    }

    private static string Write(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "efguard-test-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, text);
        return path;
    }
}
