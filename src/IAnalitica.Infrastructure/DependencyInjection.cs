using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Ingesta;
using IAnalitica.Infrastructure.Empresas;
using IAnalitica.Infrastructure.Identity;
using IAnalitica.Domain.Auditoria;
using IAnalitica.Infrastructure.Auditoria;
using IAnalitica.Infrastructure.Ingesta;
using IAnalitica.Infrastructure.Regulacion;
using IAnalitica.Infrastructure.Semantica;
using IAnalitica.Application.Semantica;
using IAnalitica.Application.Auditoria;
using IAnalitica.Application.Regulacion;
using IAnalitica.Application.Reportes;
using IAnalitica.Infrastructure.Reportes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
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

        // Reportes en segundo plano (spec 21): el portal solicita y descarga; el worker genera con la identidad del solicitante
        services.AddScoped<IRepositorioReportes, RepositorioReportes>();
        services.AddScoped<IRepositorioHallazgos, RepositorioHallazgos>();
        services.AddScoped<IImportadorPreciosMaximos, ImportadorPreciosMaximos>();
        services.AddScoped<INotificaciones, Notificaciones>();
        services.AddScoped<IPermisosUsuario, PermisosUsuario>();
        services.AddSingleton<IAlmacenReportes, AlmacenReportesDisco>();
        services.AddSingleton<IExportadorReporte, ExportadorCsv>();
        services.AddSingleton<IExportadorReporte, ExportadorXlsx>();
        services.AddSingleton<IExportadorReporte, ExportadorPdf>();
        services.AddOptions<LimitesReportes>();
        services.AddOptions<OpcionesAlmacenReportes>();
        services.TryAddScoped<IColaTrabajos>(sp => new ColaTrabajosSql(sp.GetRequiredService<IFabricaConexiones>().AbrirAsync));
        services.AddScoped<IMotorMetricas, MotorMetricas>();
        services.AddScoped<IMetasKpi, MetasKpi>();
        services.AddScoped<IOpcionesFiltros, Semantica.OpcionesFiltros>();
        services.AddScoped<IRepositorioCargas, RepositorioCargas>();
        services.AddScoped<IRepositorioApiClientes, RepositorioApiClientes>();
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

    /// <summary>
    /// Procesamiento de cargas del worker (24 §8): cola SQL, despachador y manejadores. <paramref name="cadenaEtl"/> es la conexión con
    /// un usuario de <c>rol_etl</c>; la cola vive en la misma base ($0 adicional, D23).
    /// </summary>
    public static IServiceCollection AddProcesamiento(this IServiceCollection services, string cadenaEtl)
    {
        services.AddSingleton<IFabricaConexionesEtl>(new FabricaConexionesEtl(cadenaEtl));
        services.Replace(ServiceDescriptor.Singleton<IColaTrabajos>(sp =>
        {
            var fabrica = sp.GetRequiredService<IFabricaConexionesEtl>();
            return new ColaTrabajosSql(fabrica.AbrirAsync);
        }));
        services.AddSingleton<ProcesadorCargas>();
        services.AddSingleton<IManejadorTrabajo, ManejadorProcesarCarga>();
        services.AddSingleton<IManejadorTrabajo, ManejadorCalcularCartera>();
        services.AddSingleton<IManejadorTrabajo, ManejadorCalcularCostos>();
        services.AddSingleton<MotorAuditoria>();
        services.AddSingleton<IManejadorTrabajo, ManejadorAuditarConfig>();
        services.AddSingleton<ValidadorPrecios>();
        services.AddSingleton<IManejadorTrabajo, ManejadorValidarPrecios>();
        services.AddSingleton<DespachadorTrabajos>();
        services.TryAddSingleton<IAlmacenReportes, AlmacenReportesDisco>();
        services.AddOptions<OpcionesAlmacenReportes>();
        services.AddSingleton<ExpiradorReportes>();
        return services;
    }

    /// <summary>Reglas del auditor de configuración (las aporta el módulo; el motor no las conoce).</summary>
    public static IServiceCollection AddReglasAuditor(this IServiceCollection services, IEnumerable<IReglaConfiguracion> reglas)
    {
        foreach (var r in reglas) services.AddSingleton(r);
        return services;
    }

    /// <summary>
    /// Generación de reportes en el worker: el manejador y la sesión del trabajo. Requiere <see cref="AddInfrastructure"/> (cadena de <c>rol_app</c>,
    /// con la que se consulta con la identidad del solicitante) y <see cref="AddProcesamiento"/> (cadena de <c>rol_etl</c>, para la cola y el estado).
    /// </summary>
    public static IServiceCollection AddGeneracionReportes(this IServiceCollection services)
    {
        services.AddScoped<SesionTrabajo>();
        services.AddScoped<ISesionUsuario>(sp => sp.GetRequiredService<SesionTrabajo>());
        services.AddSingleton<IManejadorTrabajo, ManejadorGenerarReporte>();
        return services;
    }
}
