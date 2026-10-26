using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IAnalitica.Infrastructure.Identity;

/// <summary>
/// Usuarios de una empresa. Todas las consultas filtran por EmpresaId además de la RLS (doble defensa, 24 §4.2).
/// No se borran usuarios: se desactivan para conservar la auditoría (spec 22 §5).
/// </summary>
internal sealed class GestionUsuariosEmpresa(UserManager<UsuarioAplicacion> gestor, AnaliticaDbContext db, TimeProvider reloj) : IGestionUsuariosEmpresa
{
    private const string ProveedorAlmacen = "[AspNetUserStore]";
    private const string NombreClaveAutenticador = "AuthenticatorKey";

    public async Task<UsuarioResumen?> ObtenerAsync(int empresaId, int usuarioId, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == usuarioId && x.EmpresaId == empresaId, ct);
        return u is null ? null : await ResumirAsync(u, ct);
    }

    public async Task<UsuarioResumen?> ObtenerAdminPrincipalAsync(int empresaId, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.EmpresaId == empresaId && x.EsAdminPrincipal, ct);
        return u is null ? null : await ResumirAsync(u, ct);
    }

    public async Task<IReadOnlyList<UsuarioResumen>> ListarAsync(int empresaId, CancellationToken ct)
    {
        var usuarios = await db.Users.AsNoTracking().Where(x => x.EmpresaId == empresaId).OrderBy(x => x.UserName).ToListAsync(ct);
        var roles = await (from ur in db.UserRoles
                           join r in db.Roles on ur.RoleId equals r.Id
                           join u in db.Users on ur.UserId equals u.Id
                           where u.EmpresaId == empresaId
                           select new { ur.UserId, r.Name }).AsNoTracking().ToListAsync(ct);
        return usuarios.Select(u => Resumen(u, roles.Where(r => r.UserId == u.Id).Select(r => r.Name!).OrderBy(n => n).ToArray())).ToArray();
    }

    public async Task<Result<int>> CrearAsync(UsuarioNuevo d, CancellationToken ct)
    {
        // Segunda barrera (la primera es el validador y la tercera, el índice único): nunca se asigna ADMIN_EMPRESA ni SUPERADMIN.
        if (d.Roles.Any(r => !Roles.Asignables.Contains(r)))
            return Result<int>.Falla(Error.Conflicto("ROL_NO_ASIGNABLE", "Ese rol no se puede asignar."));

        var usuario = new UsuarioAplicacion
        {
            UserName = d.NombreUsuario, Email = d.Email, NombreCompleto = d.NombreCompleto, EmpresaId = d.EmpresaId,
            EsAdminPrincipal = false, EsSuperAdmin = false, Activo = true, LockoutEnabled = true,
            DebeCambiarClave = true, ClaveTemporalVenceEn = d.ClaveVenceEnUtc, CreadoEn = reloj.GetUtcNow().UtcDateTime, CreadoPor = d.CreadoPor,
        };
        var creado = await gestor.CreateAsync(usuario, d.ClaveTemporal);
        if (!creado.Succeeded) return Result<int>.Falla(GestorUsuarios.Traducir(creado));
        var rol = await gestor.AddToRolesAsync(usuario, d.Roles);
        return rol.Succeeded ? Result<int>.Ok(usuario.Id) : Result<int>.Falla(GestorUsuarios.Traducir(rol));
    }

    public async Task<Result> EditarAsync(int empresaId, int usuarioId, string email, string nombreCompleto, IReadOnlyList<string> roles, CancellationToken ct)
    {
        if (roles.Any(r => !Roles.Asignables.Contains(r))) return Result.Falla(Error.Conflicto("ROL_NO_ASIGNABLE", "Ese rol no se puede asignar."));
        var u = await Buscar(empresaId, usuarioId, ct);
        if (u is null) return Result.Falla(Error.NoEncontrado("El usuario no existe en su empresa."));
        if (u.EsAdminPrincipal) return Result.Falla(Error.Conflicto("ADMIN_PRINCIPAL_PROTEGIDO", "El administrador de la empresa solo lo modifica el superadmin."));

        u.Email = email;
        u.NombreCompleto = nombreCompleto;
        var r = await gestor.UpdateAsync(u);
        if (!r.Succeeded) return Result.Falla(GestorUsuarios.Traducir(r));

        var actuales = await gestor.GetRolesAsync(u);
        var quitar = actuales.Except(roles).ToArray();
        var agregar = roles.Except(actuales).ToArray();
        if (quitar.Length > 0) { var q = await gestor.RemoveFromRolesAsync(u, quitar); if (!q.Succeeded) return Result.Falla(GestorUsuarios.Traducir(q)); }
        if (agregar.Length > 0) { var a = await gestor.AddToRolesAsync(u, agregar); if (!a.Succeeded) return Result.Falla(GestorUsuarios.Traducir(a)); }
        if (quitar.Length + agregar.Length > 0) await gestor.UpdateSecurityStampAsync(u);   // los permisos nuevos aplican en la sesión abierta
        return Result.Ok();
    }

    public async Task<Result> CambiarActivoAsync(int empresaId, int usuarioId, bool activo, CancellationToken ct)
    {
        var u = await Buscar(empresaId, usuarioId, ct);
        if (u is null) return Result.Falla(Error.NoEncontrado("El usuario no existe en su empresa."));
        if (u.EsAdminPrincipal) return Result.Falla(Error.Conflicto("ADMIN_PRINCIPAL_PROTEGIDO", "El administrador de la empresa no se puede desactivar desde la empresa."));

        u.Activo = activo;
        var r = await gestor.UpdateAsync(u);
        if (!r.Succeeded) return Result.Falla(GestorUsuarios.Traducir(r));
        // Cambiar el sello cierra las sesiones abiertas en el siguiente chequeo del cookie (cada 1 min, 24 §4.1).
        await gestor.UpdateSecurityStampAsync(u);
        return Result.Ok();
    }

    public async Task<Result> RestablecerClaveAsync(int empresaId, int usuarioId, string claveTemporal, DateTime venceEnUtc, bool quitarDobleFactor, CancellationToken ct)
    {
        var u = await Buscar(empresaId, usuarioId, ct);
        if (u is null) return Result.Falla(Error.NoEncontrado("El usuario no existe en su empresa."));

        var token = await gestor.GeneratePasswordResetTokenAsync(u);
        var r = await gestor.ResetPasswordAsync(u, token, claveTemporal);
        if (!r.Succeeded) return Result.Falla(GestorUsuarios.Traducir(r));

        u.DebeCambiarClave = true;
        u.ClaveTemporalVenceEn = venceEnUtc;
        var actualizado = await gestor.UpdateAsync(u);
        if (!actualizado.Succeeded) return Result.Falla(GestorUsuarios.Traducir(actualizado));

        await gestor.ResetAccessFailedCountAsync(u);
        await gestor.SetLockoutEndDateAsync(u, null);
        if (quitarDobleFactor)
        {
            await gestor.SetTwoFactorEnabledAsync(u, false);
            await gestor.RemoveAuthenticationTokenAsync(u, ProveedorAlmacen, NombreClaveAutenticador);   // la nueva persona configura su propia app
        }
        await gestor.UpdateSecurityStampAsync(u);
        return Result.Ok();
    }

    private Task<UsuarioAplicacion?> Buscar(int empresaId, int usuarioId, CancellationToken ct)
        => db.Users.FirstOrDefaultAsync(x => x.Id == usuarioId && x.EmpresaId == empresaId, ct);

    private async Task<UsuarioResumen> ResumirAsync(UsuarioAplicacion u, CancellationToken ct)
    {
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == u.Id orderby r.Name select r.Name!)
            .AsNoTracking().ToArrayAsync(ct);
        return Resumen(u, roles);
    }

    private static UsuarioResumen Resumen(UsuarioAplicacion u, IReadOnlyList<string> roles)
        => new(u.Id, u.UserName!, u.Email ?? "", u.NombreCompleto, roles, u.Activo, u.DebeCambiarClave, u.TwoFactorEnabled, u.EsAdminPrincipal);
}
