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
    private static readonly string MigrationDbContextAttributeName = "Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute";

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
            Reflection.WriteRecordedNotes();
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
        BuildOutcome projectBuild = await BuildAsync(request.ProjectPath, projectOutput).ConfigureAwait(false);
        if (!projectBuild.Success)
            return Failure(projectBuild.Error ?? "The selected EF project could not be built.");
        stage = "build-startup";
        if (!Path.GetFullPath(request.StartupProjectPath).Equals(Path.GetFullPath(request.ProjectPath), StringComparison.OrdinalIgnoreCase))
        {
            BuildOutcome startupBuild = await BuildAsync(request.StartupProjectPath, startupOutput).ConfigureAwait(false);
            if (!startupBuild.Success)
                return Failure(startupBuild.Error ?? "The selected startup project could not be built.");
        }

        stage = "load-assemblies";
        string[] probingPaths = [projectOutput, startupOutput];
        TargetLoadContext targetLoadContext = new(probingPaths);
        targetLoadContext.Resolving += (_, name) => ResolveAssembly(name, probingPaths, targetLoadContext);
        List<Assembly> assemblies = LoadAssemblies(probingPaths, targetLoadContext);
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
        try { createdContext = CreateContext(contextType, assemblies, request.StartupProjectPath); }
        catch (InvalidOperationException exception) { return Failure(exception.Message); }
        catch { return Failure("The DbContext could not be created during design-time extraction."); }
        if (createdContext is null)
            return Failure("The DbContext could not be created by the startup project's services, design-time factory, or default constructor.");

        object context = createdContext.Context;
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
            List<NormalizedOperation> operations = ReadMigrations(context, assemblies, provider, supported, out ProviderSqlEvidence providerSql);
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

    private static async Task<BuildOutcome> BuildAsync(string projectPath, string outputPath)
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
                return new BuildOutcome(false, "The selected EF project could not be built.");
            Task<string> output = ReadBoundedAsync(process.StandardOutput.BaseStream);
            Task<string> error = ReadBoundedAsync(process.StandardError.BaseStream);
            Task waitTask = process.WaitForExitAsync();
            Task completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(90))).ConfigureAwait(false);
            if (completed != waitTask)
            {
                TryKill(process);
                return new BuildOutcome(false, "The selected EF project did not build within the extraction timeout.");
            }
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode == 0 && output.Result.Length < 65536 && error.Result.Length < 65536)
                return new BuildOutcome(true, null);
            if (IndicatesMissingRestore(output.Result, error.Result))
                return new BuildOutcome(false, "The dependency graph for " + Path.GetFileName(projectPath) + " is not restored. Run 'dotnet restore' for the project and its referenced projects, then run EfGuard again; EfGuard does not restore packages or contact package feeds.");
            return new BuildOutcome(false, "The selected EF project could not be built.");
        }
        catch { return new BuildOutcome(false, "The selected EF project could not be built."); }
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

    private static List<NormalizedOperation> ReadMigrations(object context, IEnumerable<Assembly> assemblies, string provider, bool providerSupported, out ProviderSqlEvidence providerSql)
    {
        List<NormalizedOperation> result = [];
        List<(string Migration, List<object> Operations)> rawMigrations = [];
        foreach ((string migrationId, Type migrationType) in ResolveContextMigrations(context, assemblies, providerSupported))
        {
            try
            {
                object? migration = Activator.CreateInstance(migrationType);
                if (migration is null)
                    continue;
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
                foreach ((object operation, int index) in rawOperations.Select((operation, index) => (operation, index)))
                    result.Add(Normalize(operation, migrationId, index));
            }
            catch
            {
                result.Add(new NormalizedOperation { Kind = "custom-operation", Migration = migrationId });
            }
        }
        providerSql = GenerateMigrationSql(context, assemblies, rawMigrations);
        return result;
    }

    /// <summary>
    /// Resolves the migrations that EF Core attributes to the selected context. EF Core treats a migration
    /// as part of a context only when the migration carries a matching <c>[DbContext]</c> attribute, so
    /// EfGuard reports exactly that per-context set and never falls back to every <c>Migration</c> subclass
    /// loaded from the migrations assembly. When EF attributes no migrations to the selected context while
    /// that assembly still defines migration classes no context claims, extraction fails closed instead of
    /// reporting a trustworthy empty scan.
    /// </summary>
    private static List<(string MigrationId, Type MigrationType)> ResolveContextMigrations(object context, IEnumerable<Assembly> assemblies, bool providerSupported)
    {
        List<(string MigrationId, Type MigrationType)> migrations = [];
        Assembly migrationsAssemblyInstance;
        try
        {
            Type? contract = assemblies.Select(assembly => assembly.GetType("Microsoft.EntityFrameworkCore.Migrations.IMigrationsAssembly")).FirstOrDefault(type => type is not null);
            object? services = GetInfrastructureServiceProvider(context);
            object? migrationsAssembly = contract is null || services is not IServiceProvider serviceProvider ? null : serviceProvider.GetService(contract);
            object? entries = migrationsAssembly is null ? null : contract?.GetProperty("Migrations")?.GetValue(migrationsAssembly);
            if (entries is not System.Collections.IEnumerable enumerable)
                throw new InvalidOperationException();

            migrationsAssemblyInstance = Reflection.Value(migrationsAssembly!, "Assembly") as Assembly ?? context.GetType().Assembly;
            foreach (object entry in enumerable)
            {
                string? migrationId = Reflection.String(entry, "Key");
                object? value = Reflection.Value(entry, "Value");
                if (value is TypeInfo typeInfo)
                    value = typeInfo.AsType();
                if (value is not Type migrationType || migrationType.IsAbstract || !IsMigration(migrationType))
                    continue;
                migrations.Add((migrationId ?? MigrationId(migrationType) ?? migrationType.Name, migrationType));
            }
        }
        catch when (!providerSupported)
        {
            // Unsupported providers are reported as unsupported by the caller and never receive
            // provider-specific migration analysis, so missing relational metadata is not fatal here.
            return [];
        }
        catch
        {
            throw new ExtractionFailureException("The migrations for the selected DbContext could not be resolved during extraction.");
        }

        if (migrations.Count == 0 && providerSupported)
            EnsureNoUnattributedMigrationClasses(context.GetType(), migrationsAssemblyInstance);

        return migrations
            .OrderBy(migration => migration.MigrationId, StringComparer.Ordinal)
            .ThenBy(migration => migration.MigrationType.FullName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Fails closed when EF attributed no migrations to the selected context while the migrations assembly
    /// still defines migration classes that no context attribute claims. EF Core applies and lists a
    /// migration only when <c>[DbContext]</c> attributes it to the scanned context, so those classes cannot
    /// be attributed here and reporting a clean scan for them would be silently safe.
    /// </summary>
    private static void EnsureNoUnattributedMigrationClasses(Type contextType, Assembly migrationsAssembly)
    {
        Reflection.EnsureTypesReadable(migrationsAssembly, "the migrations assembly EF Core reports for the selected DbContext");

        List<string> unattributed = [];
        foreach (Type type in Reflection.GetTypesOrRecord(migrationsAssembly))
        {
            if (type.IsAbstract || type.ContainsGenericParameters || !IsMigration(type) || MigrationAttribution(type, out _))
                continue;

            unattributed.Add(type.FullName ?? type.Name);
        }

        if (unattributed.Count == 0)
            return;

        unattributed.Sort(StringComparer.Ordinal);
        throw new ExtractionFailureException(
            "EF Core attributed no migrations to '" + contextType.FullName + "' in migrations assembly '" + migrationsAssembly.GetName().Name
            + "', but that assembly defines " + unattributed.Count + " migration class(es) that no [DbContext] attribute claims (for example '"
            + unattributed[0] + "'). EF Core applies and lists a migration only when it carries [DbContext(typeof(" + contextType.Name
            + "))], so EfGuard cannot attribute those classes to the selected context. Add the [DbContext] attribute that EF Core writes into migration designer files, or run the scan for the context that owns those migrations.");
    }

    private static bool MigrationAttribution(Type type, out Type? contextType)
    {
        Type? current = type;
        while (current is not null && current != typeof(object))
        {
            object? attribute = null;
            try
            {
                attribute = current.GetCustomAttributes(false).FirstOrDefault(candidate => candidate.GetType().FullName == MigrationDbContextAttributeName);
            }
            catch { }

            if (attribute is not null)
            {
                contextType = attribute.GetType().GetProperty("ContextType")?.GetValue(attribute) as Type;
                return true;
            }

            current = current.BaseType;
        }

        contextType = null;
        return false;
    }

    private static NormalizedOperation Normalize(object operation, string migrationId, int operationIndex)
    {
        string type = operation.GetType().Name;
        NormalizedOperation result = new() { Migration = migrationId, OperationIndex = operationIndex };
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
            result.Kind = "alter-column"; FillTableColumn(result, operation); result.ColumnType = Reflection.String(operation, "ColumnType"); result.ClrType = Reflection.TypeName(operation, "ClrType"); result.IsNullable = Reflection.Bool(operation, "IsNullable"); result.MaxLength = Reflection.Int(operation, "MaxLength"); result.Precision = Reflection.Byte(operation, "Precision"); result.Scale = Reflection.Byte(operation, "Scale"); result.Collation = Reflection.String(operation, "Collation");
            object? old = Reflection.Value(operation, "OldColumn");
            if (old is not null) { result.HasOldColumn = true; result.OldColumnType = Reflection.String(old, "ColumnType"); result.OldClrType = Reflection.TypeName(old, "ClrType"); result.OldIsNullable = Reflection.Bool(old, "IsNullable", true); result.OldMaxLength = Reflection.Int(old, "MaxLength"); result.OldPrecision = Reflection.Byte(old, "Precision"); result.OldScale = Reflection.Byte(old, "Scale"); result.OldCollation = Reflection.String(old, "Collation"); }
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
            result.Kind = ClassifySql(sql);
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
                foreach ((object operation, int operationIndex) in operations.Select((operation, index) => (operation, index)))
                {
                    try
                    {
                        object? typedOperations = CreateOperationList(generate.GetParameters()[0].ParameterType, [operation]);
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
                                evidence.Statements.Add(new ProviderSqlStatement { Migration = migration, OperationIndex = operationIndex, Sql = sql });
                        }
                    }
                    catch { }
                }
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

    private static ContextInstance? CreateContext(Type contextType, IEnumerable<Assembly> assemblies, string startupProjectPath)
    {
        ContextInstance? startupContext = CreateFromStartupServices(contextType, assemblies, startupProjectPath);
        if (startupContext is not null)
            return startupContext;

        return CreateFromDesignTimeFactoryOrConstructor(contextType, assemblies);
    }

    private static ContextInstance? CreateFromStartupServices(Type contextType, IEnumerable<Assembly> assemblies, string startupProjectPath)
    {
        Assembly? startupAssembly = FindStartupAssembly(startupProjectPath, assemblies);
        if (startupAssembly is null)
            return null;

        object? host = TryBuildStartupHost(startupAssembly);
        if (host is null)
            return null;

        IServiceProvider? rootServices = GetServiceProvider(host);
        if (rootServices is null)
        {
            DisposeIfNeeded(host);
            return null;
        }

        IServiceProvider scopedServices = CreateScope(rootServices, out IDisposable? scope);
        object? context = null;
        bool contextCreatedByFactory = false;
        try
        {
            context = scopedServices.GetService(contextType);
            if (context is null)
            {
                Type? factoryType = assemblies
                    .Select(assembly => assembly.GetType("Microsoft.EntityFrameworkCore.IDbContextFactory`1"))
                    .FirstOrDefault(type => type is not null)?
                    .MakeGenericType(contextType);
                if (factoryType is not null)
                {
                    object? factory = scopedServices.GetService(factoryType);
                    MethodInfo? create = factory?.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(method => method.Name == "CreateDbContext" && method.GetParameters().Length == 0);
                    if (create is not null)
                    {
                        context = create.Invoke(factory, null);
                        contextCreatedByFactory = context is not null;
                    }
                }
            }
        }
        catch
        {
            context = null;
        }

        if (context is null)
        {
            DisposeIfNeeded(scope);
            DisposeIfNeeded(host);
            return null;
        }

        List<IDisposable> owners = [];
        if (contextCreatedByFactory)
            AddDisposable(owners, context);
        AddDisposable(owners, scope);
        AddDisposable(owners, host);
        return new ContextInstance(context, owners);
    }

    private static ContextInstance? CreateFromDesignTimeFactoryOrConstructor(Type contextType, IEnumerable<Assembly> assemblies)
    {
        Type? factoryType = assemblies.SelectMany(assembly => Reflection.GetTypesOrRecord(assembly))
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
                try
                {
                    object? context = method.Invoke(factory, [Array.Empty<string>()]);
                    return context is null ? null : new ContextInstance(context, [ToDisposable(context)]);
                }
                catch { throw new InvalidOperationException("The design-time factory failed during extraction."); }
            }
        }
        ConstructorInfo? constructor = contextType.GetConstructor(Type.EmptyTypes);
        if (constructor is null)
            return null;
        try
        {
            object? context = constructor.Invoke(null);
            return context is null ? null : new ContextInstance(context, [ToDisposable(context)]);
        }
        catch { throw new InvalidOperationException("The DbContext constructor failed during extraction."); }
    }

    private static Assembly? FindStartupAssembly(string startupProjectPath, IEnumerable<Assembly> assemblies)
    {
        string startupName = Path.GetFileNameWithoutExtension(startupProjectPath);
        return assemblies.FirstOrDefault(assembly => assembly.GetName().Name?.Equals(startupName, StringComparison.OrdinalIgnoreCase) == true)
            ?? assemblies.FirstOrDefault(assembly => assembly.EntryPoint is not null);
    }

    private static object? TryBuildStartupHost(Assembly startupAssembly)
    {
        Type? programType = startupAssembly.EntryPoint?.DeclaringType;
        if (programType is null)
            return null;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (string methodName in new[] { "BuildWebHost", "CreateWebHostBuilder", "CreateHostBuilder" })
        {
            MethodInfo? factory = programType.GetMethods(flags).FirstOrDefault(method =>
                method.Name == methodName
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(string[]));
            if (factory is null)
                continue;

            try
            {
                object? host = factory.Invoke(null, [Array.Empty<string>()]);
                if (methodName != "BuildWebHost")
                    host = BuildHost(host);
                if (host is not null && GetServiceProvider(host) is not null)
                    return host;
            }
            catch { }
        }

        return TryBuildHostFromEntryPoint(startupAssembly);
    }

    private static object? TryBuildHostFromEntryPoint(Assembly startupAssembly)
    {
        if (startupAssembly.EntryPoint is null)
            return null;

        try
        {
            Assembly.Load("Microsoft.Extensions.Hosting");
            using HostingListener listener = new(startupAssembly.EntryPoint, TimeSpan.FromSeconds(90), startupAssembly.GetName().Name);
            return listener.CreateHost();
        }
        catch { return null; }
    }

    private static object? BuildHost(object? builder)
    {
        if (builder is null)
            return null;
        MethodInfo? build = builder.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "Build" && method.GetParameters().Length == 0);
        return build?.Invoke(builder, null);
    }

    private static IServiceProvider? GetServiceProvider(object host)
    {
        try
        {
            if (host is IServiceProvider provider)
                return provider;
            object? services = host.GetType().GetProperty("Services", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(host);
            if (services is IServiceProvider providerFromConcreteType)
                return providerFromConcreteType;

            foreach (string assemblyName in new[] { "Microsoft.Extensions.Hosting.Abstractions", "Microsoft.AspNetCore.Hosting.Abstractions" })
            {
                Type? hostContract = Assembly.Load(assemblyName).GetType(assemblyName.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ? "Microsoft.AspNetCore.Hosting.IWebHost" : "Microsoft.Extensions.Hosting.IHost");
                services = hostContract?.GetProperty("Services")?.GetValue(host);
                if (services is IServiceProvider providerFromContract)
                    return providerFromContract;
            }
            return null;
        }
        catch { return null; }
    }

    private static IServiceProvider CreateScope(IServiceProvider rootServices, out IDisposable? scope)
    {
        scope = null;
        Type? scopeFactoryType = null;
        try
        {
            scopeFactoryType = Assembly.Load("Microsoft.Extensions.DependencyInjection.Abstractions")
                .GetType("Microsoft.Extensions.DependencyInjection.IServiceScopeFactory");
        }
        catch { }

        if (scopeFactoryType is null)
            return rootServices;

        try
        {
            object? scopeFactory = rootServices.GetService(scopeFactoryType);
            MethodInfo? createScope = scopeFactory?.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => (method.Name == "CreateScope" || method.Name.EndsWith(".CreateScope", StringComparison.Ordinal)) && method.GetParameters().Length == 0);
            object? createdScope = createScope is null
                ? scopeFactoryType.GetMethod("CreateScope")?.Invoke(scopeFactory, null)
                : createScope.Invoke(scopeFactory, null);
            Type? scopeType = null;
            try { scopeType = scopeFactoryType.Assembly.GetType("Microsoft.Extensions.DependencyInjection.IServiceScope"); } catch { }
            IServiceProvider? scopedServices = (scopeType?.GetProperty("ServiceProvider") ?? createdScope?.GetType().GetProperty("ServiceProvider"))?.GetValue(createdScope) as IServiceProvider;
            if (createdScope is IDisposable disposable && scopedServices is not null)
            {
                scope = disposable;
                return scopedServices;
            }
            DisposeIfNeeded(createdScope);
        }
        catch { }
        return rootServices;
    }

    private static void AddDisposable(List<IDisposable> owners, object? value)
    {
        if (value is IDisposable disposable)
            owners.Add(disposable);
    }

    private static IDisposable ToDisposable(object value) => value as IDisposable ?? new NoopDisposable();
    private static void DisposeIfNeeded(object? value)
    {
        try { if (value is IDisposable disposable) disposable.Dispose(); }
        catch { }
    }

    private sealed class ContextInstance(object context, IEnumerable<IDisposable> owners) : IDisposable
    {
        private readonly IDisposable[] owners = owners.ToArray();

        internal object Context { get; } = context;

        public void Dispose()
        {
            for (int index = owners.Length - 1; index >= 0; index--)
            {
                try { owners[index].Dispose(); }
                catch { }
            }
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class HostingListener(MethodInfo entryPoint, TimeSpan waitTimeout, string? assemblyName) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly MethodInfo entryPoint = entryPoint;
        private readonly TimeSpan waitTimeout = waitTimeout;
        private readonly string? assemblyName = assemblyName;
        private readonly TaskCompletionSource<object> hostSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AsyncLocal<HostingListener?> current = new();
        private IDisposable? diagnosticSubscription;

        internal object? CreateHost()
        {
            using IDisposable allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
            Thread thread = new(() => RunEntryPoint()) { IsBackground = true };
            thread.Start();
            if (!hostSource.Task.Wait(waitTimeout))
                throw new InvalidOperationException("The startup project's host did not become available within the extraction timeout.");
            return hostSource.Task.GetAwaiter().GetResult();
        }

        private void RunEntryPoint()
        {
            Exception? failure = null;
            try
            {
                current.Value = this;
                ParameterInfo[] parameters = entryPoint.GetParameters();
                if (parameters.Length == 0)
                    entryPoint.Invoke(null, null);
                else
                {
                    string[] args = assemblyName is null ? [] : ["--applicationName", assemblyName];
                    entryPoint.Invoke(null, [args]);
                }
                hostSource.TrySetException(new InvalidOperationException("The startup entry point exited without building a host."));
            }
            catch (TargetInvocationException exception) when (exception.InnerException?.GetType().Name == "HostAbortedException") { }
            catch (TargetInvocationException exception)
            {
                failure = exception.InnerException ?? exception;
                hostSource.TrySetException(failure);
            }
            catch (Exception exception)
            {
                failure = exception;
                hostSource.TrySetException(failure);
            }
            finally
            {
                current.Value = null;
                if (failure is not null)
                    hostSource.TrySetException(failure);
            }
        }

        public void OnNext(DiagnosticListener value)
        {
            if (current.Value == this && value.Name == "Microsoft.Extensions.Hosting")
                diagnosticSubscription = value.Subscribe(this);
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (current.Value != this)
                return;
            if (value.Key == "HostBuilt" && value.Value is not null)
            {
                hostSource.TrySetResult(value.Value);
                ThrowHostAborted();
            }
        }

        public void OnError(Exception error) { }
        public void OnCompleted() => diagnosticSubscription?.Dispose();

        public void Dispose()
        {
            diagnosticSubscription?.Dispose();
        }

        private static void ThrowHostAborted()
        {
            Type? exceptionType = Type.GetType("Microsoft.Extensions.Hosting.HostAbortedException, Microsoft.Extensions.Hosting.Abstractions", throwOnError: false);
            if (exceptionType is not null && Activator.CreateInstance(exceptionType) is Exception exception)
                throw exception;
            throw new HostAbortedException();
        }

        private sealed class HostAbortedException : Exception { }
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
    private static List<Assembly> LoadAssemblies(IEnumerable<string> paths, AssemblyLoadContext loadContext) => paths
        .SelectMany(path => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.dll") : [])
        .Where(path => !IsSharedFrameworkAssembly(Path.GetFileNameWithoutExtension(path)))
        .Select(path => TryLoad(path, loadContext))
        .Where(a => a is not null)
        .Cast<Assembly>()
        .Distinct()
        .ToList();
    private static Assembly? TryLoad(string path, AssemblyLoadContext loadContext) { try { return loadContext.LoadFromAssemblyPath(path); } catch { return null; } }
    private static Assembly? ResolveAssembly(AssemblyName name, IEnumerable<string> paths, AssemblyLoadContext loadContext)
    {
        if (IsSharedFrameworkAssembly(name.Name))
        {
            try { return AssemblyLoadContext.Default.LoadFromAssemblyName(name); }
            catch { }
        }

        return paths.Select(path => Path.Combine(path, name.Name + ".dll"))
            .Where(File.Exists)
            .Select(path => TryLoad(path, loadContext))
            .FirstOrDefault(a => a is not null)
            ?? AssemblyLoadContext.Default.LoadFromAssemblyName(name);
    }

    internal static bool IsSharedFrameworkAssembly(string? name)
        => name?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true
            || name?.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) == true;
    private sealed record BuildOutcome(bool Success, string? Error);
    private static ExtractionResult Failure(string message) => new() { Success = false, Error = message };
    private static string EnsureTrailingSeparator(string path) => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch { } }
    private static async Task WriteResponseAsync(string? path, ExtractionResult result)
    {
        if (path is not null)
            await ExtractionResponse.WriteAsync(path, result, JsonOptions).ConfigureAwait(false);
    }
}

/// <summary>
/// A fail-closed extraction failure that carries a message which is safe and useful to show the user, in
/// contrast to unexpected exceptions that stay behind the worker's generic stage failure text.
/// </summary>
internal sealed class ExtractionFailureException(string message) : Exception(message);

internal sealed class TargetLoadContext(IEnumerable<string> probingPaths) : AssemblyLoadContext("EfGuard.Target", isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (Program.IsSharedFrameworkAssembly(assemblyName.Name))
            return null;
        string? path = probingPaths.Select(directory => Path.Combine(directory, assemblyName.Name + ".dll")).FirstOrDefault(File.Exists);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}

internal static class Reflection
{
    private const int NotesLimit = 10;
    private const int NoteTextLimit = 400;

    // Assemblies whose whole type enumeration failed outright. EfGuard reads the selected context's own
    // assembly and its attributed migration classes from these assemblies, so an unreadable one of those is
    // fatal; every other copied assembly is peripheral to migration attribution and is skipped instead.
    private static readonly Dictionary<string, string> unreadableAssemblies = new(StringComparer.Ordinal);
    private static readonly HashSet<string> notes = new(StringComparer.Ordinal);
    private static IReadOnlyList<Assembly> assemblies = [];

    internal static void SetAssemblies(IReadOnlyList<Assembly> loadedAssemblies)
    {
        assemblies = loadedAssemblies;
        unreadableAssemblies.Clear();
        notes.Clear();
    }

    /// <summary>
    /// Reads the loadable types of one copied assembly. Type enumeration failures are recorded per assembly
    /// instead of aborting the extraction, because the worker builds the scanned project with copied
    /// dependencies: design-time packages such as <c>Microsoft.EntityFrameworkCore.Design</c> copy
    /// MSBuild/Roslyn support assemblies that are not loadable in the worker's process yet have nothing to do
    /// with the scanned context's model or migrations. Failures are still fatal when they hit an assembly
    /// EfGuard must read, which callers enforce through <see cref="EnsureTypesReadable"/>.
    /// </summary>
    internal static Type[] GetTypesOrRecord(Assembly assembly, string? requestedMember = null)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // The loader kept every type it could resolve and EF Core itself reads constructible types from
            // such a partial result, so the readable types stay usable and only the rest are skipped.
            RecordNote(assembly, requestedMember, exception);
            return exception.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
        catch (Exception exception)
        {
            RecordUnreadable(assembly, exception);
            RecordNote(assembly, requestedMember, exception);
            return [];
        }
    }

    internal static bool TryGetTypes(Assembly assembly, out Type[] types)
    {
        types = GetTypesOrRecord(assembly);
        return !unreadableAssemblies.ContainsKey(Key(assembly));
    }

    /// <summary>
    /// Fails closed when an assembly whose metadata EfGuard must read could not be enumerated at all.
    /// </summary>
    internal static void EnsureTypesReadable(Assembly assembly, string role)
    {
        if (unreadableAssemblies.ContainsKey(Key(assembly)))
            throw new ExtractionFailureException(UnreadableAssemblyMessage(assembly, role));
    }

    internal static string UnreadableAssemblyMessage(Assembly assembly, string role)
        => "EF metadata could not be inspected for " + role + " '" + DisplayName(assembly)
            + "' because enumerating its types failed with " + (unreadableAssemblies.TryGetValue(Key(assembly), out string? failure) ? failure : "an unknown reflection failure")
            + ". EfGuard fails closed instead of reporting a partial migration scan, because it has to read that assembly's types to attribute the selected DbContext's migrations. "
            + "Next step: rebuild the project and run EfGuard again ('dotnet build', then 'efguard check'); if the failure persists, resolve the reported exception, which usually means the assembly needs a missing dependency or a runtime it was not built for.";

    /// <summary>
    /// Describes assemblies whose metadata could not be enumerated, for failures that are only explained by
    /// unreadable metadata (for example a DbContext that would have been found in a skipped assembly).
    /// </summary>
    internal static string DescribeUnreadableAssemblies()
    {
        if (unreadableAssemblies.Count == 0)
            return "";

        string listed = string.Join(", ", unreadableAssemblies.Keys.Take(3).Select(key => "'" + key + "'"));
        return " EfGuard could not enumerate the types of " + unreadableAssemblies.Count + " assembly(ies) of the copied dependency graph (" + listed
            + "), so any DbContext declared there would not be visible; rebuild the project and run EfGuard again, and resolve the reported exception if it persists.";
    }

    internal static void WriteRecordedNotes()
    {
        foreach (string note in notes.Take(NotesLimit))
        {
            try { Console.Error.WriteLine("EfGuard note: " + note); }
            catch { }
        }
    }

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
                     GetTypesOrRecord(assembly, methodName).Where(type => type.IsAbstract && type.IsSealed).SelectMany(StaticMethods))
                     .Where(candidate => candidate.Name == methodName && HasSingleParameter(candidate)))
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

    private static IEnumerable<MethodInfo> StaticMethods(Type type)
    {
        try { return type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
        catch { return []; }
    }

    private static bool HasSingleParameter(MethodInfo method)
    {
        try { return method.GetParameters().Length == 1; }
        catch { return false; }
    }

    private static void RecordUnreadable(Assembly assembly, Exception exception)
        => unreadableAssemblies[Key(assembly)] = Describe(exception);

    private static void RecordNote(Assembly assembly, string? requestedMember, Exception exception)
    {
        ReflectionTypeLoadException? partial = exception as ReflectionTypeLoadException;
        string reason = partial is null
            ? Describe(exception)
            : partial.LoaderExceptions.Where(error => error is not null).Select(Describe).FirstOrDefault() ?? Describe(exception);
        string outcome = partial is null ? "skipped '" : "read only the loadable types of '";
        _ = notes.Add(outcome + DisplayName(assembly) + "'"
            + (requestedMember is null ? "" : " while resolving '" + requestedMember + "'") + " because " + reason);
    }

    private static string Describe(Exception? exception)
    {
        if (exception is null)
            return "an unknown reflection failure";

        string message = exception.Message.ReplaceLineEndings(" ");
        if (message.Length > NoteTextLimit)
            message = message[..NoteTextLimit];
        return exception.GetType().Name + ": " + message;
    }

    private static string Key(Assembly assembly) => assembly.FullName ?? assembly.GetName().Name ?? "";
    private static string DisplayName(Assembly assembly) => Key(assembly);

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
