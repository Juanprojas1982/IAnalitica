using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Mediacion;

namespace IAnalitica.Application.Behaviors;

public sealed class TransaccionBehavior<TRequest, TResponse>(ITransaccion transaccion)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    private static readonly bool Aplica = typeof(IComandoTransaccional).IsAssignableFrom(typeof(TRequest));

    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
        => Aplica ? transaccion.EjecutarAsync(() => next(), ct) : next();
}
