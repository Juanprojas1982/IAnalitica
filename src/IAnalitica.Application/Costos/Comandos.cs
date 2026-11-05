using System.Text.Json;
using FluentValidation;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;

namespace IAnalitica.Application.Costos;

public static class CodigosCostos
{
    public const string BaseInvalida = "BASE_INVALIDA";
    public const string BaseManual = "BASE_MANUAL";
    public const string CentroNoExiste = "CENTRO_NO_EXISTE";
    public const string SinCentros = "SIN_CENTROS_CLASIFICADOS";
    public const string RangoInvalido = "RANGO_INVALIDO";
}

public static class PeriodosCosto
{
    public static bool EsValido(int periodo) => periodo is >= 200001 and <= 210012 && periodo % 100 is >= 1 and <= 12;

    /// <summary>Periodos yyyymm entre dos periodos, ambos incluidos.</summary>
    public static IEnumerable<int> Entre(int desde, int hasta)
    {
        for (var anio = desde / 100; ; anio++)
            for (var mes = anio == desde / 100 ? desde % 100 : 1; mes <= 12; mes++)
            {
                var p = anio * 100 + mes;
                if (p > hasta) yield break;
                yield return p;
            }
    }
}

// ---------- Ver las reglas ----------
[RequierePermiso("admin.usuarios")]
public sealed record ObtenerReglasCostoQuery : IRequest<Result<ReglasCostoVista>>;

public sealed record ReglasCostoVista(IReadOnlyList<CentroConRegla> Centros, IReadOnlyList<BaseCosto> Bases);

public sealed class ObtenerReglasCostoHandler(IRepositorioCostos costos) : IRequestHandler<ObtenerReglasCostoQuery, Result<ReglasCostoVista>>
{
    public async Task<Result<ReglasCostoVista>> Handle(ObtenerReglasCostoQuery request, CancellationToken ct)
        => new ReglasCostoVista(await costos.ListarCentrosAsync(ct).ConfigureAwait(false), await costos.ListarBasesAsync(ct).ConfigureAwait(false));
}

// ---------- Definir o cambiar la regla de un centro ----------
/// <summary>Solo bases CALCULADAS: las MANUALES (m², kilos de ropa, porcentaje) necesitan que se carguen sus valores y esa carga aún no existe.</summary>
[RequierePermiso("admin.usuarios")]
public sealed record DefinirReglaCostoCommand(int CentroCostoId, short Orden, string BaseCodigo, int VigenteDesde) : IRequest<Result>;

public sealed class DefinirReglaCostoValidator : AbstractValidator<DefinirReglaCostoCommand>
{
    public DefinirReglaCostoValidator()
    {
        RuleFor(x => x.Orden).InclusiveBetween((short)1, (short)999).WithErrorCode("ORDEN_INVALIDO").WithMessage("El orden debe estar entre 1 y 999.");
        RuleFor(x => x.VigenteDesde).Must(PeriodosCosto.EsValido).WithErrorCode("PERIODO_INVALIDO").WithMessage("El periodo debe ser aaaamm (por ejemplo 202601).");
        RuleFor(x => x.BaseCodigo).NotEmpty().WithErrorCode(CodigosCostos.BaseInvalida).WithMessage("Elija la base de distribución.");
    }
}

public sealed class DefinirReglaCostoHandler(ISesionUsuario sesion, IRepositorioCostos costos, IAuditoriaAdministrativa auditoria) : IRequestHandler<DefinirReglaCostoCommand, Result>
{
    public async Task<Result> Handle(DefinirReglaCostoCommand request, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId || sesion.UsuarioId is not { } usuarioId) return Result.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        var bases = await costos.ListarBasesAsync(ct).ConfigureAwait(false);
        var b = bases.FirstOrDefault(x => x.Codigo == request.BaseCodigo);
        if (b is null) return Result.Falla(Error.Validacion(CodigosCostos.BaseInvalida, "La base de distribución no existe."));
        if (b.Origen != "CALCULADA")
            return Result.Falla(Error.Conflicto(CodigosCostos.BaseManual, $"La base «{b.Nombre}» es manual: sus valores deben cargarse aparte y esa carga todavía no está disponible. Use una base calculada."));
        if (!await costos.ExisteCentroAsync(request.CentroCostoId, ct).ConfigureAwait(false)) return Result.Falla(Error.NoEncontrado("El centro de costo no existe."));

        await costos.GuardarAsync(empresaId, new ReglaCosto(request.CentroCostoId, request.Orden, request.BaseCodigo, request.VigenteDesde), ct).ConfigureAwait(false);
        await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, usuarioId, "cfg.CostoRegla", $"{request.CentroCostoId}:{request.VigenteDesde}", "DEFINIR", null,
            JsonSerializer.Serialize(new { request.Orden, request.BaseCodigo }), null), ct).ConfigureAwait(false);
        return Result.Ok();
    }
}

// ---------- Quitar una regla ----------
[RequierePermiso("admin.usuarios")]
public sealed record QuitarReglaCostoCommand(int CentroCostoId, int VigenteDesde) : IRequest<Result>;

public sealed class QuitarReglaCostoHandler(ISesionUsuario sesion, IRepositorioCostos costos, IAuditoriaAdministrativa auditoria) : IRequestHandler<QuitarReglaCostoCommand, Result>
{
    public async Task<Result> Handle(QuitarReglaCostoCommand request, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId || sesion.UsuarioId is not { } usuarioId) return Result.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        if (!await costos.QuitarAsync(request.CentroCostoId, request.VigenteDesde, ct).ConfigureAwait(false)) return Result.Falla(Error.NoEncontrado("La regla no existe."));
        await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, usuarioId, "cfg.CostoRegla", $"{request.CentroCostoId}:{request.VigenteDesde}", "QUITAR", null, null, null), ct).ConfigureAwait(false);
        return Result.Ok();
    }
}

// ---------- Reglas por defecto según el tipo de centro ----------
/// <summary>
/// ADMINISTRATIVO reparte primero (orden 1) por costo de personal; APOYO después (orden 2) por actividades (24 §5.4).
/// Lo que ya tiene una regla vigente no se toca. Depende de que los centros estén clasificados: si todos son PRODUCTIVO no hay nada que sugerir.
/// </summary>
[RequierePermiso("admin.usuarios")]
public sealed record AplicarReglasPorDefectoCommand(int VigenteDesde) : IRequest<Result<int>>;

public sealed class AplicarReglasPorDefectoValidator : AbstractValidator<AplicarReglasPorDefectoCommand>
{
    public AplicarReglasPorDefectoValidator() => RuleFor(x => x.VigenteDesde).Must(PeriodosCosto.EsValido).WithErrorCode("PERIODO_INVALIDO").WithMessage("El periodo debe ser aaaamm (por ejemplo 202601).");
}

public sealed class AplicarReglasPorDefectoHandler(ISesionUsuario sesion, IRepositorioCostos costos, IAuditoriaAdministrativa auditoria) : IRequestHandler<AplicarReglasPorDefectoCommand, Result<int>>
{
    public async Task<Result<int>> Handle(AplicarReglasPorDefectoCommand request, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId || sesion.UsuarioId is not { } usuarioId) return Result<int>.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        var centros = await costos.ListarCentrosClasificadosAsync(ct).ConfigureAwait(false);
        var conRegla = (await costos.ListarCentrosAsync(ct).ConfigureAwait(false)).Where(c => c.BaseCodigo is not null).Select(c => c.CentroCostoId).ToHashSet();
        var nuevas = centros.Where(c => !conRegla.Contains(c.CentroCostoId)).ToList();
        if (centros.Count == 0)
            return Result<int>.Falla(Error.Conflicto(CodigosCostos.SinCentros, "Ningún centro de costo está clasificado como administrativo o de apoyo: defina las reglas a mano."));
        foreach (var c in nuevas)
        {
            var regla = c.TipoCC == "ADMINISTRATIVO" ? new ReglaCosto(c.CentroCostoId, 1, "COSTO_PERSONAL", request.VigenteDesde) : new ReglaCosto(c.CentroCostoId, 2, "ACTIVIDADES", request.VigenteDesde);
            await costos.GuardarAsync(empresaId, regla, ct).ConfigureAwait(false);
        }
        if (nuevas.Count > 0)
            await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, usuarioId, "cfg.CostoRegla", $"defecto:{request.VigenteDesde}", "REGLAS_POR_DEFECTO", null,
                JsonSerializer.Serialize(new { creadas = nuevas.Count }), null), ct).ConfigureAwait(false);
        return nuevas.Count;
    }
}

// ---------- Recalcular ----------
[RequierePermiso("admin.usuarios")]
public sealed record RecalcularCostosCommand(int Desde, int Hasta) : IRequest<Result<int>>;

public sealed class RecalcularCostosValidator : AbstractValidator<RecalcularCostosCommand>
{
    public const int MaxPeriodos = 24;

    public RecalcularCostosValidator()
    {
        RuleFor(x => x.Desde).Must(PeriodosCosto.EsValido).WithErrorCode("PERIODO_INVALIDO").WithMessage("El periodo inicial debe ser aaaamm (por ejemplo 202601).");
        RuleFor(x => x.Hasta).Must(PeriodosCosto.EsValido).WithErrorCode("PERIODO_INVALIDO").WithMessage("El periodo final debe ser aaaamm (por ejemplo 202601).");
        RuleFor(x => x).Must(x => x.Hasta >= x.Desde && PeriodosCosto.Entre(x.Desde, x.Hasta).Count() <= MaxPeriodos)
            .When(x => PeriodosCosto.EsValido(x.Desde) && PeriodosCosto.EsValido(x.Hasta))
            .WithErrorCode(CodigosCostos.RangoInvalido).WithMessage($"El rango debe ser ascendente y de máximo {MaxPeriodos} meses.");
    }
}

public sealed class RecalcularCostosHandler(ISesionUsuario sesion, IColaTrabajos cola) : IRequestHandler<RecalcularCostosCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RecalcularCostosCommand request, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId) return Result<int>.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        var n = 0;
        foreach (var periodo in PeriodosCosto.Entre(request.Desde, request.Hasta))
            if (await cola.EncolarAsync(new TrabajoPendiente(TiposTrabajo.CalcularCostos, empresaId, $"EMP:{empresaId}|COSTOS|{periodo}", $"{{\"periodo\":{periodo}}}", sesion.UsuarioId, Prioridad: 8), ct).ConfigureAwait(false)) n++;
        return n;
    }
}
