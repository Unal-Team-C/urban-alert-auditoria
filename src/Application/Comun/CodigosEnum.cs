using System.Text.Json;
using System.Text.Json.Serialization;

namespace Application.Comun;

// Convención de la plataforma para enums fuera del código: UPPER_SNAKE_CASE ("EN_INTERVENCION").
public static class CodigosEnum
{
    public static JsonNamingPolicy Politica { get; } = JsonNamingPolicy.SnakeCaseUpper;

    public static string ACodigo<TEnum>(TEnum valor) where TEnum : struct, Enum =>
        Politica.ConvertName(valor.ToString());

    // Lector tolerante para los mensajes de MassTransit: acepta el código ("REPORTADO")
    // y también el número que Reportes enviaba antes de adoptar la convención.
    public static JsonSerializerOptions ConfigurarMensajeria(JsonSerializerOptions opciones)
    {
        opciones.Converters.Insert(0, new JsonStringEnumConverter(Politica, allowIntegerValues: true));
        return opciones;
    }
}
