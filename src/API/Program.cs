using System.Security.Cryptography;
using System.Text;
using API.Autenticacion;
using Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string? issuer = builder.Configuration["Cognito:Issuer"];
string? appClientId = builder.Configuration["Cognito:AppClientId"];

// Cognito (por defecto, local) o Ninguna (nube, sin autenticación de clientes; plan D-006).
bool sinAutenticacion = ModoAutenticacion.Leer(builder.Configuration) == ModoAutenticacion.Ninguna;

if (!sinAutenticacion && !builder.Environment.IsDevelopment() &&
    (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(appClientId)))
    throw new InvalidOperationException("Cognito:Issuer y Cognito:AppClientId son obligatorios fuera de Development.");

// Modo de desarrollo sin Cognito (AutenticacionDesarrolloHandler). Nunca fuera de Development.
bool modoDesarrollo = builder.Configuration.GetValue<bool>("Autenticacion:ModoDesarrollo");
if (modoDesarrollo && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Autenticacion:ModoDesarrollo solo se permite en Development.");
if (modoDesarrollo && sinAutenticacion)
    throw new InvalidOperationException("Autenticacion:ModoDesarrollo no se combina con Autenticacion:Modo=Ninguna.");
const string EsquemaJwtODesarrollo = "JwtODesarrollo";

// En Lambda (AWS_LAMBDA_FUNCTION_NAME definida) el servidor es la función detrás del API Gateway (payload 2.0);
// fuera de Lambda no cambia nada y sigue Kestrel (plan D-002).
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

// Una línea por petición: método, ruta, código y duración; sin cabeceras ni cuerpos (spec RF-012).
builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields = HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath |
                            HttpLoggingFields.ResponseStatusCode | HttpLoggingFields.Duration;
    options.CombineLogs = true;
});

builder.Services.AddControllers();
builder.Services.AddOpenApi(options =>
{
    // En modo de desarrollo, Scalar muestra los headers que reemplazan al token de Cognito.
    if (modoDesarrollo)
        options.AddOperationTransformer(AutenticacionDesarrolloHandler.DocumentarHeadersAsync);
});
builder.Services.AddInfrastructure(builder.Configuration);

if (sinAutenticacion)
    builder.Services
        .AddAuthentication(AutenticacionAbiertaHandler.Esquema)
        .AddScheme<AuthenticationSchemeOptions, AutenticacionAbiertaHandler>(AutenticacionAbiertaHandler.Esquema, null);
else
    AgregarCognito(builder, issuer, appClientId, modoDesarrollo, EsquemaJwtODesarrollo);
builder.Services.AddAuthorization();

WebApplication app = builder.Build();

// En AWS la integración Lambda del gateway no reescribe rutas: llega /api/v1/auditoria/... y se sirve como
// /auditoria/... igual que detrás del Nginx local (plan D-007). UseRouting va después para ver la ruta ya recortada.
string? pathBase = app.Configuration["Gateway:PathBase"];
if (!string.IsNullOrWhiteSpace(pathBase))
    app.UsePathBase(pathBase);
app.UseRouting();
app.UseHttpLogging();

if (modoDesarrollo)
    app.Logger.LogWarning(
        "Modo de desarrollo sin Cognito: sin token, la identidad se toma de {HeaderUsuario} y {HeaderRol} (por defecto, admin).",
        AutenticacionDesarrolloHandler.HeaderUsuario, AutenticacionDesarrolloHandler.HeaderRol);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// Local: el gateway Nginx añade X-Urban-Gateway-Key. En AWS solo el gateway puede invocar la función y la integración
// Lambda no admite cabeceras fijas, así que se desactiva con Gateway:ExigirClave=false (plan D-007).
if (app.Configuration.GetValue("Gateway:ExigirClave", true))
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

static void AgregarCognito(
    WebApplicationBuilder builder, string? issuer, string? appClientId, bool modoDesarrollo, string esquemaJwtODesarrollo)
{
    AuthenticationBuilder autenticacion = builder.Services
        .AddAuthentication(modoDesarrollo ? esquemaJwtODesarrollo : JwtBearerDefaults.AuthenticationScheme)
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
            .AddPolicyScheme(esquemaJwtODesarrollo, null, options => options.ForwardDefaultSelector = context =>
                context.Request.Headers.Authorization.Count > 0
                    ? JwtBearerDefaults.AuthenticationScheme
                    : AutenticacionDesarrolloHandler.Esquema);
    }
}

public partial class Program { }
