using System.IO.Compression;
using System.Text.Json;

namespace KeelMatrix.EfGuard;

internal static class ExtractionCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static async Task<ExtractionResult> ExtractAsync(string projectPath, string? startupProjectPath, string? contextName, CancellationToken cancellationToken)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "efguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            return await RunWorkerAsync(projectPath, startupProjectPath ?? projectPath, contextName, tempRoot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    internal static async Task<ExtractionResult?> ExtractBaselineAsync(string projectPath, string? startupProjectPath, string? contextName, string contextRef, CancellationToken cancellationToken)
    {
        string? repositoryRoot = FindRepositoryRoot(Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory);
        if (repositoryRoot is null)
            throw new InvalidOperationException("A baseline requires a Git repository.");

        string relativeProject = Path.GetRelativePath(repositoryRoot, projectPath);
        string relativeStartup = Path.GetRelativePath(repositoryRoot, startupProjectPath ?? projectPath);
        if (relativeProject.StartsWith("..", StringComparison.Ordinal) || relativeStartup.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Project and startup project must be inside the repository for baseline analysis.");

        string tempRoot = Path.Combine(Path.GetTempPath(), "efguard-baseline-" + Guid.NewGuid().ToString("N"));
        string archivePath = tempRoot + ".zip";
        Directory.CreateDirectory(tempRoot);
        try
        {
            ProcessResult archive = await ProcessRunner.RunAsync("git", ["-C", repositoryRoot, "archive", "--format=zip", "--output", archivePath, contextRef], repositoryRoot, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            if (archive.ExitCode != 0 || archive.TimedOut || !File.Exists(archivePath))
                throw new InvalidOperationException("The requested baseline Git reference could not be archived.");

            ZipFile.ExtractToDirectory(archivePath, tempRoot);
            string baselineProject = Path.Combine(tempRoot, relativeProject);
            string baselineStartup = Path.Combine(tempRoot, relativeStartup);
            if (!File.Exists(baselineProject) || !File.Exists(baselineStartup))
                throw new InvalidOperationException("The requested baseline does not contain the selected project.");

            return await RunWorkerAsync(baselineProject, baselineStartup, contextName, tempRoot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(archivePath);
            TryDelete(tempRoot);
        }
    }

    internal static string? FindRepositoryRoot(string startingDirectory)
    {
        DirectoryInfo? directory = new(Path.GetFullPath(startingDirectory));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    internal static string ResolveProject(string? value, string currentDirectory)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            string explicitPath = Path.GetFullPath(value, currentDirectory);
            if (Directory.Exists(explicitPath))
                return SelectProject(explicitPath);
            if (!File.Exists(explicitPath) || !explicitPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Project path '{Path.GetFileName(explicitPath)}' is not an existing .csproj.");
            return explicitPath;
        }

        return SelectProject(currentDirectory);
    }

    private static string SelectProject(string directory)
    {
        string[] projects = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).ToArray();
        if (projects.Length == 0)
            throw new InvalidOperationException("No .csproj was found. Use --project <path>.");
        if (projects.Length > 1)
            throw new InvalidOperationException("More than one .csproj was found. Use --project <path>.");
        return projects[0];
    }

    private static async Task<ExtractionResult> RunWorkerAsync(string projectPath, string startupProjectPath, string? contextName, string tempRoot, CancellationToken cancellationToken)
    {
        string outputDirectory = Path.Combine(tempRoot, "worker-output");
        string requestPath = Path.Combine(tempRoot, "request.json");
        string responsePath = Path.Combine(tempRoot, "response.json");
        Directory.CreateDirectory(outputDirectory);

        ExtractionRequest request = new()
        {
            ProjectPath = projectPath,
            StartupProjectPath = startupProjectPath,
            ContextName = contextName,
            OutputDirectory = outputDirectory,
            ResponsePath = responsePath
        };
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), cancellationToken).ConfigureAwait(false);

        string workerPath = await LocateWorkerAsync(projectPath, cancellationToken).ConfigureAwait(false);
        ProcessResult result = await ProcessRunner.RunAsync("dotnet", [workerPath, "--request", requestPath], tempRoot, TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false);
        return await ReadWorkerResponseAsync(result, responsePath, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ExtractionResult> ReadWorkerResponseAsync(ProcessResult result, string responsePath, CancellationToken cancellationToken)
    {
        if (result.TimedOut)
            return Failure("EF extraction worker timed out.");
        if (result.OutputExceeded)
            return Failure("EF extraction worker exceeded its output limit.");

        if (result.ExitCode != 0)
        {
            if (File.Exists(responsePath))
            {
                if (ResponseExceedsLimit(responsePath))
                    return Failure("EF extraction worker response exceeded the 4 MiB limit.");

                try
                {
                    ExtractionResult? failedResponse = await ReadResponseAsync(responsePath, cancellationToken).ConfigureAwait(false);
                    if (failedResponse is not null && !failedResponse.Success && !string.IsNullOrWhiteSpace(failedResponse.Error))
                        return Failure(failedResponse.Error);
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
            return Failure("EF extraction worker failed.");
        }

        if (!File.Exists(responsePath))
            return Failure("EF extraction worker returned no result.");
        if (ResponseExceedsLimit(responsePath))
            return Failure("EF extraction worker response exceeded the 4 MiB limit.");

        try
        {
            ExtractionResult? response = await ReadResponseAsync(responsePath, cancellationToken).ConfigureAwait(false);
            return response ?? Failure("EF extraction worker returned an empty result.");
        }
        catch (JsonException)
        {
            return Failure("EF extraction worker returned an invalid result.");
        }
        catch (IOException)
        {
            return Failure("EF extraction worker response could not be read.");
        }
    }

    private static bool ResponseExceedsLimit(string responsePath)
    {
        try { return new FileInfo(responsePath).Length > ExtractionLimits.MaxResponseBytes; }
        catch { return true; }
    }

    private static async Task<ExtractionResult?> ReadResponseAsync(string responsePath, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(responsePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync<ExtractionResult>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> LocateWorkerAsync(string projectPath, CancellationToken cancellationToken)
    {
        string? targetFramework = await SelectWorkerTargetFrameworkAsync(projectPath, cancellationToken).ConfigureAwait(false);
        if (targetFramework is null)
            throw new InvalidOperationException("The selected EF project did not resolve to a supported target framework (net8.0, net9.0, or net10.0).");

        string packaged = Path.Combine(AppContext.BaseDirectory, "KeelMatrix.EfGuard.Worker.dll");
        if (targetFramework != "net8.0")
        {
            string packagedWorker = Path.Combine(AppContext.BaseDirectory, targetFramework, "KeelMatrix.EfGuard.Worker.dll");
            if (File.Exists(packagedWorker))
                return packagedWorker;
        }
        if (File.Exists(packaged))
            return packaged;

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && directory is not null; i++, directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "KeelMatrix.EfGuard.Worker", "bin", "Release", targetFramework, "KeelMatrix.EfGuard.Worker.dll");
            if (File.Exists(candidate))
                return candidate;
            candidate = Path.Combine(directory.FullName, "src", "KeelMatrix.EfGuard.Worker", "bin", "Release", targetFramework, "KeelMatrix.EfGuard.Worker.dll");
            if (File.Exists(candidate))
                return candidate;
            candidate = Path.Combine(directory.FullName, "KeelMatrix.EfGuard.Worker", "bin", "Debug", targetFramework, "KeelMatrix.EfGuard.Worker.dll");
            if (File.Exists(candidate))
                return candidate;
            candidate = Path.Combine(directory.FullName, "src", "KeelMatrix.EfGuard.Worker", "bin", "Debug", targetFramework, "KeelMatrix.EfGuard.Worker.dll");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException("The EfGuard extraction worker is not installed.");
    }

    internal static async Task<string?> SelectWorkerTargetFrameworkAsync(string projectPath, CancellationToken cancellationToken)
    {
        string? projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        if (projectDirectory is null)
            return null;

        ProcessResult result = await ProcessRunner.RunAsync(
            "dotnet",
            ["msbuild", projectPath, "-getProperty:TargetFramework", "-getProperty:TargetFrameworks", "-nologo"],
            projectDirectory,
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.TimedOut || result.OutputExceeded)
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            if (!document.RootElement.TryGetProperty("Properties", out JsonElement properties))
                return null;

            List<string> frameworks = [];
            AddFrameworks(properties, "TargetFramework", frameworks);
            AddFrameworks(properties, "TargetFrameworks", frameworks);
            foreach (string supported in new[] { "net10.0", "net9.0", "net8.0" })
                if (frameworks.Any(framework => framework.Equals(supported, StringComparison.OrdinalIgnoreCase)))
                    return supported;
        }
        catch (JsonException) { }

        return null;
    }

    private static void AddFrameworks(JsonElement properties, string propertyName, List<string> frameworks)
    {
        if (!properties.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            return;

        frameworks.AddRange(property.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static ExtractionResult Failure(string message) => new() { Success = false, Error = message };

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
