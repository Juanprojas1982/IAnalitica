using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Mediacion;

namespace IAnalitica.Application.Behaviors;

/// <summary>Caché por empresa + clave + versión de carga (§5.2). Solo para consultas que implementan <see cref="IConsultaCacheable"/>.</summary>
public sealed class CacheBehavior<TRequest, TResponse>(ISesionUsuario sesion, IVersionCarga versionCarga, ICacheConsultas cache)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    private static readonly bool Aplica = typeof(IConsultaCacheable).IsAssignableFrom(typeof(TRequest));

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (!Aplica || request is not IConsultaCacheable cacheable || sesion.EmpresaId is not { } empresaId)
            return await next().ConfigureAwait(false);

        var version = await versionCarga.ObtenerAsync(empresaId, ct).ConfigureAwait(false);
        var clave = $"{typeof(TRequest).Name}|{empresaId}|{version}|{cacheable.ClaveCache}";
        return await cache.ObtenerOCrearAsync(clave, cacheable.Duracion, () => next(), ct).ConfigureAwait(false);
    }
}
