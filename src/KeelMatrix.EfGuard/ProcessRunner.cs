using System.Diagnostics;
using System.Text;

namespace KeelMatrix.EfGuard;

internal sealed record ProcessResult(int ExitCode, bool TimedOut, bool OutputExceeded, string StandardOutput, string StandardError);
internal sealed record BoundedOutput(string Text, bool Exceeded);

internal static class ProcessRunner
{
    internal const int MaxOutputBytes = 128 * 1024;

    internal static Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        => RunAsync(fileName, arguments, workingDirectory, timeout, null, cancellationToken);

    internal static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
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
        if (environment is not null)
        {
            foreach ((string key, string? value) in environment)
            {
                if (value is null)
                    _ = startInfo.Environment.Remove(key);
                else
                    startInfo.Environment[key] = value;
            }
        }

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

        Task<BoundedOutput> stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, cancellationToken);
        Task<BoundedOutput> stderr = ReadBoundedAsync(process.StandardError.BaseStream, cancellationToken);
        Task wait = process.WaitForExitAsync(cancellationToken);
        Task completed = await Task.WhenAny(wait, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        if (completed != wait)
        {
            TryKill(process);
            BoundedOutput timedOutOutput = await SafeResult(stdout).ConfigureAwait(false);
            BoundedOutput timedOutError = await SafeResult(stderr).ConfigureAwait(false);
            return new ProcessResult(-1, true, timedOutOutput.Exceeded || timedOutError.Exceeded, timedOutOutput.Text, timedOutError.Text);
        }

        BoundedOutput output = await SafeResult(stdout).ConfigureAwait(false);
        BoundedOutput error = await SafeResult(stderr).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, false, output.Exceeded || error.Exceeded, output.Text, error.Text);
    }

    private static async Task<BoundedOutput> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        StringBuilder result = new();
        byte[] buffer = new byte[4096];
        int storedBytes = 0;
        bool exceeded = false;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            int remaining = MaxOutputBytes - storedBytes;
            int toStore = Math.Min(read, Math.Max(remaining, 0));
            if (toStore > 0)
            {
                result.Append(Encoding.UTF8.GetString(buffer, 0, toStore));
                storedBytes += toStore;
            }
            if (read > toStore)
                exceeded = true;
        }
        return new BoundedOutput(result.ToString(), exceeded);
    }

    private static async Task<BoundedOutput> SafeResult(Task<BoundedOutput> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return new BoundedOutput("", false); }
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
