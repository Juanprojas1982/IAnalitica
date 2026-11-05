namespace IAnalitica.Domain.Auditoria;

public enum Severidad { Baja = 1, Media = 2, Alta = 3, Critica = 4 }

public static class Severidades
{
    public static string ATexto(Severidad s) => s switch { Severidad.Critica => "CRITICA", Severidad.Alta => "ALTA", Severidad.Media => "MEDIA", _ => "BAJA" };
    public static Severidad? DeTexto(string? t) => t?.ToUpperInvariant() switch { "CRITICA" => Severidad.Critica, "ALTA" => Severidad.Alta, "MEDIA" => Severidad.Media, "BAJA" => Severidad.Baja, _ => null };

    /// <summary>Peso para el índice de salud: cada hallazgo abierto resta puntos según su severidad (24 §5.5, spec 16 §5) [S].</summary>
    public static int Peso(string severidad) => severidad switch { "CRITICA" => 20, "ALTA" => 10, "MEDIA" => 4, _ => 1 };
}

/// <summary>
/// Un problema encontrado por una regla. Las columnas coinciden con las que devuelve el SQL de cada regla (así Dapper las mapea por nombre).
/// <see cref="Severidad"/> es opcional: una regla puede subirla (p. ej. CRITICA si el vencimiento ya pasó).
/// </summary>
public sealed record HallazgoDetectado(
    string ClaveEntidad, string? NombreEntidad, string? ValorEncontrado, string? ValorEsperado,
    int? ImpactoCantidad, decimal? ImpactoValor, string? ImpactoDetalle, string? Severidad = null);

/// <summary>Consultas que una regla puede hacer: SQL parametrizado de lectura (constante del código) con la empresa fija.</summary>
public interface IConsultasAuditoria
{
    Task<IReadOnlyList<HallazgoDetectado>> ConsultarAsync(string sql, IReadOnlyDictionary<string, object?> parametros, CancellationToken ct);
}

public sealed record ContextoAuditoria(int EmpresaId, DateOnly Hoy, IReadOnlyDictionary<string, decimal> Umbrales, IConsultasAuditoria Datos)
{
    public int Anio => Hoy.Year;
}

/// <summary>Una regla del auditor (spec 16 §4). Una regla nueva es una clase nueva; el motor no cambia.</summary>
public interface IReglaConfiguracion
{
    string Codigo { get; }                              // "CON-01"
    string Area { get; }                                // Contabilidad, Tarifas, FE, Seguridad...
    Severidad SeveridadBase { get; }
    Task<IReadOnlyList<HallazgoDetectado>> EvaluarAsync(ContextoAuditoria ctx, CancellationToken ct);
}
