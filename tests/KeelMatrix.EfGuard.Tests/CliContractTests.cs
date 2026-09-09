using System.Text.Json;

namespace KeelMatrix.EfGuard.Tests;

[Collection("Extraction")]
public sealed class CliContractTests
{
    [Fact]
    public async Task BuiltExecutableWithoutArgumentsPrintsHelpToStandardOutput()
    {
        ProcessResult result = await RunCliAsync([]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task BuiltExecutableReturnsCleanJsonForACompletedScan()
    {
        ProcessResult result = await RunCliAsync(["check", "--project", "fixtures/Ef8Clean/Ef8CleanFixture.csproj", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("exitCode").GetInt32());
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task BuiltExecutableReturnsBlockingExitCodeAndValidJson()
    {
        ProcessResult result = await RunCliAsync(["check", "--project", "fixtures/Ef10/Ef10Fixture.csproj", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Assert.Contains(document.RootElement.GetProperty("diagnostics").EnumerateArray(), diagnostic => diagnostic.GetProperty("ruleId").GetString() == "EFG102");
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task BuiltExecutableReturnsJsonConfigurationFailureForMissingOptionValue()
    {
        ProcessResult result = await RunCliAsync(["check", "--format", "json", "--context"]);

        Assert.Equal(2, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Assert.Contains("requires a value", document.RootElement.GetProperty("errors")[0].GetString());
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task BuiltExecutableWritesConsoleContractErrorsToStandardError()
    {
        ProcessResult result = await RunCliAsync(["check", "--format", "console", "--unknown"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.StartsWith("EfGuard error:", result.StandardError);
    }

    [Fact]
    public async Task BuiltExecutableRejectsInvalidFormatOnStandardError()
    {
        ProcessResult result = await RunCliAsync(["check", "--format", "xml"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("format", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuiltExecutableRejectsUnknownCommandOnStandardError()
    {
        ProcessResult result = await RunCliAsync(["inspect"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("supported command", result.StandardError);
    }

    [Fact]
    public async Task BuiltExecutableSupportsBaselineOption()
    {
        ProcessResult result = await RunCliAsync(["check", "--project", "fixtures/Ef8/Ef8Fixture.csproj", "--baseline", "HEAD", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Assert.True(document.RootElement.GetProperty("baseline").GetProperty("requested").GetBoolean());
        Assert.True(document.RootElement.GetProperty("baseline").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task BuiltExecutableSupportsSeparateStartupProject()
    {
        ProcessResult result = await RunCliAsync([
            "check",
            "--project", "fixtures/EfFactory/EfFactoryFixture.csproj",
            "--startup-project", "fixtures/EfFactoryStartup/EfFactoryStartup.csproj",
            "--context", "FactoryDbContext",
            "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", document.RootElement.GetProperty("provider").GetString());
        Assert.True(document.RootElement.GetProperty("providerSql").GetProperty("available").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("exitCode").GetInt32());
        Assert.Contains(document.RootElement.GetProperty("diagnostics").EnumerateArray(), diagnostic => diagnostic.GetProperty("ruleId").GetString() == "EFG399");
        Assert.Empty(document.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Empty(result.StandardError);
    }

    private static async Task<ProcessResult> RunCliAsync(string[] arguments)
    {
        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string cli = Path.Combine(root, "src", "KeelMatrix.EfGuard", "bin", "Release", "net8.0", "KeelMatrix.EfGuard.dll");
        return await ProcessRunner.RunAsync("dotnet", [cli, .. arguments], root, TimeSpan.FromSeconds(60), CancellationToken.None);
    }
}
