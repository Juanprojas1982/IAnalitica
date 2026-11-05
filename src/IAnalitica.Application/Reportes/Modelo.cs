using IAnalitica.Domain.Modulos;

namespace IAnalitica.Application.Reportes;

public static class FormatosReporte
{
    public const string Pdf = "PDF";
    public const string Xlsx = "XLSX";
    public const string Csv = "CSV";
    public static readonly IReadOnlySet<string> Todos = new HashSet<string> { Pdf, Xlsx, Csv };
}

public static class EstadosReporte
{
    public const string Pendiente = "PENDIENTE";
    public const string EnProceso = "EN_PROCESO";
    public const string Listo = "LISTO";
    public const string Error = "ERROR";
    public const string Cancelado = "CANCELADO";
    public const string Expirado = "EXPIRADO";
}

/// <summary>Límites de uso de los reportes (spec 21 §4). Valores iniciales; configurables por host.</summary>
public sealed class LimitesReportes
{
    public int EnColaPorUsuario { get; set; } = 5;
    public int ReutilizarHoras { get; set; } = 24;
    public int CaducidadDias { get; set; } = 7;
    /// <summary>Filas máximas de un reporte (Excel: 200.000 en la spec; aquí por widget, con tope total).</summary>
    public int MaxFilasPorSeccion { get; set; } = 100_000;
}

public sealed record ColumnaReporte(string Nombre, string Unidad, bool EsNumero);

/// <summary>Una tabla del reporte: cada widget del dashboard se exporta como una (dimensiones como texto, métricas como números).</summary>
public sealed record SeccionReporte(string Titulo, TipoWidget Tipo, IReadOnlyList<ColumnaReporte> Columnas, IReadOnlyList<IReadOnlyList<object?>> Filas, string? Nota);

/// <summary>Contenido completo de un reporte de dashboard, listo para pasar a PDF, Excel o CSV.</summary>
public sealed record ReporteDashboard(
    string Empresa, string Titulo, string Modulo, IReadOnlyList<(string Nombre, string Valor)> Parametros,
    DateTime GeneradoEnUtc, long VersionCarga, IReadOnlyList<SeccionReporte> Secciones, string? ColorPrimario = null);

/// <summary>Convierte los datos de un widget en una tabla exportable (la misma información que se ve en pantalla, completa).</summary>
public static class ConstructorSecciones
{
    public static SeccionReporte Desde(Semantica.DatosWidget datos, int maxFilas)
    {
        var series = datos.Series;
        var dims = series.Count == 0 ? Array.Empty<string>() : series[0].Resultado.Dimensiones.ToArray();
        var columnas = dims.Select(d => new ColumnaReporte(Dimensiones.Buscar(d)?.Nombre ?? d, "", false))
            .Concat(series.Select(s => new ColumnaReporte(s.Resultado.Nombre, s.Resultado.Unidad, true))).ToList();

        static string Clave(FilaMetrica f, string[] dims) => string.Join("\u001f", dims.Select(d => f.Dimensiones[d].Clave));
        var claves = series.SelectMany(s => s.Resultado.Filas.Select(f => Clave(f, dims))).Distinct().ToList();
        var etiquetas = series.SelectMany(s => s.Resultado.Filas).GroupBy(f => Clave(f, dims)).ToDictionary(g => g.Key, g => dims.Select(d => g.First().Dimensiones[d].Etiqueta).ToList());
        var indices = series.Select(s => s.Resultado.Filas.GroupBy(f => Clave(f, dims)).ToDictionary(g => g.Key, g => g.First().Valor)).ToList();

        var cronologico = dims.Any(d => d is "periodo" or "corte" or "rango_edad");
        var orden = cronologico
            ? claves.OrderBy(c => c, StringComparer.Ordinal)
            : claves.OrderByDescending(c => indices[0].GetValueOrDefault(c) ?? decimal.MinValue);
        var filas = orden.Take(maxFilas).Select(c =>
        {
            var fila = new List<object?>();
            fila.AddRange(etiquetas[c]);
            fila.AddRange(indices.Select(ix => (object?)ix.GetValueOrDefault(c)));
            return (IReadOnlyList<object?>)fila;
        }).ToList();

        var nota = claves.Count > maxFilas ? $"Se exportaron las primeras {maxFilas} de {claves.Count} filas." : null;
        if (datos.FiltrosNoAplicados.Count > 0)
            nota = (nota is null ? "" : nota + " ") + "No aplica el filtro de " + string.Join(", ", datos.FiltrosNoAplicados.Select(f => Dimensiones.Buscar(f)?.Nombre?.ToLowerInvariant() ?? f)) + ".";
        return new SeccionReporte(datos.Titulo, datos.Tipo, columnas, filas, nota);
    }
}
