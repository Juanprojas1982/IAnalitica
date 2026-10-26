using IAnalitica.Domain.Modulos;

namespace IAnalitica.Domain.Tests;

public class CatalogoAnaliticoTests
{
    private sealed class ModuloPrueba(string codigo, Action<IRegistroModulo> registrar) : IModuloAnalitico
    {
        public string Codigo => codigo;
        public string Nombre => codigo;
        public void Registrar(IRegistroModulo registro) => registrar(registro);
    }

    private static readonly IReadOnlyDictionary<string, string> Dims = new Dictionary<string, string> { ["periodo"] = "PeriodoKey", ["pagador"] = "PagadorId" };

    private static DefinicionMetrica Metrica(string codigo = "cartera.saldo", string expr = "SUM(Saldo)", string? filtro = null, string fuente = "rpt.vw_AntiguedadCartera", string unidad = "COP")
        => new(codigo, "Saldo", unidad, SentidoMejora.MenorEsMejor, "CARTERA", fuente, expr, Dims, filtro);

    private static DefinicionDashboard Dashboard(params DefinicionWidget[] widgets)
        => new("cartera.antiguedad", "Antigüedad", "CARTERA", "dash.cartera", 1, widgets);

    private static DefinicionWidget Kpi(string metrica = "cartera.saldo", int col = 0, int ancho = 3)
        => new("k", "Saldo", TipoWidget.Kpi, new[] { metrica }, Array.Empty<string>(), 0, col, ancho, 2);

    private static CatalogoAnalitico Construir(Action<IRegistroModulo> registrar) => CatalogoAnalitico.Construir(new[] { new ModuloPrueba("CARTERA", registrar) });

    private static string[] Errores(Action<IRegistroModulo> registrar)
        => Assert.Throws<InvalidOperationException>(() => Construir(registrar)).Message.Split('\n').Where(l => l.TrimStart().StartsWith('-')).Select(l => l.Trim()).ToArray();

    [Fact]
    public void Un_registro_valido_se_construye_y_expone_metricas_y_dashboards()
    {
        var c = Construir(r => { r.Metrica(Metrica()); r.DashboardDeFabrica(Dashboard(Kpi())); });
        Assert.Contains("cartera.saldo", c.Metricas.Keys);
        Assert.Contains("cartera.antiguedad", c.Dashboards.Keys);
    }

    [Theory]
    [InlineData("SUM(Saldo)")]
    [InlineData("AVG(Dias)")]
    [InlineData("MAX(Dias)")]
    [InlineData("COUNT(*)")]
    [InlineData("COUNT(DISTINCT TerceroId)")]
    [InlineData("SUM(Saldo)/NULLIF(SUM(ValorOriginal),0)")]
    [InlineData("100.0*SUM(Saldo)/NULLIF(SUM(ValorOriginal),0)")]
    [InlineData("90.0*SUM(Saldo)/NULLIF(SUM(Facturado90),0)")]
    public void Expresiones_del_catalogo_cerrado_son_validas(string expr)
        => Construir(r => r.Metrica(Metrica(expr: expr)));

    [Theory]
    [InlineData("SUM(Saldo); DROP TABLE seg.Usuario")]
    [InlineData("SUM(Saldo) -- x")]
    [InlineData("(SELECT 1)")]
    [InlineData("SUM(CASE WHEN a=1 THEN b END)")]
    [InlineData("SUM(a.Saldo)")]
    [InlineData("SUM(Saldo)+1")]
    [InlineData("")]
    public void Expresiones_fuera_del_catalogo_se_rechazan(string expr)
        => Assert.Contains(Errores(r => r.Metrica(Metrica(expr: expr))), e => e.Contains("catálogo cerrado"));

    [Theory]
    [InlineData("RangoEdad IN ('91-120','121-150','>360')")]
    [InlineData("ClasePuc = '4'")]
    [InlineData("ClasePuc IN ('5','6','7')")]
    [InlineData("TipoCartera = 'CXC' AND Dias > 90")]
    [InlineData("TipoCartera = 'CXC' AND RangoEdad IN ('91-120','>360')")]
    public void Filtros_fijos_con_forma_permitida(string filtro)
        => Construir(r => r.Metrica(Metrica(filtro: filtro)));

    [Theory]
    [InlineData("1=1; DROP TABLE x")]
    [InlineData("RangoEdad IN (SELECT 1)")]
    [InlineData("ClasePuc = '4' OR 1=1")]
    [InlineData("ClasePuc = '4' --")]
    [InlineData("x = 'a'' OR ''b'")]
    public void Filtros_fijos_peligrosos_se_rechazan(string filtro)
        => Assert.Contains(Errores(r => r.Metrica(Metrica(filtro: filtro))), e => e.Contains("filtro fijo"));

    [Theory]
    [InlineData("dbo.Usuarios")]
    [InlineData("seg.Usuario")]
    [InlineData("rpt.vw_X; DROP")]
    [InlineData("fact.AsientoLinea")]
    public void Solo_vistas_rpt_vw_son_fuentes_validas(string fuente)
        => Assert.Contains(Errores(r => r.Metrica(Metrica(fuente: fuente))), e => e.Contains("vista rpt.vw_"));

    [Fact]
    public void Dimension_desconocida_o_columna_insegura_se_rechazan()
    {
        var mala = Metrica() with { Dimensiones = new Dictionary<string, string> { ["inventada"] = "X", ["pagador"] = "PagadorId; --" } };
        var errores = Errores(r => r.Metrica(mala));
        Assert.Contains(errores, e => e.Contains("dimensión desconocida"));
        Assert.Contains(errores, e => e.Contains("columna de dimensión no válida"));
    }

    [Fact]
    public void Codigo_modulo_y_unidad_deben_ser_coherentes()
    {
        Assert.Contains(Errores(r => r.Metrica(Metrica(codigo: "otro.saldo"))), e => e.Contains("debe empezar por"));
        Assert.Contains(Errores(r => r.Metrica(Metrica(unidad: "USD"))), e => e.Contains("unidad no válida"));
        Assert.Contains(Errores(r => { r.Metrica(Metrica()); r.Metrica(Metrica()); }), e => e.Contains("repetida"));
    }

    [Fact]
    public void Una_calculadora_debe_implementar_IMetricaCalculada()
        => Assert.Contains(Errores(r => r.Metrica(Metrica() with { Calculadora = typeof(string) })), e => e.Contains("IMetricaCalculada"));

    [Fact]
    public void Dashboard_con_permiso_widgets_o_grilla_invalidos_se_rechaza()
    {
        var d = new DefinicionDashboard("cartera.x", "X", "CARTERA", "dash.financiero", 1, new[]
        {
            Kpi("cartera.inexistente"),
            Kpi(col: 10, ancho: 4),                                                         // se sale de la grilla
            new DefinicionWidget("b", "B", TipoWidget.Barras, new[] { "cartera.saldo" }, Array.Empty<string>(), 0, 0, 6, 2),   // Barras exige 1 dimensión
            new DefinicionWidget("c", "C", TipoWidget.Kpi, new[] { "cartera.saldo" }, new[] { "sede" }, 0, 0, 3, 2),             // la métrica no tiene «sede»
            new DefinicionWidget("d", "D", TipoWidget.Kpi, new[] { "cartera.saldo" }, Array.Empty<string>(), 0, 0, 3, 2, DrillDashboard: "no.existe"),
        });
        var errores = Errores(r => { r.Metrica(Metrica()); r.DashboardDeFabrica(d); });
        Assert.Contains(errores, e => e.Contains("el permiso debe ser «dash.cartera»"));
        Assert.Contains(errores, e => e.Contains("métrica inexistente"));
        Assert.Contains(errores, e => e.Contains("grilla de 12 columnas"));
        Assert.Contains(errores, e => e.Contains("un widget Barras lleva entre 1 y 1 dimensiones"));
        Assert.Contains(errores, e => e.Contains("no tiene la dimensión «sede»"));
        Assert.Contains(errores, e => e.Contains("drill apunta a un dashboard inexistente"));
    }

    [Fact]
    public void Un_dashboard_sin_widgets_o_con_widgets_repetidos_se_rechaza()
    {
        Assert.Contains(Errores(r => { r.Metrica(Metrica()); r.DashboardDeFabrica(Dashboard()); }), e => e.Contains("no tiene widgets"));
        Assert.Contains(Errores(r => { r.Metrica(Metrica()); r.DashboardDeFabrica(Dashboard(Kpi(), Kpi(col: 4))); }), e => e.Contains("repetidos"));
    }

    [Fact]
    public void El_modulo_que_registra_debe_ser_el_de_la_metrica()
    {
        var otra = Metrica() with { Modulo = "FINANCIERO", Codigo = "financiero.x" };
        Assert.Contains(Errores(r => r.Metrica(otra)), e => e.Contains("declara el módulo FINANCIERO"));
    }

    [Fact]
    public void Los_filtros_generan_una_clave_estable_e_independiente_del_orden()
    {
        var a = new FiltrosDashboard(202601, 202603, Sedes: new[] { 2, 1 }, Pagadores: new[] { 9 });
        var b = new FiltrosDashboard(202601, 202603, Sedes: new[] { 1, 2 }, Pagadores: new[] { 9 });
        Assert.Equal(a.Clave(), b.Clave());
        Assert.NotEqual(a.Clave(), (a with { Corte = 20260131 }).Clave());
    }
}
