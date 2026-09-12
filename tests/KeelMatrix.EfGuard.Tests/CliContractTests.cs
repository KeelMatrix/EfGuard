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
        ProcessResult result = await RunCliAsync(["check", "--project", "fixtures/Ef8Clean/Ef8CleanFixture.csproj", "--baseline", "HEAD", "--format", "json"]);

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
    public async Task BuiltExecutableConsoleFindingRendersAffectedStateAndRiskDimensions()
    {
        string repository = CreateCompatibilityFixtureRepository();
        try
        {
            ProcessResult result = await RunCliAsync(["check", "--project", Path.Combine(repository, "EfFixture.csproj"), "--baseline", "HEAD", "--format", "console"]);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Affected state: Previous application + target schema", result.StandardOutput);
            Assert.Contains("Risk dimension(s): compatibility", result.StandardOutput);
            Assert.Empty(result.StandardError);
        }
        finally
        {
            DeleteDirectory(repository);
        }
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

    [Fact]
    public async Task BuiltExecutableUsesSeparateStartupProjectApplicationServices()
    {
        ProcessResult result = await RunCliAsync([
            "check",
            "--project", "fixtures/EfStartupServices/EfStartupServicesFixture.csproj",
            "--startup-project", "fixtures/EfStartupServicesHost/EfStartupServicesHost.csproj",
            "--context", "StartupServicesDbContext",
            "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", document.RootElement.GetProperty("provider").GetString());
        Assert.True(document.RootElement.GetProperty("providerSql").GetProperty("available").GetBoolean());
        Assert.Contains(document.RootElement.GetProperty("diagnostics").EnumerateArray(), diagnostic => diagnostic.GetProperty("ruleId").GetString() == "EFG399");
        Assert.Empty(document.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task BuiltExecutableReportsNoSharedAssemblyMigrationForAContextWithoutMigrations()
    {
        ProcessResult result = await RunCliAsync([
            "check",
            "--project", "fixtures/Ef8SharedAssemblyContexts/Ef8SharedAssemblyContextsFixture.csproj",
            "--context", "AlphaDbContext",
            "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Assert.Empty(document.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("EFG399", Assert.Single(document.RootElement.GetProperty("diagnostics").EnumerateArray()).GetProperty("ruleId").GetString());
        Assert.Empty(document.RootElement.GetProperty("providerSql").GetProperty("statements").EnumerateArray());
        Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("operationsInspected").GetInt32());
        Assert.DoesNotContain("BetaDropLegacy", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    private static async Task<ProcessResult> RunCliAsync(string[] arguments)
    {
        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string cli = Path.Combine(root, "src", "KeelMatrix.EfGuard", "bin", "Release", "net8.0", "KeelMatrix.EfGuard.dll");
        return await ProcessRunner.RunAsync("dotnet", [cli, .. arguments], root, TimeSpan.FromSeconds(60), CancellationToken.None);
    }

    private static string CreateCompatibilityFixtureRepository()
    {
        string repository = Path.Combine(Path.GetTempPath(), "efguard-console-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        string fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "Ef8"));
        File.Copy(Path.Combine(fixtures, "Ef8Fixture.csproj"), Path.Combine(repository, "EfFixture.csproj"));
        string source = File.ReadAllText(Path.Combine(fixtures, "Fixture.cs"));
        File.WriteAllText(Path.Combine(repository, "Fixture.cs"), source);

        RunGit(repository, ["init", "--initial-branch", "main"]);
        RunGit(repository, ["config", "user.name", "KeelMatrix"]);
        RunGit(repository, ["config", "user.email", "keelmatrix@gmail.com"]);
        RunGit(repository, ["add", "."]);
        RunGit(repository, ["commit", "-m", "Add compatibility fixture"]);
        RunDotnet(repository, ["restore", "EfFixture.csproj"]);

        string currentSource = source.Replace("            entity.Property(order => order.LegacyCode).HasMaxLength(32);\r\n", "", StringComparison.Ordinal)
            .Replace("            entity.Property(order => order.LegacyCode).HasMaxLength(32);\n", "", StringComparison.Ordinal)
            .Replace("    public string? LegacyCode { get; set; }\r\n", "", StringComparison.Ordinal)
            .Replace("    public string? LegacyCode { get; set; }\n", "", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(repository, "Fixture.cs"), currentSource);
        return repository;
    }

    private static void RunGit(string repository, string[] arguments)
    {
        ProcessResult result = ProcessRunner.RunAsync("git", ["-C", repository, .. arguments], repository, TimeSpan.FromSeconds(30), CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(0, result.ExitCode);
    }

    private static void RunDotnet(string directory, string[] arguments)
    {
        ProcessResult result = ProcessRunner.RunAsync("dotnet", arguments, directory, TimeSpan.FromSeconds(120), CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(0, result.ExitCode);
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch { }
    }
}
