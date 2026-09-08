namespace KeelMatrix.EfGuard.Tests;

public sealed class AnalyzerTests
{
    [Fact]
    public void DropAgainstBaselineProducesBlockingCompatibilityFinding()
    {
        ExtractionResult current = Current(new NormalizedOperation { Kind = "drop-column", Table = "Orders", Column = "LegacyCode", Migration = "20240101_RemoveLegacyCode" });
        ModelSnapshot baselineModel = new() { Tables = [new ModelTable { Name = "Orders", Columns = [new ModelColumn { Name = "LegacyCode" }] }] };

        Report report = Analyzer.Analyze(current, new ExtractionResult { Success = true, Provider = current.Provider, ProviderSupported = true, Model = baselineModel }, new GuardConfig(), "HEAD~1");

        Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG101" && d.Severity == FindingSeverity.Block);
        Assert.Equal(1, report.Summary.ExitCode);
    }

    [Fact]
    public void UnknownOperationFailsClosed()
    {
        Report report = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "custom-operation" }), null, new GuardConfig(), null);

        Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG399" && d.Severity == FindingSeverity.Unverified);
        Assert.Equal(1, report.Summary.ExitCode);
    }

    [Fact]
    public void ProviderSpecificIndexRulesAreDifferent()
    {
        Report postgres = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "create-index", Table = "Orders" }, "Npgsql.EntityFrameworkCore.PostgreSQL"), null, new GuardConfig(), null);
        Report sqlServer = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "create-index", Table = "Orders" }, "Microsoft.EntityFrameworkCore.SqlServer"), null, new GuardConfig(), null);

        Assert.Contains(postgres.Diagnostics, d => d.RuleId == "EFG302");
        Assert.Contains(sqlServer.Diagnostics, d => d.RuleId == "EFG301");
    }

    [Fact]
    public void SuppressionRequiresReasonAndExpiredSuppressionStaysActive()
    {
        string path = Path.Combine(Path.GetTempPath(), "efguard-config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"version\":1,\"suppressions\":[{\"rule\":\"EFG399\",\"reason\":\"temporary\",\"expires\":\"2020-01-01\"}]}");
            GuardConfig config = ConfigurationLoader.Load(path);
            Report report = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "custom-operation" }), null, config, null);

            Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG998");
            Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG399" && !d.Suppressed);
            Assert.Equal(1, report.Summary.ExitCode);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MalformedConfigurationFailsClosed()
    {
        string path = Path.Combine(Path.GetTempPath(), "efguard-config-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"version\":99}");
            Assert.Throws<InvalidOperationException>(() => ConfigurationLoader.Load(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void UnsupportedProviderReturnsExecutionFailure()
    {
        Report report = Analyzer.Analyze(Current([], "Pomelo.EntityFrameworkCore.MySql"), null, new GuardConfig(), null);

        Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG900");
        Assert.Equal(2, report.Summary.ExitCode);
    }

    private static ExtractionResult Current(NormalizedOperation operation, string provider = "Microsoft.EntityFrameworkCore.SqlServer")
        => Current([operation], provider);

    private static ExtractionResult Current(IEnumerable<NormalizedOperation> operations, string provider)
        => new() { Success = true, Provider = provider, ProviderSupported = provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) || provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase), Operations = operations.ToList() };
}
