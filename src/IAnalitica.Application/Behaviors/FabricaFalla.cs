using System.Reflection;
using IAnalitica.Application.Comun;

namespace IAnalitica.Application.Behaviors;

/// <summary>
/// Construye la falla del tipo de respuesta (<see cref="Result"/> o <see cref="Result{T}"/>) sin excepciones.
/// La reflexión ocurre una vez por tipo (campo estático genérico).
/// </summary>
internal static class FabricaFalla<TResponse>
{
    private static readonly Func<IReadOnlyList<Error>, TResponse>? Fabrica = Crear();

    public static bool Soportada => Fabrica is not null;

    public static TResponse Crear(IReadOnlyList<Error> errores)
        => Fabrica is null
            ? throw new InvalidOperationException(
                $"{typeof(TResponse).Name} no es un Result: un request protegido por behaviors debe devolver Result o Result<T>.")
            : Fabrica(errores);

    private static Func<IReadOnlyList<Error>, TResponse>? Crear()
    {
        var tipo = typeof(TResponse);
        if (!typeof(Result).IsAssignableFrom(tipo)) return null;

        var metodo = tipo.GetMethod(nameof(Result.DeFalla), BindingFlags.Public | BindingFlags.Static, new[] { typeof(IReadOnlyList<Error>) });
        return metodo is null ? null : errores => (TResponse)metodo.Invoke(null, new object[] { errores })!;
    }
}
