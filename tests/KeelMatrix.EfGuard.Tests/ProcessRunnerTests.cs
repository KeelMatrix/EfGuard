namespace KeelMatrix.EfGuard.Tests;

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
    public void BaselinePathDoesNotUseActiveWorktreeAsExtractionDirectory()
    {
        string? root = ExtractionCoordinator.FindRepositoryRoot(Environment.CurrentDirectory);
        Assert.NotNull(root);
        Assert.NotEqual(Path.GetFullPath(root), Path.GetFullPath(Path.GetTempPath()));
    }
}
