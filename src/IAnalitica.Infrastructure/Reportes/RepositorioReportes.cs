using System.Diagnostics;
using Dapper;
using IAnalitica.Application.Reportes;
using IAnalitica.Infrastructure.Persistencia;

namespace IAnalitica.Infrastructure.Reportes;

/// <summary>Solicitudes de reporte con el contexto del usuario (RLS por empresa). El archivo nunca pasa por aquí: solo su ruta.</summary>
internal sealed class RepositorioReportes(IFabricaConexiones conexiones) : IRepositorioReportes
{
    private const string Columnas = """
        SolicitudId, EmpresaId, UsuarioId, TipoReporte, Formato, ParametrosJson, Estado, SolicitadoEn, TerminadoEn, ExpiraEn, BlobRuta, TamanoBytes, VersionCarga, Error
        """;

    public async Task<int> ContarActivasAsync(int usuarioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM rpt.SolicitudReporte WHERE UsuarioId = @usuarioId AND Estado IN ('PENDIENTE', 'EN_PROCESO')", new { usuarioId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<SolicitudReporte?> BuscarReutilizableAsync(int usuarioId, string hash, long versionCarga, int horas, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.QueryFirstOrDefaultAsync<SolicitudReporte>(new CommandDefinition($"""
            SELECT TOP (1) {Columnas} FROM rpt.SolicitudReporte
            WHERE UsuarioId = @usuarioId AND HashParametros = @hash AND Estado = 'LISTO' AND VersionCarga = @versionCarga
              AND TerminadoEn > DATEADD(HOUR, -@horas, SYSUTCDATETIME()) AND (ExpiraEn IS NULL OR ExpiraEn > SYSUTCDATETIME())
            ORDER BY TerminadoEn DESC
            """, new { usuarioId, hash, versionCarga, horas }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<Guid> CrearAsync(int empresaId, int usuarioId, NuevaSolicitud s, long versionCarga, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await using var tx = await cn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var id = await cn.ExecuteScalarAsync<Guid>(new CommandDefinition("""
            INSERT rpt.SolicitudReporte (EmpresaId, UsuarioId, TipoReporte, Formato, ParametrosJson, HashParametros, VersionCarga)
            OUTPUT inserted.SolicitudId
            VALUES (@empresaId, @usuarioId, @TipoReporte, @Formato, @ParametrosJson, @HashParametros, @versionCarga)
            """, new { empresaId, usuarioId, s.TipoReporte, s.Formato, s.ParametrosJson, s.HashParametros, versionCarga }, tx, cancellationToken: ct)).ConfigureAwait(false);
        // Outbox: el trabajo nace en la misma transacción que la solicitud. Las exportaciones van detrás de catálogos y asientos (prioridad 8).
        await cn.ExecuteAsync(new CommandDefinition("""
            INSERT etl.Trabajo (Tipo, EmpresaId, UsuarioId, ClaveExclusion, Prioridad, Payload, TraceParent)
            VALUES ('GENERAR_REPORTE', @empresaId, @usuarioId, @clave, 8, @payload, @trace)
            """, new { empresaId, usuarioId, clave = $"REP:{id}", payload = $"{{\"solicitudId\":\"{id}\"}}", trace = Activity.Current?.Id }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return id;
    }

    public async Task<int> PosicionEnColaAsync(Guid solicitudId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM rpt.SolicitudReporte
            WHERE Estado IN ('PENDIENTE', 'EN_PROCESO') AND SolicitadoEn <= (SELECT SolicitadoEn FROM rpt.SolicitudReporte WHERE SolicitudId = @solicitudId)
            """, new { solicitudId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SolicitudReporte>> ListarAsync(int usuarioId, bool todasLasDeLaEmpresa, int maximo, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<SolicitudReporte>(new CommandDefinition($"""
            SELECT TOP (@maximo) {Columnas} FROM rpt.SolicitudReporte
            WHERE (@todas = 1 OR UsuarioId = @usuarioId) ORDER BY SolicitadoEn DESC
            """, new { usuarioId, todas = todasLasDeLaEmpresa, maximo }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<SolicitudReporte?> ObtenerAsync(Guid solicitudId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.QuerySingleOrDefaultAsync<SolicitudReporte>(new CommandDefinition(
            $"SELECT {Columnas} FROM rpt.SolicitudReporte WHERE SolicitudId = @solicitudId", new { solicitudId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> CancelarAsync(Guid solicitudId, int usuarioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await using var tx = await cn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var n = await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE rpt.SolicitudReporte SET Estado = 'CANCELADO', TerminadoEn = SYSUTCDATETIME()
            WHERE SolicitudId = @solicitudId AND UsuarioId = @usuarioId AND Estado = 'PENDIENTE'
            """, new { solicitudId, usuarioId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (n > 0)   // el trabajo, si nadie lo tomó aún, ya no hace falta
            await cn.ExecuteAsync(new CommandDefinition("UPDATE etl.Trabajo SET Estado = 'OK' WHERE ClaveExclusion = @clave AND Estado = 'PENDIENTE'", new { clave = $"REP:{solicitudId}" }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return n > 0;
    }

    public async Task RegistrarDescargaAsync(int usuarioId, int empresaId, Guid solicitudId, string? ip, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition(
            "INSERT aud.Acceso (UsuarioId, EmpresaId, Accion, Detalle, Ip) VALUES (@usuarioId, @empresaId, 'DESCARGA_REPORTE', @detalle, @ip)",
            new { usuarioId, empresaId, detalle = solicitudId.ToString(), ip = ip?[..Math.Min(ip.Length, 45)] }, cancellationToken: ct)).ConfigureAwait(false);
    }
}
