namespace IAnalitica.Application.Costos;

public sealed record BaseCosto(string Codigo, string Nombre, string Origen);

/// <summary>Un centro de costo con su regla de distribución vigente (la de mayor <c>VigenteDesde</c>); null si no la tiene.</summary>
public sealed record CentroConRegla(int CentroCostoId, string Codigo, string Nombre, string TipoCC, string? TipoOrigen, short? Orden, string? BaseCodigo, int? VigenteDesde);

public sealed record ReglaCosto(int CentroCostoId, short Orden, string BaseCodigo, int VigenteDesde);

/// <summary>Reglas de distribución de costos (<c>cfg.CostoRegla</c>) con el contexto del usuario (RLS por empresa).</summary>
public interface IRepositorioCostos
{
    Task<IReadOnlyList<BaseCosto>> ListarBasesAsync(CancellationToken ct);
    Task<IReadOnlyList<CentroConRegla>> ListarCentrosAsync(CancellationToken ct);
    Task<bool> ExisteCentroAsync(int centroCostoId, CancellationToken ct);
    Task GuardarAsync(int empresaId, ReglaCosto regla, CancellationToken ct);
    Task<bool> QuitarAsync(int centroCostoId, int vigenteDesde, CancellationToken ct);
    /// <summary>Centros activos clasificados como ADMINISTRATIVO o APOYO (la homologación del tipo de origen es [S], 05 §8.5).</summary>
    Task<IReadOnlyList<(int CentroCostoId, string TipoCC)>> ListarCentrosClasificadosAsync(CancellationToken ct);
}
