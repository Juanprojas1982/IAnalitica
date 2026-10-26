using System.Reflection;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;

namespace IAnalitica.Application.Behaviors;

public sealed class ModuloActivoBehavior<TRequest, TResponse>(ISesionUsuario sesion, IModulosEmpresa modulos)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    private static readonly string[] Requeridos = typeof(TRequest).GetCustomAttributes<RequiereModuloAttribute>(true).Select(a => a.Modulo).ToArray();

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (Requeridos.Length == 0) return await next().ConfigureAwait(false);

        // El superadmin no tiene empresa propia: los requests por módulo siempre se ejecutan en el contexto de una empresa.
        if (sesion.EmpresaId is not { } empresaId)
            return FabricaFalla<TResponse>.Crear(new[] { Error.NoAutorizado("No hay una empresa en el contexto de la solicitud.") });

        foreach (var modulo in Requeridos)
            if (!await modulos.EstaActivoAsync(empresaId, modulo, ct).ConfigureAwait(false))
                return FabricaFalla<TResponse>.Crear(new[] { Error.Conflicto("MODULO_INACTIVO", $"El módulo {modulo} no está activo para su empresa.") });

        return await next().ConfigureAwait(false);
    }
}
