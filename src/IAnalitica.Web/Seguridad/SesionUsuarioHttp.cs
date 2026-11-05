using System.Security.Claims;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure.Identity;

namespace IAnalitica.Web.Seguridad;

/// <summary>Usuario y empresa de la solicitud, tomados SOLO del cookie de sesión (nunca de un parámetro del cliente, §3.4).</summary>
public sealed class SesionUsuarioHttp(IHttpContextAccessor http) : ISesionUsuario
{
    private ClaimsPrincipal? Principal => http.HttpContext?.User;

    public int? UsuarioId => Principal?.Identity?.IsAuthenticated == true && int.TryParse(Principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public int? EmpresaId => int.TryParse(Principal?.FindFirstValue(Reclamos.EmpresaId), out var id) ? id : null;
    public bool EsSuperAdmin => Principal?.HasClaim(Reclamos.EsSuperAdmin, "1") == true;
    public bool TienePermiso(string permiso) => Principal?.HasClaim(Reclamos.Permiso, permiso) == true;
}
