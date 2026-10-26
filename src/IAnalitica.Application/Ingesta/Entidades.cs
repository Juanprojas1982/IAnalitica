using IAnalitica.Ingesta.Contracts;

namespace IAnalitica.Application.Ingesta;

/// <summary>Entidad del contrato 05 que la plataforma sabe procesar, con los modos que admite y su control de suma.</summary>
public sealed record EntidadIngesta(string Nombre, IReadOnlyList<string> Modos, string[] ColumnasSumaControl, string? ModuloRequerido = null, bool EsCatalogo = false);

/// <summary>
/// Entidades aceptadas en F2: catálogos, asientos y facturas. El resto del contrato (recibos, inventario, glosas, operativos...) y
/// <c>configuracion</c> (auditor, F4) se rechazan con ENTIDAD_NO_SOPORTADA hasta que exista su procesador: nunca se acepta un payload
/// que nadie va a procesar.
/// </summary>
public static class CatalogoEntidades
{
    private static readonly string[] SoloCompleta = { Modos.Completa };

    public static readonly IReadOnlyList<EntidadIngesta> Todas = new[]
    {
        Cat("sedes"), Cat("lineas-servicio"), Cat("centros-costo"), Cat("cuentas"), Cat("tipos-comprobante"),
        new EntidadIngesta("terceros", new[] { Modos.Incremental, Modos.Completa }, Array.Empty<string>(), EsCatalogo: true),
        Cat("pagadores"), Cat("contratos"), Cat("servicios"), Cat("articulos"), Cat("lotes"), Cat("almacenes"),
        Cat("especialidades"), Cat("profesionales"), Cat("usuarios-operativos"), Cat("cuentas-bancarias"),
        new EntidadIngesta("asientos", new[] { Modos.Ventana, Modos.Inicial }, new[] { "debito", "credito" }),
        new EntidadIngesta("facturas", new[] { Modos.Ventana, Modos.Inicial, Modos.Incremental }, new[] { "valorNeto" }),
    };

    public static EntidadIngesta? Buscar(string? nombre)
        => Todas.FirstOrDefault(e => string.Equals(e.Nombre, nombre, StringComparison.Ordinal));

    private static EntidadIngesta Cat(string nombre) => new(nombre, SoloCompleta, Array.Empty<string>(), EsCatalogo: true);
}
