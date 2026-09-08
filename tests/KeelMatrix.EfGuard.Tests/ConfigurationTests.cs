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

    private static string Write(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), "efguard-test-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, text);
        return path;
    }
}
