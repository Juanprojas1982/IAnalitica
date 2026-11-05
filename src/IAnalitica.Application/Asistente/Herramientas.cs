using System.ComponentModel;
using System.Text.Json;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Costos;
using IAnalitica.Application.Semantica;
using IAnalitica.Domain.Modulos;
using Microsoft.Extensions.AI;

namespace IAnalitica.Application.Asistente;

/// <summary>
/// Herramientas del asistente = métricas de los módulos activos que el usuario puede ver (24 §5.7). La IA nunca escribe SQL: elige una métrica del
/// catálogo y sus dimensiones. Todo corre con la sesión del usuario (RLS y filtro por empresa en el motor); <c>EmpresaId</c> no es parámetro.
/// Los resultados se devuelven como datos acotados (máx. <see cref="MaxFilas"/> filas) y con las personas seudonimizadas.
/// </summary>
public sealed class HerramientasAsistente(CatalogoAnalitico catalogo, IMotorMetricas motor, ISesionUsuario sesion, IModulosEmpresa modulos, Seudonimizador seudonimos)
{
    public const int MaxFilas = 200;
    public const int FilasPorDefecto = 50;

    // El resultado va al modelo, no a una página: se conservan los acentos (menos tokens y más legible)
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly List<FuenteRespuesta> _fuentes = [];
    private IReadOnlyDictionary<string, DefinicionMetrica>? _permitidas;

    public IReadOnlyList<FuenteRespuesta> Fuentes => _fuentes;

    /// <summary>Las herramientas que se declaran al modelo.</summary>
    public IList<AITool> Declarar() =>
    [
        AIFunctionFactory.Create(ListarMetricasAsync, "listar_metricas",
            "Lista las métricas disponibles para esta empresa y usuario, con su unidad y las dimensiones por las que se pueden agrupar."),
        AIFunctionFactory.Create(ConsultarMetricaAsync, "consultar_metrica",
            "Consulta una métrica del catálogo. Toda cifra de la respuesta debe salir de esta herramienta."),
    ];

    private async Task<IReadOnlyDictionary<string, DefinicionMetrica>> PermitidasAsync(CancellationToken ct)
    {
        if (_permitidas is not null) return _permitidas;
        var r = new Dictionary<string, DefinicionMetrica>();
        if (sesion.EmpresaId is { } empresaId)
            foreach (var grupo in catalogo.Metricas.Values.GroupBy(m => m.Modulo))
            {
                // El permiso de un módulo es el de sus dashboards (uno por módulo en la matriz de 24 §3.4)
                var permisos = catalogo.Dashboards.Values.Where(d => d.Modulo == grupo.Key).Select(d => d.Permiso).Distinct().ToList();
                if (!permisos.Any(sesion.TienePermiso)) continue;
                if (!await modulos.EstaActivoAsync(empresaId, grupo.Key, ct).ConfigureAwait(false)) continue;
                foreach (var m in grupo) r[m.Codigo] = m;
            }
        return _permitidas = r;
    }

    [Description("Sin parámetros.")]
    private async Task<string> ListarMetricasAsync(CancellationToken ct)
    {
        var permitidas = await PermitidasAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(permitidas.Values.OrderBy(m => m.Codigo).Select(m => new
        {
            metrica = m.Codigo, nombre = m.Nombre, unidad = m.Unidad.ToString(), agrupar_por = m.Dimensiones.Keys.ToArray(),
        }), Json);
    }

    private async Task<string> ConsultarMetricaAsync(
        [Description("Código de la métrica, tal como lo devuelve listar_metricas.")] string metrica,
        [Description("Dimensiones por las que agrupar (vacío para el total). Solo las que admite la métrica.")] string[]? agrupar_por = null,
        [Description("Periodo inicial aaaamm (por ejemplo 202601).")] int? periodo_desde = null,
        [Description("Periodo final aaaamm.")] int? periodo_hasta = null,
        [Description("Fecha de corte aaaammdd, para cartera y balance.")] int? corte = null,
        [Description("Máximo de filas (1 a 200, por defecto 50), ordenadas de mayor a menor valor.")] int? top = null,
        CancellationToken ct = default)
    {
        var permitidas = await PermitidasAsync(ct).ConfigureAwait(false);
        if (!permitidas.TryGetValue(metrica, out var def))
            return Error("La métrica no existe o no está disponible para este usuario. Use listar_metricas.");
        var dims = agrupar_por ?? [];
        var invalidas = dims.Where(d => !def.Dimensiones.ContainsKey(d)).ToList();
        if (invalidas.Count > 0) return Error($"La métrica no se puede agrupar por: {string.Join(", ", invalidas)}. Admite: {string.Join(", ", def.Dimensiones.Keys)}.");
        if (periodo_desde is { } d1 && !PeriodosCosto.EsValido(d1) || periodo_hasta is { } h1 && !PeriodosCosto.EsValido(h1))
            return Error("Los periodos deben ser aaaamm válidos.");

        var tope = Math.Clamp(top ?? FilasPorDefecto, 1, MaxFilas);
        var consulta = new ConsultaMetrica(metrica, dims, new FiltrosDashboard(periodo_desde, periodo_hasta, Corte: corte), tope);
        ResultadoMetrica resultado;
        try { resultado = await motor.EjecutarAsync(consulta, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { return Error("No se pudo consultar la métrica en este momento."); }

        _fuentes.Add(new FuenteRespuesta(def.Codigo, def.Nombre, Describir(dims, periodo_desde, periodo_hasta, corte)));
        return JsonSerializer.Serialize(new
        {
            metrica = def.Codigo, nombre = def.Nombre, unidad = def.Unidad.ToString(), agrupar_por = dims,
            periodo_desde, periodo_hasta, corte,
            filtros_no_aplicados = resultado.FiltrosNoAplicados,
            sin_datos = resultado.SinDatos,
            truncado = resultado.Filas.Count >= tope,
            filas = resultado.Filas.Take(tope).Select(f => new
            {
                dimensiones = f.Dimensiones.ToDictionary(kv => kv.Key, kv => Seudonimizador.EsPersona(kv.Key) ? seudonimos.Alias(kv.Key, kv.Value.Clave, kv.Value.Etiqueta) : kv.Value.Etiqueta),
                valor = f.Valor,
            }),
        }, Json);
    }

    private static string Describir(string[] dims, int? desde, int? hasta, int? corte)
        => string.Join(" · ", new[]
        {
            dims.Length > 0 ? "por " + string.Join(", ", dims) : "total",
            desde is null && hasta is null ? "todos los periodos" : $"{(desde is null ? "…" : Dimensiones.EtiquetaPeriodo(desde.Value))} a {(hasta is null ? "…" : Dimensiones.EtiquetaPeriodo(hasta.Value))}",
            corte is null ? "" : "corte " + Dimensiones.EtiquetaCorte(corte.Value),
        }.Where(t => t.Length > 0));

    private static string Error(string mensaje) => JsonSerializer.Serialize(new { error = mensaje }, Json);
}
