using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IAnalitica.Infrastructure.Identity;

internal sealed class GestorUsuarios(UserManager<UsuarioAplicacion> gestor, AnaliticaDbContext db, TimeProvider reloj) : IGestorUsuarios
{
    public Task<bool> ExisteNombreAsync(string nombreUsuario, CancellationToken ct)
        => db.Users.AsNoTracking().AnyAsync(u => u.NormalizedUserName == nombreUsuario.ToUpperInvariant(), ct);

    public async Task<Result<int>> CrearAdministradorPrincipalAsync(AdministradorNuevo d, CancellationToken ct)
    {
        var usuario = new UsuarioAplicacion
        {
            UserName = d.NombreUsuario, Email = d.Email, NombreCompleto = d.NombreCompleto, EmpresaId = d.EmpresaId,
            EsAdminPrincipal = true, EsSuperAdmin = false, Activo = true,
            DebeCambiarClave = true, ClaveTemporalVenceEn = d.ClaveVenceEnUtc,
            CreadoEn = reloj.GetUtcNow().UtcDateTime, CreadoPor = d.CreadoPor,
            LockoutEnabled = true,
        };

        var creado = await gestor.CreateAsync(usuario, d.ClaveTemporal).ConfigureAwait(false);
        if (!creado.Succeeded) return Result<int>.Falla(Traducir(creado));

        var rol = await gestor.AddToRoleAsync(usuario, Roles.AdminEmpresa).ConfigureAwait(false);
        if (!rol.Succeeded) return Result<int>.Falla(Traducir(rol));

        return Result<int>.Ok(usuario.Id);
    }

    internal static IEnumerable<Error> Traducir(IdentityResult resultado)
        => resultado.Errors.Select(e => Error.Validacion(e.Code, e.Description));
}
