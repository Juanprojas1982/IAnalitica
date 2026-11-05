using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IAnalitica.Application.Mediacion;

public static class MediacionExtensions
{
    /// <summary>Registra el mediador y busca los handlers por reflexión propia en los ensamblados indicados.</summary>
    public static IServiceCollection AddMediacion(this IServiceCollection services, params Assembly[] ensamblados)
        => services.AddMediacion(ensamblados.SelectMany(a => a.GetTypes()));

    /// <summary>
    /// Registra el mediador con un conjunto explícito de tipos. Valida al registrar (no en tiempo de ejecución):
    /// cada request debe tener exactamente un handler; si falta o hay más de uno se lanza <see cref="InvalidOperationException"/>.
    /// </summary>
    public static IServiceCollection AddMediacion(this IServiceCollection services, IEnumerable<Type> tipos)
    {
        var concretos = tipos.Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }).ToArray();

        RegistrarHandlersUnicos(services, concretos, typeof(IRequest<>), typeof(IRequestHandler<,>));
        RegistrarHandlersUnicos(services, concretos, typeof(IStreamRequest<>), typeof(IStreamRequestHandler<,>));

        foreach (var tipo in concretos)
            foreach (var contrato in tipo.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INotificationHandler<>)))
                services.AddScoped(contrato, tipo);

        services.TryAddScoped<Mediador>();
        services.TryAddScoped<ISender>(sp => sp.GetRequiredService<Mediador>());
        services.TryAddScoped<IPublisher>(sp => sp.GetRequiredService<Mediador>());
        return services;
    }

    private static void RegistrarHandlersUnicos(IServiceCollection services, Type[] concretos, Type contratoRequest, Type contratoHandler)
    {
        var requests = concretos.Where(t => ImplementaAbierto(t, contratoRequest)).ToArray();

        var handlers = concretos
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == contratoHandler)
                .Select(i => (Contrato: i, Implementacion: t)))
            .ToArray();

        foreach (var request in requests)
        {
            var delRequest = handlers.Where(h => h.Contrato.GetGenericArguments()[0] == request).ToArray();
            if (delRequest.Length == 0)
                throw new InvalidOperationException($"El request {request.FullName} no tiene handler.");
            if (delRequest.Length > 1)
                throw new InvalidOperationException(
                    $"El request {request.FullName} tiene {delRequest.Length} handlers ({string.Join(", ", delRequest.Select(h => h.Implementacion.Name))}); debe haber exactamente uno.");
        }

        foreach (var (contrato, implementacion) in handlers)
            services.AddScoped(contrato, implementacion);
    }

    private static bool ImplementaAbierto(Type tipo, Type abierto)
        => tipo.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == abierto);
}
