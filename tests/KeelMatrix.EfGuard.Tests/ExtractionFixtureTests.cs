namespace KeelMatrix.EfGuard.Tests;

public sealed class ExtractionFixtureTests
{
    [Theory]
    [InlineData("Ef8", "Microsoft.EntityFrameworkCore.SqlServer", "drop-column")]
    [InlineData("Ef9", "Npgsql.EntityFrameworkCore.PostgreSQL", "create-index")]
    [InlineData("Ef10", "Microsoft.EntityFrameworkCore.SqlServer", "add-column")]
    public async Task SupportedEfFixtureIsExtractedOutOfProcess(string fixture, string provider, string operationKind)
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", fixture, fixture + "Fixture.csproj"));
        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(provider, result.Provider);
        Assert.True(result.ProviderSqlGenerated);
        Assert.Contains(result.Operations, operation => operation.Kind == operationKind);
    }
}
