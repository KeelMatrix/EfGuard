using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeelMatrix.EfGuard;

namespace KeelMatrix.EfGuard.Worker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string DbContextName = "Microsoft.EntityFrameworkCore.DbContext";
    private static readonly string MigrationName = "Microsoft.EntityFrameworkCore.Migrations.Migration";

    internal static async Task<int> Main(string[] args)
    {
        string? requestPath = args.Length == 2 && args[0] == "--request" ? args[1] : null;
        if (requestPath is null)
            return 2;

        ExtractionRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<ExtractionRequest>(await File.ReadAllTextAsync(requestPath).ConfigureAwait(false), JsonOptions);
            if (request is null)
                return 2;

            ExtractionResult result = await ExtractAsync(request).ConfigureAwait(false);
            await WriteResponseAsync(request.ResponsePath, result).ConfigureAwait(false);
            return result.Success ? 0 : 1;
        }
        catch
        {
            if (request?.ResponsePath is not null)
                await WriteResponseAsync(request.ResponsePath, new ExtractionResult { Success = false, Error = "The EF extraction worker failed." }).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<ExtractionResult> ExtractAsync(ExtractionRequest request)
    {
        if (!File.Exists(request.ProjectPath) || !request.ProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return Failure("The selected EF project does not exist.");

        string projectOutput = Path.Combine(request.OutputDirectory, "project");
        string startupOutput = Path.Combine(request.OutputDirectory, "startup");
        Directory.CreateDirectory(projectOutput);
        Directory.CreateDirectory(startupOutput);
        if (!await BuildAsync(request.ProjectPath, projectOutput, Path.Combine(request.OutputDirectory, "project-obj")).ConfigureAwait(false))
            return Failure("The selected EF project could not be built.");
        if (!Path.GetFullPath(request.StartupProjectPath).Equals(Path.GetFullPath(request.ProjectPath), StringComparison.OrdinalIgnoreCase)
            && !await BuildAsync(request.StartupProjectPath, startupOutput, Path.Combine(request.OutputDirectory, "startup-obj")).ConfigureAwait(false))
            return Failure("The selected startup project could not be built.");

        string[] probingPaths = [projectOutput, startupOutput];
        AssemblyLoadContext.Default.Resolving += (_, name) => ResolveAssembly(name, probingPaths);
        List<Assembly> assemblies = LoadAssemblies(probingPaths);
        Reflection.SetAssemblies(assemblies);
        List<Type> contextTypes = assemblies.SelectMany(SafeGetTypes).Where(IsDbContext).Distinct().ToList();
        if (request.ContextName is not null)
            contextTypes = contextTypes.Where(t => t.FullName?.Equals(request.ContextName, StringComparison.Ordinal) == true || t.Name.Equals(request.ContextName, StringComparison.Ordinal)).ToList();
        if (contextTypes.Count == 0)
            return Failure(request.ContextName is null ? "No DbContext was found in the selected project." : "The requested DbContext was not found.");
        if (contextTypes.Count > 1)
            return Failure("More than one DbContext was found; specify --context.");

        Type contextType = contextTypes[0];
        object? context = CreateContext(contextType, assemblies);
        if (context is null)
            return Failure("The DbContext could not be created by the project's design-time factory or default constructor.");

        try
        {
            string? provider = ReadProvider(context);
            bool supported = provider?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true
                || provider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
            if (string.IsNullOrWhiteSpace(provider))
                return Failure("The EF provider could not be identified.");

            ModelSnapshot model = ReadModel(context);
            List<NormalizedOperation> operations = ReadMigrations(assemblies, provider);
            bool providerSqlGenerated = TryGenerateProviderSql(context);
            return new ExtractionResult
            {
                Success = true,
                Provider = provider,
                ProviderSupported = supported,
                ProviderSqlGenerated = providerSqlGenerated,
                Context = contextType.FullName,
                Model = model,
                Operations = operations
            };
        }
        finally
        {
            if (context is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private static async Task<bool> BuildAsync(string projectPath, string outputPath, string intermediatePath)
    {
        Directory.CreateDirectory(outputPath);
        Directory.CreateDirectory(intermediatePath);
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
            "build", projectPath, "--configuration", "Release", "--nologo",
            "/p:OutputPath=" + EnsureTrailingSeparator(outputPath),
            "/p:BaseIntermediateOutputPath=" + EnsureTrailingSeparator(intermediatePath),
            "/p:IntermediateOutputPath=" + EnsureTrailingSeparator(intermediatePath),
            "/p:MSBuildProjectExtensionsPath=" + EnsureTrailingSeparator(intermediatePath),
            "/p:CopyLocalLockFileAssemblies=true",
            "/p:DefaultItemExcludes=" + Path.Combine(Path.GetDirectoryName(projectPath) ?? ".", "obj", "**")
        })
            startInfo.ArgumentList.Add(argument);

        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return false;
            Task<string> output = ReadBoundedAsync(process.StandardOutput.BaseStream);
            Task<string> error = ReadBoundedAsync(process.StandardError.BaseStream);
            Task waitTask = process.WaitForExitAsync();
            Task completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(90))).ConfigureAwait(false);
            if (completed != waitTask)
            {
                TryKill(process);
                return false;
            }
            await Task.WhenAll(output, error).ConfigureAwait(false);
            return process.ExitCode == 0 && output.Result.Length < 65536 && error.Result.Length < 65536;
        }
        catch { return false; }
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

    private static ModelSnapshot ReadModel(object context)
    {
        object model = context.GetType().GetProperty("Model")?.GetValue(context) ?? throw new InvalidOperationException();
        IEnumerable<object> entities = Reflection.Enumerate(model, "GetEntityTypes");
        ModelSnapshot result = new();
        foreach (object entity in entities)
        {
            string table = Reflection.String(entity, "GetTableName") ?? Reflection.String(entity, "Name") ?? "Unknown";
            string? schema = Reflection.String(entity, "GetSchema");
            ModelTable modelTable = new() { Name = table, Schema = schema };
            foreach (object property in Reflection.Enumerate(entity, "GetProperties"))
            {
                modelTable.Columns.Add(new ModelColumn
                {
                    Name = Reflection.String(property, "GetColumnName") ?? Reflection.String(property, "Name") ?? "Unknown",
                    ClrType = Reflection.TypeName(property, "ClrType"),
                    IsNullable = Reflection.Bool(property, "IsNullable", defaultValue: true),
                    MaxLength = Reflection.Int(property, "GetMaxLength"),
                    Precision = Reflection.Byte(property, "GetPrecision"),
                    Scale = Reflection.Byte(property, "GetScale")
                });
            }
            result.Tables.Add(modelTable);
        }
        return result;
    }

    private static List<NormalizedOperation> ReadMigrations(IEnumerable<Assembly> assemblies, string provider)
    {
        List<NormalizedOperation> result = [];
        IEnumerable<Type> migrationTypes = assemblies.SelectMany(SafeGetTypes).Where(type => !type.IsAbstract && IsMigration(type)).OrderBy(MigrationId, StringComparer.Ordinal);
        foreach (Type migrationType in migrationTypes)
        {
            object? migration = Activator.CreateInstance(migrationType);
            if (migration is null)
                continue;
            string migrationId = MigrationId(migrationType) ?? migrationType.Name;
            Type? builderType = migrationType.Assembly.GetType("Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder")
                ?? assemblies.Select(a => a.GetType("Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder")).FirstOrDefault(t => t is not null);
            if (builderType is null)
                continue;
            object builder = Activator.CreateInstance(builderType, provider) ?? throw new InvalidOperationException();
            MethodInfo? up = migrationType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Up" && m.GetParameters().Length == 1);
            if (up is null)
                continue;
            try { up.Invoke(migration, [builder]); }
            catch { result.Add(new NormalizedOperation { Kind = "custom-operation", Migration = migrationId }); continue; }
            object? operations = builderType.GetProperty("Operations")?.GetValue(builder);
            if (operations is not System.Collections.IEnumerable enumerable)
                continue;
            foreach (object operation in enumerable.Cast<object>())
            {
                NormalizedOperation normalized = Normalize(operation, migrationId);
                result.Add(normalized);
            }
        }
        return result;
    }

    private static NormalizedOperation Normalize(object operation, string migrationId)
    {
        string type = operation.GetType().Name;
        NormalizedOperation result = new() { Migration = migrationId };
        if (type.EndsWith("DropColumnOperation", StringComparison.Ordinal))
        {
            result.Kind = "drop-column"; FillTableColumn(result, operation); return result;
        }
        if (type.EndsWith("DropTableOperation", StringComparison.Ordinal))
        {
            result.Kind = "drop-table"; result.Table = Reflection.String(operation, "Name"); result.Schema = Reflection.String(operation, "Schema"); return result;
        }
        if (type.EndsWith("RenameColumnOperation", StringComparison.Ordinal))
        {
            result.Kind = "rename-column"; FillTableColumn(result, operation); result.NewColumn = Reflection.String(operation, "NewName"); return result;
        }
        if (type.EndsWith("RenameTableOperation", StringComparison.Ordinal))
        {
            result.Kind = "rename-table"; result.Table = Reflection.String(operation, "Name"); result.NewTable = Reflection.String(operation, "NewName"); result.Schema = Reflection.String(operation, "Schema"); return result;
        }
        if (type.EndsWith("AlterColumnOperation", StringComparison.Ordinal))
        {
            result.Kind = "alter-column"; FillTableColumn(result, operation); result.ClrType = Reflection.TypeName(operation, "ClrType"); result.IsNullable = Reflection.Bool(operation, "IsNullable"); result.MaxLength = Reflection.Int(operation, "MaxLength"); result.Precision = Reflection.Byte(operation, "Precision"); result.Scale = Reflection.Byte(operation, "Scale");
            object? old = Reflection.Value(operation, "OldColumn");
            if (old is not null) { result.OldClrType = Reflection.TypeName(old, "ClrType"); result.OldIsNullable = Reflection.Bool(old, "IsNullable", true); result.OldMaxLength = Reflection.Int(old, "MaxLength"); result.OldPrecision = Reflection.Byte(old, "Precision"); result.OldScale = Reflection.Byte(old, "Scale"); }
            return result;
        }
        if (type.EndsWith("AddColumnOperation", StringComparison.Ordinal))
        {
            result.Kind = "add-column"; FillTableColumn(result, operation); result.IsNullable = Reflection.Bool(operation, "IsNullable", true); result.SqlShape = Reflection.Value(operation, "DefaultValue") is not null || Reflection.String(operation, "DefaultValueSql") is not null ? "has-default" : null; return result;
        }
        if (type.EndsWith("CreateIndexOperation", StringComparison.Ordinal))
        {
            result.Kind = "create-index"; result.Table = Reflection.String(operation, "Table"); result.Schema = Reflection.String(operation, "Schema"); result.IsUnique = Reflection.Bool(operation, "IsUnique"); result.IsConcurrent = Reflection.Bool(operation, "IsConcurrent") || Reflection.AnnotationBool(operation, "Npgsql:CreatedConcurrently"); result.IsOnline = Reflection.Bool(operation, "IsOnline") || Reflection.AnnotationBool(operation, "SqlServer:Online"); return result;
        }
        if (type.EndsWith("AddForeignKeyOperation", StringComparison.Ordinal))
        {
            result.Kind = "add-foreign-key"; result.Table = Reflection.String(operation, "Table"); result.Schema = Reflection.String(operation, "Schema"); result.PrincipalTable = Reflection.String(operation, "PrincipalTable"); return result;
        }
        if (type.EndsWith("SqlOperation", StringComparison.Ordinal))
        {
            result.SuppressTransaction = Reflection.Bool(operation, "SuppressTransaction");
            string? sql = Reflection.String(operation, "Sql");
            result.Kind = result.SuppressTransaction ? "sql-suppressed-transaction" : sql is not null && Regex.IsMatch(sql, @"\bUPDATE\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(sql, @"\bWHERE\b", RegexOptions.IgnoreCase) ? "sql-backfill" : "raw-sql";
            result.SqlShape = sql is not null && Regex.IsMatch(sql, @"\bUPDATE\b", RegexOptions.IgnoreCase) ? "update" : "sql";
            return result;
        }

        result.Kind = type switch
        {
            "CreateTableOperation" => "create-table",
            "AddPrimaryKeyOperation" => "add-primary-key",
            "DropPrimaryKeyOperation" => "drop-primary-key",
            "AddForeignKeyOperation" => "add-foreign-key",
            "DropForeignKeyOperation" => "drop-foreign-key",
            "DropIndexOperation" => "drop-index",
            "CreateIndexOperation" => "create-index",
            "RenameIndexOperation" => "rename-index",
            "EnsureSchemaOperation" => "ensure-schema",
            "AlterTableOperation" => "alter-table",
            _ => "custom-operation"
        };
        result.Table = Reflection.String(operation, "Table") ?? Reflection.String(operation, "Name");
        result.Schema = Reflection.String(operation, "Schema");
        return result;
    }

    private static void FillTableColumn(NormalizedOperation target, object operation)
    {
        target.Table = Reflection.String(operation, "Table");
        target.Schema = Reflection.String(operation, "Schema");
        target.Column = Reflection.String(operation, "Name");
    }

    private static bool TryGenerateProviderSql(object context)
    {
        try
        {
            object? database = context.GetType().GetProperty("Database")?.GetValue(context);
            MethodInfo? method = database?.GetType().GetMethod("GenerateCreateScript", Type.EmptyTypes);
            return method?.Invoke(database, null) is string script && script.Length > 0;
        }
        catch { return false; }
    }

    private static object? CreateContext(Type contextType, IEnumerable<Assembly> assemblies)
    {
        Type? factoryInterface = contextType.GetInterfaces().FirstOrDefault(i => i.FullName?.StartsWith("Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory", StringComparison.Ordinal) == true);
        if (factoryInterface is not null)
        {
            Type? factoryType = assemblies.SelectMany(SafeGetTypes).FirstOrDefault(t => !t.IsAbstract && factoryInterface.IsAssignableFrom(t));
            if (factoryType is not null)
            {
                object? factory = Activator.CreateInstance(factoryType);
                MethodInfo? method = factoryType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == "CreateDbContext" && m.GetParameters().Length == 1);
                if (factory is not null && method is not null)
                    return method.Invoke(factory, [Array.Empty<string>()]);
            }
        }
        ConstructorInfo? constructor = contextType.GetConstructor(Type.EmptyTypes);
        return constructor is null ? null : constructor.Invoke(null);
    }

    private static string? ReadProvider(object context)
    {
        object? database = context.GetType().GetProperty("Database")?.GetValue(context);
        return database?.GetType().GetProperty("ProviderName")?.GetValue(database) as string;
    }

    private static bool IsDbContext(Type type) => type.FullName != DbContextName && type.BaseType is not null && IsDbContextBase(type.BaseType);
    private static bool IsDbContextBase(Type type) => type.FullName == DbContextName || type.BaseType is not null && IsDbContextBase(type.BaseType);
    private static bool IsMigration(Type type) => type.BaseType is not null && (type.BaseType.FullName == MigrationName || IsMigration(type.BaseType));
    private static string? MigrationId(Type type)
    {
        object? attribute = type.GetCustomAttributes(false).FirstOrDefault(a => a.GetType().FullName?.EndsWith("MigrationAttribute", StringComparison.Ordinal) == true);
        return attribute?.GetType().GetProperty("Id")?.GetValue(attribute) as string;
    }
    private static IEnumerable<Type> SafeGetTypes(Assembly assembly) { try { return assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null)!; } catch { return []; } }
    private static List<Assembly> LoadAssemblies(IEnumerable<string> paths) => paths.SelectMany(path => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.dll") : []).Select(path => TryLoad(path)).Where(a => a is not null).Cast<Assembly>().Distinct().ToList();
    private static Assembly? TryLoad(string path) { try { return AssemblyLoadContext.Default.LoadFromAssemblyPath(path); } catch { return null; } }
    private static Assembly? ResolveAssembly(AssemblyName name, IEnumerable<string> paths) => paths.Select(path => Path.Combine(path, name.Name + ".dll")).Where(File.Exists).Select(TryLoad).FirstOrDefault(a => a is not null);
    private static ExtractionResult Failure(string message) => new() { Success = false, Error = message };
    private static string EnsureTrailingSeparator(string path) => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch { } }
    private static async Task WriteResponseAsync(string? path, ExtractionResult result) { if (path is not null) await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result)).ConfigureAwait(false); }
}

internal static class Reflection
{
    private static IReadOnlyList<Assembly> assemblies = [];

    internal static void SetAssemblies(IReadOnlyList<Assembly> loadedAssemblies) => assemblies = loadedAssemblies;

    internal static object? Value(object instance, string name) => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
    internal static string? String(object instance, string name) => Value(instance, name)?.ToString();
    internal static bool Bool(object instance, string name, bool defaultValue = false) => Value(instance, name) is bool value ? value : defaultValue;
    internal static int? Int(object instance, string name) => Value(instance, name) is int value ? value : null;
    internal static byte? Byte(object instance, string name) => Value(instance, name) switch { byte value => value, int value when value is >= 0 and <= byte.MaxValue => (byte)value, _ => null };
    internal static string? TypeName(object instance, string name) => (Value(instance, name) as Type)?.FullName;

    internal static IEnumerable<object> Enumerate(object instance, string methodName)
    {
        object? value = InvokeNoArgument(instance, methodName);
        return value is System.Collections.IEnumerable enumerable ? enumerable.Cast<object>() : [];
    }

    internal static object? InvokeNoArgument(object instance, string methodName)
    {
        MethodInfo? method = instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 0);
        if (method is not null)
            return method.Invoke(instance, null);

        foreach (MethodInfo extension in assemblies.SelectMany(assembly =>
                     assembly.GetTypes().Where(type => type.IsAbstract && type.IsSealed).SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)))
                     .Where(candidate => candidate.Name == methodName && candidate.GetParameters().Length == 1))
        {
            ParameterInfo parameter = extension.GetParameters()[0];
            if (parameter.ParameterType.IsAssignableFrom(instance.GetType()))
            {
                try { return extension.Invoke(null, [instance]); }
                catch { }
            }
        }
        return null;
    }

    internal static bool AnnotationBool(object instance, string annotationName)
    {
        try
        {
            IEnumerable<object> annotations = Enumerate(instance, "GetAnnotations");
            object? annotation = annotations.FirstOrDefault(a => String(a, "Name")?.Equals(annotationName, StringComparison.OrdinalIgnoreCase) == true);
            object? value = annotation is null ? null : Value(annotation, "Value");
            return value is bool boolean && boolean;
        }
        catch { return false; }
    }
}
