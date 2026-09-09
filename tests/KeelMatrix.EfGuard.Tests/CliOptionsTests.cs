namespace KeelMatrix.EfGuard.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void RequiredOptionsParse()
    {
        CliOptions options = CliOptions.Parse(["check", "--baseline", "origin/main", "--project", "src/App.csproj", "--startup-project", "src/Api.csproj", "--context", "AppContext", "--format", "json"]);

        Assert.Equal("origin/main", options.Baseline);
        Assert.Equal("src/App.csproj", options.Project);
        Assert.Equal(OutputFormat.Json, options.Format);
    }

    [Fact]
    public void UnknownFormatIsRejected()
        => Assert.Throws<InvalidOperationException>(() => CliOptions.Parse(["check", "--format", "xml"]));

    [Fact]
    public void NoArgumentsAndHelpAreRecognized()
    {
        Assert.True(CliOptions.Parse([]).ShowHelp);
        Assert.True(CliOptions.Parse(["check", "--help"]).ShowHelp);
    }

    [Theory]
    [InlineData("--baseline")]
    [InlineData("--project")]
    [InlineData("--startup-project")]
    [InlineData("--context")]
    [InlineData("--format")]
    public void SupportedOptionsRejectMissingValues(string option)
        => Assert.Throws<InvalidOperationException>(() => CliOptions.Parse(["check", option]));

    [Fact]
    public void UnknownCommandAndOptionAreRejected()
    {
        Assert.Equal("something-else", CliOptions.Parse(["something-else"]).Command);
        Assert.Throws<InvalidOperationException>(() => CliOptions.Parse(["check", "--not-supported"]));
    }
}
