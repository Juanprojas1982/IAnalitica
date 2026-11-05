using System.Reflection;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;

namespace IAnalitica.Application.Behaviors;

public sealed class AutorizacionBehavior<TRequest, TResponse>(ISesionUsuario sesion)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    private static readonly string[] Permisos = typeof(TRequest).GetCustomAttributes<RequierePermisoAttribute>(true).Select(a => a.Permiso).ToArray();

    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (Permisos.Length == 0) return next();

        if (sesion.UsuarioId is null)
            return Task.FromResult(FabricaFalla<TResponse>.Crear(new[] { Error.NoAutorizado("Debe iniciar sesión.") }));

        var faltantes = Permisos.Where(p => !sesion.TienePermiso(p)).ToArray();
        return faltantes.Length == 0
            ? next()
            : Task.FromResult(FabricaFalla<TResponse>.Crear(new[] { Error.NoAutorizado() }));
    }
}
