namespace IAnalitica.Application.Abstracciones;

public static class TiposTrabajo
{
    public const string ProcesarCarga = "PROCESAR_CARGA";
    public const string CalcularCartera = "CALCULAR_CARTERA";
    public const string GenerarReporte = "GENERAR_REPORTE";
    public const string AuditarConfig = "AUDITAR_CONFIG";
    public const string ValidarPrecios = "VALIDAR_PRECIOS";
}

/// <summary>Trabajo por encolar. El payload lleva solo identificadores, nunca datos (spec 20 §2).</summary>
public sealed record TrabajoPendiente(
    string Tipo, int? EmpresaId, string ClaveExclusion, string? Payload = null, int? UsuarioId = null, byte Prioridad = 5, string? TraceParent = null);

public sealed record TrabajoTomado(
    Guid TrabajoId, string Tipo, int? EmpresaId, int? UsuarioId, string? Payload, byte Intentos, byte MaxIntentos, string? TraceParent)
{
    public bool EsUltimoIntento => Intentos >= MaxIntentos;
}

/// <summary>
/// Cola de trabajos (spec 20 §2, D23). Pilotos: SQL (<c>etl.Trabajo</c>). Con Storage Queues solo previa aprobación (D32).
/// </summary>
public interface IColaTrabajos
{
    /// <summary>Devuelve false si ya hay un trabajo activo con la misma clave de exclusión (se consolida).</summary>
    Task<bool> EncolarAsync(TrabajoPendiente trabajo, CancellationToken ct);

    /// <summary>Toma el siguiente disponible con lease; null si no hay.</summary>
    Task<TrabajoTomado?> TomarSiguienteAsync(string tipoTrabajo, CancellationToken ct);

    Task CompletarAsync(Guid trabajoId, CancellationToken ct);

    /// <summary>Reintenta con espera exponencial (1, 2, 4, 8, 16 min) o pasa a VENENO al agotar los intentos.</summary>
    Task FallarAsync(Guid trabajoId, string error, CancellationToken ct);

    /// <summary>Devuelve el trabajo a la cola sin consumir un intento (p. ej. otra carga de la misma entidad está en curso).</summary>
    Task PostergarAsync(Guid trabajoId, TimeSpan espera, CancellationToken ct);
}
