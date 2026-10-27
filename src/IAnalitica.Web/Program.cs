using System.Threading.RateLimiting;
using IAnalitica.Application;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Semantica;
using IAnalitica.Application.Empresas;
using IAnalitica.Application.Mediacion;
using IAnalitica.Application.Usuarios;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Identity;
using IAnalitica.Infrastructure.Observabilidad;
using IAnalitica.Modulos.Auditor;
using IAnalitica.Modulos.Ese;
using IAnalitica.Modulos.Farmacia;
using IAnalitica.Modulos.Financiero;
using IAnalitica.Modulos.Ingreso;
using IAnalitica.Modulos.Operacion;
using IAnalitica.Web.Components;
using IAnalitica.Web.Components.Pages.Admin;
using IAnalitica.Web.Consultas;
using IAnalitica.Web.Seguridad;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

// Secretos: User Secrets en local, Key Vault en Azure. Jamás en appsettings ni en el repositorio (24 §4.4).
var cadena = builder.Configuration.GetConnectionString("App")
             ?? throw new InvalidOperationException("Falta ConnectionStrings:App (usuario de rol_app). Configúrela con User Secrets o variables de entorno.");

builder.Services.AddHttpContextAccessor();
builder.Services.AddInfrastructure(cadena);
builder.Services.AddApplication(ModuloFinanciero.Ensamblado, ModuloIngreso.Ensamblado, ModuloOperacion.Ensamblado,
    ModuloFarmacia.Ensamblado, ModuloEse.Ensamblado, ModuloAuditor.Ensamblado);
builder.Services.Configure<IAnalitica.Application.Regulacion.MapeoPreciosMaximos>(builder.Configuration.GetSection("Regulacion:PreciosMaximos:Mapeo"));
builder.Services.Configure<IAnalitica.Application.Reportes.LimitesReportes>(builder.Configuration.GetSection("Reportes:Limites"));
builder.Services.Configure<IAnalitica.Infrastructure.Reportes.OpcionesAlmacenReportes>(builder.Configuration.GetSection("Reportes:Almacen"));
builder.Services.AddCapaSemantica(new[] { ModuloFinanciero.Modulo }.Concat(ModuloIngreso.Modulos));
builder.Services.AddScoped<ISesionUsuario, SesionUsuarioHttp>();
builder.Services.AddScoped<IConfiguracionPortal, ConfiguracionPortalHttp>();

builder.Services.AddObservabilidad("IAnalitica.Web", builder.Configuration.GetSection("Observabilidad").Get<OpcionesObservabilidad>() ?? new());

// Llaves de protección (cookie, tokens): en una carpeta configurada; sin configurar, efímeras (solo desarrollo).
if (builder.Configuration["DataProtection:Ruta"] is { Length: > 0 } rutaLlaves)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(rutaLlaves)).SetApplicationName("IAnalitica");

// Sesión: cookie de Identity, inactiva 30 min (24 §4.1); el sello de seguridad se revalida cada minuto (desactivación casi inmediata).
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = ".ianalitica.sesion";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    o.SlidingExpiration = true;
    o.LoginPath = "/ingresar";
    o.AccessDeniedPath = "/acceso-denegado";
});
builder.Services.Configure<SecurityStampValidatorOptions>(o =>
    o.ValidationInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("Seguridad:IntervaloValidacionSelloSegundos", 60)));

// Un permiso = una política (spec 22; seg.RolPermiso). Los permisos viajan como reclamos «permiso» en el cookie.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("admin.empresas", p => p.RequireClaim(Reclamos.Permiso, "admin.empresas"))
    .AddPolicy("admin.usuarios", p => p.RequireClaim(Reclamos.Permiso, "admin.usuarios"))
    .AddPolicy("reportes.exportar", p => p.RequireClaim(Reclamos.Permiso, "reportes.exportar"))
    .AddPolicy("dash.auditor_config", p => p.RequireClaim(Reclamos.Permiso, "dash.auditor_config"))
    .AddPolicy("admin.catalogos", p => p.RequireClaim(Reclamos.Permiso, "admin.catalogos"))
    .AddPolicy("dash.farmacia", p => p.RequireClaim(Reclamos.Permiso, "dash.farmacia"))
    .AddPolicy("hallazgos.gestionar", p => p.RequireClaim(Reclamos.Permiso, "hallazgos.gestionar"));
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // [S] 20 solicitudes por minuto y por IP en las páginas de ingreso (el bloqueo por usuario ya lo da Identity: 5 intentos).
    o.AddPolicy("ingreso", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "desconocida",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("Seguridad:LimiteIngresoPorMinuto", 20), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddRazorComponents();   // F0: páginas estáticas del servidor. El render InteractiveAuto llega con los dashboards (F3).

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseMiddleware<CabecerasSeguridadMiddleware>();
app.MapStaticAssets();

app.UseRateLimiter();
app.UseMiddleware<ContextoAutenticacionMiddleware>();   // para validar el cookie bajo RLS
app.UseAuthentication();
app.UseMiddleware<ContextoBdMiddleware>();              // contexto real del usuario
app.UseAuthorization();
app.UseMiddleware<AccesoPendienteMiddleware>();
app.UseAntiforgery();

app.MapPost("/salir", async (HttpContext http, SignInManager<UsuarioAplicacion> sesion, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(http);
    await sesion.SignOutAsync();
    return Results.Redirect("/ingresar");
}).RequireAuthorization();

// Acciones sobre usuarios de la propia empresa (spec 22 §5). El EmpresaId sale de la sesión, nunca del formulario.
var usuarios = app.MapGroup("/admin/usuarios").RequireAuthorization("admin.usuarios");
usuarios.MapPost("/{id:int}/estado", async (int id, bool activo, ISender enviar, IAntiforgery af, HttpContext http, CancellationToken ct) =>
{
    await af.ValidateRequestAsync(http);
    var r = await enviar.Send(new CambiarEstadoUsuarioCommand(id, activo), ct);
    return Results.Redirect(r.EsExitoso ? "/admin/usuarios" : "/admin/usuarios?error=" + Uri.EscapeDataString(r.Errores[0].Mensaje));
});
usuarios.MapPost("/{id:int}/restablecer", async (int id, ISender enviar, IAntiforgery af, HttpContext http, CancellationToken ct) =>
{
    await af.ValidateRequestAsync(http);
    var r = await enviar.Send(new RestablecerClaveUsuarioCommand(id), ct);
    return r.EsExitoso
        ? (IResult)new RazorComponentResult<CredencialEntregada>(new { Credencial = r.Valor, Titulo = "Contraseña restablecida" })
        : Results.Redirect("/admin/usuarios?error=" + Uri.EscapeDataString(r.Errores[0].Mensaje));
});

app.MapConsultasQ();
app.MapReportes();
app.MapAuditor();
app.MapRegulacion();

app.MapRazorComponents<App>();

app.Run();

/// <summary>Necesario para las pruebas de integración (WebApplicationFactory).</summary>
public partial class Program;
