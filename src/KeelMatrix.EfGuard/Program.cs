using System.Text;
using System.Text.Json;
using KeelMatrix.Telemetry;

namespace KeelMatrix.EfGuard;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        CliOptions? options;
        try
        {
            options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.Out.WriteLine(CliOptions.HelpText);
                return 0;
            }
        }
        catch (InvalidOperationException exception)
        {
            return WriteError(exception.Message, RequestsJson(args));
        }

        if (!options.Command.Equals("check", StringComparison.OrdinalIgnoreCase))
            return WriteError("The supported command is 'check'.", options.Format == OutputFormat.Json);

        Report report = new();
        try
        {
            string currentDirectory = Environment.CurrentDirectory;
            string projectPath = ExtractionCoordinator.ResolveProject(options.Project, currentDirectory);
            string? startupPath = options.StartupProject is null ? null : ExtractionCoordinator.ResolveProject(options.StartupProject, currentDirectory);
            string? repositoryRoot = ExtractionCoordinator.FindRepositoryRoot(Path.GetDirectoryName(projectPath) ?? currentDirectory);
            string configPath = Path.Combine(repositoryRoot ?? currentDirectory, "efguard.json");
            GuardConfig config = ConfigurationLoader.Load(configPath);

            ExtractionResult current = await ExtractionCoordinator.ExtractAsync(projectPath, startupPath, options.Context, CancellationToken.None).ConfigureAwait(false);
            ExtractionResult? baseline = null;
            bool baselineFailure = false;
            if (options.Baseline is not null)
            {
                baseline = await ExtractionCoordinator.ExtractBaselineAsync(projectPath, startupPath, options.Context, options.Baseline, CancellationToken.None).ConfigureAwait(false);
                if (baseline is not null && !baseline.Success)
                {
                    string baselineError = baseline.Error ?? "The baseline EF model could not be extracted.";
                    baseline = null;
                    report = new Report { Errors = [baselineError], Summary = new ReportSummary { ExitCode = 2 }, Baseline = new BaselineReport { Requested = true, Reference = options.Baseline, Available = false } };
                    baselineFailure = true;
                }
            }
            if (!baselineFailure)
                report = Analyzer.Analyze(current, baseline, config, options.Baseline);
        }
        catch (InvalidOperationException exception)
        {
            report = new Report { Errors = [exception.Message], Summary = new ReportSummary { ExitCode = 2 }, Baseline = new BaselineReport { Requested = options.Baseline is not null, Reference = options.Baseline } };
        }
        catch
        {
            report = new Report { Errors = ["EfGuard could not complete the analysis."], Summary = new ReportSummary { ExitCode = 2 }, Baseline = new BaselineReport { Requested = options.Baseline is not null, Reference = options.Baseline } };
        }

        if (report.Summary.ExitCode != 2)
            TrackTelemetry();

        if (options.Format == OutputFormat.Json)
            await Console.Out.WriteLineAsync(ReportSerialization.Serialize(report)).ConfigureAwait(false);
        else
            await Console.Out.WriteLineAsync(FormatConsole(report)).ConfigureAwait(false);
        return report.Summary.ExitCode;
    }

    private static void TrackTelemetry()
    {
        try
        {
            Client client = new("efguard", typeof(Program));
            client.TrackActivation();
            client.TrackHeartbeat();
        }
        catch { }
    }

    private static int WriteError(string message, bool json)
    {
        Report report = new() { Errors = [message], Summary = new ReportSummary { ExitCode = 2 } };
        if (json)
            Console.Out.WriteLine(ReportSerialization.Serialize(report));
        else
            Console.Error.WriteLine($"EfGuard error: {message}");
        return 2;
    }

    private static bool RequestsJson(string[] args)
    {
        for (int index = 0; index < args.Length - 1; index++)
            if (args[index].Equals("--format", StringComparison.OrdinalIgnoreCase)
                && args[index + 1].Equals("json", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string FormatConsole(Report report)
    {
        StringBuilder output = new();
        _ = output.AppendLine("EfGuard report");
        if (report.Provider is not null)
            _ = output.AppendLine($"Provider: {report.Provider}");
        if (report.Baseline.Requested)
            _ = output.AppendLine($"Baseline: {report.Baseline.Reference} ({(report.Baseline.Available ? "loaded" : "unavailable")})");
        _ = output.AppendLine();

        foreach (string error in report.Errors)
            _ = output.AppendLine($"ERROR: {error}");

        foreach (Diagnostic diagnostic in report.Diagnostics)
        {
            string suffix = diagnostic.Suppressed ? " [suppressed]" : "";
            _ = output.AppendLine($"{diagnostic.RuleId} — {diagnostic.Title}{suffix}");
            _ = output.AppendLine($"Severity: {FormatSeverity(diagnostic.Severity)}");
            _ = output.AppendLine($"Confidence: {diagnostic.Confidence}");
            if (diagnostic.Provider is not null)
                _ = output.AppendLine($"Provider: {diagnostic.Provider}");
            if (diagnostic.Location is not null)
                _ = output.AppendLine($"Location: {diagnostic.Location}");
            _ = output.AppendLine();
            _ = output.AppendLine(diagnostic.Explanation);
            _ = output.AppendLine();
            _ = output.AppendLine("Recommended rollout:");
            _ = output.AppendLine($"  {diagnostic.Remediation}");
            _ = output.AppendLine();
            _ = output.AppendLine($"Note: {diagnostic.Uncertainty}");
            _ = output.AppendLine();
        }

        ReportSummary summary = report.Summary;
        _ = output.AppendLine($"{summary.OperationsInspected} migration operations inspected");
        _ = output.AppendLine($"{summary.Compatible} compatible under evaluated rules");
        _ = output.AppendLine($"{summary.Advisory} advisory, {summary.High} high, {summary.Blocking} blocking, {summary.Unverified} unverified");
        _ = output.AppendLine($"Exit code: {summary.ExitCode}");
        return output.ToString().TrimEnd();
    }

    private static string FormatSeverity(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Block => "BLOCK",
        FindingSeverity.High => "HIGH",
        FindingSeverity.Advisory => "ADVISORY",
        _ => "UNVERIFIED"
    };
}

internal enum OutputFormat { Console, Json }

internal sealed class CliOptions
{
    public string Command { get; private init; } = "check";
    public string? Baseline { get; private set; }
    public string? Project { get; private set; }
    public string? StartupProject { get; private set; }
    public string? Context { get; private set; }
    public OutputFormat Format { get; private set; }
    public bool ShowHelp { get; private set; }

    internal static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
            return new CliOptions { ShowHelp = true, Format = OutputFormat.Console };
        if (args.Contains("--help") || args.Contains("-h"))
            return new CliOptions { ShowHelp = true, Format = OutputFormat.Console };

        CliOptions options = new() { Command = args[0], Format = OutputFormat.Console };
        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            string? value = null;
            if (index + 1 < args.Length)
                value = args[++index];
            switch (argument)
            {
                case "--baseline": options.Baseline = Required(argument, value); break;
                case "--project": options.Project = Required(argument, value); break;
                case "--startup-project": options.StartupProject = Required(argument, value); break;
                case "--context": options.Context = Required(argument, value); break;
                case "--format":
                    string format = Required(argument, value);
                    options.Format = format.Equals("json", StringComparison.OrdinalIgnoreCase) ? OutputFormat.Json : format.Equals("console", StringComparison.OrdinalIgnoreCase) ? OutputFormat.Console : throw new InvalidOperationException("--format must be console or json.");
                    break;
                default: throw new InvalidOperationException($"Unknown option '{argument}'.");
            }
        }
        return options;
    }

    private static string Required(string option, string? value)
        => string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) ? throw new InvalidOperationException($"{option} requires a value.") : value;

    internal const string HelpText = """
EfGuard checks EF Core migrations for rolling-deployment compatibility and provider risks.

Usage:
  efguard check [--baseline <git-ref>] [--project <path>] [--startup-project <path>]
                [--context <DbContext>] [--format console|json]

Exit codes:
  0  trustworthy analysis completed with no blocking finding
  1  trustworthy analysis completed with a blocking or unverified finding
  2  analysis could not complete trustworthily

Design-time startup resolution:
  --startup-project first uses the startup application's scoped services or IDbContextFactory,
  then falls back to IDesignTimeDbContextFactory and a parameterless DbContext constructor.
""";
}
