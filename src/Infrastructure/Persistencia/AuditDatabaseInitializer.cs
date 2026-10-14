using System.Reflection;
using Npgsql;

namespace Infrastructure.Persistencia;

// Inicialización de la base en la nube (spec RF-010, plan §6): la invoca la función de init desde Provision con el
// usuario administrador de RDS. Crea o sincroniza los roles de la aplicación con las contraseñas de sus secretos y
// aplica 001_audit_store.sql, todo en una transacción e idempotente (se repite en cada Provision).
public static class AuditDatabaseInitializer
{
    public const string RolEscritor = "audit_writer";
    public const string RolLector = "audit_reader";
    private const string RecursoEsquema = "Infrastructure.Persistencia.Migraciones.001_audit_store.sql";

    public static async Task InicializarAsync(
        string adminConnectionString, string writerPassword, string readerPassword, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(writerPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(readerPassword);

        await using NpgsqlConnection connection = new(adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        await SincronizarRolAsync(connection, transaction, RolEscritor, writerPassword, cancellationToken);
        await SincronizarRolAsync(connection, transaction, RolLector, readerPassword, cancellationToken);

        // Los GRANT del script solo se aplican si los roles ya existen: por eso van antes.
        await using (NpgsqlCommand esquema = new(await LeerEsquemaAsync(cancellationToken), connection, transaction))
            await esquema.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SincronizarRolAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string rol, string password, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand existe = new("SELECT 1 FROM pg_roles WHERE rolname = @rol", connection, transaction);
        existe.Parameters.AddWithValue("rol", rol);
        bool creado = await existe.ExecuteScalarAsync(cancellationToken) is not null;

        // CREATE/ALTER ROLE no admiten parámetros: la contraseña va como literal escapado. El nombre del rol es constante.
        // Los atributos se fijan al crear el rol. Al sincronizarlo solo se cambia la contraseña: desde PostgreSQL 16
        // nombrar SUPERUSER/NOSUPERUSER en un ALTER exige ser superusuario, y el administrador de RDS no lo es.
        string literal = "'" + password.Replace("'", "''", StringComparison.Ordinal) + "'";
        string sql = creado
            ? $"ALTER ROLE {rol} WITH LOGIN PASSWORD {literal}"
            : $"CREATE ROLE {rol} WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD {literal}";
        await using NpgsqlCommand command = new(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> LeerEsquemaAsync(CancellationToken cancellationToken)
    {
        await using Stream recurso = Assembly.GetExecutingAssembly().GetManifestResourceStream(RecursoEsquema)
                                     ?? throw new InvalidOperationException($"No se encontró el recurso {RecursoEsquema}.");
        using StreamReader lector = new(recurso);
        return await lector.ReadToEndAsync(cancellationToken);
    }
}
