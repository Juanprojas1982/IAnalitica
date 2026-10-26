using IAnalitica.Ingesta.Contracts;

namespace IAnalitica.Application.Ingesta;

public sealed record NuevaCarga(string Entidad, string Modo, DateOnly? VentanaDesde, DateOnly? VentanaHasta, DateOnly? Corte,
    string? VersionAgente, string? VersionFuente, string? TraceParent);

public sealed record DatosCarga(long CargaId, int EmpresaId, string Entidad, string Modo, string Estado, string? Error = null);

public enum ResultadoCierre { Encolada, Duplicada, ControlNoCuadra, NoAbierta }

public sealed record RespuestaCierre(ResultadoCierre Resultado, string? Detalle = null);

/// <summary>
/// Acceso a cargas y lotes. Corre con <c>rol_app</c> y el contexto del token (RLS): una carga de otra empresa no existe para el cliente.
/// </summary>
public interface IRepositorioCargas
{
    Task<long> CrearAsync(int empresaId, NuevaCarga carga, CancellationToken ct);
    Task<DatosCarga?> ObtenerAsync(long cargaId, CancellationToken ct);
    Task<bool> PeriodoCerradoAsync(int empresaId, DateOnly desde, DateOnly hasta, CancellationToken ct);

    /// <summary>Guarda el lote en staging. Idempotente por (carga, número): reenviar el mismo número lo reemplaza.</summary>
    Task GuardarLoteAsync(int empresaId, long cargaId, string entidad, int numero, int filas, string filasJson, CancellationToken ct);

    /// <summary>Marca la carga como RECHAZADA (violación de reglas de privacidad u otra falla que invalida toda la carga).</summary>
    Task RechazarAsync(long cargaId, string motivo, CancellationToken ct);

    /// <summary>
    /// Verifica totales y suma de control, calcula el hash, detecta duplicados y, si todo cuadra, deja la carga CERRADA y el trabajo
    /// PROCESAR_CARGA en la cola en la misma transacción (outbox).
    /// </summary>
    Task<RespuestaCierre> CerrarAsync(int empresaId, DatosCarga carga, CierreCarga cierre, string[] columnasSumaControl, string? traceParent, CancellationToken ct);

    Task<EstadoCarga?> EstadoCargaAsync(long cargaId, CancellationToken ct);
    Task<IReadOnlyList<RechazoFila>> RechazosAsync(long cargaId, int maximo, CancellationToken ct);
    Task<EstadoPlataforma> EstadoPlataformaAsync(int empresaId, CancellationToken ct);
}
