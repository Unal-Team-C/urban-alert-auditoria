using System.Security.Cryptography;
using System.Text;
using API.Autenticacion;
using Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string? issuer = builder.Configuration["Cognito:Issuer"];
string? appClientId = builder.Configuration["Cognito:AppClientId"];

if (!builder.Environment.IsDevelopment() && (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(appClientId)))
    throw new InvalidOperationException("Cognito:Issuer y Cognito:AppClientId son obligatorios fuera de Development.");

// Modo de desarrollo sin Cognito (AutenticacionDesarrolloHandler). Nunca fuera de Development.
bool modoDesarrollo = builder.Configuration.GetValue<bool>("Autenticacion:ModoDesarrollo");
if (modoDesarrollo && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Autenticacion:ModoDesarrollo solo se permite en Development.");
const string EsquemaJwtODesarrollo = "JwtODesarrollo";

builder.Services.AddControllers();
builder.Services.AddOpenApi(options =>
{
    // En modo de desarrollo, Scalar muestra los headers que reemplazan al token de Cognito.
    if (modoDesarrollo)
        options.AddOperationTransformer(AutenticacionDesarrolloHandler.DocumentarHeadersAsync);
});
builder.Services.AddInfrastructure(builder.Configuration);

AuthenticationBuilder autenticacion = builder.Services
    .AddAuthentication(modoDesarrollo ? EsquemaJwtODesarrollo : JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = issuer;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrWhiteSpace(issuer),
            ValidIssuer = issuer,
            ValidateAudience = false,
            NameClaimType = "sub"
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                string? tokenUse = context.Principal?.FindFirst("token_use")?.Value;
                bool validClient = tokenUse switch
                {
                    "access" => context.Principal?.FindFirst("client_id")?.Value == appClientId,
                    "id" => context.Principal?.FindAll("aud").Any(claim => claim.Value == appClientId) == true,
                    _ => false
                };
                if (!validClient)
                    context.Fail("Se requiere un token Cognito de Urban Alert.");
                return Task.CompletedTask;
            }
        };
    });
if (modoDesarrollo)
{
    // Con Authorization: Bearer se valida el token de Cognito como siempre; sin él, se usan los
    // headers de desarrollo.
    autenticacion
        .AddScheme<AuthenticationSchemeOptions, AutenticacionDesarrolloHandler>(AutenticacionDesarrolloHandler.Esquema, null)
        .AddPolicyScheme(EsquemaJwtODesarrollo, null, options => options.ForwardDefaultSelector = context =>
            context.Request.Headers.Authorization.Count > 0
                ? JwtBearerDefaults.AuthenticationScheme
                : AutenticacionDesarrolloHandler.Esquema);
}
builder.Services.AddAuthorization();

WebApplication app = builder.Build();

if (modoDesarrollo)
    app.Logger.LogWarning(
        "Modo de desarrollo sin Cognito: sin token, la identidad se toma de {HeaderUsuario} y {HeaderRol} (por defecto, admin).",
        AutenticacionDesarrolloHandler.HeaderUsuario, AutenticacionDesarrolloHandler.HeaderRol);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health")
    {
        await next();
        return;
    }

    string? sharedSecret = app.Configuration["Gateway:SharedSecret"];
    if (string.IsNullOrWhiteSpace(sharedSecret))
    {
        if (!app.Environment.IsDevelopment())
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { message = "La clave del gateway no está configurada." });
            return;
        }
    }
    else
    {
        byte[] expected = Encoding.UTF8.GetBytes(sharedSecret);
        byte[] supplied = Encoding.UTF8.GetBytes(context.Request.Headers["X-Urban-Gateway-Key"].ToString());
        if (expected.Length != supplied.Length || !CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = "La solicitud debe ingresar por API Gateway." });
            return;
        }
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

public partial class Program { }
