namespace KeelMatrix.EfGuard.Tests;

[Collection("Extraction")]
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task ProcessCompletesWithBoundedOutput()
    {
        ProcessResult result = await ProcessRunner.RunAsync("dotnet", ["--version"], Environment.CurrentDirectory, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StandardOutput);
    }

    [Fact]
    public async Task ProcessTimeoutKillsLongRunningChild()
    {
        ProcessResult result = await RunProbeAsync("sleep", TimeSpan.FromMilliseconds(250));

        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task ProcessCrashReturnsFailureWithoutThrowing()
    {
        ProcessResult result = await RunProbeAsync("crash", TimeSpan.FromSeconds(10));

        Assert.False(result.TimedOut);
        Assert.Equal(17, result.ExitCode);
    }

    [Fact]
    public async Task NoisyChildIsDrainedAndMarkedBeyondOutputLimit()
    {
        ProcessResult result = await RunProbeAsync("output", TimeSpan.FromSeconds(10));

        Assert.False(result.TimedOut);
        Assert.True(result.OutputExceeded);
        Assert.Equal(ProcessRunner.MaxOutputBytes, System.Text.Encoding.UTF8.GetByteCount(result.StandardOutput));
    }

    [Fact]
    public async Task ExtractionTempDirectoryIsRemovedAfterFailure()
    {
        string[] before = Directory.GetDirectories(Path.GetTempPath(), "efguard-*");
        string missingProject = Path.Combine(Path.GetTempPath(), "missing-efguard-" + Guid.NewGuid().ToString("N"), "Missing.csproj");

        await Assert.ThrowsAnyAsync<Exception>(() => ExtractionCoordinator.ExtractAsync(missingProject, missingProject, null, CancellationToken.None));

        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "efguard-*"));
    }

    [Fact]
    public async Task NonzeroWorkerExitDoesNotTrustItsResponseFile()
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "Ef8", "Ef8Fixture.csproj"));

        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, "MissingContext", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("The requested DbContext was not found.", result.Error);
    }

    [Fact]
    public async Task NonzeroWorkerExitWithSuccessfulResponseIsRejected()
    {
        string responsePath = Path.Combine(Path.GetTempPath(), "efguard-response-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(responsePath, "{\"success\":true,\"provider\":\"Microsoft.EntityFrameworkCore.SqlServer\"}");
            ExtractionResult result = await ExtractionCoordinator.ReadWorkerResponseAsync(
                new ProcessResult(17, false, false, "", ""), responsePath, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("EF extraction worker failed.", result.Error);
        }
        finally
        {
            if (File.Exists(responsePath))
                File.Delete(responsePath);
        }
    }

    [Fact]
    public void BaselinePathDoesNotUseActiveWorktreeAsExtractionDirectory()
    {
        string? root = ExtractionCoordinator.FindRepositoryRoot(Environment.CurrentDirectory);
        Assert.NotNull(root);
        Assert.NotEqual(Path.GetFullPath(root), Path.GetFullPath(Path.GetTempPath()));
    }

    [Fact]
    public async Task BaselineExtractionUsesRealGitArchiveWithoutMutatingWorktree()
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "Ef8", "Ef8Fixture.csproj"));
        string fixtureSource = Path.Combine(Path.GetDirectoryName(project)!, "Fixture.cs");
        byte[] before = await File.ReadAllBytesAsync(fixtureSource);
        string[] tempBefore = Directory.GetDirectories(Path.GetTempPath(), "efguard-baseline-*");

        ExtractionResult? baseline = await ExtractionCoordinator.ExtractBaselineAsync(project, project, null, "HEAD", CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.True(baseline.Success, baseline.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixtureSource));
        Assert.Equal(tempBefore, Directory.GetDirectories(Path.GetTempPath(), "efguard-baseline-*"));
    }

    private static Task<ProcessResult> RunProbeAsync(string command, TimeSpan timeout)
    {
        string probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ProcessProbe", "bin", "Release", "net8.0", "ProcessProbe.dll"));
        return ProcessRunner.RunAsync("dotnet", [probe, command], Path.GetDirectoryName(probe), timeout, CancellationToken.None);
    }
}
