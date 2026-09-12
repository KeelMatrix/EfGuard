namespace KeelMatrix.EfGuard.Tests;

/// <summary>
/// Extraction must reuse the dependency graph the caller already restored. It must never restore
/// packages, never contact package feeds, and must fail closed when the graph is unavailable.
/// </summary>
[Collection("Extraction")]
public sealed class OfflineExtractionTests
{
    [Fact]
    public async Task ExtractionUsesTheRestoredGraphWithoutAPackageCacheOrNetwork()
    {
        string project = FixtureProject("Ef9");
        string emptyPackageCache = CreateDirectory("efguard-offline-packages-");
        try
        {
            ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, null, OfflineEnvironment(emptyPackageCache), CancellationToken.None);

            Assert.True(result.Success, $"Offline extraction failed: {result.Error ?? "(no error returned)"}");
            Assert.True(result.ProviderSqlGenerated);
            Assert.NotEmpty(result.Operations);
        }
        finally
        {
            DeleteDirectory(emptyPackageCache);
        }
    }

    [Fact]
    public async Task ExtractionFailsClosedWithoutNetworkWhenTheGraphIsNotRestored()
    {
        string source = Path.GetDirectoryName(FixtureProject("Ef9"))!;
        string projectDirectory = CreateDirectory("efguard-unrestored-");
        string emptyPackageCache = CreateDirectory("efguard-unrestored-packages-");
        try
        {
            File.Copy(Path.Combine(source, "Ef9Fixture.csproj"), Path.Combine(projectDirectory, "Ef9Fixture.csproj"));
            File.Copy(Path.Combine(source, "Fixture.cs"), Path.Combine(projectDirectory, "Fixture.cs"));
            string project = Path.Combine(projectDirectory, "Ef9Fixture.csproj");

            ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, null, OfflineEnvironment(emptyPackageCache), CancellationToken.None);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Contains("dotnet restore", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("does not restore", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(projectDirectory);
            DeleteDirectory(emptyPackageCache);
        }
    }

    private static Dictionary<string, string?> OfflineEnvironment(string packageCache) => new()
    {
        ["NUGET_PACKAGES"] = packageCache,
        ["HTTP_PROXY"] = "http://127.0.0.1:9",
        ["HTTPS_PROXY"] = "http://127.0.0.1:9",
        ["ALL_PROXY"] = "http://127.0.0.1:9",
        ["http_proxy"] = "http://127.0.0.1:9",
        ["https_proxy"] = "http://127.0.0.1:9",
        ["all_proxy"] = "http://127.0.0.1:9"
    };

    private static string FixtureProject(string fixture)
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", fixture, fixture + "Fixture.csproj"));

    private static string CreateDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
