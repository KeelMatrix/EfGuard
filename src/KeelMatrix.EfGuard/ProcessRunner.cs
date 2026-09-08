using System.Diagnostics;
using System.Text;

namespace KeelMatrix.EfGuard;

internal sealed record ProcessResult(int ExitCode, bool TimedOut, bool OutputExceeded, string StandardOutput, string StandardError);

internal static class ProcessRunner
{
    internal const int MaxOutputBytes = 128 * 1024;

    internal static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                return new ProcessResult(-1, false, false, "", "");
        }
        catch
        {
            return new ProcessResult(-1, false, false, "", "");
        }

        Task<string> stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, cancellationToken);
        Task<string> stderr = ReadBoundedAsync(process.StandardError.BaseStream, cancellationToken);
        Task wait = process.WaitForExitAsync(cancellationToken);
        Task completed = await Task.WhenAny(wait, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed != wait)
        {
            TryKill(process);
            return new ProcessResult(-1, true, false, await SafeResult(stdout).ConfigureAwait(false), await SafeResult(stderr).ConfigureAwait(false));
        }

        string output = await SafeResult(stdout).ConfigureAwait(false);
        string error = await SafeResult(stderr).ConfigureAwait(false);
        bool outputExceeded = output.Length >= MaxOutputBytes || error.Length >= MaxOutputBytes;
        return new ProcessResult(process.ExitCode, false, outputExceeded, output, error);
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        StringBuilder result = new();
        byte[] buffer = new byte[4096];
        while (result.Length < MaxOutputBytes)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            int remaining = MaxOutputBytes - result.Length;
            result.Append(Encoding.UTF8.GetString(buffer, 0, Math.Min(read, remaining)));
            if (read > remaining)
                break;
        }
        return result.ToString();
    }

    private static async Task<string> SafeResult(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return ""; }
    }

    internal static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
