using System.Security.Claims;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IAnalitica.Infrastructure.Identity;

public static class Reclamos
{
    public const string EmpresaId = "empresa_id";
    public const string EsSuperAdmin = "es_superadmin";
    public const string EsAdminPrincipal = "es_admin_principal";
    public const string Permiso = "permiso";
}

/// <summary>Agrega al cookie la empresa, las banderas de administración y los permisos de los roles (seg.RolPermiso).</summary>
public sealed class ClaimsFactory(
    UserManager<UsuarioAplicacion> gestor, RoleManager<RolAplicacion> roles, IOptions<IdentityOptions> opciones, AnaliticaDbContext db)
    : UserClaimsPrincipalFactory<UsuarioAplicacion, RolAplicacion>(gestor, roles, opciones)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(UsuarioAplicacion user)
    {
        var identidad = await base.GenerateClaimsAsync(user).ConfigureAwait(false);
        if (user.EmpresaId is { } empresa) identidad.AddClaim(new Claim(Reclamos.EmpresaId, empresa.ToString()));
        if (user.EsSuperAdmin) identidad.AddClaim(new Claim(Reclamos.EsSuperAdmin, "1"));
        if (user.EsAdminPrincipal) identidad.AddClaim(new Claim(Reclamos.EsAdminPrincipal, "1"));

        var nombresRol = await UserManager.GetRolesAsync(user).ConfigureAwait(false);
        var normalizados = nombresRol.Select(r => r.ToUpperInvariant()).ToArray();
        var permisos = await db.RolPermisos.AsNoTracking()
            .Join(db.Roles, p => p.RolId, r => r.Id, (p, r) => new { p.Permiso, r.NormalizedName })
            .Where(x => normalizados.Contains(x.NormalizedName!))
            .Select(x => x.Permiso).Distinct().ToListAsync().ConfigureAwait(false);
        foreach (var permiso in permisos) identidad.AddClaim(new Claim(Reclamos.Permiso, permiso));
        return identidad;
    }
}
