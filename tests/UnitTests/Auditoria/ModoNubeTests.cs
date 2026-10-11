using System.Net;
using System.Text.Json;
using Application.Auditoria;
using Application.Interfaces.Auditoria;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace UnitTests.Auditoria;

// Configuración de la nube (plan §7.3, D-006, D-007): sin Cognito, sin autorización por reporte (no hay
// ConnectionStrings:CorePostgres), sin cabecera del gateway y con el prefijo /api/v1 que no reescribe el gateway.
public sealed class ModoNubeTests
{
    private static readonly Dictionary<string, string?> ConfiguracionNube = new()
    {
        ["Autenticacion:Modo"] = "Ninguna",
        ["Gateway:ExigirClave"] = "false",
        ["Gateway:PathBase"] = "/api/v1",
        ["RabbitMq:Enabled"] = "false"
    };

    private static WebApplicationFactory<Program> Fabrica(
        IReadOnlyDictionary<string, string?> configuracion, Mock<IAuditStore> store, string entorno = "Production") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(entorno);
            foreach ((string clave, string? valor) in configuracion)
                builder.UseSetting(clave, valor);
            builder.ConfigureTestServices(services => services.AddSingleton(store.Object));
        });

    private static Mock<IAuditStore> StoreVacio()
    {
        Mock<IAuditStore> store = new();
        store.Setup(s => s.GetTimelineAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, long _, int _, CancellationToken _) => new AuditTimeline(id, [], null));
        store.Setup(s => s.VerifyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => new AuditIntegrityReport(id, true, 0, 0, true, null, DateTimeOffset.UtcNow));
        return store;
    }

    [Theory]
    [InlineData("/api/v1/auditoria/reportes/{0}")]
    [InlineData("/auditoria/reportes/{0}")]
    public async Task Historial_SinTokenNiCabeceraNiBaseDeReportes_Responde200(string ruta)
    {
        Guid idReporte = Guid.NewGuid();
        using WebApplicationFactory<Program> fabrica = Fabrica(ConfiguracionNube, StoreVacio());
        using HttpClient cliente = fabrica.CreateClient();

        HttpResponseMessage respuesta = await cliente.GetAsync(string.Format(ruta, idReporte));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        using JsonDocument cuerpo = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        Assert.Equal(idReporte, cuerpo.RootElement.GetProperty("reportId").GetGuid());
        Assert.Equal(0, cuerpo.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Integridad_SinToken_Responde200()
    {
        using WebApplicationFactory<Program> fabrica = Fabrica(ConfiguracionNube, StoreVacio());
        using HttpClient cliente = fabrica.CreateClient();

        HttpResponseMessage respuesta = await cliente.GetAsync($"/api/v1/auditoria/reportes/{Guid.NewGuid()}/integridad");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task Historial_ConLimiteInvalido_Responde400()
    {
        Mock<IAuditStore> store = StoreVacio();
        using WebApplicationFactory<Program> fabrica = Fabrica(ConfiguracionNube, store);
        using HttpClient cliente = fabrica.CreateClient();

        HttpResponseMessage respuesta = await cliente.GetAsync($"/api/v1/auditoria/reportes/{Guid.NewGuid()}?limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        store.Verify(s => s.GetTimelineAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfiguracionPorDefecto_SinCabeceraDelGateway_Responde403()
    {
        Dictionary<string, string?> configuracion = new()
        {
            ["Cognito:Issuer"] = "https://cognito-idp.us-east-2.amazonaws.com/us-east-2_prueba",
            ["Cognito:AppClientId"] = "cliente-prueba",
            ["Gateway:SharedSecret"] = "secreto-de-prueba",
            ["RabbitMq:Enabled"] = "false"
        };
        using WebApplicationFactory<Program> fabrica = Fabrica(configuracion, StoreVacio());
        using HttpClient cliente = fabrica.CreateClient();

        HttpResponseMessage respuesta = await cliente.GetAsync($"/auditoria/reportes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task ModoCognito_SinToken_Responde401()
    {
        Dictionary<string, string?> configuracion = new()
        {
            ["Cognito:Issuer"] = "https://cognito-idp.us-east-2.amazonaws.com/us-east-2_prueba",
            ["Cognito:AppClientId"] = "cliente-prueba",
            ["Gateway:ExigirClave"] = "false",
            ["RabbitMq:Enabled"] = "false"
        };
        using WebApplicationFactory<Program> fabrica = Fabrica(configuracion, StoreVacio());
        using HttpClient cliente = fabrica.CreateClient();

        HttpResponseMessage respuesta = await cliente.GetAsync($"/auditoria/reportes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    [Fact]
    public void ModoDesconocido_NoArranca()
    {
        Dictionary<string, string?> configuracion = new(ConfiguracionNube) { ["Autenticacion:Modo"] = "Abierto" };
        using WebApplicationFactory<Program> fabrica = Fabrica(configuracion, StoreVacio());

        Assert.ThrowsAny<Exception>(() => fabrica.CreateClient());
    }
}
