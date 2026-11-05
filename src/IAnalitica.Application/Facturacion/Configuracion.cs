using FluentValidation;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;

namespace IAnalitica.Application.Facturacion;

// ---------- Vistas (DTO sin datos personales ni textos de usuarios) ----------
public sealed record PlanFacturacionVista(short PlanId, string Codigo, string Nombre, decimal? CanonVigenteCop, DateTime? CanonDesde);
public sealed record CanonPlanVista(short PlanId, DateTime VigenteDesde, decimal CanonMensualCop);
public sealed record CondicionVista(int CondicionId, int EmpresaId, DateTime VigenteDesde, DateTime? VigenteHasta, short PlanId, string PlanNombre,
    decimal? CanonPactadoCop, short? DiasPago, bool? RequiereAprobacion, bool Facturable, string? ReferenciaContrato);
public sealed record ParametroVista(string Clave, string Valor, string? Descripcion);
public sealed record EmpresaFacturacionVista(int EmpresaId, string Nit, string RazonSocial, bool Activa);

public static class CodigosFacturacion
{
    public const string NoAutorizado = "NO_AUTORIZADO";
    public const string Vigencia = "VIGENCIA_INVALIDA";
    public const string Periodo = "PERIODO_CERRADO";
    public const string Parametro = "PARAMETRO_INVALIDO";
    public const string Canon = "CANON_FACTURADO";
    public const string Estado = "ESTADO_INVALIDO";
    public const string Pago = "PAGO_INVALIDO";
    public const string Motivo = "MOTIVO_INVALIDO";
    public const string Empresa = "EMPRESA_INVALIDA";
}

/// <summary>Configuración comercial: canon por plan, condición por empresa y parámetros. Las escrituras pasan por los procedimientos de fac.* (08 §30.3).</summary>
public interface IRepositorioFacturacionConfig
{
    Task<IReadOnlyList<PlanFacturacionVista>> ListarPlanesAsync(CancellationToken ct);
    Task<IReadOnlyList<CanonPlanVista>> ListarCanonesAsync(short planId, CancellationToken ct);
    Task<Result> GuardarCanonPlanAsync(short planId, DateOnly vigenteDesde, decimal canonMensualCop, int usuarioId, CancellationToken ct);
    Task<IReadOnlyList<CondicionVista>> ListarCondicionesAsync(int empresaId, CancellationToken ct);
    Task<Result<int>> GuardarCondicionAsync(GuardarCondicionComercialCommand c, int usuarioId, CancellationToken ct);
    Task<IReadOnlyList<ParametroVista>> ListarParametrosAsync(CancellationToken ct);
    Task<IReadOnlyList<EmpresaFacturacionVista>> ListarEmpresasAsync(CancellationToken ct);
    Task<Result> GuardarParametroAsync(string clave, string valor, int usuarioId, CancellationToken ct);
}

internal static class AccesoFacturacion
{
    /// <summary>Excepción acotada a «el EmpresaId no viene del cliente» (08 §32.2): solo el superadmin actúa sobre una empresa elegida.</summary>
    public static bool EsSuperadmin(ISesionUsuario s, out int usuarioId)
    {
        usuarioId = s.UsuarioId ?? 0;
        return s.EsSuperAdmin && s.UsuarioId is not null;
    }
}

// ---------- Canon por plan ----------
[RequierePermiso("fac.administrar")]
public sealed record GuardarCanonPlanCommand(short PlanId, DateOnly VigenteDesde, decimal CanonMensualCop) : IRequest<Result>;

public sealed class GuardarCanonPlanValidator : AbstractValidator<GuardarCanonPlanCommand>
{
    public GuardarCanonPlanValidator()
    {
        RuleFor(x => x.VigenteDesde).Must(d => d.Day == 1).WithErrorCode(CodigosFacturacion.Vigencia).WithMessage("El canon de un plan rige desde el día 1 de un mes.");
        RuleFor(x => x.CanonMensualCop).GreaterThanOrEqualTo(0).WithErrorCode(CodigosFacturacion.Canon).WithMessage("El canon no puede ser negativo.");
    }
}

public sealed class GuardarCanonPlanHandler(ISesionUsuario sesion, IRepositorioFacturacionConfig repo) : IRequestHandler<GuardarCanonPlanCommand, Result>
{
    public Task<Result> Handle(GuardarCanonPlanCommand r, CancellationToken ct)
        => AccesoFacturacion.EsSuperadmin(sesion, out var u)
            ? repo.GuardarCanonPlanAsync(r.PlanId, r.VigenteDesde, r.CanonMensualCop, u, ct)
            : Task.FromResult(Result.Falla(Error.NoAutorizado()));
}

// ---------- Condición comercial de una empresa ----------
[RequierePermiso("fac.administrar")]
public sealed record GuardarCondicionComercialCommand(int EmpresaId, DateOnly VigenteDesde, short PlanId, decimal? CanonPactadoCop, short? DiasPago,
    bool? RequiereAprobacion, bool Facturable, string? ReferenciaContrato) : IRequest<Result<int>>;

public sealed class GuardarCondicionComercialValidator : AbstractValidator<GuardarCondicionComercialCommand>
{
    public GuardarCondicionComercialValidator()
    {
        RuleFor(x => x.EmpresaId).GreaterThan(0).WithErrorCode(CodigosFacturacion.Empresa).WithMessage("Elija la empresa.");
        RuleFor(x => x.CanonPactadoCop).GreaterThanOrEqualTo(0).When(x => x.CanonPactadoCop is not null).WithErrorCode(CodigosFacturacion.Canon).WithMessage("El canon no puede ser negativo.");
        RuleFor(x => x.DiasPago).InclusiveBetween((short)0, (short)180).When(x => x.DiasPago is not null).WithErrorCode(CodigosFacturacion.Vigencia).WithMessage("Los días de pago van de 0 a 180.");
        RuleFor(x => x.ReferenciaContrato).MaximumLength(60);
    }
}

public sealed class GuardarCondicionComercialHandler(ISesionUsuario sesion, IRepositorioFacturacionConfig repo) : IRequestHandler<GuardarCondicionComercialCommand, Result<int>>
{
    public Task<Result<int>> Handle(GuardarCondicionComercialCommand r, CancellationToken ct)
        => AccesoFacturacion.EsSuperadmin(sesion, out var u)
            ? repo.GuardarCondicionAsync(r, u, ct)
            : Task.FromResult(Result<int>.Falla(Error.NoAutorizado()));
}

// ---------- Parámetros globales ----------
[RequierePermiso("fac.administrar")]
public sealed record GuardarParametroFacturacionCommand(string Clave, string Valor) : IRequest<Result>;

public sealed class GuardarParametroFacturacionValidator : AbstractValidator<GuardarParametroFacturacionCommand>
{
    public GuardarParametroFacturacionValidator()
    {
        RuleFor(x => x.Clave).NotEmpty().MaximumLength(40).WithErrorCode(CodigosFacturacion.Parametro);
        RuleFor(x => x.Valor).NotEmpty().MaximumLength(500).WithErrorCode(CodigosFacturacion.Parametro).WithMessage("El valor es obligatorio (máximo 500 caracteres).");
    }
}

public sealed class GuardarParametroFacturacionHandler(ISesionUsuario sesion, IRepositorioFacturacionConfig repo) : IRequestHandler<GuardarParametroFacturacionCommand, Result>
{
    public Task<Result> Handle(GuardarParametroFacturacionCommand r, CancellationToken ct)
        => AccesoFacturacion.EsSuperadmin(sesion, out var u)
            ? repo.GuardarParametroAsync(r.Clave, r.Valor, u, ct)
            : Task.FromResult(Result.Falla(Error.NoAutorizado()));
}

// ---------- Consultas ----------
[RequierePermiso("fac.administrar")]
public sealed record ListarPlanesFacturacionQuery : IRequest<Result<IReadOnlyList<PlanFacturacionVista>>>;

public sealed class ListarPlanesFacturacionHandler(ISesionUsuario sesion, IRepositorioFacturacionConfig repo) : IRequestHandler<ListarPlanesFacturacionQuery, Result<IReadOnlyList<PlanFacturacionVista>>>
{
    public async Task<Result<IReadOnlyList<PlanFacturacionVista>>> Handle(ListarPlanesFacturacionQuery r, CancellationToken ct)
        => sesion.EsSuperAdmin ? Result<IReadOnlyList<PlanFacturacionVista>>.Ok(await repo.ListarPlanesAsync(ct).ConfigureAwait(false)) : Result<IReadOnlyList<PlanFacturacionVista>>.Falla(Error.NoAutorizado());
}

[RequierePermiso("fac.administrar")]
public sealed record ListarCanonesPlanQuery(short PlanId) : IRequest<Result<IReadOnlyList<CanonPlanVista>>>;

public sealed class ListarCanonesPlanHandler(ISesionUsuario sesion, IRepositorioFacturacionConfig repo) : IRequestHandler<ListarCanonesPlanQuery, Result<IReadOnlyList<CanonPlanVista>>>
{
    public async Task<Result<IReadOnlyList<CanonPlanVista>>> Handle(ListarCanonesPlanQuery r, CancellationToken ct)
        => sesion.EsSuperAdmin ? Result<IReadOnlyList<CanonPlanVista>>.Ok(await repo.ListarCanonesAsync(r.PlanId, ct).ConfigureAwait(false)) : Result<IReadOnlyList<CanonPlanVista>>.Falla(Error.NoAutorizado());
}

[RequierePermiso("fac.administrar")]
public sealed record ListarCondicionesComercialesQuery(int EmpresaId) : IRequest<Result<IReadOnlyList<CondicionVista>>>;

public sealed class ListarCondicionesComercialesHandler(ISesionUsuario sesion, IRepositorioFacturacionConfig repo) : IRequestHandler<ListarCondicionesComercialesQuery, Result<IReadOnlyList<CondicionVista>>>
{
    public async Task<Result<IReadOnlyList<CondicionVista>>> Handle(ListarCondicionesComercialesQuery r, CancellationToken ct)
        => sesion.EsSuperAdmin ? Result<IReadOnlyList<CondicionVista>>.Ok(await repo.ListarCondicionesAsync(r.EmpresaId, ct).ConfigureAwait(false)) : Result<IReadOnlyList<CondicionVista>>.Falla(Error.NoAutorizado());
}

[RequierePermiso("fac.administrar")]
public sealed record ListarParametrosFacturacionQuery : IRequest<Result<IReadOnlyList<ParametroVista>>>;

public sealed class ListarParametrosFacturacionHandler(ISesionUsuario sesion, IRepositorioFacturacionConfig repo) : IRequestHandler<ListarParametrosFacturacionQuery, Result<IReadOnlyList<ParametroVista>>>
{
    public async Task<Result<IReadOnlyList<ParametroVista>>> Handle(ListarParametrosFacturacionQuery r, CancellationToken ct)
        => sesion.EsSuperAdmin ? Result<IReadOnlyList<ParametroVista>>.Ok(await repo.ListarParametrosAsync(ct).ConfigureAwait(false)) : Result<IReadOnlyList<ParametroVista>>.Falla(Error.NoAutorizado());
}

[RequierePermiso("fac.administrar")]
public sealed record ListarEmpresasFacturacionQuery : IRequest<Result<IReadOnlyList<EmpresaFacturacionVista>>>;

public sealed class ListarEmpresasFacturacionHandler(ISesionUsuario sesion, IRepositorioFacturacionConfig repo) : IRequestHandler<ListarEmpresasFacturacionQuery, Result<IReadOnlyList<EmpresaFacturacionVista>>>
{
    public async Task<Result<IReadOnlyList<EmpresaFacturacionVista>>> Handle(ListarEmpresasFacturacionQuery r, CancellationToken ct)
        => sesion.EsSuperAdmin ? Result<IReadOnlyList<EmpresaFacturacionVista>>.Ok(await repo.ListarEmpresasAsync(ct).ConfigureAwait(false)) : Result<IReadOnlyList<EmpresaFacturacionVista>>.Falla(Error.NoAutorizado());
}
