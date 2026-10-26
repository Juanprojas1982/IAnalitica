using IAnalitica.Application.Asistente;
using IAnalitica.Application.Costos;

namespace IAnalitica.Application.Tests;

public class AsistenteTests
{
    [Theory]
    [InlineData("Los ingresos fueron $ 3.000.", "Los ingresos fueron $ 3.000.")]
    [InlineData("Hola <b>mundo</b>", "Hola mundo")]
    [InlineData("<script>alert(1)</script>Dato", "alert(1)Dato")]
    [InlineData("Mire [aquí](https://malo.example/x) y [esto](//malo.example)", "Mire aquí y esto")]
    [InlineData("![img](https://malo.example/a.png)Texto", "Texto")]
    [InlineData("Visite https://malo.example/x ya", "Visite [enlace omitido] ya")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void La_salida_del_modelo_no_lleva_html_ni_enlaces_externos(string? entrada, string esperado)
        => Assert.Equal(esperado, SanitizadorSalida.Limpiar(entrada));

    [Fact]
    public void Un_alias_mas_corto_no_pisa_a_uno_mas_largo()
    {
        var s = new Seudonimizador();
        s.Alias("tercero", "1", "Ana");
        s.Alias("tercero", "12", "Beatriz");
        Assert.Equal("Beatriz y Ana", s.Rehidratar("Tercero #12 y Tercero #1"));
    }

    [Theory]
    [InlineData(202601, true)]
    [InlineData(202613, false)]
    [InlineData(202600, false)]
    [InlineData(199912, false)]
    public void Los_periodos_son_aaaamm_validos(int periodo, bool valido) => Assert.Equal(valido, PeriodosCosto.EsValido(periodo));

    [Fact]
    public void Los_periodos_entre_dos_fechas_cruzan_el_cambio_de_anio()
        => Assert.Equal(new[] { 202511, 202512, 202601, 202602 }, PeriodosCosto.Entre(202511, 202602));

    [Fact]
    public void Las_reglas_de_recalculo_limitan_el_rango()
    {
        var v = new RecalcularCostosValidator();
        Assert.True(v.Validate(new RecalcularCostosCommand(202601, 202712)).IsValid);      // justo 24 meses
        Assert.False(v.Validate(new RecalcularCostosCommand(202601, 202801)).IsValid);     // 37 meses
        Assert.False(v.Validate(new RecalcularCostosCommand(202606, 202601)).IsValid);     // invertido
        Assert.False(v.Validate(new RecalcularCostosCommand(202613, 202614)).IsValid);
    }
}
