using Dapper;
using IAnalitica.Application.Abstracciones;
using Microsoft.Data.SqlClient;

namespace IAnalitica.Infrastructure.Ingesta;

/// <summary>
/// Cola en <c>etl.Trabajo</c> (spec 20 §2). Lectura con <c>UPDLOCK, READPAST, ROWLOCK</c>: varios workers toman trabajos distintos.
/// Lease: un trabajo EN_PROCESO con el lease vencido vuelve a estar disponible. Reintentos 1, 2, 4, 8, 16 min; al agotarse, VENENO.
/// </summary>
public sealed class ColaTrabajosSql(Func<CancellationToken, Task<SqlConnection>> abrir) : IColaTrabajos
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);

    public async Task<bool> EncolarAsync(TrabajoPendiente t, CancellationToken ct)
    {
        await using var cn = await abrir(ct).ConfigureAwait(false);
        try
        {
            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT etl.Trabajo (Tipo, EmpresaId, UsuarioId, ClaveExclusion, Prioridad, Payload, TraceParent)
                VALUES (@Tipo, @EmpresaId, @UsuarioId, @ClaveExclusion, @Prioridad, @Payload, @TraceParent)
                """, t, cancellationToken: ct)).ConfigureAwait(false);
            return true;
        }
        catch (SqlException e) when (e.Number is 2601 or 2627)
        {
            return false;   // ya hay uno activo con esa clave: se consolida
        }
    }

    public async Task<TrabajoTomado?> TomarSiguienteAsync(string tipoTrabajo, CancellationToken ct)
    {
        await using var cn = await abrir(ct).ConfigureAwait(false);
        // Un trabajo cuyo lease venció tras agotar sus intentos no se vuelve a tomar: pasa a VENENO.
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE etl.Trabajo SET Estado = 'VENENO', Error = ISNULL(Error, N'Lease vencido sin completar'), LeaseHasta = NULL
            WHERE Tipo = @tipoTrabajo AND Estado = 'EN_PROCESO' AND LeaseHasta < SYSUTCDATETIME() AND Intentos >= MaxIntentos
            """, new { tipoTrabajo }, cancellationToken: ct)).ConfigureAwait(false);

        var fila = await cn.QuerySingleOrDefaultAsync<TrabajoFila>(new CommandDefinition("""
            WITH c AS (
                SELECT TOP (1) * FROM etl.Trabajo WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Tipo = @tipoTrabajo
                  AND ((Estado = 'PENDIENTE' AND DisponibleEn <= DATEADD(SECOND, 1, SYSUTCDATETIME()))   -- datetime2(0) redondea: hasta 0,5 s a futuro
                    OR (Estado = 'EN_PROCESO' AND LeaseHasta < SYSUTCDATETIME() AND Intentos < MaxIntentos))
                ORDER BY Prioridad, DisponibleEn, CreadoEn)
            UPDATE c SET Estado = 'EN_PROCESO', Intentos = Intentos + 1, LeaseHasta = DATEADD(SECOND, @lease, SYSUTCDATETIME())
            OUTPUT inserted.TrabajoId, inserted.Tipo, inserted.EmpresaId, inserted.UsuarioId, inserted.Payload, inserted.Intentos, inserted.MaxIntentos, inserted.TraceParent
            """, new { tipoTrabajo, lease = (int)Lease.TotalSeconds }, cancellationToken: ct)).ConfigureAwait(false);
        return fila is null ? null : new TrabajoTomado(fila.TrabajoId, fila.Tipo, fila.EmpresaId, fila.UsuarioId, fila.Payload, fila.Intentos, fila.MaxIntentos, fila.TraceParent);
    }

    public async Task CompletarAsync(Guid trabajoId, CancellationToken ct)
    {
        await using var cn = await abrir(ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE etl.Trabajo SET Estado = 'OK', LeaseHasta = NULL, Error = NULL WHERE TrabajoId = @trabajoId", new { trabajoId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task FallarAsync(Guid trabajoId, string error, CancellationToken ct)
    {
        await using var cn = await abrir(ct).ConfigureAwait(false);
        // Espera: 2^(intento-1) minutos → 1, 2, 4, 8, 16. Al agotar MaxIntentos queda en VENENO (hay que alertar).
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE etl.Trabajo SET
                Estado = CASE WHEN Intentos >= MaxIntentos THEN 'VENENO' ELSE 'PENDIENTE' END,
                DisponibleEn = CASE WHEN Intentos >= MaxIntentos THEN DisponibleEn ELSE DATEADD(MINUTE, POWER(2, Intentos - 1), SYSUTCDATETIME()) END,
                LeaseHasta = NULL, Error = LEFT(@error, 2000)
            WHERE TrabajoId = @trabajoId
            """, new { trabajoId, error }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task PostergarAsync(Guid trabajoId, TimeSpan espera, CancellationToken ct)
    {
        await using var cn = await abrir(ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE etl.Trabajo SET Estado = 'PENDIENTE', Intentos = CASE WHEN Intentos > 0 THEN Intentos - 1 ELSE 0 END, LeaseHasta = NULL,
                   DisponibleEn = DATEADD(SECOND, @seg, SYSUTCDATETIME())
            WHERE TrabajoId = @trabajoId
            """, new { trabajoId, seg = (int)espera.TotalSeconds }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private sealed record TrabajoFila(Guid TrabajoId, string Tipo, int? EmpresaId, int? UsuarioId, string? Payload, byte Intentos, byte MaxIntentos, string? TraceParent);
}
