using Microsoft.Extensions.Configuration;

namespace Infrastructure;

// Autenticacion:Modo. Cognito es el comportamiento local y el valor por defecto; Ninguna es el de la nube mientras el
// gateway no autentica clientes (spec RF-007, plan D-006).
public static class ModoAutenticacion
{
    public const string Cognito = "Cognito";
    public const string Ninguna = "Ninguna";

    public static string Leer(IConfiguration configuration)
    {
        string? modo = configuration["Autenticacion:Modo"];
        if (string.IsNullOrWhiteSpace(modo) || string.Equals(modo, Cognito, StringComparison.OrdinalIgnoreCase))
            return Cognito;
        if (string.Equals(modo, Ninguna, StringComparison.OrdinalIgnoreCase))
            return Ninguna;
        throw new InvalidOperationException($"'Autenticacion:Modo' no admite '{modo}' ({Cognito} | {Ninguna}).");
    }
}
