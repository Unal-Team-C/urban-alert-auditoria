using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace API.Autenticacion;

// Modo de la nube sin autenticación de clientes (Autenticacion:Modo=Ninguna; spec RF-007, plan D-006): toda
// petición que llega por el gateway se autentica con una identidad fija de administrador. SinAutorizacionService
// no aplica restricciones por reporte. Se elimina cuando el gateway autentique con Cognito.
public sealed class AutenticacionAbiertaHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Esquema = "Abierta";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        ClaimsIdentity identity = new(
            [new Claim("sub", Guid.Empty.ToString()), new Claim("cognito:groups", "admin")],
            Esquema,
            nameType: "sub",
            roleType: "cognito:groups");

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Esquema)));
    }
}
