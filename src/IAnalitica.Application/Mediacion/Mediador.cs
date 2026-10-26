using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Application.Mediacion;

/// <summary>
/// Mediador propio (D34). La reflexión ocurre solo la primera vez por tipo de request; después se usa el wrapper en caché.
/// </summary>
public sealed class Mediador(IServiceProvider servicios) : ISender, IPublisher
{
    private static readonly ConcurrentDictionary<Type, object> Wrappers = new();
    private static int _creacionesDeWrapper;

    /// <summary>Cuántos wrappers se crearon por reflexión (para la prueba «sin reflexión en la segunda llamada»).</summary>
    internal static int CreacionesDeWrapper => Volatile.Read(ref _creacionesDeWrapper);

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var wrapper = (WrapperBase<TResponse>)Wrappers.GetOrAdd(request.GetType(), t => CrearWrapper(t, typeof(TResponse), typeof(IRequest<>), typeof(RequestWrapper<,>)));
        return wrapper.Handle(request, servicios, ct);
    }

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var wrapper = (StreamWrapperBase<TResponse>)Wrappers.GetOrAdd(request.GetType(), t => CrearWrapper(t, typeof(TResponse), typeof(IStreamRequest<>), typeof(StreamWrapper<,>)));
        return wrapper.Handle(request, servicios, ct);
    }

    public async Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);
        // En secuencia y en el mismo scope. Si un handler falla, la excepción sube sin envolver y los siguientes no corren.
        foreach (var handler in servicios.GetServices<INotificationHandler<TNotification>>())
            await handler.Handle(notification, ct).ConfigureAwait(false);
    }

    private static object CrearWrapper(Type tipoRequest, Type tipoRespuesta, Type _, Type wrapperAbierto)
    {
        Interlocked.Increment(ref _creacionesDeWrapper);
        return Activator.CreateInstance(wrapperAbierto.MakeGenericType(tipoRequest, tipoRespuesta))!;
    }

    private abstract class WrapperBase<TResponse>
    {
        public abstract Task<TResponse> Handle(IRequest<TResponse> request, IServiceProvider sp, CancellationToken ct);
    }

    private sealed class RequestWrapper<TRequest, TResponse> : WrapperBase<TResponse> where TRequest : IRequest<TResponse>
    {
        public override Task<TResponse> Handle(IRequest<TResponse> request, IServiceProvider sp, CancellationToken ct)
        {
            var tipado = (TRequest)request;
            var handler = sp.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
            RequestHandlerDelegate<TResponse> siguiente = () => handler.Handle(tipado, ct);

            // El primer behavior registrado es el más externo: se envuelve desde el último hacia el primero.
            var behaviors = sp.GetServices<IPipelineBehavior<TRequest, TResponse>>().ToArray();
            for (var i = behaviors.Length - 1; i >= 0; i--)
            {
                var behavior = behaviors[i];
                var interno = siguiente;
                siguiente = () => behavior.Handle(tipado, interno, ct);
            }

            return siguiente();
        }
    }

    private abstract class StreamWrapperBase<TResponse>
    {
        public abstract IAsyncEnumerable<TResponse> Handle(IStreamRequest<TResponse> request, IServiceProvider sp, CancellationToken ct);
    }

    private sealed class StreamWrapper<TRequest, TResponse> : StreamWrapperBase<TResponse> where TRequest : IStreamRequest<TResponse>
    {
        public override IAsyncEnumerable<TResponse> Handle(IStreamRequest<TResponse> request, IServiceProvider sp, CancellationToken ct)
            => sp.GetRequiredService<IStreamRequestHandler<TRequest, TResponse>>().Handle((TRequest)request, ct);
    }
}
