using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using IAnalitica.Api;
using IAnalitica.Application;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Semantica;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Observabilidad;
using IAnalitica.Ingesta.Contracts;
using IAnalitica.Modulos.Auditor;
using IAnalitica.Modulos.Ese;
using IAnalitica.Modulos.Farmacia;
using IAnalitica.Modulos.Financiero;
using IAnalitica.Modulos.Ingreso;
using IAnalitica.Modulos.Operacion;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

// API de ingesta (24 §4.3, §7.1; contrato 05): token OAuth2 client credentials con OpenIddict, cargas por lotes gzip,
// límite por cliente y procesamiento asíncrono (la API solo valida, guarda en staging y encola: 202).
var builder = WebApplication.CreateBuilder(args);

var cadena = builder.Configuration.GetConnectionString("App")
             ?? throw new InvalidOperationException("Falta ConnectionStrings:App (usuario de rol_app). Configúrela con User Secrets o variables de entorno.");

builder.Services.AddObservabilidad("IAnalitica.Api", builder.Configuration.GetSection("Observabilidad").Get<OpcionesObservabilidad>() ?? new());
builder.Services.AddHttpContextAccessor();
builder.Services.AddInfrastructure(cadena);
builder.Services.AddApplication(ModuloFinanciero.Ensamblado, ModuloIngreso.Ensamblado, ModuloOperacion.Ensamblado,
    ModuloFarmacia.Ensamblado, ModuloEse.Ensamblado, ModuloAuditor.Ensamblado);
builder.Services.AddCapaSemantica(new[] { ModuloFinanciero.Modulo }.Concat(ModuloIngreso.Modulos));
builder.Services.AddScoped<ISesionUsuario, SesionApi>();
builder.Services.AddScoped<IConfiguracionPortal, ConfiguracionPortalApi>();
builder.Services.AddOpenApi();

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 10 * 1024 * 1024);   // lote ≤ 10 MB comprimido (05 §1)
builder.Services.AddRequestDecompression();                                                // Content-Encoding: gzip

builder.Services.AddOpenIddict()
    .AddServer(o =>
    {
        o.SetTokenEndpointUris("/connect/token");
        o.AllowClientCredentialsFlow();
        o.RegisterScopes(Reclamos.AlcanceIngesta);
        o.SetAccessTokenLifetime(TimeSpan.FromMinutes(60));
        o.DisableAccessTokenEncryption();   // JWT firmado (05 §1); lo valida la propia API
        // [S] Llaves efímeras: sirven con una sola instancia. En Azure las llaves salen de Key Vault (24 §4.4, F6).
        o.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
        var aspnet = o.UseAspNetCore().EnableTokenEndpointPassthrough();
        if (builder.Configuration.GetValue<bool>("Ingesta:PermitirHttp")) aspnet.DisableTransportSecurityRequirement();   // solo pruebas locales
    })
    .AddValidation(o =>
    {
        o.UseLocalServer();
        o.UseAspNetCore();
    });

builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddAuthorizationBuilder().AddPolicy("ingesta", p => p
    .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
    .RequireAuthenticatedUser()
    .RequireAssertion(c => c.User.HasClaim(Reclamos.EmpresaId, c.User.FindFirstValue(Reclamos.EmpresaId) ?? "-") && c.User.HasScope(Reclamos.AlcanceIngesta)));

// Límite por cliente (LimitePorMinuto de seg.ApiClienteEmpresa, viaja en el token)
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (contexto, ct) =>
    {
        contexto.HttpContext.Response.Headers.RetryAfter = "60";
        await Errores.Problema(StatusCodes.Status429TooManyRequests, CodigosError.LimiteExcedido, "Superó el límite de solicitudes por minuto. Reintente en 60 s.")
            .ExecuteAsync(contexto.HttpContext);
    };
    o.AddPolicy("ingesta", http =>
    {
        var cliente = http.User.FindFirstValue(Claims.Subject) ?? "anonimo";
        var limite = int.TryParse(http.User.FindFirstValue(Reclamos.LimitePorMinuto), CultureInfo.InvariantCulture, out var l) && l > 0 ? l : 60;
        return RateLimitPartition.GetFixedWindowLimiter(cliente, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limite, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true,
        });
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseHttpsRedirection();
app.UseRequestDecompression();
app.UseAuthentication();
app.UseMiddleware<ContextoApiMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/salud", () => Results.Ok(new { estado = "ok" }));

// Token: OpenIddict ya validó ClientId/ClientSecret contra su almacén; aquí se resuelve la empresa del cliente.
app.MapPost("/connect/token", async (HttpContext http, IRepositorioApiClientes clientes, CancellationToken ct) =>
{
    var peticion = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("Solicitud OpenID Connect no disponible.");
    if (!peticion.IsClientCredentialsGrantType())
        return Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?> { [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.UnsupportedGrantType }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    var cliente = await clientes.ObtenerActivoAsync(peticion.ClientId!, ct);
    if (cliente is null)
        return Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidClient,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "El cliente o su empresa están inactivos.",
        }), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    var identidad = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
    identidad.SetClaim(Claims.Subject, cliente.ClientId);
    identidad.SetClaim(Reclamos.EmpresaId, cliente.EmpresaId.ToString(CultureInfo.InvariantCulture));
    identidad.SetClaim(Reclamos.LimitePorMinuto, cliente.LimitePorMinuto.ToString(CultureInfo.InvariantCulture));
    identidad.SetDestinations(_ => [Destinations.AccessToken]);

    var principal = new ClaimsPrincipal(identidad);
    principal.SetScopes(peticion.GetScopes().Intersect([Reclamos.AlcanceIngesta]).DefaultIfEmpty(Reclamos.AlcanceIngesta));
    return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
}).AllowAnonymous();

app.MapIngesta();

app.Run();

/// <summary>Necesario para las pruebas de integración (WebApplicationFactory).</summary>
public partial class ApiApp;
