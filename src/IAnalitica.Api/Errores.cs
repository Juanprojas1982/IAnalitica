using IAnalitica.Application.Comun;
using IAnalitica.Ingesta.Contracts;

namespace IAnalitica.Api;

/// <summary>Errores de negocio → Problem Details (RFC 9457) con los códigos de la spec 05 §7 en la extensión «codigo».</summary>
internal static class Errores
{
    public static IResult AProblema(IReadOnlyList<Error> errores)
    {
        var primero = errores[0];
        var estado = primero.Codigo switch
        {
            "NO_ENCONTRADO" => StatusCodes.Status404NotFound,
            "NO_AUTORIZADO" => StatusCodes.Status403Forbidden,
            CodigosError.CargaDuplicada or CodigosError.PeriodoCerrado or CodigosError.CargaNoAbierta => StatusCodes.Status409Conflict,
            CodigosError.LoteGrande => StatusCodes.Status413PayloadTooLarge,
            CodigosError.ValidacionNegocio or CodigosError.ControlNoCuadra => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status400BadRequest,
        };
        return Problema(estado, primero.Codigo, primero.Mensaje, errores.Count > 1 ? errores.Select(e => new { e.Codigo, e.Mensaje }).ToArray() : null);
    }

    public static IResult Problema(int estado, string codigo, string mensaje, object? errores = null)
    {
        var extensiones = new Dictionary<string, object?> { ["codigo"] = codigo };
        if (errores is not null) extensiones["errores"] = errores;
        return Results.Problem(statusCode: estado, title: mensaje, extensions: extensiones);
    }
}
