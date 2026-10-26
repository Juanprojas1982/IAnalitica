using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace IAnalitica.Infrastructure.Identity;

/// <summary>
/// Identity + OpenIddict en el esquema <c>seg</c> (23 §1). Es el único contexto con migraciones de EF Core:
/// su script se genera con <c>dotnet ef migrations script</c> y se guarda como database/scripts/0005_identidad.sql.
/// </summary>
public class IdentidadDbContext(DbContextOptions options)
    : IdentityDbContext<UsuarioAplicacion, RolAplicacion, int, IdentityUserClaim<int>, IdentityUserRole<int>,
        IdentityUserLogin<int>, IdentityRoleClaim<int>, IdentityUserToken<int>>(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UsuarioAplicacion>(e =>
        {
            e.ToTable("Usuario", "seg");
            e.Property(x => x.NombreCompleto).HasMaxLength(150).IsRequired().HasDefaultValue("");
            e.Property(x => x.EsSuperAdmin).HasDefaultValue(false);
            e.Property(x => x.EsAdminPrincipal).HasDefaultValue(false);
            e.Property(x => x.DebeCambiarClave).HasDefaultValue(false);
            e.Property(x => x.Activo).HasDefaultValue(true);
            e.Property(x => x.ClaveTemporalVenceEn).HasColumnType("datetime2");
            e.Property(x => x.CreadoEn).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()");
        });
        modelBuilder.Entity<RolAplicacion>().ToTable("Rol", "seg");
        modelBuilder.Entity<IdentityUserRole<int>>().ToTable("UsuarioRol", "seg");
        modelBuilder.Entity<IdentityUserClaim<int>>().ToTable("UsuarioClaim", "seg");
        modelBuilder.Entity<IdentityUserLogin<int>>().ToTable("UsuarioLogin", "seg");
        modelBuilder.Entity<IdentityUserToken<int>>().ToTable("UsuarioToken", "seg");
        modelBuilder.Entity<IdentityRoleClaim<int>>().ToTable("RolClaim", "seg");

        // OpenIddict (tokens de la API de ingesta, client credentials). Las tablas se renombran después de aplicar su modelo.
        modelBuilder.UseOpenIddict();
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreApplication>().ToTable("OidcAplicacion", "seg");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreAuthorization>().ToTable("OidcAutorizacion", "seg");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreScope>().ToTable("OidcAlcance", "seg");
        modelBuilder.Entity<OpenIddictEntityFrameworkCoreToken>().ToTable("OidcToken", "seg");
    }
}
