using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Infrastructure.Persistencia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Auditoria.Lambda;

public sealed record RespuestaInit([property: JsonPropertyName("ok")] bool Ok);

// Función urban-alert-auditoria-init: la invoca Provision (scripts/run-init.sh) antes de conectar la regla y publicar
// las rutas (spec RF-010, plan §4.3 paso 5). Con el usuario maestro de RDS (secreto Init:AdminSecretArn, gestionado
// por RDS) crea o sincroniza audit_writer y audit_reader con las contraseñas de sus secretos y aplica el esquema.
// Nunca devuelve ni registra cadenas de conexión ni contraseñas.
public sealed class InitHandler
{
    public async Task<RespuestaInit> ManejarAsync(ILambdaContext contexto)
    {
        IConfiguration configuracion = Arranque.Servicios.GetRequiredService<IConfiguration>();
        ILogger<InitHandler> logger = Arranque.Servicios.GetRequiredService<ILogger<InitHandler>>();
        try
        {
            NpgsqlConnectionStringBuilder escritor = new(Requerido(configuracion.GetConnectionString("AuditWriter"), "ConnectionStrings:AuditWriter"));
            NpgsqlConnectionStringBuilder lector = new(Requerido(configuracion.GetConnectionString("AuditReader"), "ConnectionStrings:AuditReader"));
            (string usuario, string contrasena) = await LeerAdministradorAsync(Requerido(configuracion["Init:AdminSecretArn"], "Init:AdminSecretArn"));

            // Misma instancia, base y opciones TLS que la aplicación; sin pool: es una sola conexión.
            NpgsqlConnectionStringBuilder administrador = new(escritor.ConnectionString)
            {
                Username = usuario,
                Password = contrasena,
                Pooling = false
            };

            await AuditDatabaseInitializer.InicializarAsync(
                administrador.ConnectionString, escritor.Password!, lector.Password!, CancellationToken.None);
            logger.LogInformation("Base de auditoría inicializada: roles {Escritor} y {Lector} y esquema 001_audit_store.",
                AuditDatabaseInitializer.RolEscritor, AuditDatabaseInitializer.RolLector);
            return new RespuestaInit(true);
        }
        catch (Exception exception)
        {
            // Solo el tipo y el código de PostgreSQL: los mensajes de conexión pueden incluir el host o el usuario.
            string detalle = exception is PostgresException postgres ? $"{postgres.SqlState} {postgres.MessageText}" : exception.GetType().Name;
            logger.LogError("La inicialización de la base falló: {Detalle}", detalle);
            throw new InvalidOperationException($"La inicialización de la base falló: {detalle}");
        }
    }

    private static string Requerido(string? valor, string clave) =>
        string.IsNullOrWhiteSpace(valor) ? throw new InvalidOperationException($"Falta la configuración '{clave}'.") : valor;

    private static async Task<(string Usuario, string Contrasena)> LeerAdministradorAsync(string arn)
    {
        using AmazonSecretsManagerClient cliente = new();
        GetSecretValueResponse respuesta = await cliente.GetSecretValueAsync(new GetSecretValueRequest { SecretId = arn });
        using JsonDocument documento = JsonDocument.Parse(respuesta.SecretString);
        return (documento.RootElement.GetProperty("username").GetString()!, documento.RootElement.GetProperty("password").GetString()!);
    }
}
