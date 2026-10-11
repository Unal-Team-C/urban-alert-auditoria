using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace API.Autenticacion;

// Modo de desarrollo sin Cognito: la identidad sale de los headers X-Usuario-Id y X-Usuario-Rol
// (ciudadano, gestor o admin). Sin headers, la petición es del administrador de los datos semilla
// de Usuarios. Produce los mismos claims que un token de Cognito ("sub" y "cognito:groups"), así
// que AuditAuthorizationService aplica las mismas reglas de acceso.
// Solo se registra con Autenticacion:ModoDesarrollo=true en Development (ver Program.cs).
public sealed class AutenticacionDesarrolloHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Esquema = "Desarrollo";
    public const string HeaderUsuario = "X-Usuario-Id";
    public const string HeaderRol = "X-Usuario-Rol";

    // Administrador de los datos semilla del servicio de Usuarios.
    public static readonly Guid AdministradorPorDefecto = Guid.Parse("018f4c2a-0000-7000-8000-000000000001");

    private static readonly string[] Roles = ["ciudadano", "gestor", "admin"];

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? idUsuario = Request.Headers[HeaderUsuario].FirstOrDefault();
        string? rol = Request.Headers[HeaderRol].FirstOrDefault();

        Guid sub = AdministradorPorDefecto;
        if (!string.IsNullOrWhiteSpace(idUsuario) && (!Guid.TryParse(idUsuario, out sub) || sub == Guid.Empty))
            return Task.FromResult(AuthenticateResult.Fail($"{HeaderUsuario} debe ser un GUID válido."));

        rol = string.IsNullOrWhiteSpace(rol) ? "admin" : rol.Trim().ToLowerInvariant();
        if (!Roles.Contains(rol))
            return Task.FromResult(AuthenticateResult.Fail($"{HeaderRol} debe ser ciudadano, gestor o admin."));

        ClaimsIdentity identity = new(
            [new Claim("sub", sub.ToString()), new Claim("cognito:groups", rol)],
            Esquema,
            nameType: "sub",
            roleType: "cognito:groups");

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Esquema)));
    }

    public static Task DocumentarHeadersAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.RelativePath?.StartsWith("auditoria", StringComparison.Ordinal) != true)
            return Task.CompletedTask;

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = HeaderUsuario,
            In = ParameterLocation.Header,
            Description = $"Modo de desarrollo: usuario que consulta. Por defecto {AdministradorPorDefecto}.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
        });
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = HeaderRol,
            In = ParameterLocation.Header,
            Description = "Modo de desarrollo: ciudadano, gestor o admin. Por defecto admin.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
        return Task.CompletedTask;
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        AuthenticateResult result = await HandleAuthenticateOnceSafeAsync();
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsJsonAsync(new { message = result.Failure?.Message ?? "No autenticado." });
    }
}
