using System.Text.RegularExpressions;
using FluentValidation;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using Microsoft.Extensions.Options;

namespace IAnalitica.Application.Regulacion;

public static class CodigosRegulacion
{
    public const string MapeoNoConfigurado = "MAPEO_NO_CONFIGURADO";
    public const string ArchivoInvalido = "ARCHIVO_INVALIDO";
}

public static partial class FormatoCum
{
    [GeneratedRegex(@"^[0-9]+-[0-9]+$")] private static partial Regex Patron();
    public static bool EsValido(string? cum) => cum is not null && Patron().IsMatch(cum.Trim());
}

// ---------- Importar una lista de precios máximos ----------
[RequierePermiso("admin.catalogos")]
public sealed record ImportarPreciosMaximosCommand(DatosCircular Circular, Stream Json) : IRequest<Result<ResultadoImportacion>>;

public sealed class ImportarPreciosMaximosValidator : AbstractValidator<ImportarPreciosMaximosCommand>
{
    public ImportarPreciosMaximosValidator()
    {
        RuleFor(x => x.Circular.Entidad).NotEmpty().MaximumLength(20).WithErrorCode("ENTIDAD_REQUERIDA").WithMessage("Indique la entidad (por ejemplo CNPMDM).");
        RuleFor(x => x.Circular.Numero).NotEmpty().MaximumLength(20).WithErrorCode("NUMERO_REQUERIDO").WithMessage("Indique el número de la circular.");
        RuleFor(x => x.Circular.Tema).NotEmpty().MaximumLength(40).WithErrorCode("TEMA_REQUERIDO").WithMessage("Indique el tema (por ejemplo PRECIO_MAXIMO).");
        RuleFor(x => x.Circular.UrlFuente).NotEmpty().MaximumLength(400).WithErrorCode("FUENTE_REQUERIDA").WithMessage("Indique la URL de la fuente oficial.");
        RuleFor(x => x.Circular.Anio).InclusiveBetween((short)2000, (short)2100).WithErrorCode("ANIO_INVALIDO").WithMessage("El año de la circular no es válido.");
        RuleFor(x => x.Circular).Must(c => c.VigenteHasta is null || c.VigenteHasta >= c.VigenteDesde).WithErrorCode("VIGENCIA_INVALIDA").WithMessage("La vigencia termina antes de empezar.");
    }
}

public sealed class ImportarPreciosMaximosHandler(IImportadorPreciosMaximos importador, IOptions<MapeoPreciosMaximos> mapeo, ISesionUsuario sesion, IAuditoriaAdministrativa auditoria)
    : IRequestHandler<ImportarPreciosMaximosCommand, Result<ResultadoImportacion>>
{
    public async Task<Result<ResultadoImportacion>> Handle(ImportarPreciosMaximosCommand request, CancellationToken ct)
    {
        if (!mapeo.Value.EstaCompleto)
            return Result<ResultadoImportacion>.Falla(Error.Conflicto(CodigosRegulacion.MapeoNoConfigurado,
                "Falta configurar el mapeo de campos (Regulacion:PreciosMaximos:Mapeo): los campos del dataset oficial aún no están verificados."));
        ResultadoImportacion r;
        try { r = await importador.ImportarAsync(request.Circular, mapeo.Value, request.Json, ct).ConfigureAwait(false); }
        catch (System.Text.Json.JsonException) { return Result<ResultadoImportacion>.Falla(Error.Validacion(CodigosRegulacion.ArchivoInvalido, "El archivo no es un arreglo JSON de objetos.")); }

        if (sesion.UsuarioId is { } usuarioId)
            await auditoria.RegistrarAsync(new CambioAdministrativo(null, usuarioId, "reg.PrecioMaximo", $"{request.Circular.Entidad}-{request.Circular.Numero}-{request.Circular.Anio}", "IMPORTAR",
                null, System.Text.Json.JsonSerializer.Serialize(new { r.Importados, r.Rechazados }), null), ct).ConfigureAwait(false);
        return r;
    }
}

// ---------- Factor de presentación validado por el químico farmacéutico ----------
[RequierePermiso("admin.catalogos")]
public sealed record RegistrarFactorPresentacionCommand(string Cum, decimal UnidadesPorPresentacion, string ValidadoPor) : IRequest<Result>;

public sealed class RegistrarFactorPresentacionValidator : AbstractValidator<RegistrarFactorPresentacionCommand>
{
    public RegistrarFactorPresentacionValidator()
    {
        RuleFor(x => x.Cum).Must(FormatoCum.EsValido).WithErrorCode("CUM_INVALIDO").WithMessage("El CUM debe tener el formato expediente-consecutivo (por ejemplo 12345-1).");
        RuleFor(x => x.UnidadesPorPresentacion).GreaterThan(0).LessThanOrEqualTo(100000).WithErrorCode("FACTOR_INVALIDO").WithMessage("Las unidades por presentación deben ser mayores que cero.");
        RuleFor(x => x.ValidadoPor).NotEmpty().MaximumLength(150).WithErrorCode("VALIDADOR_REQUERIDO").WithMessage("Indique quién validó el factor (químico farmacéutico).");
    }
}

public sealed class RegistrarFactorPresentacionHandler(IImportadorPreciosMaximos importador, ISesionUsuario sesion, IAuditoriaAdministrativa auditoria)
    : IRequestHandler<RegistrarFactorPresentacionCommand, Result>
{
    public async Task<Result> Handle(RegistrarFactorPresentacionCommand request, CancellationToken ct)
    {
        await importador.RegistrarFactorAsync(request.Cum.Trim(), request.UnidadesPorPresentacion, request.ValidadoPor.Trim(), ct).ConfigureAwait(false);
        if (sesion.UsuarioId is { } usuarioId)
            await auditoria.RegistrarAsync(new CambioAdministrativo(null, usuarioId, "reg.FactorPresentacion", request.Cum.Trim(), "VALIDAR_FACTOR", null,
                System.Text.Json.JsonSerializer.Serialize(new { request.UnidadesPorPresentacion, request.ValidadoPor }), null), ct).ConfigureAwait(false);
        return Result.Ok();
    }
}
