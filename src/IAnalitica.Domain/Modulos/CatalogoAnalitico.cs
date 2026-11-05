using System.Text.RegularExpressions;

namespace IAnalitica.Domain.Modulos;

/// <summary>
/// Registro validado de módulos, métricas y dashboards de fábrica. Se construye una vez al arrancar y FALLA RÁPIDO si algo registrado
/// no cumple la lista blanca: la fuente, las columnas y las expresiones se concatenan al SQL del motor, así que deben ser constantes
/// con forma conocida (nunca texto del usuario).
/// </summary>
public sealed partial class CatalogoAnalitico
{
    public IReadOnlyDictionary<string, IModuloAnalitico> Modulos { get; }
    public IReadOnlyDictionary<string, DefinicionMetrica> Metricas { get; }
    public IReadOnlyDictionary<string, DefinicionDashboard> Dashboards { get; }

    private CatalogoAnalitico(Dictionary<string, IModuloAnalitico> modulos, Dictionary<string, DefinicionMetrica> metricas, Dictionary<string, DefinicionDashboard> dashboards)
    {
        Modulos = modulos; Metricas = metricas; Dashboards = dashboards;
    }

    public static CatalogoAnalitico Construir(IEnumerable<IModuloAnalitico> modulos)
    {
        var errores = new List<string>();
        var mods = new Dictionary<string, IModuloAnalitico>();
        var metricas = new Dictionary<string, DefinicionMetrica>();
        var dashboards = new Dictionary<string, DefinicionDashboard>();

        foreach (var modulo in modulos)
        {
            if (!mods.TryAdd(modulo.Codigo, modulo)) { errores.Add($"Módulo repetido: {modulo.Codigo}."); continue; }
            var registro = new Registro(modulo.Codigo, metricas, dashboards, errores);
            modulo.Registrar(registro);
        }

        foreach (var m in metricas.Values) ValidarMetrica(m, mods, errores);
        foreach (var d in dashboards.Values) ValidarDashboard(d, mods, metricas, dashboards, errores);

        if (errores.Count > 0)
            throw new InvalidOperationException("El registro de módulos analíticos no es válido:" + Environment.NewLine + string.Join(Environment.NewLine, errores.Select(e => " - " + e)));
        return new CatalogoAnalitico(mods, metricas, dashboards);
    }

    private sealed class Registro(string modulo, Dictionary<string, DefinicionMetrica> metricas, Dictionary<string, DefinicionDashboard> dashboards, List<string> errores) : IRegistroModulo
    {
        public void Metrica(DefinicionMetrica metrica)
        {
            if (metrica.Modulo != modulo) errores.Add($"Métrica {metrica.Codigo}: declara el módulo {metrica.Modulo} pero la registra {modulo}.");
            if (!metricas.TryAdd(metrica.Codigo, metrica)) errores.Add($"Métrica repetida: {metrica.Codigo}.");
        }

        public void DashboardDeFabrica(DefinicionDashboard dashboard)
        {
            if (dashboard.Modulo != modulo) errores.Add($"Dashboard {dashboard.Codigo}: declara el módulo {dashboard.Modulo} pero lo registra {modulo}.");
            if (!dashboards.TryAdd(dashboard.Codigo, dashboard)) errores.Add($"Dashboard repetido: {dashboard.Codigo}.");
        }
    }

    // ---------- Validación ----------

    [GeneratedRegex(@"^[a-z][a-z0-9_]*\.[a-z][a-z0-9_]*$")] private static partial Regex CodigoMetrica();
    [GeneratedRegex(@"^[a-z][a-z0-9_.]*$")] private static partial Regex CodigoSimple();
    [GeneratedRegex(@"^rpt\.vw_[A-Za-z][A-Za-z0-9]*$")] private static partial Regex FuenteValida();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")] private static partial Regex Identificador();
    private const string Id = @"[A-Za-z][A-Za-z0-9_]*";
    [GeneratedRegex($@"^(?:(?:SUM|AVG|MIN|MAX)\({Id}\)|COUNT\(\*\)|COUNT\(DISTINCT {Id}\)|(?:\d+(?:\.\d+)?\*)?SUM\({Id}\)/NULLIF\(SUM\({Id}\),0\))$")] private static partial Regex ExpresionValida();
    // «col = 'x'», «col <> 3», «col IN ('a','b')», unidos con AND (mezclados)
    private const string Valor = @"(?:'[^']*'|-?\d+(?:\.\d+)?)";
    private const string Termino = $@"{Id}\s*(?:(?:=|<>|>=|<=|>|<)\s*{Valor}|IN\s*\({Valor}(?:\s*,\s*{Valor})*\))";
    [GeneratedRegex($@"^{Termino}(?:\s+AND\s+{Termino})*$")] private static partial Regex FiltroValido();

    private static void ValidarMetrica(DefinicionMetrica m, Dictionary<string, IModuloAnalitico> mods, List<string> e)
    {
        var p = $"Métrica {m.Codigo}: ";
        if (!CodigoMetrica().IsMatch(m.Codigo)) e.Add(p + "el código debe ser «modulo.nombre» en minúsculas.");
        else if (!m.Codigo.StartsWith(m.Modulo.ToLowerInvariant() + ".", StringComparison.Ordinal)) e.Add(p + $"el código debe empezar por «{m.Modulo.ToLowerInvariant()}.».");
        if (!mods.ContainsKey(m.Modulo)) e.Add(p + $"módulo desconocido {m.Modulo}.");
        if (string.IsNullOrWhiteSpace(m.Nombre)) e.Add(p + "falta el nombre.");
        if (!Unidades.Todas.Contains(m.Unidad)) e.Add(p + $"unidad no válida «{m.Unidad}».");
        if (!FuenteValida().IsMatch(m.Fuente)) e.Add(p + $"la fuente debe ser una vista rpt.vw_*, no «{m.Fuente}».");
        if (m.Calculadora is null)
        {
            if (!ExpresionValida().IsMatch(m.Expresion)) e.Add(p + $"la expresión «{m.Expresion}» no está en el catálogo cerrado (SUM(col), AVG(col), MIN/MAX(col), COUNT(*), COUNT(DISTINCT col), SUM(a)/NULLIF(SUM(b),0)).");
        }
        else if (!typeof(IMetricaCalculada).IsAssignableFrom(m.Calculadora)) e.Add(p + $"{m.Calculadora.Name} no implementa IMetricaCalculada.");

        if (m.FiltroFijo is not null && !FiltroValido().IsMatch(m.FiltroFijo)) e.Add(p + $"el filtro fijo «{m.FiltroFijo}» no tiene una forma permitida.");
        foreach (var (dim, columna) in m.Dimensiones)
        {
            if (Dimensiones.Buscar(dim) is null) e.Add(p + $"dimensión desconocida «{dim}».");
            if (!Identificador().IsMatch(columna)) e.Add(p + $"columna de dimensión no válida «{columna}».");
        }
    }

    private static void ValidarDashboard(DefinicionDashboard d, Dictionary<string, IModuloAnalitico> mods, Dictionary<string, DefinicionMetrica> metricas,
        Dictionary<string, DefinicionDashboard> dashboards, List<string> e)
    {
        var p = $"Dashboard {d.Codigo}: ";
        if (!CodigoSimple().IsMatch(d.Codigo)) e.Add(p + "el código debe estar en minúsculas (a-z, 0-9, _ y .).");
        if (!mods.ContainsKey(d.Modulo)) e.Add(p + $"módulo desconocido {d.Modulo}.");
        else if (d.Permiso != "dash." + d.Modulo.ToLowerInvariant()) e.Add(p + $"el permiso debe ser «dash.{d.Modulo.ToLowerInvariant()}», no «{d.Permiso}».");
        if (d.Widgets.Count == 0) e.Add(p + "no tiene widgets.");
        if (d.Widgets.Select(w => w.Codigo).Distinct().Count() != d.Widgets.Count) e.Add(p + "hay códigos de widget repetidos.");

        foreach (var w in d.Widgets)
        {
            var pw = p + $"widget {w.Codigo}: ";
            if (w.Columna < 0 || w.Ancho < 1 || w.Columna + w.Ancho > 12 || w.Fila < 0 || w.Alto < 1) e.Add(pw + "la posición se sale de la grilla de 12 columnas.");
            if (w.Metricas.Count == 0) e.Add(pw + "no tiene métricas.");
            foreach (var codigo in w.Metricas)
            {
                if (!metricas.TryGetValue(codigo, out var m)) { e.Add(pw + $"métrica inexistente «{codigo}»."); continue; }
                foreach (var dim in w.Dimensiones)
                    if (!m.Dimensiones.ContainsKey(dim)) e.Add(pw + $"la métrica {codigo} no tiene la dimensión «{dim}».");
            }
            var (minM, maxM, minD, maxD) = w.Tipo switch
            {
                TipoWidget.Kpi => (1, 1, 0, 0),
                TipoWidget.Linea => (1, 4, 1, 1),
                TipoWidget.Barras or TipoWidget.Dona or TipoWidget.Embudo or TipoWidget.Cascada => (1, 1, 1, 1),
                TipoWidget.BarrasApiladas or TipoWidget.MapaCalor => (1, 1, 2, 2),
                _ => (1, 12, 0, 3),   // Tabla
            };
            if (w.Metricas.Count < minM || w.Metricas.Count > maxM) e.Add(pw + $"un widget {w.Tipo} lleva entre {minM} y {maxM} métricas.");
            if (w.Dimensiones.Count < minD || w.Dimensiones.Count > maxD) e.Add(pw + $"un widget {w.Tipo} lleva entre {minD} y {maxD} dimensiones.");
            if (w.DrillDashboard is not null && !dashboards.ContainsKey(w.DrillDashboard)) e.Add(pw + $"el drill apunta a un dashboard inexistente «{w.DrillDashboard}».");
            foreach (var mod in w.ModulosAdicionales ?? []) if (!mods.ContainsKey(mod)) e.Add(pw + $"módulo adicional desconocido {mod}.");
        }
    }
}
