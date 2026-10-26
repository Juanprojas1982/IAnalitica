using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using IAnalitica.Domain.Modulos;

namespace IAnalitica.Application.Semantica;

public static class CodigosSemantica
{
    public const string DashboardNoEncontrado = "DASHBOARD_NO_ENCONTRADO";
    public const string ModuloInactivo = "MODULO_INACTIVO";
    public const string WidgetNoEncontrado = "WIDGET_NO_ENCONTRADO";
}

/// <summary>Reglas de visibilidad comunes: el dashboard existe, el usuario tiene su permiso y los módulos están activos.</summary>
internal sealed class VisibilidadDashboards(CatalogoAnalitico catalogo, ISesionUsuario sesion, IModulosEmpresa modulos)
{
    public async Task<Result<DashboardVisible>> ResolverAsync(string codigo, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId || sesion.UsuarioId is null)
            return Result<DashboardVisible>.Falla(Error.NoAutorizado("Debe iniciar sesión con una empresa."));
        if (!catalogo.Dashboards.TryGetValue(codigo, out var d))
            return Result<DashboardVisible>.Falla(Error.NoEncontrado("El dashboard no existe."));
        if (!sesion.TienePermiso(d.Permiso)) return Result<DashboardVisible>.Falla(Error.NoAutorizado());
        if (!await modulos.EstaActivoAsync(empresaId, d.Modulo, ct).ConfigureAwait(false))
            return Result<DashboardVisible>.Falla(Error.Conflicto(CodigosSemantica.ModuloInactivo, $"El módulo {catalogo.Modulos[d.Modulo].Nombre} no está activo para su empresa."));

        var widgets = new List<DefinicionWidget>();
        foreach (var w in d.Widgets)
        {
            var activo = true;
            foreach (var m in w.ModulosAdicionales ?? [])
                if (!await modulos.EstaActivoAsync(empresaId, m, ct).ConfigureAwait(false)) { activo = false; break; }
            if (activo) widgets.Add(w);
        }
        return new DashboardVisible(d, widgets, catalogo.Modulos[d.Modulo].Nombre);
    }
}

// ---------- Menú: dashboards que el usuario puede ver ----------
public sealed record ListarDashboardsQuery : IRequest<Result<IReadOnlyList<ResumenDashboard>>>;

public sealed class ListarDashboardsHandler(CatalogoAnalitico catalogo, ISesionUsuario sesion, IModulosEmpresa modulos)
    : IRequestHandler<ListarDashboardsQuery, Result<IReadOnlyList<ResumenDashboard>>>
{
    public async Task<Result<IReadOnlyList<ResumenDashboard>>> Handle(ListarDashboardsQuery request, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId || sesion.UsuarioId is null)
            return Result<IReadOnlyList<ResumenDashboard>>.Ok(Array.Empty<ResumenDashboard>());

        var lista = new List<ResumenDashboard>();
        foreach (var grupo in catalogo.Dashboards.Values.Where(d => sesion.TienePermiso(d.Permiso)).GroupBy(d => d.Modulo))
        {
            if (!await modulos.EstaActivoAsync(empresaId, grupo.Key, ct).ConfigureAwait(false)) continue;
            var nombre = catalogo.Modulos[grupo.Key].Nombre;
            lista.AddRange(grupo.OrderBy(d => d.Codigo).Select(d => new ResumenDashboard(d.Codigo, d.Titulo, d.Modulo, nombre)));
        }
        return Result<IReadOnlyList<ResumenDashboard>>.Ok(lista);
    }
}

// ---------- Definición de un dashboard ----------
public sealed record ObtenerDashboardQuery(string Codigo) : IRequest<Result<DashboardVisible>>;

public sealed class ObtenerDashboardHandler(CatalogoAnalitico catalogo, ISesionUsuario sesion, IModulosEmpresa modulos)
    : IRequestHandler<ObtenerDashboardQuery, Result<DashboardVisible>>
{
    public Task<Result<DashboardVisible>> Handle(ObtenerDashboardQuery request, CancellationToken ct)
        => new VisibilidadDashboards(catalogo, sesion, modulos).ResolverAsync(request.Codigo, ct);
}

// ---------- Datos de un widget ----------
public sealed record ObtenerDatosWidgetQuery(string Dashboard, string Widget, FiltrosDashboard Filtros) : IRequest<Result<DatosWidget>>;

/// <summary>
/// Autoriza (permiso y módulos) ANTES de consultar y de usar la caché: la caché es por empresa + versión de carga, así que lo que
/// entra en ella ya pasó por la autorización del usuario que la llenó, pero otro usuario sin permiso no debe poder leerla.
/// </summary>
public sealed class ObtenerDatosWidgetHandler(CatalogoAnalitico catalogo, ISesionUsuario sesion, IModulosEmpresa modulos, IMotorMetricas motor,
    IMetasKpi metas, IVersionCarga version, ICacheConsultas cache) : IRequestHandler<ObtenerDatosWidgetQuery, Result<DatosWidget>>
{
    private static readonly TimeSpan DuracionCache = TimeSpan.FromMinutes(10);

    public async Task<Result<DatosWidget>> Handle(ObtenerDatosWidgetQuery request, CancellationToken ct)
    {
        var visible = await new VisibilidadDashboards(catalogo, sesion, modulos).ResolverAsync(request.Dashboard, ct).ConfigureAwait(false);
        if (!visible.EsExitoso) return Result<DatosWidget>.Falla(visible.Errores);
        var widget = visible.Valor.Widgets.FirstOrDefault(w => w.Codigo == request.Widget);
        if (widget is null) return Result<DatosWidget>.Falla(Error.NoEncontrado("El widget no existe o no está disponible."));

        var empresaId = sesion.EmpresaId!.Value;
        var v = await version.ObtenerAsync(empresaId, ct).ConfigureAwait(false);
        var clave = $"widget|{empresaId}|{v}|{request.Dashboard}|{request.Widget}|{request.Filtros.Clave()}";
        var datos = await cache.ObtenerOCrearAsync(clave, DuracionCache, () => CalcularAsync(request, widget, ct), ct).ConfigureAwait(false);
        return datos;
    }

    private async Task<DatosWidget> CalcularAsync(ObtenerDatosWidgetQuery request, DefinicionWidget widget, CancellationToken ct)
    {
        var series = new List<SerieWidget>();
        var noAplicados = new SortedSet<string>();
        foreach (var codigo in widget.Metricas)
        {
            var metrica = catalogo.Metricas[codigo];
            var resultado = await motor.EjecutarAsync(new ConsultaMetrica(codigo, widget.Dimensiones, request.Filtros, Top: LimiteFilas(widget)), ct).ConfigureAwait(false);
            foreach (var f in resultado.FiltrosNoAplicados) noAplicados.Add(f);

            decimal? anterior = null;
            MetaKpi? meta = null;
            var semaforo = Semaforo.SinMeta;
            if (widget.Tipo == TipoWidget.Kpi)
            {
                anterior = await PeriodoAnteriorAsync(metrica, request.Filtros, ct).ConfigureAwait(false);
                if (metrica.KpiMeta is { } kpi)
                {
                    meta = await metas.ObtenerAsync(kpi, request.Filtros.PeriodoHasta, ct).ConfigureAwait(false);
                    semaforo = Semaforos.Calcular(resultado.Total, meta, metrica.Sentido);
                }
            }
            series.Add(new SerieWidget(resultado, anterior, meta, semaforo));
        }
        return new DatosWidget(request.Dashboard, request.Widget, widget.Tipo, widget.Titulo, series, noAplicados.ToList());
    }

    /// <summary>Tablas y barras muestran como máximo 50 filas (respuestas acotadas, spec 20 §4.4); las líneas y las demás, todas.</summary>
    private static int? LimiteFilas(DefinicionWidget w) => w.Tipo is TipoWidget.Barras or TipoWidget.Tabla or TipoWidget.Dona ? 50 : null;

    /// <summary>Mismo valor para el periodo inmediatamente anterior de igual longitud (solo si hay rango de periodos y la métrica es por periodo).</summary>
    private async Task<decimal?> PeriodoAnteriorAsync(DefinicionMetrica metrica, FiltrosDashboard f, CancellationToken ct)
    {
        if (!metrica.Dimensiones.ContainsKey(Dimensiones.Periodo) || metrica.Acumulado || f.PeriodoDesde is not { } desde || f.PeriodoHasta is not { } hasta) return null;
        var meses = (hasta / 100 * 12 + hasta % 100) - (desde / 100 * 12 + desde % 100) + 1;
        static int Restar(int periodo, int meses) { var total = periodo / 100 * 12 + (periodo % 100 - 1) - meses; return total / 12 * 100 + total % 12 + 1; }
        var anterior = f with { PeriodoDesde = Restar(desde, meses), PeriodoHasta = Restar(hasta, meses) };
        var r = await motor.EjecutarAsync(new ConsultaMetrica(metrica.Codigo, Array.Empty<string>(), anterior), ct).ConfigureAwait(false);
        return r.Total;
    }
}

// ---------- Opciones de la barra de filtros ----------
public sealed record OpcionFiltro(int Id, string Nombre);

public sealed record OpcionesFiltros(IReadOnlyList<OpcionFiltro> Sedes, IReadOnlyList<OpcionFiltro> Pagadores, IReadOnlyList<OpcionFiltro> CentrosCosto);

/// <summary>Valores de los filtros globales de la empresa de la sesión (con RLS). Acotado a 500 por lista.</summary>
public interface IOpcionesFiltros
{
    Task<OpcionesFiltros> ObtenerAsync(CancellationToken ct);
}

public sealed record ListarOpcionesFiltrosQuery : IRequest<Result<OpcionesFiltros>>;

public sealed class ListarOpcionesFiltrosHandler(ISesionUsuario sesion, IOpcionesFiltros opciones) : IRequestHandler<ListarOpcionesFiltrosQuery, Result<OpcionesFiltros>>
{
    public async Task<Result<OpcionesFiltros>> Handle(ListarOpcionesFiltrosQuery request, CancellationToken ct)
        => sesion.EmpresaId is null || sesion.UsuarioId is null
            ? Result<OpcionesFiltros>.Falla(Error.NoAutorizado("Debe iniciar sesión con una empresa."))
            : await opciones.ObtenerAsync(ct).ConfigureAwait(false);
}
