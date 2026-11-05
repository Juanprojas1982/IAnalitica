using System.Globalization;
using IAnalitica.Domain.Modulos;

namespace IAnalitica.Web.Components.Dashboards;

/// <summary>
/// Filtros globales tal como viajan en la URL (estado en la URL, spec 14 §5): <c>desde=2026-01&amp;hasta=2026-12&amp;sede=1&amp;pagador=2&amp;cc=3&amp;corte=2026-03-01</c>.
/// Lo que no venga, toma un valor por omisión (últimos 12 meses) o no filtra.
/// </summary>
public sealed record FiltrosPagina(string? Desde = null, string? Hasta = null, int? Sede = null, int? Pagador = null, int? CentroCosto = null, string? Corte = null)
{
    public FiltrosDashboard ParaMotor(DateTime hoyColombia)
    {
        var hasta = ParsePeriodo(Hasta) ?? hoyColombia.Year * 100 + hoyColombia.Month;
        var desde = ParsePeriodo(Desde) ?? RestarMeses(hasta, 11);
        if (desde > hasta) (desde, hasta) = (hasta, desde);
        return new FiltrosDashboard(desde, hasta,
            Sede is { } s ? new[] { s } : null, Pagador is { } p ? new[] { p } : null, CentroCosto is { } c ? new[] { c } : null, ParseCorte(Corte));
    }

    /// <summary>Cadena de consulta con solo lo que el usuario fijó. <paramref name="cambios"/> reemplaza o agrega filtros (para el drill).</summary>
    public string Consulta(Func<FiltrosPagina, FiltrosPagina>? cambios = null)
    {
        var f = cambios?.Invoke(this) ?? this;
        var partes = new List<string>();
        void Agregar(string nombre, string? valor) { if (!string.IsNullOrEmpty(valor)) partes.Add($"{nombre}={Uri.EscapeDataString(valor)}"); }
        Agregar("desde", f.Desde); Agregar("hasta", f.Hasta);
        Agregar("sede", f.Sede?.ToString(CultureInfo.InvariantCulture)); Agregar("pagador", f.Pagador?.ToString(CultureInfo.InvariantCulture));
        Agregar("cc", f.CentroCosto?.ToString(CultureInfo.InvariantCulture)); Agregar("corte", f.Corte);
        return partes.Count == 0 ? "" : "?" + string.Join("&", partes);
    }

    /// <summary>Filtro por la llave de una dimensión (drill desde una barra o una fila).</summary>
    public FiltrosPagina Con(string dimension, string clave)
        => int.TryParse(clave, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? dimension switch
            {
                Dimensiones.Sede => this with { Sede = id },
                Dimensiones.Pagador => this with { Pagador = id },
                Dimensiones.CentroCosto => this with { CentroCosto = id },
                _ => this,
            }
            : this;

    public static bool EsFiltrable(string dimension) => dimension is Dimensiones.Sede or Dimensiones.Pagador or Dimensiones.CentroCosto;

    public static int? ParsePeriodo(string? texto)
        => texto is { Length: 7 } && texto[4] == '-' && int.TryParse(texto[..4], out var a) && int.TryParse(texto[5..], out var m) && m is >= 1 and <= 12 && a is >= 2000 and <= 2100
            ? a * 100 + m : null;

    public static int? ParseCorte(string? texto)
        => texto is not null && DateTime.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Year * 10000 + d.Month * 100 + d.Day : null;

    public static int RestarMeses(int periodo, int meses)
    {
        var total = periodo / 100 * 12 + (periodo % 100 - 1) - meses;
        return total / 12 * 100 + total % 12 + 1;
    }

    public static string TextoPeriodo(int periodo) => $"{periodo / 100:0000}-{periodo % 100:00}";
}
