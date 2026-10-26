using IAnalitica.Domain.Modulos;

namespace IAnalitica.Application.Semantica;

/// <summary>Ejecuta una métrica con la sesión del usuario (RLS) y la empresa de la sesión. Implementación en Infrastructure.</summary>
public interface IMotorMetricas
{
    Task<ResultadoMetrica> EjecutarAsync(ConsultaMetrica consulta, CancellationToken ct);
}

public sealed record MetaKpi(decimal Meta, decimal? Umbral, int PeriodoKey);

/// <summary>Metas por KPI de la empresa de la sesión (<c>cfg.KpiMeta</c>).</summary>
public interface IMetasKpi
{
    /// <summary>La meta vigente al periodo indicado (la más reciente que no lo supere); null si no hay.</summary>
    Task<MetaKpi?> ObtenerAsync(string kpiCodigo, int? periodoHasta, CancellationToken ct);
}

public enum Semaforo { SinMeta, Verde, Amarillo, Rojo }

public static class Semaforos
{
    /// <summary>
    /// Verde si se cumple la meta; Amarillo si no, pero se está dentro del umbral de alerta; Rojo si se pasa del umbral.
    /// Sin umbral solo hay verde o rojo. Las métricas neutras no tienen semáforo.
    /// </summary>
    public static Semaforo Calcular(decimal? valor, MetaKpi? meta, SentidoMejora sentido)
    {
        if (valor is not { } v || meta is null || sentido == SentidoMejora.Neutro) return Semaforo.SinMeta;
        var cumple = sentido == SentidoMejora.MayorEsMejor ? v >= meta.Meta : v <= meta.Meta;
        if (cumple) return Semaforo.Verde;
        if (meta.Umbral is not { } u) return Semaforo.Rojo;
        var dentro = sentido == SentidoMejora.MayorEsMejor ? v >= u : v <= u;
        return dentro ? Semaforo.Amarillo : Semaforo.Rojo;
    }
}

public sealed record SerieWidget(ResultadoMetrica Resultado, decimal? Anterior, MetaKpi? Meta, Semaforo Semaforo)
{
    /// <summary>Variación relativa frente al periodo anterior (solo KPI con ambos valores y anterior distinto de cero).</summary>
    public decimal? Variacion => Resultado.Total is { } a && Anterior is { } b && b != 0 ? (a - b) / Math.Abs(b) : null;
}

public sealed record DatosWidget(string Dashboard, string Widget, TipoWidget Tipo, string Titulo, IReadOnlyList<SerieWidget> Series, IReadOnlyList<string> FiltrosNoAplicados)
{
    public bool SinDatos => Series.All(s => s.Resultado.SinDatos);
}

public sealed record WidgetVisible(DefinicionWidget Definicion);

/// <summary>Dashboard visible para el usuario: sin los widgets que dependen de módulos no activos.</summary>
public sealed record DashboardVisible(DefinicionDashboard Definicion, IReadOnlyList<DefinicionWidget> Widgets, string NombreModulo);

public sealed record ResumenDashboard(string Codigo, string Titulo, string Modulo, string NombreModulo);
