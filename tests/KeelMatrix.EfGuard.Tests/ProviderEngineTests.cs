using Microsoft.Data.SqlClient;
using Npgsql;

namespace KeelMatrix.EfGuard.Tests;

/// <summary>
/// Real-engine integration evidence for provider-behavior findings. The tests run only when the
/// corresponding engine is selected for the run, and they record the claims they verified so the
/// repository can compare the recorded evidence with the shipped provider engine evidence.
/// </summary>
public sealed class PostgreSqlProviderEngineTests
{
    private const string Provider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string Engine = "postgresql";

    [Fact]
    public async Task NonConcurrentIndexCreationBlocksConcurrentWrites()
    {
        if (!ProviderEngine.IsEnabled(Engine))
            return;

        string connectionString = ProviderEngine.RequireConnection(Engine);
        string schema = "efguard_" + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection connection = await ProviderEngine.OpenPostgreSqlAsync(connectionString);
        await ExecuteAsync(connection, $"CREATE SCHEMA {schema}");
        await ExecuteAsync(connection, $"CREATE TABLE {schema}.\"Orders\" (\"Id\" integer NOT NULL)");

        string createIndex = await ProviderEngine.GeneratedIndexSqlAsync("Ef8Postgres");
        await using NpgsqlConnection writer = await ProviderEngine.OpenPostgreSqlAsync(connectionString);
        await ExecuteAsync(writer, $"SET search_path TO {schema}");

        await ExecuteAsync(connection, $"SET search_path TO {schema}");
        await ExecuteAsync(connection, "BEGIN");
        await ExecuteAsync(connection, createIndex);

        PostgresException blocked = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(writer, "SET lock_timeout = '2000ms';\nINSERT INTO \"Orders\" (\"Id\") VALUES (1)"));
        Assert.Equal("55P03", blocked.SqlState);
        await ExecuteAsync(connection, "ROLLBACK");

        ProviderEngine.Record(Engine, "EFG302", Provider, "non-concurrent-index-creation-blocks-concurrent-writes", nameof(PostgreSqlProviderEngineTests) + "." + nameof(NonConcurrentIndexCreationBlocksConcurrentWrites));
    }

    [Fact]
    public async Task ConcurrentIndexCreationRunsOutsideTransactionsOnly()
    {
        if (!ProviderEngine.IsEnabled(Engine))
            return;

        string connectionString = ProviderEngine.RequireConnection(Engine);
        string schema = "efguard_" + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection connection = await ProviderEngine.OpenPostgreSqlAsync(connectionString);
        await ExecuteAsync(connection, $"CREATE SCHEMA {schema}");
        await ExecuteAsync(connection, $"CREATE TABLE {schema}.\"Orders\" (\"Id\" integer NOT NULL)");
        await ExecuteAsync(connection, $"SET search_path TO {schema}");

        string createIndex = await ProviderEngine.GeneratedIndexSqlAsync("Ef8Postgres");
        string concurrentIndex = createIndex.Replace("CREATE INDEX", "CREATE INDEX CONCURRENTLY", StringComparison.Ordinal);
        Assert.Contains("CONCURRENTLY", concurrentIndex, StringComparison.Ordinal);
        await ExecuteAsync(connection, concurrentIndex);

        await ExecuteAsync(connection, "BEGIN");
        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, concurrentIndex.Replace("IX_Orders_Id", "IX_Orders_Id_2", StringComparison.Ordinal)));
        Assert.Equal("25001", rejected.SqlState);
        await ExecuteAsync(connection, "ROLLBACK");

        ProviderEngine.Record(Engine, "EFG302", Provider, "concurrent-index-creation-requires-no-transaction", nameof(PostgreSqlProviderEngineTests) + "." + nameof(ConcurrentIndexCreationRunsOutsideTransactionsOnly));
    }

    [Fact]
    public async Task ForeignKeyValidationBlocksConcurrentWrites()
    {
        if (!ProviderEngine.IsEnabled(Engine))
            return;

        string connectionString = ProviderEngine.RequireConnection(Engine);
        string schema = "efguard_" + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection connection = await ProviderEngine.OpenPostgreSqlAsync(connectionString);
        await ExecuteAsync(connection, $"CREATE SCHEMA {schema}");
        await ExecuteAsync(connection, $"CREATE TABLE {schema}.\"Orders\" (\"Id\" integer NOT NULL, CONSTRAINT \"PK_Orders\" PRIMARY KEY (\"Id\"))");
        await ExecuteAsync(connection, $"CREATE TABLE {schema}.\"OrderLines\" (\"Id\" integer NOT NULL, \"OrderId\" integer NOT NULL)");
        await ExecuteAsync(connection, $"SET search_path TO {schema}");

        await using NpgsqlConnection writer = await ProviderEngine.OpenPostgreSqlAsync(connectionString);
        await ExecuteAsync(writer, $"SET search_path TO {schema}");

        await ExecuteAsync(connection, "BEGIN");
        await ExecuteAsync(connection, "ALTER TABLE \"OrderLines\" ADD CONSTRAINT \"FK_OrderLines_Orders\" FOREIGN KEY (\"OrderId\") REFERENCES \"Orders\" (\"Id\")");
        PostgresException blocked = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(writer, "SET lock_timeout = '2000ms';\nINSERT INTO \"OrderLines\" (\"Id\", \"OrderId\") VALUES (1, 1)"));
        Assert.Equal("55P03", blocked.SqlState);
        await ExecuteAsync(connection, "ROLLBACK");

        ProviderEngine.Record(Engine, "EFG303", Provider, "foreign-key-validation-blocks-concurrent-writes", nameof(PostgreSqlProviderEngineTests) + "." + nameof(ForeignKeyValidationBlocksConcurrentWrites));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class SqlServerProviderEngineTests
{
    private const string Provider = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string Engine = "sqlserver";

    [Fact]
    public async Task OfflineIndexCreationBlocksConcurrentWrites()
    {
        if (!ProviderEngine.IsEnabled(Engine))
            return;

        string connectionString = ProviderEngine.RequireConnection(Engine);
        string target = await ProviderEngine.EnsureSqlServerDatabaseAsync(connectionString, orderTable: true);
        try
        {
            string createIndex = await ProviderEngine.GeneratedIndexSqlAsync("Ef9SqlServer");
            await using SqlConnection connection = await ProviderEngine.OpenSqlServerAsync(target);
            await using SqlConnection writer = await ProviderEngine.OpenSqlServerAsync(target);

            await ExecuteAsync(connection, "BEGIN TRAN");
            await ExecuteAsync(connection, createIndex);
            Task<int> blockedWrite = ExecuteAsync(writer, "INSERT INTO [Orders] ([Id]) VALUES (1)");
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.False(blockedWrite.IsCompleted, "Offline index creation did not block a concurrent write.");
            await ExecuteAsync(connection, "ROLLBACK");
            Assert.Equal(1, await blockedWrite);

            ProviderEngine.Record(Engine, "EFG301", Provider, "offline-index-creation-blocks-concurrent-writes", nameof(SqlServerProviderEngineTests) + "." + nameof(OfflineIndexCreationBlocksConcurrentWrites));
        }
        finally
        {
            await ProviderEngine.DropSqlServerDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task OnlineIndexCreationIsAcceptedByEngine()
    {
        if (!ProviderEngine.IsEnabled(Engine))
            return;

        string connectionString = ProviderEngine.RequireConnection(Engine);
        string target = await ProviderEngine.EnsureSqlServerDatabaseAsync(connectionString, orderTable: true);
        try
        {
            await using SqlConnection connection = await ProviderEngine.OpenSqlServerAsync(target);
            await ExecuteAsync(connection, "CREATE INDEX [IX_Orders_Id] ON [Orders] ([Id])");
            await ExecuteAsync(connection, "DROP INDEX [IX_Orders_Id] ON [Orders]");
            await ExecuteAsync(connection, "CREATE INDEX [IX_Orders_Id] ON [Orders] ([Id]) WITH (ONLINE = ON)");

            ProviderEngine.Record(Engine, "EFG301", Provider, "online-index-creation-is-supported", nameof(SqlServerProviderEngineTests) + "." + nameof(OnlineIndexCreationIsAcceptedByEngine));
        }
        finally
        {
            await ProviderEngine.DropSqlServerDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task ForeignKeyValidationBlocksConcurrentWrites()
    {
        if (!ProviderEngine.IsEnabled(Engine))
            return;

        string connectionString = ProviderEngine.RequireConnection(Engine);
        string target = await ProviderEngine.EnsureSqlServerDatabaseAsync(connectionString, orderTable: false);
        try
        {
            await using SqlConnection connection = await ProviderEngine.OpenSqlServerAsync(target);
            await using SqlConnection writer = await ProviderEngine.OpenSqlServerAsync(target);
            await ExecuteAsync(connection, "CREATE TABLE [Orders] ([Id] int NOT NULL CONSTRAINT [PK_Orders] PRIMARY KEY)");
            await ExecuteAsync(connection, "CREATE TABLE [OrderLines] ([Id] int NOT NULL, [OrderId] int NOT NULL)");

            await ExecuteAsync(connection, "BEGIN TRAN");
            await ExecuteAsync(connection, "ALTER TABLE [OrderLines] WITH CHECK ADD CONSTRAINT [FK_OrderLines_Orders] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id])");
            Task<int> blockedWrite = ExecuteAsync(writer, "INSERT INTO [OrderLines] ([Id], [OrderId]) VALUES (1, 1)");
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.False(blockedWrite.IsCompleted, "Foreign-key validation did not block a concurrent write.");
            await ExecuteAsync(connection, "ROLLBACK");
            Assert.Equal(1, await blockedWrite);

            ProviderEngine.Record(Engine, "EFG303", Provider, "foreign-key-validation-blocks-concurrent-writes", nameof(SqlServerProviderEngineTests) + "." + nameof(ForeignKeyValidationBlocksConcurrentWrites));
        }
        finally
        {
            await ProviderEngine.DropSqlServerDatabaseAsync(connectionString);
        }
    }

    private static async Task<int> ExecuteAsync(SqlConnection connection, string sql)
    {
        await using SqlCommand command = new(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }
}

internal static class ProviderEngine
{
    private static readonly object Gate = new();
    private static readonly List<ProviderEngineClaim> Claims = [];

    internal static bool IsEnabled(string engine)
        => Environment.GetEnvironmentVariable("EFGUARD_PROVIDER_ENGINE")?.Equals(engine, StringComparison.OrdinalIgnoreCase) == true;

    internal static string RequireConnection(string engine)
    {
        string name = engine.Equals("postgresql", StringComparison.OrdinalIgnoreCase) ? "EFGUARD_POSTGRES_CONNECTION" : "EFGUARD_SQLSERVER_CONNECTION";
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} must be set for the {engine} provider engine evidence run.");
        return value;
    }

    internal static async Task<string> GeneratedIndexSqlAsync(string fixture)
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", fixture, fixture + "Fixture.csproj"));
        ExtractionResult result = await ExtractionCoordinator.ExtractAsync(project, project, null, CancellationToken.None);
        Assert.True(result.Success, $"Extraction failed for {fixture}: {result.Error ?? "(no error returned)"}");
        string sql = Assert.Single(result.ProviderSql.Statements, statement => statement.Sql.Contains("CREATE INDEX", StringComparison.OrdinalIgnoreCase)).Sql;
        Assert.Contains("CREATE INDEX", sql, StringComparison.OrdinalIgnoreCase);
        return sql.Trim().TrimEnd(';');
    }

    internal static async Task<NpgsqlConnection> OpenPostgreSqlAsync(string connectionString)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            NpgsqlConnection connection = new(connectionString);
            try
            {
                await connection.OpenAsync();
                return connection;
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
            {
                last = exception;
                await connection.DisposeAsync();
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new InvalidOperationException("The PostgreSQL engine did not become available for the evidence run.", last);
    }

    internal static async Task<SqlConnection> OpenSqlServerAsync(string connectionString)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            SqlConnection connection = new(connectionString);
            try
            {
                await connection.OpenAsync();
                return connection;
            }
            catch (Exception exception) when (exception is SqlException or InvalidOperationException)
            {
                last = exception;
                await connection.DisposeAsync();
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new InvalidOperationException("The SQL Server engine did not become available for the evidence run.", last);
    }

    internal static async Task<string> EnsureSqlServerDatabaseAsync(string connectionString, bool orderTable)
    {
        string database = DatabaseName(connectionString);
        string master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString;
        await using (SqlConnection connection = await OpenSqlServerAsync(master))
        await using (SqlCommand command = new($"IF DB_ID('{database}') IS NULL CREATE DATABASE [{database}];", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        string target = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;
        if (orderTable)
        {
            await using SqlConnection created = await OpenSqlServerAsync(target);
            await using SqlCommand table = new("IF OBJECT_ID('dbo.Orders') IS NULL CREATE TABLE [Orders] ([Id] int NOT NULL);", created);
            await table.ExecuteNonQueryAsync();
        }

        return target;
    }

    internal static async Task DropSqlServerDatabaseAsync(string connectionString)
    {
        SqlConnectionStringBuilder builder = new(connectionString) { InitialCatalog = "master" };
        string database = DatabaseName(connectionString);
        try
        {
            await using SqlConnection connection = await OpenSqlServerAsync(builder.ConnectionString);
            await using SqlCommand command = new($"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (SqlException)
        {
        }
    }

    internal static void Record(string engine, string rule, string provider, string behavior, string verifiedBy)
    {
        lock (Gate)
        {
            Claims.Add(new ProviderEngineClaim { Rule = rule, Provider = provider, Engine = engine, Behavior = behavior, VerifiedBy = verifiedBy });
            string? path = Environment.GetEnvironmentVariable("EFGUARD_PROVIDER_ENGINE_EVIDENCE");
            if (string.IsNullOrWhiteSpace(path))
                return;

            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            ProviderEngineEvidence evidence = new()
            {
                Version = 1,
                Claims = Claims
                    .Where(claim => claim.Engine.Equals(engine, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(claim => claim.Rule, StringComparer.Ordinal)
                    .ThenBy(claim => claim.Provider, StringComparer.Ordinal)
                    .ThenBy(claim => claim.Behavior, StringComparer.Ordinal)
                    .ToList()
            };
            File.WriteAllText(path, evidence.Serialize());
        }
    }

    private static string DatabaseName(string connectionString)
    {
        string? configured = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        return string.IsNullOrWhiteSpace(configured) || configured.Equals("master", StringComparison.OrdinalIgnoreCase)
            ? "EfGuardEngineTests"
            : configured;
    }
}
