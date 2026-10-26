using System.Reflection;
using FluentValidation;
using IAnalitica.Application.Behaviors;
using IAnalitica.Application.Mediacion;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Application;

public static class DependencyInjection
{
    /// <summary>Mediador, behaviors (en el orden normativo de §3.5) y validadores de FluentValidation.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services, params Assembly[] ensamblesDeModulos)
    {
        var ensamblados = ensamblesDeModulos.Append(typeof(DependencyInjection).Assembly).Distinct().ToArray();
        return services.AddApplication(ensamblados.SelectMany(a => a.GetTypes()));
    }

    /// <summary>Igual que la anterior con un conjunto explícito de tipos (pruebas).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services, IEnumerable<Type> tipos)
    {
        var lista = tipos.Concat(typeof(DependencyInjection).Assembly.GetTypes()).Distinct().ToArray();
        services.AddMediacion(lista);
        foreach (var validador in lista.Where(t => t is { IsClass: true, IsAbstract: false }))
            foreach (var contrato in validador.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>)))
                services.AddScoped(contrato, validador);

        // Orden de registro = orden de ejecución (el primero es el más externo).
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TelemetriaBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AutorizacionBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ModuloActivoBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidacionBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(CacheBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransaccionBehavior<,>));
        return services;
    }
}
