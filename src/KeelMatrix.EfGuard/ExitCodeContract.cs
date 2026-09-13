namespace KeelMatrix.EfGuard;

internal static class ExitCodeContract
{
    internal const int Clean = 0;
    internal const int Findings = 1;
    internal const int Untrustworthy = 2;

    private const string CleanDescription = "trustworthy analysis completed with no configured blocking or unverified diagnostic";
    private const string FindingsDescription = "trustworthy analysis completed with a blocking or unverified diagnostic";
    private const string UntrustworthyDescription = "analysis could not complete trustworthily because of configuration, extraction, build, provider, baseline, or internal error";

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
}
