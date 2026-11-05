namespace IAnalitica.Domain.Modulos;

/// <summary>
/// Un módulo analítico (spec 14 §2): agrupa métricas y dashboards de un área de la salud. La activación por empresa vive en
/// <c>seg.EmpresaModulo</c>; el permiso de ver sus dashboards es <c>dash.&lt;modulo&gt;</c>.
/// </summary>
public interface IModuloAnalitico
{
    string Codigo { get; }                                   // "CARTERA"
    string Nombre { get; }
    void Registrar(IRegistroModulo registro);
}

public interface IRegistroModulo
{
    void Metrica(DefinicionMetrica metrica);
    void DashboardDeFabrica(DefinicionDashboard dashboard);
}

public enum SentidoMejora { MayorEsMejor, MenorEsMejor, Neutro }

public enum TipoWidget { Kpi, Linea, Barras, BarrasApiladas, Cascada, Tabla, MapaCalor, Embudo, Dona }

public static class Unidades
{
    public const string Pesos = "COP";
    public const string Porcentaje = "%";
    public const string Dias = "dias";
    public const string Unidades_ = "unidades";
    public const string Veces = "veces";
    public static readonly IReadOnlySet<string> Todas = new HashSet<string> { Pesos, Porcentaje, Dias, Unidades_, Veces };
}

/// <summary>
/// Métrica con nombre. Si <see cref="Calculadora"/> es null es declarativa: el motor arma
/// <c>SELECT dims, expr FROM fuente WHERE filtros GROUP BY dims</c> con SQL parametrizado, SOLO con la fuente, las columnas y la
/// expresión registradas aquí (lista blanca). Texto del usuario nunca llega al SQL.
/// </summary>
public sealed record DefinicionMetrica(
    string Codigo,                                           // "cartera.saldo_vencido_mas_90"
    string Nombre,
    string Unidad,                                           // COP | % | dias | unidades | veces
    SentidoMejora Sentido,
    string Modulo,
    string Fuente,                                           // vista rpt.* (lista blanca)
    string Expresion,                                        // catálogo cerrado: SUM(col), COUNT(*), AVG(col), SUM(a)/NULLIF(SUM(b),0)
    IReadOnlyDictionary<string, string> Dimensiones,         // "pagador" -> "PagadorId", "periodo" -> "PeriodoKey"
    string? FiltroFijo = null,                               // «col IN ('a','b')» con el mismo catálogo cerrado
    Type? Calculadora = null,                                // implementa IMetricaCalculada
    string? KpiMeta = null,                                  // código en cfg.KpiMeta para semáforo
    bool Acumulado = false);                                 // saldo a una fecha: suma desde el origen hasta PeriodoHasta e ignora PeriodoDesde

public sealed record DefinicionDashboard(
    string Codigo, string Titulo, string Modulo, string Permiso, int Version,
    IReadOnlyList<DefinicionWidget> Widgets);

public sealed record DefinicionWidget(
    string Codigo, string Titulo, TipoWidget Tipo,
    IReadOnlyList<string> Metricas,
    IReadOnlyList<string> Dimensiones,
    int Fila, int Columna, int Ancho, int Alto,              // grilla de 12 columnas
    string? DrillDashboard = null,
    IReadOnlyList<string>? ModulosAdicionales = null);       // el widget se oculta si falta alguno
