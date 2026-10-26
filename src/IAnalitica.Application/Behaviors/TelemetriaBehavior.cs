using System.Diagnostics;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using Microsoft.Extensions.Logging;

namespace IAnalitica.Application.Behaviors;

/// <summary>Span de OpenTelemetry y log estructurado: nombre, duración y resultado. Nunca el contenido del request (§10).</summary>
public sealed class TelemetriaBehavior<TRequest, TResponse>(ILogger<TelemetriaBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    public static readonly ActivitySource Fuente = new("IAnalitica.Application");

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var nombre = typeof(TRequest).Name;
        using var actividad = Fuente.StartActivity(nombre);
        var inicio = Stopwatch.GetTimestamp();
        try
        {
            var respuesta = await next().ConfigureAwait(false);
            var exitoso = respuesta is not IResultado r || r.EsExitoso;
            actividad?.SetTag("resultado", exitoso ? "ok" : "falla");
            logger.LogInformation("Request {Request} terminó en {Duracion} ms con resultado {Resultado}",
                nombre, Stopwatch.GetElapsedTime(inicio).TotalMilliseconds, exitoso ? "ok" : "falla");
            return respuesta;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            actividad?.SetStatus(ActivityStatusCode.Error);
            actividad?.SetTag("error.tipo", ex.GetType().Name);
            logger.LogError("Request {Request} lanzó {TipoExcepcion} tras {Duracion} ms",
                nombre, ex.GetType().Name, Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
            throw;
        }
    }
}
