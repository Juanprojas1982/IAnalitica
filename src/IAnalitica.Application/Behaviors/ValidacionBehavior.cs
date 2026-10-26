using FluentValidation;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;

namespace IAnalitica.Application.Behaviors;

public sealed class ValidacionBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validadores)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var lista = validadores as IValidator<TRequest>[] ?? validadores.ToArray();
        if (lista.Length == 0) return await next().ConfigureAwait(false);

        var contexto = new ValidationContext<TRequest>(request);
        var errores = new List<Error>();
        foreach (var validador in lista)
        {
            var resultado = await validador.ValidateAsync(contexto, ct).ConfigureAwait(false);
            errores.AddRange(resultado.Errors.Select(e => Error.Validacion(e.ErrorCode is { Length: > 0 } c ? c : "VALIDACION", e.ErrorMessage)));
        }

        return errores.Count == 0 ? await next().ConfigureAwait(false) : FabricaFalla<TResponse>.Crear(errores);
    }
}
