using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure.Persistencia;

namespace IAnalitica.Infrastructure.Ingesta;

internal sealed class RepositorioApiClientes(IFabricaConexiones conexiones, IContextoBdAccesor contexto) : IRepositorioApiClientes
{
    public async Task<ClienteApi?> ObtenerActivoAsync(string clientId, CancellationToken ct)
    {
        // 1. Antes de conocer la empresa solo el modo autenticación puede leer seg.ApiClienteEmpresa (siempre/9000_rls.sql).
        ClienteApi? cliente;
        using (contexto.ParaAutenticacion())
        {
            await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
            cliente = await cn.QuerySingleOrDefaultAsync<ClienteApi>(new CommandDefinition("""
                UPDATE seg.ApiClienteEmpresa SET UltimoUso = SYSUTCDATETIME()
                OUTPUT inserted.ClientId, inserted.EmpresaId, inserted.LimitePorMinuto
                WHERE ClientId = @clientId AND Activo = 1
                """, new { clientId }, cancellationToken: ct)).ConfigureAwait(false);
        }
        if (cliente is null) return null;

        // 2. La empresa debe estar activa. seg.Empresa solo se ve con el contexto de esa empresa: el modo autenticación no la abre.
        var anterior = contexto.Actual;
        contexto.Establecer(ContextoBd.DeApi(cliente.EmpresaId));
        try
        {
            await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
            var activa = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM seg.Empresa WHERE EmpresaId = @EmpresaId AND Activa = 1", new { cliente.EmpresaId }, cancellationToken: ct)).ConfigureAwait(false);
            return activa == 1 ? cliente : null;
        }
        finally { contexto.Establecer(anterior); }
    }
}
