using IAnalitica.Application.Mediacion;

namespace IAnalitica.Application.Tests.Mediacion;

// Requests y handlers de prueba. Los de «mal registro» están en clases anidadas para no contaminar el escaneo de los demás tests.
public sealed record Suma(int A, int B) : IRequest<int>;
public sealed class SumaHandler : IRequestHandler<Suma, int>
{
    public Task<int> Handle(Suma request, CancellationToken ct) => Task.FromResult(request.A + request.B);
}

public sealed record EsperaToken : IRequest<CancellationToken>;
public sealed class EsperaTokenHandler : IRequestHandler<EsperaToken, CancellationToken>
{
    public Task<CancellationToken> Handle(EsperaToken request, CancellationToken ct) => Task.FromResult(ct);
}

public sealed record Falla : IRequest<int>;
public sealed class FallaHandler : IRequestHandler<Falla, int>
{
    public Task<int> Handle(Falla request, CancellationToken ct) => throw new NotSupportedException("falla del handler");
}

public sealed record Numeros(int Hasta) : IStreamRequest<int>;
public sealed class NumerosHandler : IStreamRequestHandler<Numeros, int>
{
    public async IAsyncEnumerable<int> Handle(Numeros request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 1; i <= request.Hasta; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return i;
        }
    }
}

public sealed record Evento(string Texto) : INotification;
public sealed class Registro
{
    public List<string> Lineas { get; } = new();
}
public sealed class EventoHandlerUno(Registro registro) : INotificationHandler<Evento>
{
    public Task Handle(Evento n, CancellationToken ct) { registro.Lineas.Add("uno:" + n.Texto); return Task.CompletedTask; }
}
public sealed class EventoHandlerDos(Registro registro) : INotificationHandler<Evento>
{
    public Task Handle(Evento n, CancellationToken ct) { registro.Lineas.Add("dos:" + n.Texto); return Task.CompletedTask; }
}

public sealed record Ordenado : IRequest<string>;
public sealed class OrdenadoHandler(Registro registro) : IRequestHandler<Ordenado, string>
{
    public Task<string> Handle(Ordenado request, CancellationToken ct) { registro.Lineas.Add("handler"); return Task.FromResult("ok"); }
}
public sealed class BehaviorA<TRequest, TResponse>(Registro registro) : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        registro.Lineas.Add("A:antes");
        var r = await next();
        registro.Lineas.Add("A:despues");
        return r;
    }
}
public sealed class BehaviorB<TRequest, TResponse>(Registro registro) : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        registro.Lineas.Add("B:antes");
        var r = await next();
        registro.Lineas.Add("B:despues");
        return r;
    }
}

public static class MalRegistro
{
    public sealed record SinHandler : IRequest<int>;

    public sealed record Duplicado : IRequest<int>;
    public sealed class DuplicadoUno : IRequestHandler<Duplicado, int> { public Task<int> Handle(Duplicado r, CancellationToken ct) => Task.FromResult(1); }
    public sealed class DuplicadoDos : IRequestHandler<Duplicado, int> { public Task<int> Handle(Duplicado r, CancellationToken ct) => Task.FromResult(2); }

    public sealed record StreamSinHandler : IStreamRequest<int>;
}
