using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.Configuracion;

// En la nube cada función solo recibe el ARN de su secreto (ConnectionStrings:<Nombre>SecretArn) y la cadena de
// conexión se lee de Secrets Manager una vez al arrancar (spec RF-011, plan D-008). Sin ARN no hace nada: en local
// las cadenas siguen llegando por User Secrets o variables de entorno.
public static class SecretosConexion
{
    public const string SufijoArn = "SecretArn";

    public static IConfigurationBuilder AddSecretosConexion(
        this IConfigurationBuilder builder, Func<IAmazonSecretsManager>? crearCliente = null) =>
        builder.Add(new Fuente(crearCliente ?? (() => new AmazonSecretsManagerClient())));

    private sealed class Fuente(Func<IAmazonSecretsManager> crearCliente) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            ConfigurationBuilder previas = new();
            foreach (IConfigurationSource fuente in builder.Sources.Where(fuente => !ReferenceEquals(fuente, this)))
                previas.Add(fuente);
            return new Proveedor(previas.Build(), crearCliente);
        }
    }

    private sealed class Proveedor(IConfiguration previa, Func<IAmazonSecretsManager> crearCliente) : ConfigurationProvider
    {
        public override void Load()
        {
            List<(string Nombre, string Arn)> pendientes = previa.GetSection("ConnectionStrings").GetChildren()
                .Where(c => c.Key.EndsWith(SufijoArn, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(c.Value))
                .Select(c => (c.Key[..^SufijoArn.Length], c.Value!))
                .ToList();
            if (pendientes.Count == 0)
                return;

            using IAmazonSecretsManager cliente = crearCliente();
            foreach ((string nombre, string arn) in pendientes)
                Data[$"ConnectionStrings:{nombre}"] = LeerCadena(cliente, nombre, arn);
        }

        private static string LeerCadena(IAmazonSecretsManager cliente, string nombre, string arn)
        {
            string? valor = cliente.GetSecretValueAsync(new GetSecretValueRequest { SecretId = arn })
                .GetAwaiter().GetResult().SecretString;
            try
            {
                using JsonDocument documento = JsonDocument.Parse(valor ?? "");
                if (documento.RootElement.ValueKind == JsonValueKind.Object &&
                    documento.RootElement.TryGetProperty("connection_string", out JsonElement cadena) &&
                    cadena.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cadena.GetString()))
                    return cadena.GetString()!;
            }
            catch (JsonException)
            {
                // Sin la excepción original: su mensaje puede incluir parte del secreto.
            }

            throw new InvalidOperationException(
                $"El secreto de ConnectionStrings:{nombre} no es un JSON con 'connection_string'.");
        }
    }
}
