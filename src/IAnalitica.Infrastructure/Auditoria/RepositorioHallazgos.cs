using Dapper;
using IAnalitica.Application.Auditoria;
using IAnalitica.Infrastructure.Persistencia;

namespace IAnalitica.Infrastructure.Auditoria;

/// <summary>Hallazgos del auditor con el contexto del usuario (RLS por empresa).</summary>
internal sealed class RepositorioHallazgos(IFabricaConexiones conexiones) : IRepositorioHallazgos
{
    private const string Seleccion = """
        SELECT h.HallazgoId, h.Codigo, r.Area, r.Titulo, h.Severidad, h.Estado, h.ClaveEntidad, h.NombreEntidad, h.ValorEncontrado, h.ValorEsperado,
               h.ImpactoCantidad, h.ImpactoValor, h.ImpactoDetalle, r.PorQueImporta, r.ComoCorregir, r.RutaSaludIPS, r.PorVerificar,
               h.Justificacion, h.PrimeraDeteccion, h.UltimaDeteccion
        FROM fact.HallazgoConfiguracion h JOIN cfg.ReglaConfiguracion r ON r.Codigo = h.Codigo
        """;

    public async Task<SaludConfiguracion> ObtenerAsync(bool incluirCerrados, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        // Prioridad: severidad, luego impacto en $ y en cantidad (spec 16 §5)
        var lista = (await cn.QueryAsync<HallazgoVista>(new CommandDefinition($"""
            {Seleccion}
            WHERE (@incluirCerrados = 1 OR h.Estado IN ('NUEVO','ABIERTO'))
            ORDER BY CASE h.Severidad WHEN 'CRITICA' THEN 0 WHEN 'ALTA' THEN 1 WHEN 'MEDIA' THEN 2 ELSE 3 END,
                     CASE WHEN h.Estado IN ('NUEVO','ABIERTO') THEN 0 ELSE 1 END, h.ImpactoValor DESC, h.ImpactoCantidad DESC, h.HallazgoId
            """, new { incluirCerrados }, cancellationToken: ct)).ConfigureAwait(false)).ToList();

        var indice = (await cn.QueryAsync<(DateTime Fecha, string Area, int Indice, int Abiertos)>(new CommandDefinition("""
            SELECT Fecha, Area, Indice, Abiertos FROM fact.IndiceSaludConfiguracion
            WHERE Fecha IN (SELECT DISTINCT TOP (2) Fecha FROM fact.IndiceSaludConfiguracion ORDER BY Fecha DESC)
            """, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var ultima = indice.Count == 0 ? (DateTime?)null : indice.Max(x => x.Fecha);
        var areas = indice.Where(x => x.Fecha == ultima)
            .Select(x => new IndiceArea(x.Area, x.Indice, x.Abiertos, indice.Where(y => y.Fecha < ultima && y.Area == x.Area).Select(y => (int?)y.Indice).FirstOrDefault()))
            .OrderBy(a => a.Area == "TOTAL" ? 0 : 1).ThenBy(a => a.Indice).ToList();
        return new SaludConfiguracion(ultima is null ? null : DateOnly.FromDateTime(ultima.Value), areas, lista);
    }

    public async Task<IReadOnlyList<HallazgoTarifarioVista>> ObtenerTarifariosAsync(bool incluirCerrados, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<HallazgoTarifarioVista>(new CommandDefinition("""
            SELECT h.HallazgoId, h.Regla, h.Severidad, h.Estado, h.ClaveEntidad, h.NombreEntidad, h.Cum, h.ValorObservado, h.ValorLimite, h.ExcesoUnitario, h.UnidadesAfectadas, h.MontoEnRiesgo,
                   c.Numero AS CircularNumero, c.VigenteDesde AS CircularVigenteDesde, h.FactorValidado, h.Detalle, h.Justificacion, h.DetectadoEn
            FROM fact.HallazgoTarifario h LEFT JOIN reg.Circular c ON c.CircularId = h.CircularId
            WHERE (@incluirCerrados = 1 OR h.Estado IN ('NUEVO','ABIERTO'))
            ORDER BY CASE h.Severidad WHEN 'CRITICA' THEN 0 WHEN 'ALTA' THEN 1 WHEN 'MEDIA' THEN 2 ELSE 3 END,
                     CASE WHEN h.Estado IN ('NUEVO','ABIERTO') THEN 0 ELSE 1 END, h.MontoEnRiesgo DESC, h.ExcesoUnitario DESC, h.HallazgoId
            """, new { incluirCerrados }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    // El nombre de la tabla sale de este enum, nunca de texto externo
    private static string Tabla(OrigenHallazgo o) => o == OrigenHallazgo.Tarifario ? "fact.HallazgoTarifario" : "fact.HallazgoConfiguracion";

    public async Task<EstadoDeHallazgo?> ObtenerEstadoAsync(OrigenHallazgo origen, long hallazgoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        var codigo = origen == OrigenHallazgo.Tarifario ? "Regla" : "Codigo";
        return await cn.QueryFirstOrDefaultAsync<EstadoDeHallazgo>(new CommandDefinition(
            $"SELECT HallazgoId, {codigo} AS Codigo, ClaveEntidad, Estado FROM {Tabla(origen)} WHERE HallazgoId = @hallazgoId", new { hallazgoId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> CambiarEstadoAsync(OrigenHallazgo origen, long hallazgoId, string estadoNuevo, string? justificacion, int usuarioId, IReadOnlyCollection<string> estadosPermitidos, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        var cerrando = estadoNuevo is EstadosHallazgo.Aceptado or EstadosHallazgo.FalsoPositivo;
        var n = await cn.ExecuteAsync(new CommandDefinition($"""
            UPDATE {Tabla(origen)}
            SET Estado = @estadoNuevo, Justificacion = @justificacion,
                CerradoEn = CASE WHEN @cerrando = 1 THEN SYSUTCDATETIME() END, CerradoPor = CASE WHEN @cerrando = 1 THEN @usuarioId END
            WHERE HallazgoId = @hallazgoId AND Estado IN @estadosPermitidos
            """, new { hallazgoId, estadoNuevo, justificacion, usuarioId, cerrando, estadosPermitidos = estadosPermitidos.ToArray() }, cancellationToken: ct)).ConfigureAwait(false);
        return n > 0;
    }
}
