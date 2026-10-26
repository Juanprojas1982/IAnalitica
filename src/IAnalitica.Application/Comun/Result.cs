namespace IAnalitica.Application.Comun;

/// <summary>Error de negocio. <see cref="Codigo"/> es estable (para el cliente y las pruebas); <see cref="Mensaje"/> va en español.</summary>
public sealed record Error(string Codigo, string Mensaje)
{
    public static Error Validacion(string codigo, string mensaje) => new(codigo, mensaje);
    public static Error NoAutorizado(string mensaje = "No tiene permiso para esta operación.") => new("NO_AUTORIZADO", mensaje);
    public static Error NoEncontrado(string mensaje) => new("NO_ENCONTRADO", mensaje);
    public static Error Conflicto(string codigo, string mensaje) => new(codigo, mensaje);
}

/// <summary>Resultado de un flujo de negocio. Las excepciones se reservan para lo excepcional (§3.4).</summary>
public interface IResultado
{
    bool EsExitoso { get; }
    IReadOnlyList<Error> Errores { get; }
}

public class Result : IResultado
{
    private static readonly IReadOnlyList<Error> SinErrores = Array.Empty<Error>();

    protected Result(bool exitoso, IReadOnlyList<Error> errores)
    {
        EsExitoso = exitoso;
        Errores = errores;
    }

    public bool EsExitoso { get; }
    public IReadOnlyList<Error> Errores { get; }

    public static Result Ok() => new(true, SinErrores);
    public static Result Falla(params Error[] errores) => new(false, errores);
    public static Result Falla(IEnumerable<Error> errores) => new(false, errores.ToArray());

    /// <summary>Usada por los behaviors para devolver una falla sin conocer el tipo concreto del resultado.</summary>
    public static Result DeFalla(IReadOnlyList<Error> errores) => new(false, errores);
}

public sealed class Result<T> : Result
{
    private readonly T? _valor;

    private Result(T valor) : base(true, Array.Empty<Error>()) => _valor = valor;
    private Result(IReadOnlyList<Error> errores) : base(false, errores) { }

    public T Valor => EsExitoso
        ? _valor!
        : throw new InvalidOperationException("No se puede leer el valor de un resultado fallido.");

    public static Result<T> Ok(T valor) => new(valor);
    public static new Result<T> Falla(params Error[] errores) => new(errores);
    public static new Result<T> Falla(IEnumerable<Error> errores) => new(errores.ToArray());
    public static new Result<T> DeFalla(IReadOnlyList<Error> errores) => new(errores);

    public static implicit operator Result<T>(T valor) => Ok(valor);
}
