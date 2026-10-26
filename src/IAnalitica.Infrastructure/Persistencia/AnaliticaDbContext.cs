using IAnalitica.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace IAnalitica.Infrastructure.Persistencia;

/// <summary>
/// Contexto de la aplicación: Identity + OpenIddict (heredados, con migraciones) y las tablas de negocio mapeadas DB-first.
/// Las tablas las crea DbUp (database/scripts); EF no las migra.
/// </summary>
public class AnaliticaDbContext(DbContextOptions<AnaliticaDbContext> options) : IdentidadDbContext(options)
{
    internal DbSet<EmpresaEntidad> Empresas => Set<EmpresaEntidad>();
    internal DbSet<EmpresaModuloEntidad> EmpresaModulos => Set<EmpresaModuloEntidad>();
    internal DbSet<EmpresaContactoEntidad> EmpresaContactos => Set<EmpresaContactoEntidad>();
    internal DbSet<ApiClienteEmpresaEntidad> ApiClientes => Set<ApiClienteEmpresaEntidad>();
    internal DbSet<HistorialClaveEntidad> HistorialClaves => Set<HistorialClaveEntidad>();
    internal DbSet<CambioAdministrativoEntidad> CambiosAdministrativos => Set<CambioAdministrativoEntidad>();
    internal DbSet<SeguridadEventoEntidad> EventosSeguridad => Set<SeguridadEventoEntidad>();
    internal DbSet<IaPlanEntidad> IaPlanes => Set<IaPlanEntidad>();
    internal DbSet<IaModeloEntidad> IaModelos => Set<IaModeloEntidad>();
    internal DbSet<IaParametroEntidad> IaParametros => Set<IaParametroEntidad>();
    internal DbSet<IaEmpresaConfigEntidad> IaEmpresaConfigs => Set<IaEmpresaConfigEntidad>();
    internal DbSet<RolPermisoEntidad> RolPermisos => Set<RolPermisoEntidad>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<EmpresaEntidad>(e =>
        {
            e.ToTable("Empresa", "seg");
            e.HasKey(x => x.EmpresaId);
            e.Property(x => x.DigitoVerificacion).HasColumnType("char(1)");
            e.Property(x => x.ColorPrimario).HasColumnType("char(7)");
            e.Property(x => x.ColorSecundario).HasColumnType("char(7)");
            e.HasMany(x => x.Modulos).WithOne().HasForeignKey(x => x.EmpresaId);
            e.HasMany(x => x.Contactos).WithOne().HasForeignKey(x => x.EmpresaId);
        });
        b.Entity<EmpresaModuloEntidad>(e => { e.ToTable("EmpresaModulo", "seg"); e.HasKey(x => new { x.EmpresaId, x.Modulo }); });
        b.Entity<EmpresaContactoEntidad>(e => { e.ToTable("EmpresaContacto", "seg"); e.HasKey(x => x.ContactoId); });
        b.Entity<ApiClienteEmpresaEntidad>(e => { e.ToTable("ApiClienteEmpresa", "seg"); e.HasKey(x => x.ClientId); });
        b.Entity<HistorialClaveEntidad>(e =>
        {
            e.ToTable("HistorialClave", "seg");
            e.HasKey(x => x.HistorialId);
            e.Property(x => x.CreadoEn).HasColumnType("datetime2(0)").ValueGeneratedOnAdd().HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne(x => x.Usuario).WithMany().HasForeignKey(x => x.UsuarioId);
        });
        b.Entity<CambioAdministrativoEntidad>(e =>
        {
            e.ToTable("CambioAdministrativo", "aud");
            e.HasKey(x => x.CambioId);
            e.Property<DateTime>("Fecha").HasColumnType("datetime2(0)").ValueGeneratedOnAdd().HasDefaultValueSql("SYSUTCDATETIME()");
        });
        b.Entity<SeguridadEventoEntidad>(e =>
        {
            e.ToTable("Seguridad", "aud");
            e.HasKey(x => x.EventoId);
            e.Property<DateTime>("Fecha").HasColumnType("datetime2(0)").ValueGeneratedOnAdd().HasDefaultValueSql("SYSUTCDATETIME()");
        });
        b.Entity<IaPlanEntidad>(e => { e.ToTable("PlanComercial", "ia"); e.HasKey(x => x.PlanId); });
        b.Entity<IaModeloEntidad>(e => { e.ToTable("Modelo", "ia"); e.HasKey(x => x.ModeloId); });
        b.Entity<IaParametroEntidad>(e => { e.ToTable("Parametro", "ia"); e.HasKey(x => x.Clave); });
        b.Entity<IaEmpresaConfigEntidad>(e => { e.ToTable("EmpresaConfig", "ia"); e.HasKey(x => x.EmpresaId); });
        b.Entity<RolPermisoEntidad>(e => { e.ToTable("RolPermiso", "seg"); e.HasKey(x => new { x.RolId, x.Permiso }); });
    }
}
