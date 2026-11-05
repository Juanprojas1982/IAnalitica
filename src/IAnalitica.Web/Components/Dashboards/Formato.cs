using System.Globalization;
using IAnalitica.Domain.Modulos;

namespace IAnalitica.Web.Components.Dashboards;

/// <summary>Formato numérico es-CO (punto de miles y coma decimal), explícito para no depender de los datos de ICU del servidor.</summary>
public static class Formato
{
    private static readonly NumberFormatInfo Co = new()
    {
        NumberGroupSeparator = ".", NumberDecimalSeparator = ",", NumberGroupSizes = new[] { 3 }, NegativeSign = "-",
    };

    public static string Numero(decimal v, int decimales = 0) => v.ToString("#,##0" + (decimales > 0 ? "." + new string('0', decimales) : ""), Co);

    public static string Valor(decimal? valor, string unidad)
    {
        if (valor is not { } v) return "—";
        return unidad switch
        {
            Unidades.Pesos => (v < 0 ? "-" : "") + "$ " + Numero(Math.Abs(v)),
            Unidades.Porcentaje => v.ToString("#,##0.0", Co) + " %",
            Unidades.Dias => v.ToString("#,##0.#", Co) + " d",
            Unidades.Veces => v.ToString("#,##0.00", Co),
            _ => Numero(v),
        };
    }

    /// <summary>Versión corta para ejes de gráficos: 1,2 M, 350 mil.</summary>
    public static string Corto(decimal v, string unidad)
    {
        var abs = Math.Abs(v);
        if (unidad != Unidades.Pesos && unidad != Unidades.Unidades_) return Valor(v, unidad);
        var prefijo = unidad == Unidades.Pesos ? "$ " : "";
        var texto = abs >= 1_000_000_000 ? (abs / 1_000_000_000m).ToString("0.#", Co) + " mil M"
                  : abs >= 1_000_000 ? (abs / 1_000_000m).ToString("0.#", Co) + " M"
                  : abs >= 1_000 ? (abs / 1_000m).ToString("0.#", Co) + " mil"
                  : abs.ToString("0.#", Co);
        return (v < 0 ? "-" : "") + prefijo + texto;
    }

    public static string Variacion(decimal fraccion) => (fraccion > 0 ? "+" : "") + (fraccion * 100).ToString("0.#", Co) + " %";

    /// <summary>Orden canónico de las edades de cartera (el texto no ordena bien: «121-150» &lt; «31-60»).</summary>
    public static readonly string[] OrdenEdades = { "0-30", "31-60", "61-90", "91-120", "121-150", "151-180", "181-360", ">360" };

    public static IEnumerable<string> Ordenar(string dimension, IEnumerable<string> claves)
        => dimension == "rango_edad"
            ? claves.OrderBy(c => Array.IndexOf(OrdenEdades, c) is var i && i < 0 ? int.MaxValue : i).ThenBy(c => c, StringComparer.Ordinal)
            : claves.OrderBy(c => long.TryParse(c, out var n) ? n : long.MaxValue).ThenBy(c => c, StringComparer.Ordinal);
}
