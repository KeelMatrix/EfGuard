using System.Diagnostics;
using System.Text.RegularExpressions;

namespace KeelMatrix.EfGuard.Worker;

internal static class ProjectBuilder
{
    internal static async Task<ProjectBuildOutcome> BuildAsync(string projectPath, string outputPath)
    {
        Directory.CreateDirectory(outputPath);
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "build", projectPath, "--configuration", "Release", "--nologo", "--no-restore",
            "/p:OutputPath=" + EnsureTrailingSeparator(outputPath),
            "/p:CopyLocalLockFileAssemblies=true"
        })
            startInfo.ArgumentList.Add(argument);

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new ProjectBuildOutcome(false, "The selected EF project could not be built.");
            Task<string> output = ReadBoundedAsync(process.StandardOutput.BaseStream);
            Task<string> error = ReadBoundedAsync(process.StandardError.BaseStream);
            Task waitTask = process.WaitForExitAsync();
            Task completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(90))).ConfigureAwait(false);
            if (completed != waitTask)
            {
                TryKill(process);
                return new ProjectBuildOutcome(false, "The selected EF project did not build within the extraction timeout.");
            }
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode == 0 && output.Result.Length < 65536 && error.Result.Length < 65536)
                return new ProjectBuildOutcome(true, null);
            if (IndicatesMissingRestore(output.Result, error.Result))
                return new ProjectBuildOutcome(false, "The dependency graph for " + Path.GetFileName(projectPath) + " is not restored. Run 'dotnet restore' for the project and its referenced projects, then run EfGuard again; EfGuard does not restore packages or contact package feeds.");
            return new ProjectBuildOutcome(false, "The selected EF project could not be built.");
        }
        catch { return new ProjectBuildOutcome(false, "The selected EF project could not be built."); }
    }

    private static bool IndicatesMissingRestore(string output, string error)
    {
        string combined = output + "\n" + error;
        return Regex.IsMatch(combined, @"NETSDK1004|project\.assets\.json\s*(was not found|not found)|Run a NuGet package restore", RegexOptions.IgnoreCase);
    }

    private static async Task<string> ReadBoundedAsync(Stream stream)
    {
        using MemoryStream memory = new();
        byte[] buffer = new byte[4096];
        while (memory.Length < 65536)
        {
            int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
                break;
            memory.Write(buffer, 0, Math.Min(read, 65536 - (int)memory.Length));
            if (read > 65536 - memory.Length)
                break;
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    private static string EnsureTrailingSeparator(string path) => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch { } }
}

internal sealed record ProjectBuildOutcome(bool Success, string? Error);
