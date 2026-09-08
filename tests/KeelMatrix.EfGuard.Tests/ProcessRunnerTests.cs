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
    public void BaselinePathDoesNotUseActiveWorktreeAsExtractionDirectory()
    {
        string? root = ExtractionCoordinator.FindRepositoryRoot(Environment.CurrentDirectory);
        Assert.NotNull(root);
        Assert.NotEqual(Path.GetFullPath(root), Path.GetFullPath(Path.GetTempPath()));
    }

    private static Task<ProcessResult> RunProbeAsync(string command, TimeSpan timeout)
    {
        string probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ProcessProbe", "bin", "Release", "net8.0", "ProcessProbe.dll"));
        return ProcessRunner.RunAsync("dotnet", [probe, command], Path.GetDirectoryName(probe), timeout, CancellationToken.None);
    }
}
