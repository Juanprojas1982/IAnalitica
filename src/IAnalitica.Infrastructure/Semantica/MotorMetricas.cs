using System.Globalization;
using System.Text;
using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Semantica;
using IAnalitica.Domain.Modulos;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Infrastructure.Semantica;

/// <summary>
/// Motor de métricas (spec 14 §4). Arma <c>SELECT dims, expr FROM fuente WHERE filtros GROUP BY dims</c> SOLO con lo registrado por los
/// módulos (<see cref="CatalogoAnalitico"/> valida fuente, columnas, expresión y filtro fijo al arrancar). Los valores de los filtros
/// viajan como parámetros; ningún texto del usuario se concatena. Corre con la conexión del usuario, así que la RLS aplica, y
/// filtra además por la empresa de la sesión.
/// </summary>
internal sealed class MotorMetricas(CatalogoAnalitico catalogo, ISesionUsuario sesion, IFabricaConexiones conexiones, IServiceProvider servicios) : IMotorMetricas
{
    /// <summary>Tope duro de filas por consulta agrupada (spec 20 §4.4): ni una exportación puede traer más.</summary>
    internal const int TopeDuro = 100_000;

    public async Task<ResultadoMetrica> EjecutarAsync(ConsultaMetrica consulta, CancellationToken ct)
    {
        var empresaId = sesion.EmpresaId ?? throw new InvalidOperationException("No hay empresa en la sesión.");
        if (!catalogo.Metricas.TryGetValue(consulta.Metrica, out var metrica))
            throw new ArgumentException($"Métrica desconocida: {consulta.Metrica}.", nameof(consulta));
        foreach (var dim in consulta.AgruparPor)
            if (!metrica.Dimensiones.ContainsKey(dim))
                throw new ArgumentException($"La métrica {metrica.Codigo} no tiene la dimensión «{dim}».", nameof(consulta));

        if (metrica.Calculadora is not null)
        {
            var calculadora = (IMetricaCalculada)ActivatorUtilities.CreateInstance(servicios, metrica.Calculadora);
            return await calculadora.CalcularAsync(metrica, consulta, new ContextoCalculo(empresaId, conexiones), ct).ConfigureAwait(false);
        }

        var (sql, parametros, noAplicados) = ArmarSql(metrica, consulta, empresaId);
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        var filas = (await cn.QueryAsync(new CommandDefinition(sql, parametros, commandTimeout: 60, cancellationToken: ct)).ConfigureAwait(false))
            .Cast<IDictionary<string, object?>>().ToList();

        var etiquetas = await ResolverEtiquetasAsync(cn, consulta.AgruparPor, filas, ct).ConfigureAwait(false);
        var resultado = filas.Select(f => new FilaMetrica(
            consulta.AgruparPor.Select((dim, i) => (dim, i)).ToDictionary(x => x.dim, x => Valor(x.dim, f[$"d{x.i}"], etiquetas)),
            f["valor"] is null or DBNull ? null : Convert.ToDecimal(f["valor"], CultureInfo.InvariantCulture))).ToList();
        return new ResultadoMetrica(metrica.Codigo, metrica.Nombre, metrica.Unidad, metrica.Sentido, consulta.AgruparPor, resultado, noAplicados);
    }

    /// <summary>Construye el SQL y sus parámetros. Público para poder probarlo sin base de datos.</summary>
    internal static (string Sql, DynamicParameters Parametros, IReadOnlyList<string> NoAplicados) ArmarSql(DefinicionMetrica m, ConsultaMetrica c, int empresaId)
    {
        var p = new DynamicParameters();
        p.Add("empresaId", empresaId);
        var donde = new List<string> { "EmpresaId = @empresaId" };
        var noAplicados = new List<string>();
        var f = c.Filtros;

        string? Col(string dim) => m.Dimensiones.TryGetValue(dim, out var col) ? $"[{col}]" : null;

        // periodo (los saldos acumulados ignoran el inicio)
        if (f.PeriodoDesde is not null || f.PeriodoHasta is not null)
        {
            if (Col(Dimensiones.Periodo) is { } per)
            {
                if (f.PeriodoDesde is { } d && !m.Acumulado) { donde.Add($"{per} >= @periodoDesde"); p.Add("periodoDesde", d); }
                if (f.PeriodoHasta is { } h) { donde.Add($"{per} <= @periodoHasta"); p.Add("periodoHasta", h); }
            }
            else noAplicados.Add(Dimensiones.Periodo);
        }

        void Lista(IReadOnlyList<int>? valores, string dim, string parametro)
        {
            if (valores is not { Count: > 0 }) return;
            if (Col(dim) is { } col) { donde.Add($"{col} IN @{parametro}"); p.Add(parametro, valores.ToArray()); }
            else noAplicados.Add(dim);
        }
        Lista(f.Sedes, Dimensiones.Sede, "sedes");
        Lista(f.Pagadores, Dimensiones.Pagador, "pagadores");
        Lista(f.CentrosCosto, Dimensiones.CentroCosto, "centros");

        // corte (cartera): el pedido o, si no hay, el más reciente cargado
        if (Col("corte") is { } corte)
        {
            if (f.Corte is { } k) { donde.Add($"{corte} = @corte"); p.Add("corte", k); }
            else donde.Add($"{corte} = (SELECT MAX({corte}) FROM {m.Fuente} WHERE EmpresaId = @empresaId)");
        }
        else if (f.Corte is not null) noAplicados.Add("corte");

        if (m.FiltroFijo is not null) donde.Add("(" + m.FiltroFijo + ")");

        var sql = new StringBuilder("SELECT ");
        if (c.AgruparPor.Count > 0) sql.Append($"TOP ({Math.Clamp(c.Top ?? TopeDuro, 1, TopeDuro).ToString(CultureInfo.InvariantCulture)}) ");
        for (var i = 0; i < c.AgruparPor.Count; i++) sql.Append($"{Col(c.AgruparPor[i])} AS d{i}, ");
        sql.Append($"{m.Expresion} AS valor FROM {m.Fuente} WHERE {string.Join(" AND ", donde)}");
        if (c.AgruparPor.Count > 0)
        {
            sql.Append(" GROUP BY ").Append(string.Join(", ", c.AgruparPor.Select(d => Col(d))));
            sql.Append(c.Top is not null
                ? $" ORDER BY valor {(c.Descendente ? "DESC" : "ASC")}"
                : " ORDER BY " + string.Join(", ", c.AgruparPor.Select((_, i) => $"d{i}")));
        }
        return (sql.ToString(), p, noAplicados);
    }

    private static DimensionValor Valor(string dim, object? crudo, Dictionary<string, Dictionary<string, string>> etiquetas)
    {
        if (crudo is null or DBNull) return new DimensionValor("", "(sin dato)");
        var clave = Convert.ToString(crudo, CultureInfo.InvariantCulture) ?? "";
        if (dim == Dimensiones.Periodo && int.TryParse(clave, out var per)) return new DimensionValor(clave, Dimensiones.EtiquetaPeriodo(per));
        if (dim == "corte" && int.TryParse(clave, out var corte)) return new DimensionValor(clave, Dimensiones.EtiquetaCorte(corte));
        if (etiquetas.TryGetValue(dim, out var mapa) && mapa.TryGetValue(clave, out var texto))
            return new DimensionValor(clave, Dimensiones.Buscar(dim)?.ClaveTexto == true ? $"{clave} · {texto}" : texto);
        return new DimensionValor(clave, Dimensiones.EtiquetaFija(dim, clave) ?? clave);
    }

    /// <summary>Etiquetas de las dimensiones que vienen de una tabla: una consulta por dimensión (lista blanca), con RLS.</summary>
    private static async Task<Dictionary<string, Dictionary<string, string>>> ResolverEtiquetasAsync(
        System.Data.Common.DbConnection cn, IReadOnlyList<string> dims, List<IDictionary<string, object?>> filas, CancellationToken ct)
    {
        var resultado = new Dictionary<string, Dictionary<string, string>>();
        for (var i = 0; i < dims.Count; i++)
        {
            var def = Dimensiones.Buscar(dims[i]);
            if (def?.Tabla is null || filas.Count == 0) continue;
            var crudos = filas.Select(f => f[$"d{i}"]).Where(v => v is not null and not DBNull).ToList();
            if (crudos.Count == 0) continue;
            var sql = $"SELECT CAST({def.ColumnaId} AS varchar(25)) AS Id, {def.ColumnaEtiqueta} AS Etiqueta FROM {def.Tabla} WHERE {def.ColumnaId} IN @ids";
            var filasEtiqueta = def.ClaveTexto
                ? await cn.QueryAsync<(string Id, string? Etiqueta)>(new CommandDefinition(sql, new { ids = crudos.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)!).Distinct().ToArray() }, cancellationToken: ct)).ConfigureAwait(false)
                : await cn.QueryAsync<(string Id, string? Etiqueta)>(new CommandDefinition(sql, new { ids = crudos.Select(Convert.ToInt32).Distinct().ToArray() }, cancellationToken: ct)).ConfigureAwait(false);
            var mapa = filasEtiqueta.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Etiqueta ?? g.Key);
            resultado[dims[i]] = mapa;
        }
        return resultado;
    }

    private sealed class ContextoCalculo(int empresaId, IFabricaConexiones conexiones) : IContextoCalculo
    {
        public int EmpresaId => empresaId;

        public async Task<IReadOnlyList<T>> ConsultarAsync<T>(string sql, object parametros, CancellationToken ct)
        {
            await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
            return (await cn.QueryAsync<T>(new CommandDefinition(sql, parametros, commandTimeout: 60, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        }
    }
}

/// <summary>Metas de KPI de la empresa de la sesión (con RLS).</summary>
internal sealed class MetasKpi(IFabricaConexiones conexiones, ISesionUsuario sesion) : IMetasKpi
{
    public async Task<MetaKpi?> ObtenerAsync(string kpiCodigo, int? periodoHasta, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId) return null;
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.QueryFirstOrDefaultAsync<MetaKpi>(new CommandDefinition("""
            SELECT TOP (1) Meta, UmbralAlerta AS Umbral, PeriodoKey FROM cfg.KpiMeta
            WHERE EmpresaId = @empresaId AND KpiCodigo = @kpiCodigo AND (@periodoHasta IS NULL OR PeriodoKey <= @periodoHasta)
            ORDER BY PeriodoKey DESC
            """, new { empresaId, kpiCodigo, periodoHasta }, cancellationToken: ct)).ConfigureAwait(false);
    }
}

/// <summary>Sedes, pagadores y centros de costo para la barra de filtros. Las dimensiones salen de la RLS, no de la sesión.</summary>
internal sealed class OpcionesFiltros(IFabricaConexiones conexiones) : IOpcionesFiltros
{
    public async Task<Application.Semantica.OpcionesFiltros> ObtenerAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        async Task<IReadOnlyList<OpcionFiltro>> Lista(string sql)
            => (await cn.QueryAsync<OpcionFiltro>(new CommandDefinition(sql, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        return new Application.Semantica.OpcionesFiltros(
            await Lista("SELECT TOP (500) SedeId AS Id, Nombre FROM dim.Sede WHERE Activa = 1 ORDER BY Nombre").ConfigureAwait(false),
            await Lista("SELECT TOP (500) PagadorId AS Id, Nombre FROM dim.Pagador WHERE Activo = 1 ORDER BY Nombre").ConfigureAwait(false),
            await Lista("SELECT TOP (500) CentroCostoId AS Id, Nombre FROM dim.CentroCosto WHERE Activo = 1 ORDER BY Nombre").ConfigureAwait(false));
    }
}
