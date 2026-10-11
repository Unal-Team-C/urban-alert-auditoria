using System.Text.Json;
using Application.Comun;
using Application.Reportes.Eventos;
using Domain.Reportes;

namespace UnitTests.Auditoria;

public sealed class CodigosEnumTests
{
    private static readonly JsonSerializerOptions OpcionesMensajeria =
        CodigosEnum.ConfigurarMensajeria(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Theory]
    [InlineData("\"REPORTADO\"", EstadoReporte.Reportado)]
    [InlineData("\"EN_INTERVENCION\"", EstadoReporte.EnIntervencion)]
    [InlineData("0", EstadoReporte.Reportado)]
    [InlineData("3", EstadoReporte.EnIntervencion)]
    public void Mensajeria_LeeElEstadoComoCodigoOComoNumeroAnterior(string estado, EstadoReporte esperado)
    {
        string json = $$$"""
            {"idEvento":"{{{Guid.NewGuid()}}}","reporte":{"id":"{{{Guid.NewGuid()}}}","estado":{{{estado}}},
             "idCoordenada":"{{{Guid.NewGuid()}}}","idUsuario":"{{{Guid.NewGuid()}}}","fecha":"2026-10-06T03:01:43Z"}}
            """;

        ReporteCreadoEvent? evento = JsonSerializer.Deserialize<ReporteCreadoEvent>(json, OpcionesMensajeria);

        Assert.Equal(esperado, evento!.Reporte.Estado);
    }

    [Theory]
    [InlineData(EstadoReporte.Reportado, "REPORTADO")]
    [InlineData(EstadoReporte.EnIntervencion, "EN_INTERVENCION")]
    public void ACodigo_UsaUpperSnakeCase(EstadoReporte estado, string esperado)
    {
        Assert.Equal(esperado, CodigosEnum.ACodigo(estado));
    }
}
