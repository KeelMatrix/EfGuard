namespace KeelMatrix.EfGuard.Tests;

public sealed class CompatibilityFixtureTests
{
    [Fact]
    public void V1ConfigurationFixtureLoadsWithoutRegeneration()
    {
        string path = FixturePath("v1-config.json");
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        GuardConfig config = ConfigurationLoader.Load(path);

        Assert.Equal(1, config.MinimumCompatibleVersions);
        Assert.Equal(FindingSeverity.Block, config.RuleSeverities["EFG302"]);
        Assert.Equal(FindingSeverity.High, config.RuleSeverities["EFG998"]);
        Assert.Equal("EFG399", Assert.Single(config.Suppressions).Rule);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void V1ReportSerializationIsDeterministicAndMatchesCommittedFixture()
    {
        ExtractionResult current = new()
        {
            Success = true,
            Provider = "Microsoft.EntityFrameworkCore.SqlServer",
            ProviderSupported = true
        };
        Report report = Analyzer.Analyze(current, null, new GuardConfig(), null);
        string expected = File.ReadAllText(FixturePath("v1-report.json")).TrimEnd();

        Assert.Equal(expected, ReportSerialization.Serialize(report));
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", name);
}
