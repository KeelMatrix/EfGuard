namespace KeelMatrix.EfGuard.Tests;

[Collection("Extraction")]
public sealed class ExtractionFixtureTests
{
    [Fact]
    public async Task WorkerSelectionUsesEvaluatedTargetFrameworkFromDirectoryBuildProps()
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "Ef9Net9", "Ef9Net9Fixture.csproj"));

        string? targetFramework = await ExtractionCoordinator.SelectWorkerTargetFrameworkAsync(project, CancellationToken.None);

        Assert.Equal("net9.0", targetFramework);
    }

    [Fact]
    public async Task WorkerSelectionChoosesSupportedFrameworkFromEvaluatedMultiTargetProject()
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "KeelMatrix.EfGuard.Worker", "KeelMatrix.EfGuard.Worker.csproj"));

        string? targetFramework = await ExtractionCoordinator.SelectWorkerTargetFrameworkAsync(project, CancellationToken.None);

        Assert.Equal("net10.0", targetFramework);
    }

    [Theory]
    [InlineData("Ef8", "Microsoft.EntityFrameworkCore.SqlServer", "drop-column")]
    [InlineData("Ef8Postgres", "Npgsql.EntityFrameworkCore.PostgreSQL", "create-index")]
    [InlineData("Ef9", "Npgsql.EntityFrameworkCore.PostgreSQL", "create-index")]
    [InlineData("Ef9Net9", "Npgsql.EntityFrameworkCore.PostgreSQL", "create-index")]
    [InlineData("Ef9SqlServer", "Microsoft.EntityFrameworkCore.SqlServer", "create-index")]
    [InlineData("Ef10", "Microsoft.EntityFrameworkCore.SqlServer", "add-column")]
    [InlineData("Ef10Postgres", "Npgsql.EntityFrameworkCore.PostgreSQL", "add-column")]
    [InlineData("Ef8Design", "Microsoft.EntityFrameworkCore.SqlServer", "drop-column")]
    [InlineData("Ef9Design", "Microsoft.EntityFrameworkCore.SqlServer", "drop-column")]
    [InlineData("Ef10Design", "Microsoft.EntityFrameworkCore.SqlServer", "drop-column")]
    [InlineData("Ef10DesignPrivate", "Microsoft.EntityFrameworkCore.SqlServer", "create-index")]
    public async Task SupportedEfFixtureIsExtractedOutOfProcess(string fixture, string provider, string operationKind)
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", fixture, fixture + "Fixture.csproj"));
        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, null, CancellationToken.None);

        Assert.True(result.Success, $"Extraction failed: {result.Error ?? "(no error returned)"}");
        Assert.Equal(provider, result.Provider);
        Assert.True(result.ProviderSqlGenerated);
        Assert.Contains(result.Operations, operation => operation.Kind == operationKind);
    }

    /// <summary>
    /// The worker copies the scanned project's dependency graph, so the standard design-time package
    /// <c>Microsoft.EntityFrameworkCore.Design</c> puts MSBuild/Roslyn support assemblies next to the project
    /// output. One of those assemblies failing to load must not abort the read-model for an EF Core 8/9/10
    /// project that references it.
    /// </summary>
    [Fact]
    public async Task DesignTimePackageInTheStartupProjectIsExtracted()
    {
        string project = FixtureProject("Ef9DesignApp");
        string startup = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, "..", "Ef9DesignAppHost", "Ef9DesignAppHost.csproj"));

        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, startup, null, CancellationToken.None);

        Assert.True(result.Success, $"Extraction failed: {result.Error ?? "(no error returned)"}");
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", result.Provider);
        Assert.Contains(result.Operations, operation => operation.Kind == "drop-column" && operation.Migration == "20240206000000_DropStartupDesignLegacyCode");
        Assert.True(result.ProviderSql.Available);
    }

    [Fact]
    public async Task SeparateDesignTimeFactoryCreatesOptionsOnlyContext()
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "EfFactory", "EfFactoryFixture.csproj"));
        string startup = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "EfFactoryStartup", "EfFactoryStartup.csproj"));

        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, startup, "FactoryDbContext", CancellationToken.None);

        Assert.True(result.Success, $"Extraction failed: {result.Error ?? "(no error returned)"}");
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", result.Provider);
        Assert.Contains(result.Operations, operation => operation.Kind == "create-index");
        Assert.Contains(result.Operations, operation => operation.Kind == "alter-column" && operation.Collation == "Latin1_General_100_BIN2" && operation.OldCollation == "Latin1_General_100_CI_AS");
        Assert.Contains(result.Operations, operation => operation.Kind == "unique-constraint");
        Assert.Contains(result.Operations, operation => operation.Kind == "sql-backfill");
        Assert.Contains(result.Operations, operation => operation.Kind == "raw-sql");
        Assert.Contains(result.Operations, operation => operation.Kind == "custom-operation");
        Assert.True(result.ProviderSql.Available);
        Assert.Contains(result.Operations, operation => operation.Migration == "20240505000000_AddFactoryIndex" && operation.Kind == "create-index" && operation.OperationIndex == 0);
        Assert.Contains(result.Operations, operation => operation.Migration == "20240505000000_AddFactoryIndex" && operation.Kind == "raw-sql" && operation.OperationIndex == 1);
        Assert.Contains(result.ProviderSql.Statements, statement => statement.Migration == "20240505000000_AddFactoryIndex" && statement.OperationIndex == 0 && statement.Sql.Contains("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.ProviderSql.Statements, statement => statement.Migration == "20240505000000_AddFactoryIndex" && statement.OperationIndex == 1 && statement.Sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SeparateStartupProjectCreatesOptionsOnlyContextFromApplicationServices()
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "EfStartupServices", "EfStartupServicesFixture.csproj"));
        string startup = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "EfStartupServicesHost", "EfStartupServicesHost.csproj"));

        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, startup, "StartupServicesDbContext", CancellationToken.None);

        Assert.True(result.Success, $"Extraction failed: {result.Error ?? "(no error returned)"}");
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", result.Provider);
        Assert.Contains(result.Operations, operation => operation.Kind == "create-index");
        Assert.True(result.ProviderSql.Available);
    }

    [Fact]
    public async Task MultipleContextsRequireExplicitContextSelection()
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "EfFactory", "EfFactoryFixture.csproj"));

        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("More than one DbContext was found; specify --context.", result.Error);
    }

    [Theory]
    [InlineData("Ef9NarrowSqlServer", "Microsoft.EntityFrameworkCore.SqlServer", "nvarchar(max)", "nvarchar(32)")]
    [InlineData("Ef9NarrowPostgres", "Npgsql.EntityFrameworkCore.PostgreSQL", "text", "character varying(32)")]
    public async Task UnboundedStringNarrowingRetainsPreviousColumnEvidence(string fixture, string provider, string previousType, string currentType)
    {
        string project = FixtureProject(fixture);

        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, null, CancellationToken.None);

        Assert.True(result.Success, $"Extraction failed: {result.Error ?? "(no error returned)"}");
        Assert.Equal(provider, result.Provider);
        NormalizedOperation operation = Assert.Single(result.Operations, candidate => candidate.Kind == "alter-column");
        Assert.True(operation.HasOldColumn);
        Assert.Null(operation.OldMaxLength);
        Assert.Equal(32, operation.MaxLength);
        Assert.Equal(previousType, operation.OldColumnType);
        Assert.Equal(currentType, operation.ColumnType);
    }

    [Fact]
    public async Task ExplicitContextSelectionAnalyzesOnlyTheSelectedContextMigrations()
    {
        string ordersProject = FixtureProject("Ef8MultiContextOrders");
        string customersProject = FixtureProject("Ef8MultiContextCustomers");
        string hostProject = Path.Combine(Path.GetDirectoryName(FixtureProject("Ef8MultiContextHost"))!, "Ef8MultiContextHost.csproj");
        const string ordersMigration = "20240701000000_AddOrdersCodeIndex";
        const string customersMigration = "20240702000000_DropCustomersLegacyName";

        ExtractionResult orders = await ExtractionCoordinator.ExtractAsync(ordersProject, hostProject, "OrdersDbContext", CancellationToken.None);
        Assert.True(orders.Success, $"Extraction failed: {orders.Error ?? "(no error returned)"}");
        Assert.Contains(orders.Operations, operation => operation.Migration == ordersMigration);
        Assert.DoesNotContain(orders.Operations, operation => operation.Migration == customersMigration);
        Assert.DoesNotContain(orders.ProviderSql.Statements, statement => statement.Migration == customersMigration);

        ExtractionResult customers = await ExtractionCoordinator.ExtractAsync(customersProject, hostProject, "CustomersDbContext", CancellationToken.None);
        Assert.True(customers.Success, $"Extraction failed: {customers.Error ?? "(no error returned)"}");
        Assert.Contains(customers.Operations, operation => operation.Migration == customersMigration);
        Assert.DoesNotContain(customers.Operations, operation => operation.Migration == ordersMigration);
        Assert.DoesNotContain(customers.ProviderSql.Statements, statement => statement.Migration == ordersMigration);
    }

    [Fact]
    public async Task SharedAssemblyContextsAnalyzeOnlyMigrationsAttributedToTheSelectedContext()
    {
        string project = FixtureProject("Ef8SharedAssemblyContexts");
        const string betaMigration = "20250102000000_BetaDropLegacy";
        const string gammaMigration = "20250103000000_GammaAddInvoiceReferenceIndex";

        ExtractionResult beta = await ExtractionCoordinator.ExtractAsync(project, project, "BetaDbContext", CancellationToken.None);
        Assert.True(beta.Success, $"Extraction failed: {beta.Error ?? "(no error returned)"}");
        Assert.Contains(beta.Operations, operation => operation.Migration == betaMigration && operation.Kind == "drop-column");
        Assert.Contains(beta.ProviderSql.Statements, statement => statement.Migration == betaMigration);
        Assert.DoesNotContain(beta.Operations, operation => operation.Migration == gammaMigration);
        Assert.DoesNotContain(beta.ProviderSql.Statements, statement => statement.Migration == gammaMigration);

        ExtractionResult gamma = await ExtractionCoordinator.ExtractAsync(project, project, "GammaDbContext", CancellationToken.None);
        Assert.True(gamma.Success, $"Extraction failed: {gamma.Error ?? "(no error returned)"}");
        Assert.Contains(gamma.Operations, operation => operation.Migration == gammaMigration && operation.Kind == "create-index");
        Assert.DoesNotContain(gamma.Operations, operation => operation.Migration == betaMigration);
        Assert.DoesNotContain(gamma.ProviderSql.Statements, statement => statement.Migration == betaMigration);

        ExtractionResult alpha = await ExtractionCoordinator.ExtractAsync(project, project, "AlphaDbContext", CancellationToken.None);
        Assert.True(alpha.Success, $"Extraction failed: {alpha.Error ?? "(no error returned)"}");
        Assert.Empty(alpha.Operations);
        Assert.Empty(alpha.ProviderSql.Statements);
        Assert.False(alpha.ProviderSql.Available);
    }

    [Fact]
    public async Task UnattributedMigrationClassesFailClosedInsteadOfFallingBackToTheMigrationsAssembly()
    {
        string project = FixtureProject("Ef8UnattributedMigrations");

        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, "UnattributedDbContext", CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("UnattributedDropLegacy", result.Error);
        Assert.Contains("[DbContext]", result.Error);
        Assert.Empty(result.Operations);
        Assert.False(result.ProviderSql.Available);
    }

    [Fact]
    public async Task PartialOwnAssemblyTypeLoadFailsClosedWithAssemblySpecificLoaderError()
    {
        string project = FixtureProject("EfPartialOwnAssembly");

        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("EfPartialOwnAssembly", result.Error, StringComparison.Ordinal);
        Assert.Contains("ReflectionTypeLoadException", result.Error, StringComparison.Ordinal);
        Assert.Contains("MissingDependency", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialPeripheralAssemblyIsToleratedAndRecordedAsANote()
    {
        string project = FixtureProject("EfPeripheralPartialAssembly");

        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Notes, note => note.Contains("BrokenPeripheral", StringComparison.Ordinal)
            && note.Contains("ReflectionTypeLoadException", StringComparison.Ordinal)
            && note.Contains("MissingDependency", StringComparison.Ordinal));
    }

    private static string FixtureProject(string fixture)
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", fixture, fixture + "Fixture.csproj"));
}
