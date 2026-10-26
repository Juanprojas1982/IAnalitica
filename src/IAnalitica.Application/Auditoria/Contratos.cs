namespace IAnalitica.Application.Auditoria;

public static class EstadosHallazgo
{
    public const string Nuevo = "NUEVO", Abierto = "ABIERTO", Corregido = "CORREGIDO", Aceptado = "ACEPTADO", FalsoPositivo = "FALSO_POSITIVO";
    public static bool EstaAbierto(string estado) => estado is Nuevo or Abierto;
}

/// <summary>Un hallazgo con los textos de su regla: qué está mal, por qué importa y cómo y dónde se corrige (spec 16 §5).</summary>
public sealed record HallazgoVista(
    long HallazgoId, string Codigo, string Area, string Titulo, string Severidad, string Estado,
    string ClaveEntidad, string? NombreEntidad, string? ValorEncontrado, string? ValorEsperado,
    int? ImpactoCantidad, decimal? ImpactoValor, string? ImpactoDetalle,
    string PorQueImporta, string ComoCorregir, string? RutaSaludIPS, bool PorVerificar,
    string? Justificacion, DateTime PrimeraDeteccion, DateTime UltimaDeteccion);

/// <param name="Anterior">Índice de la medición anterior (para la tendencia); null si es la primera.</param>
public sealed record IndiceArea(string Area, int Indice, int Abiertos, int? Anterior);

public sealed record SaludConfiguracion(DateOnly? Fecha, IReadOnlyList<IndiceArea> Areas, IReadOnlyList<HallazgoVista> Hallazgos)
{
    public IndiceArea? Total => Areas.FirstOrDefault(a => a.Area == "TOTAL");
}

/// <summary>Hallazgos del auditor con el contexto del usuario (RLS por empresa).</summary>
public interface IRepositorioHallazgos
{
    Task<SaludConfiguracion> ObtenerAsync(bool incluirCerrados, CancellationToken ct);
    Task<HallazgoVista?> ObtenerHallazgoAsync(long hallazgoId, CancellationToken ct);
    /// <returns>false si el hallazgo cambió de estado mientras tanto (no estaba en <paramref name="estadosPermitidos"/>).</returns>
    Task<bool> CambiarEstadoAsync(long hallazgoId, string estadoNuevo, string? justificacion, int usuarioId, IReadOnlyCollection<string> estadosPermitidos, CancellationToken ct);
}
