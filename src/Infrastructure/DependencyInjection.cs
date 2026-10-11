using Amazon;
using Amazon.S3;
using Application.Comun;
using Application.Interfaces.Auditoria;
using Infrastructure.Archivado;
using Infrastructure.Mensajeria;
using Infrastructure.Persistencia;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditStore, AuditStore>();
        // Cognito (por defecto): autorización por reporte leyendo la tabla "Reportes". Ninguna: sin restricciones (plan D-006).
        if (ModoAutenticacion.Leer(configuration) == ModoAutenticacion.Ninguna)
            services.AddSingleton<IAuditAuthorizationService, SinAutorizacionService>();
        else
            services.AddSingleton<IAuditAuthorizationService, AuditAuthorizationService>();
        services.AddSingleton<IAuditArchive>(_ => CreateAuditArchive(configuration));

        if (configuration.GetValue<bool>("RabbitMq:Enabled"))
            AgregarMassTransit(services, configuration);

        return services;
    }

    private static IAuditArchive CreateAuditArchive(IConfiguration configuration)
    {
        string? bucket = configuration["AuditArchive:Bucket"];
        if (string.IsNullOrWhiteSpace(bucket))
            return new NoOpAuditArchive();

        int retentionDays = configuration.GetValue<int?>("AuditArchive:ObjectLockRetentionDays") ?? 2555;
        if (retentionDays < 1)
            throw new InvalidOperationException("AuditArchive:ObjectLockRetentionDays debe ser mayor que cero.");

        AmazonS3Config s3Configuration = new()
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(configuration["AWS_REGION"] ?? "us-east-1")
        };
        string? serviceUrl = configuration["AuditArchive:ServiceUrl"];
        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            s3Configuration.ServiceURL = serviceUrl;
            s3Configuration.ForcePathStyle = true;
        }

        return new S3AuditArchive(new AmazonS3Client(s3Configuration), bucket, retentionDays);
    }

    private static void AgregarMassTransit(IServiceCollection services, IConfiguration configuration)
    {
        string rabbitHost = configuration["RabbitMq:Host"] ?? "localhost";
        string virtualHost = configuration["RabbitMq:VirtualHost"] ?? "/";
        string username = configuration["RabbitMq:Username"] ?? "guest";
        string password = configuration["RabbitMq:Password"] ?? "guest";
        string queue = configuration["RabbitMq:Queue"] ?? "q_auditoria_dotnet";

        services.AddMassTransit(registration =>
        {
            registration.AddConsumer<ReporteCreadoConsumer>();
            registration.UsingRabbitMq((context, bus) =>
            {
                bus.Host(rabbitHost, virtualHost, host =>
                {
                    host.Username(username);
                    host.Password(password);
                });
                bus.ConfigureJsonSerializerOptions(CodigosEnum.ConfigurarMensajeria);
                bus.ReceiveEndpoint(queue, endpoint =>
                {
                    endpoint.UseMessageRetry(retry => retry.Intervals(
                        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));
                    endpoint.ConfigureConsumer<ReporteCreadoConsumer>(context);
                });
            });
        });
    }
}
