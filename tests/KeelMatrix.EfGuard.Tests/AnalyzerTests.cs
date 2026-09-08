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
    public void UniqueProviderIndexReportsBothProviderAndUniquenessRisks()
    {
        Report report = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "create-index", Table = "Orders", IsUnique = true }), null, new GuardConfig(), null);

        Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG301");
        Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG204");
    }

    [Fact]
    public void ProviderRuleWithoutMigrationSqlEvidenceIsExplicitlyUnverified()
    {
        Report report = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "create-index", Table = "Orders" }, "Npgsql.EntityFrameworkCore.PostgreSQL"), null, new GuardConfig(), null);

        Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG302" && d.Severity == FindingSeverity.Unverified && d.Confidence == FindingConfidence.Unknown);
    }

    [Fact]
    public void ProviderRuleConsumesRetainedMigrationSqlEvidence()
    {
        ExtractionResult current = Current(new NormalizedOperation { Kind = "create-index", Table = "Orders", Migration = "20240201_Index" }, "Npgsql.EntityFrameworkCore.PostgreSQL");
        current.ProviderSql = new ProviderSqlEvidence
        {
            Available = true,
            Statements = [new ProviderSqlStatement { Migration = "20240201_Index", Sql = "CREATE INDEX ..." }]
        };

        Report report = Analyzer.Analyze(current, null, new GuardConfig(), null);

        Assert.True(report.ProviderSql.Available);
        Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG302" && d.Severity == FindingSeverity.High && d.Confidence == FindingConfidence.High);
    }

    [Fact]
    public void UniqueConstraintReportsUniquenessRisk()
    {
        Report report = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "unique-constraint", Table = "Orders" }), null, new GuardConfig(), null);

        Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG204");
    }

    [Fact]
    public void CollationChangeReportsUnsafeAlteration()
    {
        Report report = Analyzer.Analyze(Current(new NormalizedOperation
        {
            Kind = "alter-column",
            Table = "Orders",
            Column = "Code",
            OldClrType = "System.String",
            ClrType = "System.String",
            OldCollation = "Latin1_General_100_CI_AS",
            Collation = "Latin1_General_100_BIN2"
        }), null, new GuardConfig(), null);

        Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG202");
    }

    [Fact]
    public void LatestMigrationIsSelectedWhenNoBaselineIsProvided()
    {
        Report report = Analyzer.Analyze(Current([
            new NormalizedOperation { Kind = "custom-operation", Migration = "20240101_Historical" },
            new NormalizedOperation { Kind = "create-table", Table = "Orders", Migration = "20240201_Current" }
        ], "Microsoft.EntityFrameworkCore.SqlServer"), null, new GuardConfig(), null);

        Assert.DoesNotContain(report.Diagnostics, d => d.RuleId == "EFG399");
        Assert.Equal(1, report.Summary.OperationsInspected);
    }

    [Fact]
    public void RollingMatrixReportsCurrentApplicationAgainstPreviousSchema()
    {
        ExtractionResult current = Current(new NormalizedOperation { Kind = "add-column", Table = "Orders", Column = "Code", IsNullable = true });
        current.Model = new ModelSnapshot { Tables = [new ModelTable { Name = "Orders", Columns = [new ModelColumn { Name = "Id" }, new ModelColumn { Name = "Code" }] }] };
        ExtractionResult baseline = new() { Success = true, Provider = current.Provider, ProviderSupported = true, Model = new ModelSnapshot { Tables = [new ModelTable { Name = "Orders", Columns = [new ModelColumn { Name = "Id" }] }] } };

        Report report = Analyzer.Analyze(current, baseline, new GuardConfig(), "HEAD~1");

        Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG101" && d.AffectedState == "Current application + previous schema");
    }

    [Fact]
    public void RollingPolicyRequiresConfiguredCompatibilityHistory()
    {
        ExtractionResult current = Current(new NormalizedOperation { Kind = "create-table", Table = "Orders", Migration = "20240201_Current" });
        ExtractionResult baseline = new() { Success = true, Provider = current.Provider, ProviderSupported = true, Operations = [new NormalizedOperation { Kind = "create-table", Table = "Customers", Migration = "20240101_Previous" }] };

        Report report = Analyzer.Analyze(current, baseline, new GuardConfig { MinimumCompatibleVersions = 2 }, "HEAD~1");

        Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG399" && d.Title == "Insufficient compatibility history");
        Assert.Equal(1, report.Summary.ExitCode);
    }

    [Fact]
    public void BaselineSelectsOnlyMigrationsAddedAfterReference()
    {
        ExtractionResult current = Current([
            new NormalizedOperation { Kind = "custom-operation", Migration = "20240101_Historical" },
            new NormalizedOperation { Kind = "create-table", Table = "Orders", Migration = "20240201_Current" }
        ], "Microsoft.EntityFrameworkCore.SqlServer");
        ExtractionResult baseline = new()
        {
            Success = true,
            Provider = current.Provider,
            ProviderSupported = true,
            Operations = [new NormalizedOperation { Kind = "custom-operation", Migration = "20240101_Historical" }]
        };

        Report report = Analyzer.Analyze(current, baseline, new GuardConfig(), "HEAD~1");

        Assert.DoesNotContain(report.Diagnostics, d => d.RuleId == "EFG399" && d.Migration == "20240101_Historical");
        Assert.Equal(1, report.Summary.OperationsInspected);
    }

    [Fact]
    public void BlueGreenStrategyDoesNotRequireOverlapStates()
    {
        ExtractionResult current = Current(new NormalizedOperation { Kind = "drop-column", Table = "Orders", Column = "LegacyCode" });
        current.Model = new ModelSnapshot { Tables = [new ModelTable { Name = "Orders" }] };
        ExtractionResult baseline = new() { Success = true, Provider = current.Provider, ProviderSupported = true, Model = new ModelSnapshot { Tables = [new ModelTable { Name = "Orders", Columns = [new ModelColumn { Name = "LegacyCode" }] }] } };

        Report report = Analyzer.Analyze(current, baseline, new GuardConfig { Strategy = "blue-green" }, "HEAD~1");

        Assert.DoesNotContain(report.Diagnostics, d => d.RuleId == "EFG101");
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
