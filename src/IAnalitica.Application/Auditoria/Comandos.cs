using System.Text.Json;
using FluentValidation;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;

namespace IAnalitica.Application.Auditoria;

public static class CodigosAuditoria
{
    public const string AccionInvalida = "ACCION_INVALIDA";
    public const string JustificacionRequerida = "JUSTIFICACION_REQUERIDA";
    public const string EstadoNoPermitido = "ESTADO_NO_PERMITIDO";
}

public static class AccionesHallazgo
{
    public const string Aceptar = "ACEPTAR", FalsoPositivo = "FALSO_POSITIVO", Reabrir = "REABRIR";
    public static readonly IReadOnlySet<string> Todas = new HashSet<string> { Aceptar, FalsoPositivo, Reabrir };
}

// ---------- Salud de la configuración ----------
[RequierePermiso("dash.auditor_config")]
public sealed record ObtenerSaludConfiguracionQuery(bool IncluirCerrados = false) : IRequest<Result<SaludConfiguracion>>;

public sealed class ObtenerSaludConfiguracionHandler(ISesionUsuario sesion, IModulosEmpresa modulos, IRepositorioHallazgos hallazgos)
    : IRequestHandler<ObtenerSaludConfiguracionQuery, Result<SaludConfiguracion>>
{
    public const string Modulo = "AUDITOR_CONFIG";

    public async Task<Result<SaludConfiguracion>> Handle(ObtenerSaludConfiguracionQuery request, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId) return Result<SaludConfiguracion>.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        if (!await modulos.EstaActivoAsync(empresaId, Modulo, ct).ConfigureAwait(false))
            return Result<SaludConfiguracion>.Falla(Error.Conflicto("MODULO_INACTIVO", "El auditor de configuración no está activo para su empresa."));
        return Result<SaludConfiguracion>.Ok(await hallazgos.ObtenerAsync(request.IncluirCerrados, ct).ConfigureAwait(false));
    }
}

// ---------- Cumplimiento de precios ----------
[RequierePermiso("dash.farmacia")]
public sealed record ObtenerCumplimientoPreciosQuery(bool IncluirCerrados = false) : IRequest<Result<IReadOnlyList<HallazgoTarifarioVista>>>;

public sealed class ObtenerCumplimientoPreciosHandler(ISesionUsuario sesion, IModulosEmpresa modulos, IRepositorioHallazgos hallazgos)
    : IRequestHandler<ObtenerCumplimientoPreciosQuery, Result<IReadOnlyList<HallazgoTarifarioVista>>>
{
    public const string Modulo = "FARMACIA";

    public async Task<Result<IReadOnlyList<HallazgoTarifarioVista>>> Handle(ObtenerCumplimientoPreciosQuery request, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId) return Result<IReadOnlyList<HallazgoTarifarioVista>>.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        if (!await modulos.EstaActivoAsync(empresaId, Modulo, ct).ConfigureAwait(false))
            return Result<IReadOnlyList<HallazgoTarifarioVista>>.Falla(Error.Conflicto("MODULO_INACTIVO", "El módulo de farmacia no está activo para su empresa."));
        return Result<IReadOnlyList<HallazgoTarifarioVista>>.Ok(await hallazgos.ObtenerTarifariosAsync(request.IncluirCerrados, ct).ConfigureAwait(false));
    }
}

// ---------- Aceptar, marcar falso positivo o reabrir ----------
/// <summary>
/// ACEPTAR (riesgo asumido) y FALSO_POSITIVO exigen justificación y suprimen el hallazgo: el auditor no lo vuelve a crear.
/// REABRIR devuelve un hallazgo suprimido a ABIERTO. Todo cambio queda en aud.CambioAdministrativo.
/// </summary>
[RequierePermiso("hallazgos.gestionar")]
public sealed record CambiarEstadoHallazgoCommand(long HallazgoId, string Accion, string? Justificacion, OrigenHallazgo Origen = OrigenHallazgo.Configuracion) : IRequest<Result>;

public sealed class CambiarEstadoHallazgoValidator : AbstractValidator<CambiarEstadoHallazgoCommand>
{
    public CambiarEstadoHallazgoValidator()
    {
        RuleFor(x => x.Accion).Must(a => AccionesHallazgo.Todas.Contains(a)).WithErrorCode(CodigosAuditoria.AccionInvalida).WithMessage("Acción no válida.");
        RuleFor(x => x.Justificacion).MaximumLength(500).WithErrorCode("JUSTIFICACION_LARGA").WithMessage("La justificación admite hasta 500 caracteres.");
        RuleFor(x => x.Justificacion).Must(j => !string.IsNullOrWhiteSpace(j) && j.Trim().Length >= 10)
            .When(x => x.Accion is AccionesHallazgo.Aceptar or AccionesHallazgo.FalsoPositivo)
            .WithErrorCode(CodigosAuditoria.JustificacionRequerida).WithMessage("Escriba una justificación de al menos 10 caracteres.");
    }
}

public sealed class CambiarEstadoHallazgoHandler(ISesionUsuario sesion, IRepositorioHallazgos hallazgos, IAuditoriaAdministrativa auditoria)
    : IRequestHandler<CambiarEstadoHallazgoCommand, Result>
{
    public async Task<Result> Handle(CambiarEstadoHallazgoCommand request, CancellationToken ct)
    {
        if (sesion.UsuarioId is not { } usuarioId || sesion.EmpresaId is not { } empresaId) return Result.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        var h = await hallazgos.ObtenerEstadoAsync(request.Origen, request.HallazgoId, ct).ConfigureAwait(false);   // la RLS limita a la empresa
        if (h is null) return Result.Falla(Error.NoEncontrado("El hallazgo no existe."));

        var (nuevo, permitidos) = request.Accion switch
        {
            AccionesHallazgo.Aceptar => (EstadosHallazgo.Aceptado, new[] { EstadosHallazgo.Nuevo, EstadosHallazgo.Abierto }),
            AccionesHallazgo.FalsoPositivo => (EstadosHallazgo.FalsoPositivo, new[] { EstadosHallazgo.Nuevo, EstadosHallazgo.Abierto }),
            _ => (EstadosHallazgo.Abierto, new[] { EstadosHallazgo.Aceptado, EstadosHallazgo.FalsoPositivo }),
        };
        var justificacion = request.Accion == AccionesHallazgo.Reabrir ? null : request.Justificacion!.Trim();
        if (!permitidos.Contains(h.Estado) || !await hallazgos.CambiarEstadoAsync(request.Origen, h.HallazgoId, nuevo, justificacion, usuarioId, permitidos, ct).ConfigureAwait(false))
            return Result.Falla(Error.Conflicto(CodigosAuditoria.EstadoNoPermitido, $"El hallazgo está «{h.Estado}»: no se puede aplicar «{request.Accion}»."));

        await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, usuarioId, request.Origen == OrigenHallazgo.Tarifario ? "fact.HallazgoTarifario" : "fact.HallazgoConfiguracion", $"{h.Codigo}:{h.ClaveEntidad}", request.Accion,
            JsonSerializer.Serialize(new { estado = h.Estado }), JsonSerializer.Serialize(new { estado = nuevo }), justificacion), ct).ConfigureAwait(false);
        return Result.Ok();
    }
}
