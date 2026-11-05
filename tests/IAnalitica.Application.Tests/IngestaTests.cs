using IAnalitica.Application.Ingesta;
using IAnalitica.Ingesta.Contracts;

namespace IAnalitica.Application.Tests;

public class IngestaTests
{
    private static IniciarCargaCommand Cmd(string entidad, string modo, DateTime? desde = null, DateTime? hasta = null, DateTime? corte = null)
        => new(new SolicitudCarga { Entidad = entidad, Modo = modo, VentanaDesde = desde, VentanaHasta = hasta, Corte = corte }, null);

    private static string[] Codigos(IniciarCargaCommand c) => new IniciarCargaCommandValidator().Validate(c).Errors.Select(e => e.ErrorCode).ToArray();

    [Fact]
    public void Una_ventana_valida_no_tiene_errores()
        => Assert.Empty(Codigos(Cmd("asientos", Modos.Ventana, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31))));

    [Theory]
    [InlineData("recibos", "VENTANA", CodigosError.EntidadNoSoportada)]
    [InlineData("configuracion", "VENTANA", CodigosError.ContratoInvalido)]
    [InlineData("asientos", "COMPLETA", CodigosError.ContratoInvalido)]
    [InlineData("sedes", "VENTANA", CodigosError.ContratoInvalido)]
    [InlineData("terceros", "INICIAL", CodigosError.ContratoInvalido)]
    public void Entidad_o_modo_no_permitidos(string entidad, string modo, string codigo)
        => Assert.Contains(codigo, Codigos(Cmd(entidad, modo, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31))));

    [Fact]
    public void Ventana_e_inicial_exigen_fechas_en_orden()
    {
        Assert.Contains(CodigosError.ContratoInvalido, Codigos(Cmd("asientos", Modos.Ventana)));
        Assert.Contains(CodigosError.ContratoInvalido, Codigos(Cmd("asientos", Modos.Inicial, new DateTime(2026, 2, 1), new DateTime(2026, 1, 1))));
    }

    [Fact]
    public void Todo_catalogo_es_COMPLETA_salvo_terceros_que_tambien_admite_INCREMENTAL()
    {
        foreach (var e in CatalogoEntidades.Todas.Where(e => e.EsCatalogo))
            Assert.Contains(Modos.Completa, e.Modos);
        Assert.Contains(Modos.Incremental, CatalogoEntidades.Buscar("terceros")!.Modos);
        Assert.Equal(new[] { "debito", "credito" }, CatalogoEntidades.Buscar("asientos")!.ColumnasSumaControl);
        Assert.Equal(new[] { "valorNeto" }, CatalogoEntidades.Buscar("facturas")!.ColumnasSumaControl);
    }

    [Fact]
    public void Un_lote_se_valida_por_numero_y_tamano()
    {
        var v = new RecibirLoteCommandValidator();
        Assert.True(v.Validate(new RecibirLoteCommand(1, 1, 5000, "[]")).IsValid);
        Assert.Contains(v.Validate(new RecibirLoteCommand(1, 1, 5001, "[]")).Errors, e => e.ErrorCode == CodigosError.LoteGrande);
        Assert.False(v.Validate(new RecibirLoteCommand(1, 0, 10, "[]")).IsValid);
        Assert.False(v.Validate(new RecibirLoteCommand(1, 1, 0, "[]")).IsValid);
    }

    [Fact]
    public void La_privacidad_detecta_pacientes_con_nombre_y_devuelve_solo_su_codigo()
    {
        const string json = """
            [{"codigoOrigen":"A","esPaciente":true,"nombre":"María"},
             {"codigoOrigen":"B","esPaciente":true},
             {"codigoOrigen":"C","esPaciente":false,"nombre":"EPS"},
             {"codigoOrigen":"D","esPaciente":true,"nombre":"  "}]
            """;
        Assert.Equal(new[] { "A" }, ReglasPrivacidad.Revisar("terceros", json));
        Assert.Empty(ReglasPrivacidad.Revisar("sedes", json));   // solo aplica a terceros
    }
}
