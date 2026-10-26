using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IAnalitica.Infrastructure.Identity;

/// <summary>Solo para <c>dotnet ef</c>: no se conecta a ninguna base; la cadena es ficticia y no es un secreto.</summary>
internal sealed class IdentidadDesignTimeFactory : IDesignTimeDbContextFactory<IdentidadDbContext>
{
    public IdentidadDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<IdentidadDbContext>()
            .UseSqlServer("Server=.;Database=IAnaliticaDiseno;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
}
