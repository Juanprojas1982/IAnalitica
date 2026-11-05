using Dapper;
using IAnalitica.Application.Facturacion;
using IAnalitica.Infrastructure.Persistencia;

namespace IAnalitica.Infrastructure.Facturacion;

/// <summary>Contactos de facturación de la empresa de la sesión: la RLS de <c>seg.EmpresaContacto</c> limita lectura y escritura a esa empresa.</summary>
internal sealed class RepositorioContactosFacturacion(IFabricaConexiones conexiones) : IRepositorioContactosFacturacion
{
    public async Task<IReadOnlyList<ContactoFacturacionVista>> ListarAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<ContactoFacturacionVista>(new CommandDefinition(
            "SELECT ContactoId, Tipo, Nombre, Email FROM seg.EmpresaContacto WHERE Tipo IN ('FACTURACION', 'CONTADOR') ORDER BY Tipo, ContactoId", cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<int> GuardarAsync(int empresaId, string tipo, string nombre, string email, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT seg.EmpresaContacto (EmpresaId, Tipo, Nombre, Email) VALUES (@empresaId, @tipo, @nombre, @email);
            SELECT CAST(SCOPE_IDENTITY() AS int)
            """, new { empresaId, tipo, nombre, email }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> QuitarAsync(int contactoId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.ExecuteAsync(new CommandDefinition("DELETE seg.EmpresaContacto WHERE ContactoId = @contactoId AND Tipo IN ('FACTURACION', 'CONTADOR')", new { contactoId }, cancellationToken: ct)).ConfigureAwait(false) == 1;
    }
}
