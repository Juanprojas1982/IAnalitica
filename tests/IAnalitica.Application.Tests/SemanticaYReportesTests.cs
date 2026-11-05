using IAnalitica.Application.Reportes;
using IAnalitica.Application.Semantica;
using IAnalitica.Domain.Modulos;

namespace IAnalitica.Application.Tests;

public class SemanticaYReportesTests
{
    private static readonly MetaKpi Meta = new(80m, 60m, 202601);

    [Theory]
    [InlineData(100, SentidoMejora.MayorEsMejor, Semaforo.Verde)]
    [InlineData(80, SentidoMejora.MayorEsMejor, Semaforo.Verde)]
    [InlineData(70, SentidoMejora.MayorEsMejor, Semaforo.Amarillo)]
    [InlineData(60, SentidoMejora.MayorEsMejor, Semaforo.Amarillo)]
    [InlineData(59.9, SentidoMejora.MayorEsMejor, Semaforo.Rojo)]
    [InlineData(80, SentidoMejora.MenorEsMejor, Semaforo.Verde)]
    [InlineData(70, SentidoMejora.MenorEsMejor, Semaforo.Verde)]
    [InlineData(81, SentidoMejora.MenorEsMejor, Semaforo.Amarillo)]
    [InlineData(101, SentidoMejora.MenorEsMejor, Semaforo.Rojo)]
    public void El_semaforo_sigue_el_sentido_de_mejora_la_meta_y_el_umbral(double valor, SentidoMejora sentido, Semaforo esperado)
        => Assert.Equal(esperado, Semaforos.Calcular((decimal)valor, Meta with { Umbral = sentido == SentidoMejora.MenorEsMejor ? 100m : 60m }, sentido));

    [Fact]
    public void Sin_umbral_solo_hay_verde_o_rojo_y_lo_neutro_o_sin_meta_no_tiene_semaforo()
    {
        var sinUmbral = new MetaKpi(80m, null, 202601);
        Assert.Equal(Semaforo.Rojo, Semaforos.Calcular(79m, sinUmbral, SentidoMejora.MayorEsMejor));
        Assert.Equal(Semaforo.SinMeta, Semaforos.Calcular(79m, Meta, SentidoMejora.Neutro));
        Assert.Equal(Semaforo.SinMeta, Semaforos.Calcular(79m, null, SentidoMejora.MayorEsMejor));
        Assert.Equal(Semaforo.SinMeta, Semaforos.Calcular(null, Meta, SentidoMejora.MayorEsMejor));
    }

    [Fact]
    public void La_variacion_solo_existe_con_anterior_distinto_de_cero()
    {
        ResultadoMetrica R(decimal? v) => new("m", "M", "COP", SentidoMejora.Neutro, Array.Empty<string>(), new[] { new FilaMetrica(new Dictionary<string, DimensionValor>(), v) }, Array.Empty<string>());
        Assert.Equal(0.5m, new SerieWidget(R(150m), 100m, null, Semaforo.SinMeta).Variacion);
        Assert.Equal(-0.5m, new SerieWidget(R(50m), 100m, null, Semaforo.SinMeta).Variacion);
        Assert.Null(new SerieWidget(R(50m), 0m, null, Semaforo.SinMeta).Variacion);
        Assert.Null(new SerieWidget(R(50m), null, null, Semaforo.SinMeta).Variacion);
    }

    [Fact]
    public void Los_parametros_de_un_reporte_viajan_en_JSON_y_el_hash_depende_de_quien_lo_pide()
    {
        var p = new ParametrosReporte("cartera.dso", new FiltrosDashboard(202601, 202603, Sedes: new[] { 1 }, Pagadores: new[] { 4, 5 }, Corte: 20260301));
        var vuelta = ParametrosReporte.DeJson(p.AJson())!;
        Assert.Equal("cartera.dso", vuelta.Dashboard);
        Assert.Equal(202601, vuelta.Filtros.PeriodoDesde);
        Assert.Equal(new[] { 4, 5 }, vuelta.Filtros.Pagadores);
        Assert.Equal(20260301, vuelta.Filtros.Corte);
        Assert.Null(vuelta.Filtros.CentrosCosto);

        Assert.Equal(p.Hash(7, "PDF"), vuelta.Hash(7, "PDF"));
        Assert.NotEqual(p.Hash(7, "PDF"), p.Hash(8, "PDF"));       // otro usuario: no se reutiliza
        Assert.NotEqual(p.Hash(7, "PDF"), p.Hash(7, "XLSX"));      // otro formato
        Assert.Null(ParametrosReporte.DeJson("{\"filtros\":{}}"));
    }

    [Fact]
    public void Las_secciones_pivotan_dimensiones_y_metricas_y_avisan_del_recorte()
    {
        DimensionValor D(string clave, string etiqueta) => new(clave, etiqueta);
        FilaMetrica F(string cat, decimal v) => new(new Dictionary<string, DimensionValor> { ["pagador"] = D(cat, "EPS " + cat) }, v);
        var a = new ResultadoMetrica("m.a", "Facturado", "COP", SentidoMejora.Neutro, new[] { "pagador" }, new[] { F("1", 10), F("2", 30), F("3", 20) }, Array.Empty<string>());
        var b = new ResultadoMetrica("m.b", "Facturas", "unidades", SentidoMejora.Neutro, new[] { "pagador" }, new[] { F("2", 3) }, new[] { "sede" });
        var datos = new DatosWidget("d", "w", TipoWidget.Tabla, "Por pagador",
            new[] { new SerieWidget(a, null, null, Semaforo.SinMeta), new SerieWidget(b, null, null, Semaforo.SinMeta) }, new[] { "sede" });

        var s = ConstructorSecciones.Desde(datos, maxFilas: 2);

        Assert.Equal(new[] { "Pagador", "Facturado", "Facturas" }, s.Columnas.Select(c => c.Nombre));
        Assert.Equal(2, s.Filas.Count);
        Assert.Equal(new object?[] { "EPS 2", 30m, 3m }, s.Filas[0].ToArray());          // mayor valor primero
        Assert.Equal(new object?[] { "EPS 3", 20m, null }, s.Filas[1].ToArray());         // sin dato en la segunda métrica
        Assert.Contains("primeras 2 de 3", s.Nota);
        Assert.Contains("filtro de sede", s.Nota);
    }
}
