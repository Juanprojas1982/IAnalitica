using System.Security.Claims;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure.Persistencia;

namespace IAnalitica.Api;

public static class Reclamos
{
    public const string EmpresaId = "empresa_id";
    public const string LimitePorMinuto = "limite_minuto";
    public const string AlcanceIngesta = "ingesta";
}

/// <summary>Sesión de la API: la empresa sale del token, nunca del cuerpo de la solicitud (contrato 05 §1).</summary>
internal sealed class SesionApi(IHttpContextAccessor http) : ISesionUsuario
{
    public int? UsuarioId => null;
    public int? EmpresaId => int.TryParse(http.HttpContext?.User.FindFirstValue(Reclamos.EmpresaId), out var e) ? e : null;
    public bool EsSuperAdmin => false;
    public bool TienePermiso(string permiso) => false;
}

/// <summary>
/// Fija el contexto de BD (RLS) de cada solicitud: con token válido, la empresa del token; sin token, solo el modo autenticación
/// (lo único que necesita el endpoint de token para leer <c>seg.ApiClienteEmpresa</c>). Va después de UseAuthentication.
/// </summary>
internal sealed class ContextoApiMiddleware(RequestDelegate siguiente)
{
    public Task InvokeAsync(HttpContext http, IContextoBdAccesor accesor)
    {
        accesor.Establecer(int.TryParse(http.User.FindFirstValue(Reclamos.EmpresaId), out var empresaId)
            ? ContextoBd.DeApi(empresaId)
            : ContextoBd.ParaAutenticar);
        return siguiente(http);
    }
}

/// <summary>La API no muestra URLs de portal; el kit de entrega solo lo arma el portal. Existe porque los handlers de Application se registran todos.</summary>
internal sealed class ConfiguracionPortalApi(IConfiguration configuracion) : IConfiguracionPortal
{
    public string UrlPortal => configuracion["Portal:UrlPortal"] ?? "";
    public string UrlApi => configuracion["Portal:UrlApi"] ?? "";
}
