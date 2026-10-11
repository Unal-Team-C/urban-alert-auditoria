using System.Security.Claims;
using System.Text.Encodings.Web;
using API.Autenticacion;
using Application.Auditoria;
using Infrastructure.Persistencia;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace UnitTests.Auditoria;

public sealed class AutenticacionDesarrolloTests
{
    private static async Task<AuthenticateResult> AutenticarAsync(Dictionary<string, string>? headers = null)
    {
        Mock<IOptionsMonitor<AuthenticationSchemeOptions>> options = new();
        options.Setup(monitor => monitor.Get(It.IsAny<string>())).Returns(new AuthenticationSchemeOptions());

        AutenticacionDesarrolloHandler handler = new(options.Object, NullLoggerFactory.Instance, UrlEncoder.Default);
        DefaultHttpContext context = new();
        foreach ((string nombre, string valor) in headers ?? [])
            context.Request.Headers[nombre] = valor;

        await handler.InitializeAsync(
            new AuthenticationScheme(AutenticacionDesarrolloHandler.Esquema, null, typeof(AutenticacionDesarrolloHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    // Las mismas reglas que con un token de Cognito.
    private static Task<AuditUserContext> ResolverAsync(ClaimsPrincipal principal) =>
        new AuditAuthorizationService(new ConfigurationBuilder().Build()).ResolveUserAsync(principal, CancellationToken.None);

    [Fact]
    public async Task SinHeaders_EsElAdministradorPorDefecto()
    {
        AuthenticateResult result = await AutenticarAsync();

        Assert.True(result.Succeeded);
        AuditUserContext user = await ResolverAsync(result.Principal!);
        Assert.Equal(AutenticacionDesarrolloHandler.AdministradorPorDefecto, user.Subject);
        Assert.Equal("admin", user.Role);
    }

    [Theory]
    [InlineData("ciudadano")]
    [InlineData("Gestor")]
    [InlineData("ADMIN")]
    public async Task ConHeaders_UsaElUsuarioYElRolEnviados(string rol)
    {
        Guid idUsuario = Guid.NewGuid();

        AuthenticateResult result = await AutenticarAsync(new()
        {
            [AutenticacionDesarrolloHandler.HeaderUsuario] = idUsuario.ToString(),
            [AutenticacionDesarrolloHandler.HeaderRol] = rol
        });

        Assert.True(result.Succeeded);
        AuditUserContext user = await ResolverAsync(result.Principal!);
        Assert.Equal(idUsuario, user.Subject);
        Assert.Equal(rol.ToLowerInvariant(), user.Role);
    }

    [Theory]
    [InlineData(AutenticacionDesarrolloHandler.HeaderUsuario, "no-es-un-guid")]
    [InlineData(AutenticacionDesarrolloHandler.HeaderUsuario, "00000000-0000-0000-0000-000000000000")]
    [InlineData(AutenticacionDesarrolloHandler.HeaderRol, "superusuario")]
    public async Task HeaderInvalido_FallaLaAutenticacion(string header, string valor)
    {
        AuthenticateResult result = await AutenticarAsync(new() { [header] = valor });

        Assert.False(result.Succeeded);
        Assert.Contains(header, result.Failure!.Message);
    }
}
