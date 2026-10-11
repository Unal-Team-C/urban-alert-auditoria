using System.Net;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.AspNetCoreServer;
using Amazon.Lambda.AspNetCoreServer.Internal;
using Amazon.Lambda.TestUtilities;
using Application.Auditoria;
using Application.Interfaces.Auditoria;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace UnitTests.Lambda;

// Riesgo R-7 del plan: con la integración AWS_PROXY (payload 2.0, stage $default) la función recibe la ruta pública
// completa; la app debe servirla gracias a Gateway:PathBase=/api/v1 (D-007).
public sealed class ApiLambdaHostTests
{
    // Solo se usa la traducción de la petición; el proveedor aporta el logger que esta necesita.
    private sealed class FuncionDePrueba()
        : APIGatewayHttpApiV2ProxyFunction(new ServiceCollection().AddLogging().BuildServiceProvider())
    {
        public IHttpRequestFeature Traducir(APIGatewayHttpApiV2ProxyRequest peticion)
        {
            InvokeFeatures features = new();
            MarshallRequest(features, peticion, new TestLambdaContext());
            return (IHttpRequestFeature)features;
        }
    }

    private static APIGatewayHttpApiV2ProxyRequest PeticionDelGateway(string ruta) => new()
    {
        Version = "2.0",
        RouteKey = "GET /api/v1/auditoria/reportes/{reportId}",
        RawPath = ruta,
        RawQueryString = "limit=10",
        Headers = new Dictionary<string, string> { ["host"] = "aotsp8k5ad.execute-api.us-east-2.amazonaws.com" },
        RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
        {
            Stage = "$default",
            Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = "GET", Path = ruta, Protocol = "HTTP/1.1" }
        }
    };

    [Fact]
    public async Task RutaDelGateway_LlegaAlControladorConElPrefijoConfigurado()
    {
        Guid idReporte = Guid.NewGuid();
        IHttpRequestFeature traducida = new FuncionDePrueba().Traducir(PeticionDelGateway($"/api/v1/auditoria/reportes/{idReporte}"));
        Assert.Equal($"/api/v1/auditoria/reportes/{idReporte}", traducida.Path);
        Assert.True(string.IsNullOrEmpty(traducida.PathBase));

        Mock<IAuditStore> store = new();
        store.Setup(s => s.GetTimelineAsync(idReporte, 0, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditTimeline(idReporte, [], null));
        using WebApplicationFactory<Program> fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Autenticacion:Modo", "Ninguna");
            builder.UseSetting("Gateway:ExigirClave", "false");
            builder.UseSetting("Gateway:PathBase", "/api/v1");
            builder.UseSetting("RabbitMq:Enabled", "false");
            builder.ConfigureTestServices(services => services.AddSingleton(store.Object));
        });
        using HttpClient cliente = fabrica.CreateClient();

        HttpResponseMessage respuesta = await cliente.GetAsync(traducida.Path + traducida.QueryString);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }
}
