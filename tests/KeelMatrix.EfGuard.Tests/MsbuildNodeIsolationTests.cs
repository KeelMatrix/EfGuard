namespace KeelMatrix.EfGuard.Tests;

public sealed class MsbuildNodeIsolationTests
{
    private sealed record IsolationCase(string Name, string RelativePath, string[] RequiredTokens, int ProviderInvocationCount = 0);

    [Fact]
    public void MSB4166NodeIsolationMatrixCoversEveryRepositoryControlledPath()
    {
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        IsolationCase[] matrix =
        [
            new(
                "ProjectBuilder child builds",
                "src/KeelMatrix.EfGuard.Worker/ProjectBuilder.cs",
                ["MSBUILDDISABLENODEREUSE", "\"-m:1\"", "\"-nodeReuse:false\""]),
            new(
                "ExtractionCoordinator target-framework probe",
                "src/KeelMatrix.EfGuard/ExtractionCoordinator.cs",
                ["MSBUILDDISABLENODEREUSE", "\"-m:1\"", "\"-nodeReuse:false\""]),
            new(
                "build/test.ps1",
                "build/test.ps1",
                ["$env:MSBUILDDISABLENODEREUSE = \"1\"", "\"-m:1\"", "\"-nodeReuse:false\""]),
            new(
                "build/validate.ps1",
                "build/validate.ps1",
                ["$env:MSBUILDDISABLENODEREUSE = \"1\"", "\"-m:1\"", "\"-nodeReuse:false\""]),
            new(
                ".github/workflows/ci.yml",
                ".github/workflows/ci.yml",
                ["MSBUILDDISABLENODEREUSE: \"1\"", "-m:1", "-nodeReuse:false"],
                ProviderInvocationCount: 3),
            new(
                ".github/workflows/release.yml",
                ".github/workflows/release.yml",
                ["MSBUILDDISABLENODEREUSE: \"1\"", "-m:1", "-nodeReuse:false"])
        ];

        foreach (IsolationCase isolationCase in matrix)
        {
            string path = Path.Combine(repositoryRoot, isolationCase.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"The MSB4166 isolation matrix path is missing: {isolationCase.RelativePath}");
            string contents = File.ReadAllText(path);

            foreach (string token in isolationCase.RequiredTokens)
                Assert.Contains(token, contents, StringComparison.Ordinal);

            if (isolationCase.RelativePath.EndsWith(".yml", StringComparison.Ordinal))
                AssertWorkflowInvocationsAreIsolated(contents);

            if (isolationCase.ProviderInvocationCount > 0)
            {
                const string providerIsolation = "\"/p:NodeReuse=false\", \"/p:MaxCpuCount=1\", \"--\"";
                Assert.Equal(isolationCase.ProviderInvocationCount, CountOccurrences(contents, providerIsolation));
            }
        }
    }

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static void AssertWorkflowInvocationsAreIsolated(string contents)
    {
        string[] commands = ExtractRunCommands(contents)
            .Where(command => command.Contains("dotnet restore", StringComparison.Ordinal) ||
                              command.Contains("dotnet build", StringComparison.Ordinal) ||
                              command.Contains("dotnet test", StringComparison.Ordinal) ||
                              command.Contains("dotnet pack", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(commands);
        foreach (string command in commands)
        {
            Assert.Contains("-m:1", command, StringComparison.Ordinal);
            Assert.Contains("-nodeReuse:false", command, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> ExtractRunCommands(string contents)
    {
        string[] lines = contents.Split(["\r\n", "\n"], StringSplitOptions.None);
        for (int index = 0; index < lines.Length; index++)
        {
            string trimmed = lines[index].TrimStart();
            int indentation = lines[index].Length - trimmed.Length;
            if (!trimmed.StartsWith("run:", StringComparison.Ordinal))
                continue;

            string value = trimmed[4..].TrimStart();
            if (value is not ("|" or ">-" or ">"))
            {
                yield return value;
                continue;
            }

            System.Text.StringBuilder block = new();
            for (int next = index + 1; next < lines.Length; next++)
            {
                string nextTrimmed = lines[next].TrimStart();
                int nextIndentation = lines[next].Length - nextTrimmed.Length;
                if (nextTrimmed.Length > 0 && nextIndentation <= indentation)
                    break;

                if (block.Length > 0)
                    block.AppendLine();
                block.Append(nextTrimmed);
                index = next;
            }

            yield return block.ToString();
        }
    }
}
