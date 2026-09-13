using System.Reflection;
using KeelMatrix.EfGuard;

namespace KeelMatrix.EfGuard.Worker;

internal static class ExtractionWorker
{
    private const string DbContextName = "Microsoft.EntityFrameworkCore.DbContext";

    internal static async Task<ExtractionResult> ExtractAsync(ExtractionRequest request)
    {
        string stage = "validate";
        if (!File.Exists(request.ProjectPath) || !request.ProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return Failure("The selected EF project does not exist.");

        string projectOutput = Path.Combine(request.OutputDirectory, "project");
        string startupOutput = Path.Combine(request.OutputDirectory, "startup");
        Directory.CreateDirectory(projectOutput);
        Directory.CreateDirectory(startupOutput);
        stage = "build-project";
        ProjectBuildOutcome projectBuild = await ProjectBuilder.BuildAsync(request.ProjectPath, projectOutput).ConfigureAwait(false);
        if (!projectBuild.Success)
            return Failure(projectBuild.Error ?? "The selected EF project could not be built.");
        stage = "build-startup";
        if (!Path.GetFullPath(request.StartupProjectPath).Equals(Path.GetFullPath(request.ProjectPath), StringComparison.OrdinalIgnoreCase))
        {
            ProjectBuildOutcome startupBuild = await ProjectBuilder.BuildAsync(request.StartupProjectPath, startupOutput).ConfigureAwait(false);
            if (!startupBuild.Success)
                return Failure(startupBuild.Error ?? "The selected startup project could not be built.");
        }

        stage = "load-assemblies";
        string[] probingPaths = [projectOutput, startupOutput];
        TargetLoadContext targetLoadContext = new(probingPaths);
        targetLoadContext.Resolving += (_, name) => AssemblyLoader.ResolveAssembly(name, probingPaths, targetLoadContext);
        List<Assembly> assemblies = AssemblyLoader.LoadAssemblies(probingPaths, targetLoadContext);
        Reflection.SetAssemblies(assemblies);
        List<Type> contextTypes = assemblies.SelectMany(assembly => Reflection.GetTypesOrRecord(assembly)).Where(IsDbContext).Distinct().ToList();
        if (request.ContextName is not null)
            contextTypes = contextTypes.Where(t => t.FullName?.Equals(request.ContextName, StringComparison.Ordinal) == true || t.Name.Equals(request.ContextName, StringComparison.Ordinal)).ToList();
        if (contextTypes.Count == 0)
            return Failure(request.ContextName is null ? "No DbContext was found in the selected project." + Reflection.DescribeUnreadableAssemblies() : "The requested DbContext was not found.");
        if (contextTypes.Count > 1)
            return Failure("More than one DbContext was found; specify --context.");

        Type contextType = contextTypes[0];
        if (!Reflection.TryGetTypes(contextType.Assembly, out _))
            return Failure(Reflection.UnreadableAssemblyMessage(contextType.Assembly, "the assembly that declares the selected DbContext"));

        stage = "create-context";
        ContextInstance? createdContext;
        try { createdContext = ContextFactory.CreateContext(contextType, assemblies, request.StartupProjectPath); }
        catch (InvalidOperationException exception) { return Failure(exception.Message); }
        catch { return Failure("The DbContext could not be created during design-time extraction."); }
        if (createdContext is null)
            return Failure("The DbContext could not be created by the startup project's services, design-time factory, or default constructor.");

        object context = createdContext.Context;
        try
        {
            stage = "read-provider";
            string? provider = ContextFactory.ReadProvider(context);
            bool supported = provider?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true
                || provider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
            if (string.IsNullOrWhiteSpace(provider))
                return Failure("The EF provider could not be identified.");

            stage = "read-model";
            ModelSnapshot model = ModelExtractor.ReadModel(context);
            stage = "read-migrations";
            List<NormalizedOperation> operations = MigrationExtractor.ReadMigrations(context, assemblies, provider, supported, out ProviderSqlEvidence providerSql);
            stage = "generate-provider-sql";
            return new ExtractionResult
            {
                Success = true,
                Provider = provider,
                ProviderSupported = supported,
                ProviderSqlGenerated = providerSql.Available,
                ProviderSql = providerSql,
                Context = contextType.FullName,
                Model = model,
                Operations = operations
            };
        }
        catch (ExtractionFailureException exception)
        {
            return Failure(exception.Message);
        }
        catch { return Failure("The EF extraction worker failed during " + stage + "."); }
        finally
        {
            createdContext.Dispose();
        }
    }

    private static bool IsDbContext(Type type) => type.FullName != DbContextName && type.BaseType is not null && IsDbContextBase(type.BaseType);
    private static bool IsDbContextBase(Type type) => type.FullName == DbContextName || type.BaseType is not null && IsDbContextBase(type.BaseType);
    private static ExtractionResult Failure(string message) => new() { Success = false, Error = message };
}
