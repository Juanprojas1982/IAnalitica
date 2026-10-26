namespace IAnalitica.Domain.Modulos;

/// <summary>
/// Dimensión global de los dashboards: cómo se obtiene la etiqueta de una llave. Todo es constante del código (lista blanca):
/// <see cref="Tabla"/>, <see cref="ColumnaId"/> y <see cref="ColumnaEtiqueta"/> se concatenan al SQL; nunca vienen del usuario.
/// </summary>
public sealed record DefinicionDimension(string Codigo, string Nombre, string? Tabla, string? ColumnaId, string? ColumnaEtiqueta, string? FiltroGlobal);

public static class Dimensiones
{
    public const string Periodo = "periodo";
    public const string Sede = "sede";
    public const string Pagador = "pagador";
    public const string CentroCosto = "centro_costo";

    public static readonly IReadOnlyList<DefinicionDimension> Todas = new[]
    {
        new DefinicionDimension(Periodo, "Periodo", null, null, null, "periodo"),
        new DefinicionDimension(Sede, "Sede", "dim.Sede", "SedeId", "Nombre", "sedes"),
        new DefinicionDimension(Pagador, "Pagador", "dim.Pagador", "PagadorId", "Nombre", "pagadores"),
        new DefinicionDimension(CentroCosto, "Centro de costo", "dim.CentroCosto", "CentroCostoId", "Nombre", "centros_costo"),
        new DefinicionDimension("linea_servicio", "Línea de servicio", "dim.LineaServicio", "LineaServicioId", "Nombre", null),
        new DefinicionDimension("usuario", "Usuario", "dim.UsuarioOperativo", "UsuarioOpId", "Nombre", null),
        new DefinicionDimension("tercero", "Tercero", "dim.Tercero", "TerceroId", "Nombre", null),
        new DefinicionDimension("cuenta", "Cuenta", "dim.CuentaContable", "CuentaId", "Nombre", null),
        // dimensiones cuya llave ya es legible
        new DefinicionDimension("corte", "Fecha de corte", null, null, null, "corte"),
        new DefinicionDimension("rango_edad", "Edad de la cartera", null, null, null, null),
        new DefinicionDimension("tipo_cartera", "Tipo de cartera", null, null, null, null),
        new DefinicionDimension("clase_puc", "Clase", null, null, null, null),
        new DefinicionDimension("grupo_resultados", "Grupo", null, null, null, null),
        new DefinicionDimension("grupo_balance", "Grupo del balance", null, null, null, null),
        new DefinicionDimension("grupo_puc", "Grupo PUC", null, null, null, null),
        new DefinicionDimension("estado_fe", "Estado de factura electrónica", null, null, null, null),
        new DefinicionDimension("etapa", "Etapa", null, null, null, null),
    };

    public static DefinicionDimension? Buscar(string codigo) => Todas.FirstOrDefault(d => d.Codigo == codigo);

    /// <summary>Etiqueta legible de una llave de periodo yyyymm → «2026-01».</summary>
    public static string EtiquetaCorte(int corteKey) => $"{corteKey / 10000:0000}-{corteKey / 100 % 100:00}-{corteKey % 100:00}";

    public static string EtiquetaPeriodo(int periodoKey) => $"{periodoKey / 100:0000}-{periodoKey % 100:00}";
}
