namespace KeelMatrix.EfGuard.Tests;

[Collection("Extraction")]
public sealed class ExtractionFixtureTests
{
    [Theory]
    [InlineData("Ef8", "Microsoft.EntityFrameworkCore.SqlServer", "drop-column")]
    [InlineData("Ef8Postgres", "Npgsql.EntityFrameworkCore.PostgreSQL", "create-index")]
    [InlineData("Ef9", "Npgsql.EntityFrameworkCore.PostgreSQL", "create-index")]
    [InlineData("Ef9SqlServer", "Microsoft.EntityFrameworkCore.SqlServer", "create-index")]
    [InlineData("Ef10", "Microsoft.EntityFrameworkCore.SqlServer", "add-column")]
    [InlineData("Ef10Postgres", "Npgsql.EntityFrameworkCore.PostgreSQL", "add-column")]
    public async Task SupportedEfFixtureIsExtractedOutOfProcess(string fixture, string provider, string operationKind)
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", fixture, fixture + "Fixture.csproj"));
        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, null, CancellationToken.None);

        Assert.True(result.Success, $"Extraction failed: {result.Error ?? "(no error returned)"}");
        Assert.Equal(provider, result.Provider);
        Assert.True(result.ProviderSqlGenerated);
        Assert.Contains(result.Operations, operation => operation.Kind == operationKind);
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
        Assert.Contains(result.ProviderSql.Statements, statement => statement.Migration == "20240505000000_AddFactoryIndex");
    }

    [Fact]
    public async Task MultipleContextsRequireExplicitContextSelection()
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "EfFactory", "EfFactoryFixture.csproj"));

        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("More than one DbContext was found; specify --context.", result.Error);
    }
}
