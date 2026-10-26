namespace IAnalitica.Domain.Modulos;

/// <summary>Atajos para declarar métricas y widgets sin repetir la grilla ni los diccionarios (los módulos los usan a diario).</summary>
public static class Declarar
{
    public static IReadOnlyDictionary<string, string> Dims(params (string Dimension, string Columna)[] dims)
        => dims.ToDictionary(d => d.Dimension, d => d.Columna);

    public static DefinicionWidget Kpi(string codigo, string titulo, string metrica, int fila, int columna, int ancho = 3, int alto = 2, string? drill = null, params string[] modulosAdicionales)
        => new(codigo, titulo, TipoWidget.Kpi, new[] { metrica }, Array.Empty<string>(), fila, columna, ancho, alto, drill, modulosAdicionales.Length == 0 ? null : modulosAdicionales);

    public static DefinicionWidget Grafico(string codigo, string titulo, TipoWidget tipo, string[] metricas, string[] dimensiones, int fila, int columna, int ancho, int alto, string? drill = null, params string[] modulosAdicionales)
        => new(codigo, titulo, tipo, metricas, dimensiones, fila, columna, ancho, alto, drill, modulosAdicionales.Length == 0 ? null : modulosAdicionales);
}
