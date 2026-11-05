using IAnalitica.Application.Mediacion;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Application.Tests.Mediacion;

// Misma colección: el contador de reflexión es estático y no admite pruebas en paralelo.
[Collection("Mediacion")]
public class MediadorTests
{
    private static readonly Type[] Normales =
    {
        typeof(Suma), typeof(SumaHandler), typeof(EsperaToken), typeof(EsperaTokenHandler), typeof(Falla), typeof(FallaHandler),
        typeof(Numeros), typeof(NumerosHandler), typeof(Evento), typeof(EventoHandlerUno), typeof(EventoHandlerDos),
        typeof(Ordenado), typeof(OrdenadoHandler),
    };

    private static ServiceProvider Proveedor(Action<IServiceCollection>? configurar = null, IEnumerable<Type>? tipos = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddScoped<Registro>();
        servicios.AddMediacion(tipos ?? Normales);
        configurar?.Invoke(servicios);
        return servicios.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task Send_resuelve_el_handler_y_devuelve_la_respuesta()
    {
        await using var sp = Proveedor();
        using var scope = sp.CreateScope();
        Assert.Equal(5, await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Suma(2, 3)));
    }

    [Fact]
    public void Falla_al_registrar_si_un_request_no_tiene_handler()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Proveedor(tipos: new[] { typeof(MalRegistro.SinHandler) }));
        Assert.Contains("no tiene handler", ex.Message);
    }

    [Fact]
    public void Falla_al_registrar_si_un_request_tiene_mas_de_un_handler()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Proveedor(tipos: new[]
            { typeof(MalRegistro.Duplicado), typeof(MalRegistro.DuplicadoUno), typeof(MalRegistro.DuplicadoDos) }));
        Assert.Contains("2 handlers", ex.Message);
    }

    [Fact]
    public void Falla_al_registrar_si_un_stream_no_tiene_handler()
        => Assert.Throws<InvalidOperationException>(() => Proveedor(tipos: new[] { typeof(MalRegistro.StreamSinHandler) }));

    [Fact]
    public async Task Los_behaviors_corren_en_el_orden_de_registro_y_envuelven_al_handler()
    {
        await using var sp = Proveedor(s =>
        {
            s.AddScoped(typeof(IPipelineBehavior<,>), typeof(BehaviorA<,>));
            s.AddScoped(typeof(IPipelineBehavior<,>), typeof(BehaviorB<,>));
        });
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Ordenado());

        Assert.Equal(new[] { "A:antes", "B:antes", "handler", "B:despues", "A:despues" },
            scope.ServiceProvider.GetRequiredService<Registro>().Lineas);
    }

    [Fact]
    public async Task El_CancellationToken_llega_al_handler()
    {
        await using var sp = Proveedor();
        using var scope = sp.CreateScope();
        using var cts = new CancellationTokenSource();
        var recibido = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new EsperaToken(), cts.Token);
        Assert.Equal(cts.Token, recibido);
    }

    [Fact]
    public async Task La_excepcion_del_handler_sube_sin_envolver()
    {
        await using var sp = Proveedor();
        using var scope = sp.CreateScope();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => scope.ServiceProvider.GetRequiredService<ISender>().Send(new Falla()));
        Assert.Equal("falla del handler", ex.Message);
    }

    [Fact]
    public async Task El_streaming_entrega_los_elementos_y_es_cancelable()
    {
        await using var sp = Proveedor();
        using var scope = sp.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var todos = new List<int>();
        await foreach (var n in sender.CreateStream(new Numeros(3))) todos.Add(n);
        Assert.Equal(new[] { 1, 2, 3 }, todos);

        using var cts = new CancellationTokenSource();
        var parcial = new List<int>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var n in sender.CreateStream(new Numeros(1000), cts.Token))
            {
                parcial.Add(n);
                if (n == 2) cts.Cancel();
            }
        });
        Assert.Equal(new[] { 1, 2 }, parcial);
    }

    [Fact]
    public async Task Publish_entrega_a_todos_los_handlers_en_secuencia()
    {
        await using var sp = Proveedor();
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new Evento("x"));
        Assert.Equal(new[] { "uno:x", "dos:x" }, scope.ServiceProvider.GetRequiredService<Registro>().Lineas);
    }

    [Fact]
    public async Task Publish_sin_handlers_no_falla()
    {
        await using var sp = Proveedor(tipos: new[] { typeof(Suma), typeof(SumaHandler) });
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new Evento("nadie"));
    }

    private sealed record SoloEstaPrueba(int N) : IRequest<int>;
    private sealed class SoloEstaPruebaHandler : IRequestHandler<SoloEstaPrueba, int>
    {
        public Task<int> Handle(SoloEstaPrueba r, CancellationToken ct) => Task.FromResult(r.N);
    }

    [Fact]
    public async Task La_segunda_llamada_no_usa_reflexion()
    {
        await using var sp = Proveedor(tipos: new[] { typeof(SoloEstaPrueba), typeof(SoloEstaPruebaHandler) });
        using var scope = sp.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var antes = Mediador.CreacionesDeWrapper;
        await sender.Send(new SoloEstaPrueba(1));
        var tras1 = Mediador.CreacionesDeWrapper;
        await sender.Send(new SoloEstaPrueba(2));
        await sender.Send(new SoloEstaPrueba(3));
        var tras3 = Mediador.CreacionesDeWrapper;

        Assert.Equal(antes + 1, tras1);   // la primera llamada crea el wrapper por reflexión
        Assert.Equal(tras1, tras3);       // las siguientes usan el wrapper en caché
    }
}
