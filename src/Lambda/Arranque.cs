using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Infrastructure;
using Infrastructure.Configuracion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace Auditoria.Lambda;

// Servicios compartidos por las funciones de registro e init, creados una vez por instancia de ejecución: configuración
// desde las variables de entorno de la función (plan §7.3), cadenas de conexión desde Secrets Manager (D-008) y logs
// de consola en JSON (D-011). RabbitMQ queda desactivado: en la nube los eventos llegan por la cola.
public static class Arranque
{
    private static readonly Lazy<ServiceProvider> Proveedor = new(Crear);

    public static IServiceProvider Servicios => Proveedor.Value;

    private static ServiceProvider Crear()
    {
        IConfiguration configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RabbitMq:Enabled"] = "false" })
            .AddEnvironmentVariables()
            .AddSecretosConexion()
            .Build();

        ServiceCollection servicios = new();
        servicios.AddSingleton(configuracion);
        servicios.AddLogging(logging => logging
            .AddConfiguration(configuracion.GetSection("Logging"))
            .AddConsole());
        servicios.AddInfrastructure(configuracion);
        return servicios.BuildServiceProvider();
    }
}
