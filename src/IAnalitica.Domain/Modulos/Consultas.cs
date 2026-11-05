namespace IAnalitica.Domain.Modulos;

/// <summary>Filtros globales de un dashboard (spec 14 §5). Los valores salen de la barra de filtros; el motor los pasa como parámetros.</summary>
public sealed record FiltrosDashboard(
    int? PeriodoDesde = null,                                // yyyymm
    int? PeriodoHasta = null,                                // yyyymm
    IReadOnlyList<int>? Sedes = null,
    IReadOnlyList<int>? Pagadores = null,
    IReadOnlyList<int>? CentrosCosto = null,
    int? Corte = null)                                       // yyyymmdd (cartera, balance): fecha de corte
{
    public static readonly FiltrosDashboard Ninguno = new();

    /// <summary>Clave estable para la caché (mismos filtros → mismo texto).</summary>
    public string Clave() => string.Join('|',
        PeriodoDesde, PeriodoHasta, Lista(Sedes), Lista(Pagadores), Lista(CentrosCosto), Corte);

    private static string Lista(IReadOnlyList<int>? v) => v is null ? "" : string.Join(',', v.OrderBy(x => x));
}

/// <summary>
/// Lo que se pide al motor: una métrica, agrupada por las dimensiones indicadas (nombres de <see cref="DefinicionMetrica.Dimensiones"/>),
/// con los filtros globales y, opcionalmente, un tope de filas ordenadas por valor.
/// </summary>
public sealed record ConsultaMetrica(
    string Metrica, IReadOnlyList<string> AgruparPor, FiltrosDashboard Filtros, int? Top = null, bool Descendente = true);

public sealed record FilaMetrica(IReadOnlyDictionary<string, DimensionValor> Dimensiones, decimal? Valor);

/// <summary>Valor de una dimensión: la llave (para drill y filtros) y la etiqueta legible.</summary>
public sealed record DimensionValor(string Clave, string Etiqueta);

public sealed record ResultadoMetrica(
    string Metrica, string Nombre, string Unidad, SentidoMejora Sentido,
    IReadOnlyList<string> Dimensiones, IReadOnlyList<FilaMetrica> Filas,
    IReadOnlyList<string> FiltrosNoAplicados)
{
    /// <summary>Valor del total (sin agrupar): la primera fila.</summary>
    public decimal? Total => Filas.Count == 0 ? null : Filas[0].Valor;
    public bool SinDatos => Filas.Count == 0 || Filas.All(f => f.Valor is null);
}

/// <summary>Métrica con lógica propia (p. ej. DSO por EPS). Corre con la conexión del usuario, así que la RLS aplica.</summary>
public interface IMetricaCalculada
{
    Task<ResultadoMetrica> CalcularAsync(DefinicionMetrica definicion, ConsultaMetrica consulta, IContextoCalculo contexto, CancellationToken ct);
}

/// <summary>Acceso que el motor le da a una calculadora: consultas parametrizadas con la sesión del usuario.</summary>
public interface IContextoCalculo
{
    int EmpresaId { get; }
    Task<IReadOnlyList<T>> ConsultarAsync<T>(string sql, object parametros, CancellationToken ct);
}
