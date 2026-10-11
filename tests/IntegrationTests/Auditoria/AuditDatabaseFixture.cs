using Npgsql;
using Testcontainers.PostgreSql;

namespace IntegrationTests.Auditoria;

[CollectionDefinition("audit-postgres")]
public sealed class AuditDatabaseCollection : ICollectionFixture<AuditDatabaseFixture>
{
    public const string Name = "audit-postgres";
}

public sealed class AuditDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("urban_alert_audit_tests")
        .WithUsername("audit_test")
        .WithPassword("audit_test")
        .Build();

    public string ConnectionString => _database.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        await PostgresListo.EsperarAsync(ConnectionString);
        string schemaPath = Path.Combine(AppContext.BaseDirectory, "Migraciones", "001_audit_store.sql");
        string schema = await File.ReadAllTextAsync(schemaPath);
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(schema, connection);
        await command.ExecuteNonQueryAsync();
        await using NpgsqlCommand reportsTable = new(
            """
            CREATE TABLE "Reportes" (
                "Id" UUID PRIMARY KEY,
                "IdUsuario" UUID NOT NULL,
                "IdResponsable" UUID NULL
            )
            """, connection);
        await reportsTable.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    public async Task InsertReportAsync(Guid reportId, Guid ownerId, Guid? responsibleId = null)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            "INSERT INTO \"Reportes\" (\"Id\", \"IdUsuario\", \"IdResponsable\") VALUES (@id, @owner, @responsible)",
            connection);
        command.Parameters.AddWithValue("id", reportId);
        command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.Add(new NpgsqlParameter("responsible", NpgsqlTypes.NpgsqlDbType.Uuid)
        {
            Value = (object?)responsibleId ?? DBNull.Value
        });
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> CountEventsAsync()
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("SELECT count(*) FROM audit_events", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
