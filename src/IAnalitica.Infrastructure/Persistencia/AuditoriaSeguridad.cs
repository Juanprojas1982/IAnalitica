using IAnalitica.Application.Abstracciones;

namespace IAnalitica.Infrastructure.Persistencia;

internal sealed class AuditoriaSeguridad(AnaliticaDbContext db) : IAuditoriaSeguridad
{
    public async Task RegistrarAsync(string evento, int? empresaId, int? usuarioId, string? nombreUsuario, string? detalle, string? ip, CancellationToken ct)
    {
        db.EventosSeguridad.Add(new SeguridadEventoEntidad
        {
            Evento = evento, EmpresaId = empresaId, UsuarioId = usuarioId, NombreUsuario = nombreUsuario,
            Detalle = detalle is { Length: > 400 } ? detalle[..400] : detalle, Ip = ip,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
