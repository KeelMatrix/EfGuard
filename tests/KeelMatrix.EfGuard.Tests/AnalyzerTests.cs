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
    public void RollingPolicyWithoutBaselineReportsUnverifiedCompatibilityHistory()
    {
        Report report = Analyzer.Analyze(Current(Array.Empty<NormalizedOperation>(), "Microsoft.EntityFrameworkCore.SqlServer"), null, new GuardConfig { MinimumCompatibleVersions = 2 }, null);

        Assert.Contains(report.Diagnostics, d => d.RuleId == "EFG399" && d.Title == "Insufficient compatibility history" && d.Severity == FindingSeverity.Unverified);
        Assert.Equal(1, report.Summary.Unverified);
        Assert.Equal(1, report.Summary.ExitCode);
    }

    [Fact]
    public void CompatibleBaselineWithDefaultHistoryPolicyRemainsClean()
    {
        ExtractionResult current = Current(Array.Empty<NormalizedOperation>(), "Microsoft.EntityFrameworkCore.SqlServer");
        current.Model = new ModelSnapshot { Tables = [new ModelTable { Name = "Orders", Columns = [new ModelColumn { Name = "Id" }] }] };
        ExtractionResult baseline = new()
        {
            Success = true,
            Provider = current.Provider,
            ProviderSupported = true,
            Model = new ModelSnapshot { Tables = [new ModelTable { Name = "Orders", Columns = [new ModelColumn { Name = "Id" }] }] }
        };

        Report report = Analyzer.Analyze(current, baseline, new GuardConfig(), "HEAD~1");

        Assert.Empty(report.Diagnostics);
        Assert.Equal(0, report.Summary.ExitCode);
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

    [Fact]
    public void RuleRegressionCoversDestructiveAndBoundaryCases()
    {
        Report destructive = Analyzer.Analyze(Current(new NormalizedOperation
        {
            Kind = "drop-column",
            Table = "Orders",
            Column = "LegacyCode",
            Migration = "20240101_RemoveLegacyCode"
        }), null, new GuardConfig(), null);

        Diagnostic finding = Assert.Single(destructive.Diagnostics, d => d.RuleId == "EFG201");
        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.NotEmpty(finding.Remediation);
        Assert.DoesNotContain(Analyzer.Analyze(Current(new NormalizedOperation { Kind = "create-table", Table = "Orders" }), null, new GuardConfig(), null).Diagnostics, d => d.RuleId == "EFG201");
    }

    [Fact]
    public void RequiredColumnRuleHasPositiveAndSafeDefaultBoundaries()
    {
        Report required = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "add-column", Table = "Orders", Column = "Code", IsNullable = false }), null, new GuardConfig(), null);
        Diagnostic finding = Assert.Single(required.Diagnostics, d => d.RuleId == "EFG102");
        Assert.Equal(FindingSeverity.Block, finding.Severity);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.NotEmpty(finding.Remediation);

        Report withDefault = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "add-column", Table = "Orders", Column = "Code", IsNullable = false, SqlShape = "has-default" }), null, new GuardConfig(), null);
        Assert.DoesNotContain(withDefault.Diagnostics, d => d.RuleId == "EFG102");
    }

    [Fact]
    public void UnsafeAlterationRuleDistinguishesNarrowingFromWidening()
    {
        Report narrowing = Analyzer.Analyze(Current(new NormalizedOperation
        {
            Kind = "alter-column",
            Table = "Orders",
            Column = "Code",
            OldClrType = "System.String",
            ClrType = "System.String",
            OldMaxLength = 128,
            MaxLength = 32
        }), null, new GuardConfig(), null);
        Diagnostic finding = Assert.Single(narrowing.Diagnostics, d => d.RuleId == "EFG202");
        Assert.Equal(FindingSeverity.Block, finding.Severity);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.NotEmpty(finding.Remediation);

        Report widening = Analyzer.Analyze(Current(new NormalizedOperation
        {
            Kind = "alter-column",
            Table = "Orders",
            Column = "Code",
            OldClrType = "System.String",
            ClrType = "System.String",
            OldMaxLength = 32,
            MaxLength = 128
        }), null, new GuardConfig(), null);
        Assert.DoesNotContain(widening.Diagnostics, d => d.RuleId == "EFG202");
    }

    [Fact]
    public void UniqueConstraintRuleDistinguishesUniqueAndOrdinaryIndexes()
    {
        Report unique = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "create-index", Table = "Orders", IsUnique = true }), null, new GuardConfig(), null);
        Diagnostic finding = Assert.Single(unique.Diagnostics, d => d.RuleId == "EFG204");
        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.NotEmpty(finding.Remediation);

        Report ordinary = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "create-index", Table = "Orders", IsUnique = false }), null, new GuardConfig(), null);
        Assert.DoesNotContain(ordinary.Diagnostics, d => d.RuleId == "EFG204");
    }

    [Fact]
    public void SqlServerIndexRuleRequiresProviderEvidenceAndOnlineBoundary()
    {
        Report unverified = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "create-index", Table = "Orders" }), null, new GuardConfig(), null);
        Diagnostic missingEvidence = Assert.Single(unverified.Diagnostics, d => d.RuleId == "EFG301");
        Assert.Equal(FindingSeverity.Unverified, missingEvidence.Severity);
        Assert.Equal(FindingConfidence.Unknown, missingEvidence.Confidence);

        ExtractionResult current = Current(new NormalizedOperation { Kind = "create-index", Table = "Orders", IsOnline = true }, "Microsoft.EntityFrameworkCore.SqlServer");
        current.ProviderSql = new ProviderSqlEvidence { Available = true, Statements = [new ProviderSqlStatement { Migration = null, Sql = "CREATE INDEX ..." }] };
        Report online = Analyzer.Analyze(current, null, new GuardConfig(), null);
        Assert.DoesNotContain(online.Diagnostics, d => d.RuleId == "EFG301");
    }

    [Fact]
    public void PostgreSqlIndexRuleRequiresConcurrentBoundary()
    {
        Report blocking = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "create-index", Table = "Orders" }, "Npgsql.EntityFrameworkCore.PostgreSQL"), null, new GuardConfig(), null);
        Diagnostic finding = Assert.Single(blocking.Diagnostics, d => d.RuleId == "EFG302");
        Assert.Equal(FindingSeverity.Unverified, finding.Severity);
        Assert.Equal(FindingConfidence.Unknown, finding.Confidence);
        Assert.NotEmpty(finding.Remediation);

        Report concurrent = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "create-index", Table = "Orders", IsConcurrent = true }, "Npgsql.EntityFrameworkCore.PostgreSQL"), null, new GuardConfig(), null);
        Assert.DoesNotContain(concurrent.Diagnostics, d => d.RuleId == "EFG302");
    }

    [Fact]
    public void ForeignKeyRuleAndNonForeignKeyBoundaryAreCovered()
    {
        Report foreignKey = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "add-foreign-key", Table = "OrderLines", PrincipalTable = "Orders" }), null, new GuardConfig(), null);
        Diagnostic finding = Assert.Single(foreignKey.Diagnostics, d => d.RuleId == "EFG303");
        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Equal(FindingConfidence.High, finding.Confidence);
        Assert.NotEmpty(finding.Remediation);

        Report drop = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "drop-foreign-key", Table = "OrderLines" }), null, new GuardConfig(), null);
        Assert.DoesNotContain(drop.Diagnostics, d => d.RuleId == "EFG303");
    }

    [Fact]
    public void BackfillAndTransactionRulesRemainDistinctFromBoundedSql()
    {
        Report backfill = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "sql-backfill", Table = "Orders" }), null, new GuardConfig(), null);
        Diagnostic backfillFinding = Assert.Single(backfill.Diagnostics, d => d.RuleId == "EFG304");
        Assert.Equal(FindingSeverity.High, backfillFinding.Severity);
        Assert.Equal(FindingConfidence.High, backfillFinding.Confidence);
        Assert.NotEmpty(backfillFinding.Remediation);

        Report suppressedTransaction = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "sql-suppressed-transaction", Table = "Orders" }), null, new GuardConfig(), null);
        Diagnostic transactionFinding = Assert.Single(suppressedTransaction.Diagnostics, d => d.RuleId == "EFG305");
        Assert.Equal(FindingSeverity.High, transactionFinding.Severity);
        Assert.Equal(FindingConfidence.High, transactionFinding.Confidence);
        Assert.NotEmpty(transactionFinding.Remediation);

        Report bounded = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "raw-sql", Table = "Orders" }), null, new GuardConfig(), null);
        Assert.DoesNotContain(bounded.Diagnostics, d => d.RuleId == "EFG304");
        Assert.DoesNotContain(bounded.Diagnostics, d => d.RuleId == "EFG305");
        Assert.Contains(bounded.Diagnostics, d => d.RuleId == "EFG399");
    }

    [Fact]
    public void UnverifiedOperationRuleHasKnownOperationBoundary()
    {
        Report unknown = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "custom-operation" }), null, new GuardConfig(), null);
        Diagnostic finding = Assert.Single(unknown.Diagnostics, d => d.RuleId == "EFG399");
        Assert.Equal(FindingSeverity.Unverified, finding.Severity);
        Assert.Equal(FindingConfidence.Unknown, finding.Confidence);
        Assert.NotEmpty(finding.Remediation);

        Report known = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "create-table", Table = "Orders" }), null, new GuardConfig(), null);
        Assert.DoesNotContain(known.Diagnostics, d => d.RuleId == "EFG399");
    }

    [Fact]
    public void UnsupportedProviderRuleIsFailClosedWithSupportedProviderBoundary()
    {
        Report unsupported = Analyzer.Analyze(Current([], "Microsoft.EntityFrameworkCore.Sqlite"), null, new GuardConfig(), null);
        Diagnostic finding = Assert.Single(unsupported.Diagnostics, d => d.RuleId == "EFG900");
        Assert.Equal(FindingSeverity.Unverified, finding.Severity);
        Assert.Equal(FindingConfidence.Unknown, finding.Confidence);
        Assert.Equal(2, unsupported.Summary.ExitCode);

        Report supported = Analyzer.Analyze(Current([], "Microsoft.EntityFrameworkCore.SqlServer"), null, new GuardConfig(), null);
        Assert.DoesNotContain(supported.Diagnostics, d => d.RuleId == "EFG900");
    }

    [Fact]
    public void ExpiredSuppressionRuleHasUnexpiredBoundary()
    {
        GuardConfig expired = new();
        expired.Suppressions.Add(new Suppression { Rule = "EFG399", Reason = "temporary", Expires = new DateOnly(2020, 1, 1) });
        Report expiredReport = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "custom-operation" }), null, expired, null);
        Diagnostic expiredFinding = Assert.Single(expiredReport.Diagnostics, d => d.RuleId == "EFG998");
        Assert.Equal(FindingSeverity.Block, expiredFinding.Severity);
        Assert.Equal(FindingConfidence.High, expiredFinding.Confidence);
        Assert.NotEmpty(expiredFinding.Remediation);

        GuardConfig active = new();
        active.Suppressions.Add(new Suppression { Rule = "EFG399", Reason = "temporary", Expires = DateOnly.MaxValue });
        Report activeReport = Analyzer.Analyze(Current(new NormalizedOperation { Kind = "custom-operation" }), null, active, null);
        Assert.DoesNotContain(activeReport.Diagnostics, d => d.RuleId == "EFG998");
        Assert.Contains(activeReport.Diagnostics, d => d.RuleId == "EFG399" && d.Suppressed);
    }

    private static ExtractionResult Current(NormalizedOperation operation, string provider = "Microsoft.EntityFrameworkCore.SqlServer")
        => Current([operation], provider);

    private static ExtractionResult Current(IEnumerable<NormalizedOperation> operations, string provider)
        => new() { Success = true, Provider = provider, ProviderSupported = provider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) || provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase), Operations = operations.ToList() };
}
