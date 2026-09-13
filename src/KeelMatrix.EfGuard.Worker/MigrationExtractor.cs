using System.Reflection;
using System.Text.RegularExpressions;
using KeelMatrix.EfGuard;

namespace KeelMatrix.EfGuard.Worker;

internal static class MigrationExtractor
{
    private const string MigrationName = "Microsoft.EntityFrameworkCore.Migrations.Migration";
    private const string MigrationDbContextAttributeName = "Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute";

    internal static List<NormalizedOperation> ReadMigrations(object context, IEnumerable<Assembly> assemblies, string provider, bool providerSupported, out ProviderSqlEvidence providerSql)
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
            object? services = ContextFactory.GetInfrastructureServiceProvider(context);
            object? migrationsAssembly = contract is null || services is not IServiceProvider serviceProvider ? null : serviceProvider.GetService(contract);
            migrationsAssemblyInstance = migrationsAssembly is null ? context.GetType().Assembly : Reflection.Value(migrationsAssembly, "Assembly") as Assembly ?? context.GetType().Assembly;
            Reflection.EnsureTypesReadable(migrationsAssemblyInstance, "the migrations assembly EF Core reports for the selected DbContext");
            object? entries = migrationsAssembly is null ? null : contract?.GetProperty("Migrations")?.GetValue(migrationsAssembly);
            if (entries is not System.Collections.IEnumerable enumerable)
                throw new InvalidOperationException();

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
        catch (ExtractionFailureException)
        {
            throw;
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
            object? services = ContextFactory.GetInfrastructureServiceProvider(context);
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

    private static string ClassifySql(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "raw-sql";
        string withoutComments = Regex.Replace(sql, @"--[^\r\n]*|/\*.*?\*/", " ", RegexOptions.Singleline);
        string normalized = Regex.Replace(withoutComments, @"'(?:''|[^'])*'", " ", RegexOptions.Singleline);
        Match update = Regex.Match(normalized, @"\bUPDATE\b", RegexOptions.IgnoreCase);
        return update.Success && !Regex.IsMatch(normalized[update.Index..], @"\bWHERE\b", RegexOptions.IgnoreCase) ? "sql-backfill" : "raw-sql";
    }

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
}
