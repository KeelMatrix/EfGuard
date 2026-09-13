namespace KeelMatrix.EfGuard;

internal static class ExitCodeContract
{
    internal const int Clean = 0;
    internal const int Findings = 1;
    internal const int Untrustworthy = 2;

    private const string CleanOutcome = "no configured blocking or unverified diagnostic";
    private const string FindingsOutcome = "a blocking or unverified diagnostic";
    private const string UntrustworthyOutcome = "analysis could not complete trustworthily because of configuration, extraction, build, provider, baseline, or internal error";

    private const string CleanDescription = $"trustworthy analysis completed with {CleanOutcome}";
    private const string FindingsDescription = $"trustworthy analysis completed with {FindingsOutcome}";
    private const string UntrustworthyDescription = UntrustworthyOutcome;

    internal static string HelpText => string.Join(Environment.NewLine,
    [
        $"  {Clean}  {CleanDescription}",
        $"  {Findings}  {FindingsDescription}",
        $"  {Untrustworthy}  {UntrustworthyDescription}"
    ]);

    internal static string ReadmeSection => string.Join(Environment.NewLine,
    [
        "## Exit Codes and Output",
        "",
        $"- `{Clean}`: {CleanDescription}.",
        $"- `{Findings}`: {FindingsDescription}.",
        $"- `{Untrustworthy}`: {UntrustworthyDescription}."
    ]);

    internal static string ChangelogSection => string.Join(Environment.NewLine,
    [
        $"- Console reports render the reason, affected state, risk dimensions, provider evidence, remediation guidance, and uncertainty; versioned JSON reports retain the same stable diagnostic contract and exit codes `{Clean}` ({CleanOutcome}), `{Findings}` ({FindingsOutcome}), and `{Untrustworthy}` ({UntrustworthyOutcome})."
    ]);
}
