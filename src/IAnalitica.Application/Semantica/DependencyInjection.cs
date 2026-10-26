using IAnalitica.Domain.Modulos;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Application.Semantica;

public static class SemanticaExtensions
{
    /// <summary>Registra el catálogo validado de módulos analíticos (falla al arrancar si algo registrado no cumple la lista blanca).</summary>
    public static IServiceCollection AddCapaSemantica(this IServiceCollection services, IEnumerable<IModuloAnalitico> modulos)
    {
        var catalogo = CatalogoAnalitico.Construir(modulos);
        return services.AddSingleton(catalogo);
    }
}
