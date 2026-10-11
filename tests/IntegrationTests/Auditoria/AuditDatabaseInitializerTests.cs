using Infrastructure.Persistencia;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IntegrationTests.Auditoria;

// Inicialización de la base en la nube (spec RF-010, RNF-007): sobre una base vacía, como la RDS recién creada.
// Contenedor propio: los roles son globales del servidor y no deben mezclarse con los de otras pruebas.
public sealed class AuditDatabaseInitializerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("urbanalert_auditoria")
        .WithUsername("postgres_admin")
        .WithPassword("admin_test")
        .Build();

    public Task InitializeAsync() => _database.StartAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private string ComoRol(string usuario, string contrasena) =>
        new NpgsqlConnectionStringBuilder(_database.GetConnectionString()) { Username = usuario, Password = contrasena }.ConnectionString;

    private static async Task<object?> EscalarAsync(string conexion, string sql)
    {
        await using NpgsqlConnection connection = new(conexion);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private Task InicializarAsync(string escritor = "escritor-1", string lector = "lector-1") =>
        AuditDatabaseInitializer.InicializarAsync(_database.GetConnectionString(), escritor, lector, CancellationToken.None);

    [Fact]
    public async Task BaseVacia_CreaRolesYEsquema()
    {
        await InicializarAsync();

        Assert.Equal(0L, await EscalarAsync(ComoRol("audit_writer", "escritor-1"), "SELECT count(*) FROM audit_events"));
        Assert.Equal(new string('0', 64),
            ((string)(await EscalarAsync(ComoRol("audit_reader", "lector-1"), "SELECT last_hash FROM audit_chain_state"))!).Trim());
        Assert.False((bool)(await EscalarAsync(_database.GetConnectionString(),
            "SELECT rolsuper OR rolcreaterole OR rolcreatedb FROM pg_roles WHERE rolname = 'audit_writer'"))!);
    }

    [Fact]
    public async Task SegundaEjecucion_ConservaLosDatosYResincronizaContrasenas()
    {
        await InicializarAsync();
        await EscalarAsync(ComoRol("audit_writer", "escritor-1"),
            """
            INSERT INTO audit_events (event_id, event_type, event_version, occurred_at, actor_id, payload, previous_hash, record_hash)
            VALUES (gen_random_uuid(), 'reporte.creado', 1, now(), gen_random_uuid(), '{}', repeat('0', 64), repeat('b', 64))
            """);

        await InicializarAsync("escritor-2", "lector-2");

        Assert.Equal(1L, await EscalarAsync(ComoRol("audit_reader", "lector-2"), "SELECT count(*) FROM audit_events"));
        await Assert.ThrowsAsync<PostgresException>(() => EscalarAsync(ComoRol("audit_writer", "escritor-1"), "SELECT 1"));
    }

    [Fact]
    public async Task Lector_NoPuedeInsertar()
    {
        await InicializarAsync();

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => EscalarAsync(ComoRol("audit_reader", "lector-1"),
            """
            INSERT INTO audit_events (event_id, event_type, event_version, occurred_at, actor_id, payload, previous_hash, record_hash)
            VALUES (gen_random_uuid(), 'reporte.creado', 1, now(), gen_random_uuid(), '{}', repeat('0', 64), repeat('c', 64))
            """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task EventosRegistrados_NoSePuedenModificar()
    {
        await InicializarAsync();
        await EscalarAsync(_database.GetConnectionString(),
            """
            INSERT INTO audit_events (event_id, event_type, event_version, occurred_at, actor_id, payload, previous_hash, record_hash)
            VALUES (gen_random_uuid(), 'reporte.creado', 1, now(), gen_random_uuid(), '{}', repeat('0', 64), repeat('d', 64))
            """);

        await Assert.ThrowsAsync<PostgresException>(() =>
            EscalarAsync(_database.GetConnectionString(), "UPDATE audit_events SET event_type = 'otro'"));
        await Assert.ThrowsAsync<PostgresException>(() =>
            EscalarAsync(ComoRol("audit_writer", "escritor-1"), "DELETE FROM audit_events"));
    }
}
