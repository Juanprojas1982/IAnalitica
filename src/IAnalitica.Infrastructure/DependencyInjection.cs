using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure.Empresas;
using IAnalitica.Infrastructure.Identity;
using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Infrastructure.Seguridad;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Infraestructura de F0. <paramref name="cadenaApp"/> es la conexión con un usuario de <c>rol_app</c> (sujeto a RLS);
    /// el worker usará otra cadena con <c>rol_etl</c> (24 §4.2). Las cadenas vienen de User Secrets o Key Vault, nunca de appsettings.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string cadenaApp)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddDataProtection();   // tokens de Identity; el portal fija dónde persisten las llaves (Web)
        services.AddScoped<IContextoBdAccesor, ContextoBdAccesor>();
        services.AddScoped<SessionContextInterceptor>();
        services.AddScoped<IFabricaConexiones>(sp => new FabricaConexiones(cadenaApp, sp.GetRequiredService<IContextoBdAccesor>()));

        services.AddDbContext<AnaliticaDbContext>((sp, o) =>
        {
            o.UseSqlServer(cadenaApp);
            o.AddInterceptors(sp.GetRequiredService<SessionContextInterceptor>());
        });

        services.AddIdentityCore<UsuarioAplicacion>(opciones =>
            {
                // Política de contraseñas (24 §4.1)
                opciones.Password.RequiredLength = 10;
                opciones.Password.RequireUppercase = true;
                opciones.Password.RequireLowercase = true;
                opciones.Password.RequireDigit = true;
                opciones.Password.RequireNonAlphanumeric = true;
                // Bloqueo: 5 intentos, 15 minutos
                opciones.Lockout.AllowedForNewUsers = true;
                opciones.Lockout.MaxFailedAccessAttempts = 5;
                opciones.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                opciones.User.RequireUniqueEmail = false;
                opciones.Tokens.AuthenticatorTokenProvider = TokenOptions.DefaultAuthenticatorProvider;
            })
            .AddRoles<RolAplicacion>()
            .AddSignInManager()
            .AddEntityFrameworkStores<AnaliticaDbContext>()
            .AddClaimsPrincipalFactory<ClaimsFactory>()
            .AddPasswordValidator<HistorialClaveValidator>()
            .AddErrorDescriber<DescriptorErroresEs>()
            .AddDefaultTokenProviders();

        services.AddOpenIddict().AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AnaliticaDbContext>());

        services.AddScoped<IRepositorioEmpresas, RepositorioEmpresas>();
        services.AddScoped<IGestorUsuarios, GestorUsuarios>();
        services.AddScoped<IGestionUsuariosEmpresa, GestionUsuariosEmpresa>();
        services.AddScoped<IGestorCredencialesApi, GestorCredencialesApi>();
        services.AddScoped<IAuditoriaAdministrativa, AuditoriaAdministrativa>();
        services.AddScoped<IAuditoriaSeguridad, AuditoriaSeguridad>();
        services.AddScoped<ITransaccion, TransaccionEf>();
        services.AddScoped<IModulosEmpresa, ModulosEmpresa>();
        services.AddScoped<IVersionCarga, VersionCarga>();
        services.AddMemoryCache();
        services.AddSingleton<ICacheConsultas, CacheConsultasMemoria>();
        services.AddSingleton<IGeneradorSecretos, GeneradorSecretos>();
        return services;
    }
}
