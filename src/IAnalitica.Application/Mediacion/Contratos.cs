namespace IAnalitica.Application.Mediacion;

public readonly record struct Unit { public static readonly Unit Value = new(); }

/// <summary>Comandos y consultas.</summary>
public interface IRequest<TResponse> { }

/// <summary>Respuestas en streaming (chat de IA por SSE).</summary>
public interface IStreamRequest<TResponse> { }

public interface IRequestHandler<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken ct);
}

public interface IStreamRequestHandler<in TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken ct);
}

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

public interface IPipelineBehavior<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct);
}

public interface ISender
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default);
    IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default);
}

/// <summary>Eventos internos (p. ej. CargaProcesada → encolar CALCULAR_CARTERA). Se publican en secuencia, en el mismo scope.</summary>
public interface INotification { }

public interface INotificationHandler<in TNotification> where TNotification : INotification
{
    Task Handle(TNotification notification, CancellationToken ct);
}

public interface IPublisher
{
    Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : INotification;
}
