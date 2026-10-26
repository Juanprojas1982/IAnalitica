using System.Security.Claims;
using IAnalitica.Application.Seguridad;
using IAnalitica.Infrastructure.Identity;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Identity;

namespace IAnalitica.Web.Seguridad;

/// <summary>
/// Va ANTES de UseAuthentication: validar el cookie (sello de seguridad) lee seg.Usuario, así que necesita el modo autenticación.
/// Cuando termina la autenticación, <see cref="ContextoBdMiddleware"/> lo reemplaza por el contexto real del usuario.
/// </summary>
public sealed class ContextoAutenticacionMiddleware(RequestDelegate siguiente)
{
    public Task InvokeAsync(HttpContext http, IContextoBdAccesor accesor)
    {
        accesor.Establecer(ContextoBd.ParaAutenticar);
        return siguiente(http);
    }
}

/// <summary>
/// Fija el contexto de base de datos (RLS) desde el cookie. Un anónimo solo puede llegar a las páginas de ingreso,
/// por eso conserva el modo autenticación; un usuario con sesión queda confinado a su empresa (24 §4.2).
/// </summary>
public sealed class ContextoBdMiddleware(RequestDelegate siguiente)
{
    public Task InvokeAsync(HttpContext http, IContextoBdAccesor accesor)
    {
        var p = http.User;
        if (p.Identity?.IsAuthenticated == true && int.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var usuarioId))
        {
            if (p.HasClaim(Reclamos.EsSuperAdmin, "1"))
                accesor.Establecer(ContextoBd.DeSuperadmin(usuarioId));
            else if (int.TryParse(p.FindFirstValue(Reclamos.EmpresaId), out var empresaId))
                accesor.Establecer(ContextoBd.DeEmpresa(empresaId, usuarioId, p.HasClaim(Reclamos.EsAdminPrincipal, "1")));
            else
                accesor.Establecer(ContextoBd.Vacio);   // sesión sin empresa ni superadmin: no ve nada
        }
        else
        {
            accesor.Establecer(ContextoBd.ParaAutenticar);
        }
        return siguiente(http);
    }
}

/// <summary>
/// Antes de ver cualquier dato: cambiar la clave inicial y configurar el doble factor (spec 22 §2). Mientras falte alguno,
/// el usuario solo puede abrir las páginas de /cuenta. Se evalúa contra la base en cada solicitud (un restablecimiento aplica de inmediato).
/// </summary>
public sealed class AccesoPendienteMiddleware(RequestDelegate siguiente)
{
    public static bool EsRutaLibre(PathString ruta)
        => ruta.StartsWithSegments("/cuenta") || ruta.StartsWithSegments("/salir") || ruta.StartsWithSegments("/ingresar")
           || ruta.StartsWithSegments("/_framework") || ruta.StartsWithSegments("/lib") || ruta.StartsWithSegments("/js")
           || ruta.StartsWithSegments("/acceso-denegado") || ruta.StartsWithSegments("/error")
           || ruta.Value is "/app.css" or "/favicon.png" || ruta.Value?.EndsWith(".styles.css", StringComparison.Ordinal) == true;

    public async Task InvokeAsync(HttpContext http, UserManager<UsuarioAplicacion> usuarios, SignInManager<UsuarioAplicacion> sesion, TimeProvider reloj)
    {
        if (http.User.Identity?.IsAuthenticated != true || EsRutaLibre(http.Request.Path))
        {
            await siguiente(http);
            return;
        }

        var u = await usuarios.GetUserAsync(http.User);
        if (u is null)
        {
            await sesion.SignOutAsync();
            http.Response.Redirect("/ingresar");
            return;
        }

        var paso = PoliticaAcceso.Evaluar(
            new DatosAcceso(u.Activo, u.DebeCambiarClave, u.ClaveTemporalVenceEn,
                DobleFactorObligatorio: PoliticaAcceso.DobleFactorObligatorio(u.EsSuperAdmin, u.EsAdminPrincipal, empresaLoExigeATodos: false),
                DobleFactorActivo: u.TwoFactorEnabled, DobleFactorVerificadoEnEstaSesion: true),   // el cookie solo existe después del 2FA
            reloj.GetUtcNow().UtcDateTime);

        switch (paso)
        {
            case PasoAcceso.Permitido:
                await siguiente(http);
                return;
            case PasoAcceso.CambiarClave:
                http.Response.Redirect("/cuenta/cambiar-clave");
                return;
            case PasoAcceso.ConfigurarDobleFactor:
                http.Response.Redirect("/cuenta/doble-factor");
                return;
            default:   // Desactivado, ClaveTemporalVencida
                await sesion.SignOutAsync();
                http.Response.Redirect("/ingresar?aviso=" + (paso == PasoAcceso.Desactivado ? "desactivado" : "clave-vencida"));
                return;
        }
    }
}

/// <summary>Cabeceras de seguridad en todas las respuestas (sin scripts en línea: el CSP los bloquea).</summary>
public sealed class CabecerasSeguridadMiddleware(RequestDelegate siguiente)
{
    public Task InvokeAsync(HttpContext http)
    {
        var h = http.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        // Las pantallas con credenciales (kit de entrega, claves temporales) no se guardan en caché ni en el historial.
        h.CacheControl = "no-store";
        return siguiente(http);
    }
}
