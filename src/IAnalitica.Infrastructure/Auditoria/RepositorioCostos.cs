using Dapper;
using IAnalitica.Application.Costos;
using IAnalitica.Infrastructure.Persistencia;

namespace IAnalitica.Infrastructure.Auditoria;

/// <summary>Reglas de distribución de costos con el contexto del usuario (RLS por empresa).</summary>
internal sealed class RepositorioCostos(IFabricaConexiones conexiones) : IRepositorioCostos
{
    public async Task<IReadOnlyList<BaseCosto>> ListarBasesAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<BaseCosto>(new CommandDefinition("SELECT BaseCodigo AS Codigo, Nombre, Origen FROM cfg.CostoBase ORDER BY Origen, Nombre", cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<IReadOnlyList<CentroConRegla>> ListarCentrosAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<CentroConRegla>(new CommandDefinition("""
            SELECT cc.CentroCostoId, cc.CodigoOrigen AS Codigo, cc.Nombre, cc.TipoCC, cc.TipoOrigen, r.Orden, r.BaseCodigo, r.VigenteDesde
            FROM dim.CentroCosto cc
            OUTER APPLY (SELECT TOP (1) Orden, BaseCodigo, VigenteDesde FROM cfg.CostoRegla WHERE CentroCostoId = cc.CentroCostoId ORDER BY VigenteDesde DESC) r
            WHERE cc.Activo = 1
            ORDER BY CASE WHEN r.Orden IS NULL THEN 1 ELSE 0 END, r.Orden, cc.CodigoOrigen
            """, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<bool> ExisteCentroAsync(int centroCostoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT CASE WHEN EXISTS (SELECT 1 FROM dim.CentroCosto WHERE CentroCostoId = @centroCostoId AND Activo = 1) THEN 1 ELSE 0 END",
            new { centroCostoId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task GuardarAsync(int empresaId, ReglaCosto r, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition("""
            MERGE cfg.CostoRegla AS t USING (SELECT @empresaId AS EmpresaId, @CentroCostoId AS CentroCostoId, @VigenteDesde AS VigenteDesde) s
              ON t.EmpresaId = s.EmpresaId AND t.CentroCostoId = s.CentroCostoId AND t.VigenteDesde = s.VigenteDesde
            WHEN MATCHED THEN UPDATE SET Orden = @Orden, BaseCodigo = @BaseCodigo
            WHEN NOT MATCHED THEN INSERT (EmpresaId, CentroCostoId, Orden, BaseCodigo, VigenteDesde) VALUES (@empresaId, @CentroCostoId, @Orden, @BaseCodigo, @VigenteDesde);
            """, new { empresaId, r.CentroCostoId, r.Orden, r.BaseCodigo, r.VigenteDesde }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> QuitarAsync(int centroCostoId, int vigenteDesde, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.ExecuteAsync(new CommandDefinition("DELETE cfg.CostoRegla WHERE CentroCostoId = @centroCostoId AND VigenteDesde = @vigenteDesde", new { centroCostoId, vigenteDesde }, cancellationToken: ct)).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyList<(int CentroCostoId, string TipoCC)>> ListarCentrosClasificadosAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<(int, string)>(new CommandDefinition("SELECT CentroCostoId, TipoCC FROM dim.CentroCosto WHERE Activo = 1 AND TipoCC IN ('ADMINISTRATIVO','APOYO')", cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }
}
