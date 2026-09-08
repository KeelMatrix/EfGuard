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
        string stage = "validate";
        if (!File.Exists(request.ProjectPath) || !request.ProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return Failure("The selected EF project does not exist.");

        string projectOutput = Path.Combine(request.OutputDirectory, "project");
        string startupOutput = Path.Combine(request.OutputDirectory, "startup");
        Directory.CreateDirectory(projectOutput);
        Directory.CreateDirectory(startupOutput);
        stage = "build-project";
        if (!await BuildAsync(request.ProjectPath, projectOutput, Path.Combine(request.OutputDirectory, "project-obj")).ConfigureAwait(false))
            return Failure("The selected EF project could not be built.");
        stage = "build-startup";
        if (!Path.GetFullPath(request.StartupProjectPath).Equals(Path.GetFullPath(request.ProjectPath), StringComparison.OrdinalIgnoreCase)
            && !await BuildAsync(request.StartupProjectPath, startupOutput, Path.Combine(request.OutputDirectory, "startup-obj")).ConfigureAwait(false))
            return Failure("The selected startup project could not be built.");

        stage = "load-assemblies";
        string[] probingPaths = [projectOutput, startupOutput];
        TargetLoadContext targetLoadContext = new(probingPaths);
        targetLoadContext.Resolving += (_, name) => ResolveAssembly(name, probingPaths, targetLoadContext);
        List<Assembly> assemblies = LoadAssemblies(probingPaths, targetLoadContext);
        Reflection.SetAssemblies(assemblies);
        List<Type> contextTypes = assemblies.SelectMany(SafeGetTypes).Where(IsDbContext).Distinct().ToList();
        if (request.ContextName is not null)
            contextTypes = contextTypes.Where(t => t.FullName?.Equals(request.ContextName, StringComparison.Ordinal) == true || t.Name.Equals(request.ContextName, StringComparison.Ordinal)).ToList();
        if (contextTypes.Count == 0)
            return Failure(request.ContextName is null ? "No DbContext was found in the selected project." : "The requested DbContext was not found.");
        if (contextTypes.Count > 1)
            return Failure("More than one DbContext was found; specify --context.");

        Type contextType = contextTypes[0];
        stage = "create-context";
        object? context;
        try { context = CreateContext(contextType, assemblies); }
        catch (InvalidOperationException exception) { return Failure(exception.Message); }
        catch { return Failure("The DbContext could not be created during design-time extraction."); }
        if (context is null)
            return Failure("The DbContext could not be created by the project's design-time factory or default constructor.");

        try
        {
            stage = "read-provider";
            string? provider = ReadProvider(context);
            bool supported = provider?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true
                || provider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
            if (string.IsNullOrWhiteSpace(provider))
                return Failure("The EF provider could not be identified.");

            stage = "read-model";
            ModelSnapshot model = ReadModel(context);
            stage = "read-migrations";
            List<NormalizedOperation> operations = ReadMigrations(assemblies, provider, context, out ProviderSqlEvidence providerSql);
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
        catch { return Failure("The EF extraction worker failed during " + stage + "."); }
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
                    Scale = Reflection.Byte(property, "GetScale"),
                    Collation = Reflection.String(property, "GetCollation")
                });
            }
            result.Tables.Add(modelTable);
        }
        return result;
    }

    private static List<NormalizedOperation> ReadMigrations(IEnumerable<Assembly> assemblies, string provider, object context, out ProviderSqlEvidence providerSql)
    {
        List<NormalizedOperation> result = [];
        List<(string Migration, List<object> Operations)> rawMigrations = [];
        IEnumerable<Type> migrationTypes = assemblies.SelectMany(SafeGetTypes).Where(type => !type.IsAbstract && IsMigration(type)).OrderBy(MigrationId, StringComparer.Ordinal);
        foreach (Type migrationType in migrationTypes)
        {
            try
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
                up.Invoke(migration, [builder]);
                object? operations = builderType.GetProperty("Operations")?.GetValue(builder);
                if (operations is not System.Collections.IEnumerable enumerable)
                    continue;
                List<object> rawOperations = enumerable.Cast<object>().ToList();
                rawMigrations.Add((migrationId, rawOperations));
                foreach (object operation in rawOperations)
                    result.Add(Normalize(operation, migrationId));
            }
            catch
            {
                result.Add(new NormalizedOperation { Kind = "custom-operation", Migration = MigrationId(migrationType) ?? migrationType.Name });
            }
        }
        providerSql = GenerateMigrationSql(context, assemblies, rawMigrations);
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
            result.Kind = "alter-column"; FillTableColumn(result, operation); result.ClrType = Reflection.TypeName(operation, "ClrType"); result.IsNullable = Reflection.Bool(operation, "IsNullable"); result.MaxLength = Reflection.Int(operation, "MaxLength"); result.Precision = Reflection.Byte(operation, "Precision"); result.Scale = Reflection.Byte(operation, "Scale"); result.Collation = Reflection.String(operation, "Collation");
            object? old = Reflection.Value(operation, "OldColumn");
            if (old is not null) { result.OldClrType = Reflection.TypeName(old, "ClrType"); result.OldIsNullable = Reflection.Bool(old, "IsNullable", true); result.OldMaxLength = Reflection.Int(old, "MaxLength"); result.OldPrecision = Reflection.Byte(old, "Precision"); result.OldScale = Reflection.Byte(old, "Scale"); result.OldCollation = Reflection.String(old, "Collation"); }
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
        if (type.EndsWith("AddUniqueConstraintOperation", StringComparison.Ordinal))
        {
            result.Kind = "unique-constraint"; result.Table = Reflection.String(operation, "Table"); result.Schema = Reflection.String(operation, "Schema"); result.IsUnique = true; return result;
        }
        if (type.EndsWith("AddForeignKeyOperation", StringComparison.Ordinal))
        {
            result.Kind = "add-foreign-key"; result.Table = Reflection.String(operation, "Table"); result.Schema = Reflection.String(operation, "Schema"); result.PrincipalTable = Reflection.String(operation, "PrincipalTable"); return result;
        }
        if (type.EndsWith("SqlOperation", StringComparison.Ordinal))
        {
            result.SuppressTransaction = Reflection.Bool(operation, "SuppressTransaction");
            string? sql = Reflection.String(operation, "Sql");
            result.Kind = result.SuppressTransaction ? "sql-suppressed-transaction" : ClassifySql(sql);
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

    private static ProviderSqlEvidence GenerateMigrationSql(object context, IEnumerable<Assembly> assemblies, IEnumerable<(string Migration, List<object> Operations)> migrations)
    {
        ProviderSqlEvidence evidence = new();
        try
        {
            Type? generatorContract = assemblies.Select(assembly => assembly.GetType("Microsoft.EntityFrameworkCore.Migrations.IMigrationsSqlGenerator"))
                .FirstOrDefault(type => type is not null);
            object? services = GetInfrastructureServiceProvider(context);
            object? generator = generatorContract is null || services is not IServiceProvider serviceProvider ? null : serviceProvider.GetService(generatorContract);
            MethodInfo? generate = generatorContract?.GetMethods().FirstOrDefault(method => method.Name == "Generate");
            object? model = context.GetType().GetProperty("Model")?.GetValue(context);
            if (generator is null || generate is null || model is null)
                return evidence;

            foreach ((string migration, List<object> operations) in migrations)
            {
                try
                {
                    object? typedOperations = CreateOperationList(generate.GetParameters()[0].ParameterType, operations);
                    if (typedOperations is null)
                        continue;
                    object?[] arguments = new object?[generate.GetParameters().Length];
                    arguments[0] = typedOperations;
                    if (arguments.Length > 1)
                        arguments[1] = model;
                    for (int index = 2; index < arguments.Length; index++)
                        arguments[index] = generate.GetParameters()[index].ParameterType.IsValueType ? Activator.CreateInstance(generate.GetParameters()[index].ParameterType) : null;
                    object? commands = generate.Invoke(generator, arguments);
                    if (commands is not System.Collections.IEnumerable enumerable)
                        continue;
                    foreach (object command in enumerable.Cast<object>())
                    {
                        string? sql = Reflection.String(command, "CommandText") ?? Reflection.String(command, "Text");
                        if (!string.IsNullOrWhiteSpace(sql))
                            evidence.Statements.Add(new ProviderSqlStatement { Migration = migration, Sql = sql });
                    }
                }
                catch { }
            }
            evidence.Available = evidence.Statements.Count > 0;
        }
        catch { }
        return evidence;
    }

    private static object? CreateOperationList(Type parameterType, IEnumerable<object> operations)
    {
        Type? operationType = parameterType.IsGenericType ? parameterType.GetGenericArguments().FirstOrDefault() : null;
        if (operationType is null)
            return null;
        Type listType = typeof(List<>).MakeGenericType(operationType);
        if (Activator.CreateInstance(listType) is not System.Collections.IList typedOperations)
            return null;
        foreach (object operation in operations)
            if (operationType.IsInstanceOfType(operation))
                typedOperations.Add(operation);
        return typedOperations;
    }

    private static object? GetInfrastructureServiceProvider(object context)
    {
        Type? infrastructure = context.GetType().GetInterfaces().FirstOrDefault(type =>
            type.IsGenericType
            && type.GetGenericTypeDefinition().FullName == "Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure`1"
            && type.GetGenericArguments()[0] == typeof(IServiceProvider));
        return infrastructure?.GetProperty("Instance")?.GetValue(context);
    }

    private static string ClassifySql(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "raw-sql";
        string withoutComments = Regex.Replace(sql, @"--[^\r\n]*|/\*.*?\*/", " ", RegexOptions.Singleline);
        string normalized = Regex.Replace(withoutComments, @"'(?:''|[^'])*'", " ", RegexOptions.Singleline);
        Match update = Regex.Match(normalized, @"\bUPDATE\b", RegexOptions.IgnoreCase);
        return update.Success && !Regex.IsMatch(normalized[update.Index..], @"\bWHERE\b", RegexOptions.IgnoreCase) ? "sql-backfill" : "raw-sql";
    }

    private static object? CreateContext(Type contextType, IEnumerable<Assembly> assemblies)
    {
        Type? factoryType = assemblies.SelectMany(SafeGetTypes)
            .Where(type => IsFactoryForContext(type, contextType))
            .OrderBy(type => type.Assembly == contextType.Assembly ? 0 : 1)
            .ThenBy(type => type.FullName, StringComparer.Ordinal)
            .FirstOrDefault();
        if (factoryType is not null)
        {
            object? factory = Activator.CreateInstance(factoryType);
            MethodInfo? method = factoryType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == "CreateDbContext" && m.GetParameters().Length == 1);
            if (factory is not null && method is not null)
            {
                try { return method.Invoke(factory, [Array.Empty<string>()]); }
                catch { throw new InvalidOperationException("The design-time factory failed during extraction."); }
            }
        }
        ConstructorInfo? constructor = contextType.GetConstructor(Type.EmptyTypes);
        if (constructor is null)
            return null;
        try { return constructor.Invoke(null); }
        catch { throw new InvalidOperationException("The DbContext constructor failed during extraction."); }
    }

    private static bool IsFactoryForContext(Type type, Type contextType)
    {
        try
        {
            return !type.IsAbstract
                && type.GetConstructor(Type.EmptyTypes) is not null
                && type.GetInterfaces().Any(interfaceType =>
                    interfaceType.IsGenericType
                    && interfaceType.GetGenericTypeDefinition().FullName == "Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory`1"
                    && interfaceType.GetGenericArguments()[0] == contextType);
        }
        catch { return false; }
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
        try
        {
            object? attribute = type.GetCustomAttributes(false).FirstOrDefault(a => a.GetType().FullName?.EndsWith("MigrationAttribute", StringComparison.Ordinal) == true);
            return attribute?.GetType().GetProperty("Id")?.GetValue(attribute) as string;
        }
        catch { return null; }
    }
    private static IEnumerable<Type> SafeGetTypes(Assembly assembly) { try { return assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null)!; } catch { return []; } }
    private static List<Assembly> LoadAssemblies(IEnumerable<string> paths, AssemblyLoadContext loadContext) => paths.SelectMany(path => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.dll") : []).Select(path => TryLoad(path, loadContext)).Where(a => a is not null).Cast<Assembly>().Distinct().ToList();
    private static Assembly? TryLoad(string path, AssemblyLoadContext loadContext) { try { return loadContext.LoadFromAssemblyPath(path); } catch { return null; } }
    private static Assembly? ResolveAssembly(AssemblyName name, IEnumerable<string> paths, AssemblyLoadContext loadContext) => paths.Select(path => Path.Combine(path, name.Name + ".dll")).Where(File.Exists).Select(path => TryLoad(path, loadContext)).FirstOrDefault(a => a is not null) ?? AssemblyLoadContext.Default.LoadFromAssemblyName(name);
    private static ExtractionResult Failure(string message) => new() { Success = false, Error = message };
    private static string EnsureTrailingSeparator(string path) => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch { } }
    private static async Task WriteResponseAsync(string? path, ExtractionResult result)
    {
        if (path is not null)
            await ExtractionResponse.WriteAsync(path, result, JsonOptions).ConfigureAwait(false);
    }
}

internal sealed class TargetLoadContext(IEnumerable<string> probingPaths) : AssemblyLoadContext("EfGuard.Target", isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? path = probingPaths.Select(directory => Path.Combine(directory, assemblyName.Name + ".dll")).FirstOrDefault(File.Exists);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}

internal static class Reflection
{
    private static IReadOnlyList<Assembly> assemblies = [];

    internal static void SetAssemblies(IReadOnlyList<Assembly> loadedAssemblies) => assemblies = loadedAssemblies;

    internal static object? Value(object instance, string name) => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
    internal static string? String(object instance, string name) => Value(instance, name)?.ToString() ?? InvokeNoArgument(instance, name)?.ToString();
    internal static bool Bool(object instance, string name, bool defaultValue = false) => Value(instance, name) is bool value ? value : InvokeNoArgument(instance, name) is bool invoked ? invoked : defaultValue;
    internal static int? Int(object instance, string name) => Value(instance, name) is int value ? value : InvokeNoArgument(instance, name) is int invoked ? invoked : null;
    internal static byte? Byte(object instance, string name) => Value(instance, name) switch { byte value => value, int value when value is >= 0 and <= byte.MaxValue => (byte)value, _ => InvokeNoArgument(instance, name) switch { byte invoked => invoked, int invoked when invoked is >= 0 and <= byte.MaxValue => (byte)invoked, _ => null } };
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

        foreach (Type interfaceType in instance.GetType().GetInterfaces())
        {
            MethodInfo? interfaceMethod = interfaceType.GetMethods().FirstOrDefault(candidate => candidate.Name == methodName && candidate.GetParameters().Length == 0);
            if (interfaceMethod is not null)
            {
                try { return interfaceMethod.Invoke(instance, null); }
                catch { }
            }
        }

        foreach (MethodInfo extension in assemblies.SelectMany(assembly =>
                     GetTypesOrFail(assembly).Where(type => type.IsAbstract && type.IsSealed).SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)))
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

    private static Type[] GetTypesOrFail(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch { throw new InvalidOperationException("EF metadata could not be inspected during extraction."); }
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
