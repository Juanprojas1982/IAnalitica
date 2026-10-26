using IAnalitica.Application.Abstracciones;

namespace IAnalitica.Infrastructure.Persistencia;

internal sealed class AuditoriaAdministrativa(AnaliticaDbContext db) : IAuditoriaAdministrativa
{
    public async Task RegistrarAsync(CambioAdministrativo c, CancellationToken ct)
    {
        db.CambiosAdministrativos.Add(new CambioAdministrativoEntidad
        {
            EmpresaId = c.EmpresaId, UsuarioId = c.UsuarioId, Entidad = c.Entidad, ClaveEntidad = c.ClaveEntidad, Accion = c.Accion,
            ValorAnteriorJson = c.ValorAnteriorJson, ValorNuevoJson = c.ValorNuevoJson, Motivo = c.Motivo,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
