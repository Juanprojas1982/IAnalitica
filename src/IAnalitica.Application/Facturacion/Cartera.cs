using FluentValidation;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using IAnalitica.Domain.Facturacion;

namespace IAnalitica.Application.Facturacion;

// ---------- Vistas (sin SoporteRuta, sin textos internos: la RLS es por fila, no por columna; 08 §30.4) ----------
public sealed record CuentaCobroResumen(int CuentaCobroId, int EmpresaId, string EmpresaNombre, int PeriodoKey, string? Numero, string Estado,
    decimal TotalCop, decimal PagadoCop, decimal SaldoCop, DateTime? FechaEmision, DateTime? FechaVencimiento, bool EnMora, string? Edad, string? MotivosRetencion);
public sealed record LineaVista(string Concepto, int DiasFacturados, int DiasPeriodo, decimal ValorCop);
public sealed record EnvioVista(Guid EnvioId, int CuentaCobroId, string? CuentaNumero, string Tipo, string? Hito, string Estado, bool EsReenvio, bool ConAdjunto,
    byte Intentos, string? CodigoError, DateTime CreadoEn, DateTime? EnviadoEn);
public sealed record AplicacionVista(int AplicacionId, int PagoId, decimal ValorCop, DateTime AplicadaEn, bool Reversada);
public sealed record CuentaCobroDetalle(CuentaCobroResumen Cuenta, IReadOnlyList<LineaVista> Lineas, IReadOnlyList<EnvioVista> Envios,
    IReadOnlyList<AplicacionVista> Aplicaciones, int? ReemplazaA, bool PdfDisponible, string? MotivoAnulacion);
public sealed record PagoVista(int PagoId, int EmpresaId, string EmpresaNombre, string Clase, string? Medio, DateTime FechaPago, decimal ValorCop, string? Referencia,
    decimal ValorAplicadoCop, decimal SaldoSinAplicarCop, string Estado, string? Observacion);
public sealed record AlertaVista(int AlertaId, int? EmpresaId, string? EmpresaNombre, int? PeriodoKey, int? CuentaCobroId, string Codigo, string? Detalle, DateTime CreadaEn);
public sealed record EstadoCuentaEmpresa(decimal SaldoCop, decimal VencidoCop, decimal SaldoAFavorCop, IReadOnlyList<CuentaCobroResumen> Cuentas);
public sealed record CarteraFacturacion(DateOnly Corte, IReadOnlyDictionary<string, decimal> PorEdad, decimal TotalSaldoCop, IReadOnlyList<CuentaCobroResumen> Cuentas);
public sealed record FiltroCuentas(int? EmpresaId, int? PeriodoKey, string? Estado, bool SoloEmitidasYPagadas = false, bool SoloConSaldo = false);
public sealed record AplicacionSolicitada(int CuentaCobroId, decimal ValorCop);

public interface IRepositorioFacturacion
{
    Task<IReadOnlyList<CuentaCobroResumen>> ListarCuentasAsync(FiltroCuentas filtro, CancellationToken ct);
    Task<CuentaCobroDetalle?> ObtenerCuentaAsync(int cuentaCobroId, CancellationToken ct);
    Task<IReadOnlyList<PagoVista>> ListarPagosAsync(int? empresaId, CancellationToken ct);
    Task<decimal> SaldoAFavorAsync(int? empresaId, CancellationToken ct);
    Task<IReadOnlyList<EnvioVista>> ListarEnviosAsync(bool soloFallidos, CancellationToken ct);
    Task<IReadOnlyList<AlertaVista>> ListarAlertasAsync(CancellationToken ct);

    Task<Result<string>> EmitirAsync(int cuentaCobroId, byte[]? versionFila, int usuarioId, CancellationToken ct);
    Task<Result> DescartarBorradorAsync(int cuentaCobroId, string motivo, int usuarioId, CancellationToken ct);
    Task<Result> RegenerarBorradorAsync(int cuentaCobroId, int usuarioId, CancellationToken ct);
    Task<Result> AnularAsync(int cuentaCobroId, string motivo, bool emitirReemplazo, int usuarioId, CancellationToken ct);
    Task<Result> ReintentarEnvioAsync(Guid envioId, int usuarioId, CancellationToken ct);
    Task<Result> ReenviarAsync(int cuentaCobroId, int usuarioId, CancellationToken ct);
    Task<Result> MarcarEnvioRebotadoAsync(Guid envioId, string codigoError, int usuarioId, CancellationToken ct);
    Task<Result<int>> RegistrarPagoAsync(RegistrarPagoCommand pago, int usuarioId, CancellationToken ct);
    Task<Result> AplicarPagoAsync(int pagoId, int cuentaCobroId, decimal valorCop, int usuarioId, CancellationToken ct);
    Task<Result> AplicarAutomaticoAsync(int pagoId, int usuarioId, CancellationToken ct);
    Task<Result> ReversarAplicacionAsync(int aplicacionId, string motivo, int usuarioId, CancellationToken ct);
    Task<Result> AnularPagoAsync(int pagoId, string motivo, int usuarioId, CancellationToken ct);
    Task<Result> AtenderAlertaAsync(int alertaId, int usuarioId, CancellationToken ct);

    /// <summary>Ruta del PDF y estado de la cuenta, leídos con la RLS de quien consulta.</summary>
    Task<(string? Ruta, string? Numero, string Estado)?> ObtenerPdfAsync(int cuentaCobroId, CancellationToken ct);
    Task RegistrarDescargaPdfAsync(int usuarioId, int? empresaId, int cuentaCobroId, string? ip, CancellationToken ct);
}

internal static class Cartera
{
    public static bool Superadmin(ISesionUsuario s, out int usuarioId) => AccesoFacturacion.EsSuperadmin(s, out usuarioId);
    public static DateOnly Hoy(TimeProvider reloj) => DateOnly.FromDateTime(reloj.GetUtcNow().UtcDateTime.AddHours(-5));   // America/Bogota

    public static CuentaCobroResumen ConEdad(CuentaCobroResumen c, DateOnly hoy)
        => c.Estado == EstadosCuentaCobro.Emitida && c.FechaVencimiento is { } v
            ? c with { EnMora = EdadCartera.EnMora(DateOnly.FromDateTime(v), hoy, c.SaldoCop), Edad = EdadCartera.Rango(DateOnly.FromDateTime(v), hoy) }
            : c;
}

// ---------- Consultas del cliente y del superadmin ----------
[RequierePermiso("fac.consultar")]
public sealed record ListarCuentasCobroQuery(int? EmpresaId, int? PeriodoKey, string? Estado, bool SoloMora) : IRequest<Result<IReadOnlyList<CuentaCobroResumen>>>;

public sealed class ListarCuentasCobroHandler(ISesionUsuario sesion, IRepositorioFacturacion repo, TimeProvider reloj) : IRequestHandler<ListarCuentasCobroQuery, Result<IReadOnlyList<CuentaCobroResumen>>>
{
    public async Task<Result<IReadOnlyList<CuentaCobroResumen>>> Handle(ListarCuentasCobroQuery r, CancellationToken ct)
    {
        // El cliente ve solo lo suyo (la sesión manda, no el parámetro) y nunca borradores ni descartes.
        var super = sesion.EsSuperAdmin;
        if (!super && sesion.EmpresaId is null) return Result<IReadOnlyList<CuentaCobroResumen>>.Falla(Error.NoAutorizado());
        var filtro = new FiltroCuentas(super ? r.EmpresaId : sesion.EmpresaId, r.PeriodoKey, r.Estado, SoloEmitidasYPagadas: !super);
        var hoy = Cartera.Hoy(reloj);
        var lista = (await repo.ListarCuentasAsync(filtro, ct).ConfigureAwait(false)).Select(c => Cartera.ConEdad(c, hoy))
            .Where(c => super || EstadosCuentaCobro.VisibleParaCliente(c.Estado)).Where(c => !r.SoloMora || c.EnMora).ToList();
        return Result<IReadOnlyList<CuentaCobroResumen>>.Ok(lista);
    }
}

[RequierePermiso("fac.consultar")]
public sealed record ObtenerCuentaCobroQuery(int CuentaCobroId) : IRequest<Result<CuentaCobroDetalle>>;

public sealed class ObtenerCuentaCobroHandler(ISesionUsuario sesion, IRepositorioFacturacion repo, TimeProvider reloj) : IRequestHandler<ObtenerCuentaCobroQuery, Result<CuentaCobroDetalle>>
{
    public async Task<Result<CuentaCobroDetalle>> Handle(ObtenerCuentaCobroQuery r, CancellationToken ct)
    {
        var d = await repo.ObtenerCuentaAsync(r.CuentaCobroId, ct).ConfigureAwait(false);   // la RLS ya filtró por empresa
        if (d is null) return Result<CuentaCobroDetalle>.Falla(Error.NoEncontrado("La cuenta de cobro no existe."));
        if (!sesion.EsSuperAdmin && !EstadosCuentaCobro.VisibleParaCliente(d.Cuenta.Estado)) return Result<CuentaCobroDetalle>.Falla(Error.NoEncontrado("La cuenta de cobro no existe."));
        var cuenta = Cartera.ConEdad(d.Cuenta, Cartera.Hoy(reloj));
        // El cliente ve fecha, tipo y estado de sus envíos, no el detalle técnico.
        return Result<CuentaCobroDetalle>.Ok(d with { Cuenta = cuenta, Envios = sesion.EsSuperAdmin ? d.Envios : d.Envios.Select(e => e with { CodigoError = null }).ToList() });
    }
}

[RequierePermiso("fac.consultar")]
public sealed record EstadoCuentaEmpresaQuery : IRequest<Result<EstadoCuentaEmpresa>>;

public sealed class EstadoCuentaEmpresaHandler(ISesionUsuario sesion, IRepositorioFacturacion repo, TimeProvider reloj) : IRequestHandler<EstadoCuentaEmpresaQuery, Result<EstadoCuentaEmpresa>>
{
    public async Task<Result<EstadoCuentaEmpresa>> Handle(EstadoCuentaEmpresaQuery r, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId) return Result<EstadoCuentaEmpresa>.Falla(Error.NoAutorizado());
        var hoy = Cartera.Hoy(reloj);
        var cuentas = (await repo.ListarCuentasAsync(new FiltroCuentas(empresaId, null, null, SoloEmitidasYPagadas: true), ct).ConfigureAwait(false)).Select(c => Cartera.ConEdad(c, hoy)).ToList();
        var abiertas = cuentas.Where(c => c.Estado == EstadosCuentaCobro.Emitida).ToList();
        return Result<EstadoCuentaEmpresa>.Ok(new EstadoCuentaEmpresa(abiertas.Sum(c => c.SaldoCop), abiertas.Where(c => c.EnMora).Sum(c => c.SaldoCop),
            await repo.SaldoAFavorAsync(empresaId, ct).ConfigureAwait(false), cuentas));
    }
}

[RequierePermiso("fac.administrar")]
public sealed record CarteraFacturacionQuery(DateOnly? Corte) : IRequest<Result<CarteraFacturacion>>;

public sealed class CarteraFacturacionHandler(ISesionUsuario sesion, IRepositorioFacturacion repo, TimeProvider reloj) : IRequestHandler<CarteraFacturacionQuery, Result<CarteraFacturacion>>
{
    public async Task<Result<CarteraFacturacion>> Handle(CarteraFacturacionQuery r, CancellationToken ct)
    {
        if (!sesion.EsSuperAdmin) return Result<CarteraFacturacion>.Falla(Error.NoAutorizado());
        var corte = r.Corte ?? Cartera.Hoy(reloj);
        var cuentas = (await repo.ListarCuentasAsync(new FiltroCuentas(null, null, EstadosCuentaCobro.Emitida, SoloConSaldo: true), ct).ConfigureAwait(false)).Select(c => Cartera.ConEdad(c, corte)).ToList();
        var porEdad = cuentas.GroupBy(c => c.Edad ?? EdadCartera.AlDia).ToDictionary(g => g.Key, g => g.Sum(c => c.SaldoCop));
        return Result<CarteraFacturacion>.Ok(new CarteraFacturacion(corte, porEdad, cuentas.Sum(c => c.SaldoCop), cuentas));
    }
}

[RequierePermiso("fac.consultar")]
public sealed record ListarPagosQuery(int? EmpresaId) : IRequest<Result<IReadOnlyList<PagoVista>>>;

public sealed class ListarPagosHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<ListarPagosQuery, Result<IReadOnlyList<PagoVista>>>
{
    public async Task<Result<IReadOnlyList<PagoVista>>> Handle(ListarPagosQuery r, CancellationToken ct)
    {
        var empresa = sesion.EsSuperAdmin ? r.EmpresaId : sesion.EmpresaId;
        if (!sesion.EsSuperAdmin && empresa is null) return Result<IReadOnlyList<PagoVista>>.Falla(Error.NoAutorizado());
        return Result<IReadOnlyList<PagoVista>>.Ok(await repo.ListarPagosAsync(empresa, ct).ConfigureAwait(false));
    }
}

[RequierePermiso("fac.administrar")]
public sealed record ListarEnviosQuery(bool SoloFallidos) : IRequest<Result<IReadOnlyList<EnvioVista>>>;

public sealed class ListarEnviosHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<ListarEnviosQuery, Result<IReadOnlyList<EnvioVista>>>
{
    public async Task<Result<IReadOnlyList<EnvioVista>>> Handle(ListarEnviosQuery r, CancellationToken ct)
        => sesion.EsSuperAdmin ? Result<IReadOnlyList<EnvioVista>>.Ok(await repo.ListarEnviosAsync(r.SoloFallidos, ct).ConfigureAwait(false)) : Result<IReadOnlyList<EnvioVista>>.Falla(Error.NoAutorizado());
}

[RequierePermiso("fac.administrar")]
public sealed record ListarAlertasQuery : IRequest<Result<IReadOnlyList<AlertaVista>>>;

public sealed class ListarAlertasHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<ListarAlertasQuery, Result<IReadOnlyList<AlertaVista>>>
{
    public async Task<Result<IReadOnlyList<AlertaVista>>> Handle(ListarAlertasQuery r, CancellationToken ct)
        => sesion.EsSuperAdmin ? Result<IReadOnlyList<AlertaVista>>.Ok(await repo.ListarAlertasAsync(ct).ConfigureAwait(false)) : Result<IReadOnlyList<AlertaVista>>.Falla(Error.NoAutorizado());
}

/// <summary>Prueba la conexión SMTP sin enviar nada. Devuelve solo códigos (jamás la clave, el usuario ni textos del servidor).</summary>
[RequierePermiso("fac.administrar")]
public sealed record ProbarConexionCorreoCommand : IRequest<Result<ResultadoEnvioCorreo>>;

public sealed class ProbarConexionCorreoHandler(ISesionUsuario sesion, ICorreoSaliente correo) : IRequestHandler<ProbarConexionCorreoCommand, Result<ResultadoEnvioCorreo>>
{
    public async Task<Result<ResultadoEnvioCorreo>> Handle(ProbarConexionCorreoCommand r, CancellationToken ct)
        => sesion.EsSuperAdmin ? Result<ResultadoEnvioCorreo>.Ok(await correo.ProbarConexionAsync(ct).ConfigureAwait(false)) : Result<ResultadoEnvioCorreo>.Falla(Error.NoAutorizado());
}

// ---------- Cuenta de cobro: emitir, descartar, regenerar, anular ----------
[RequierePermiso("fac.administrar")]
public sealed record EmitirCuentaCobroCommand(int CuentaCobroId, byte[]? VersionFila) : IRequest<Result<string>>;

public sealed class EmitirCuentaCobroHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<EmitirCuentaCobroCommand, Result<string>>
{
    public Task<Result<string>> Handle(EmitirCuentaCobroCommand r, CancellationToken ct)
        => Cartera.Superadmin(sesion, out var u) ? repo.EmitirAsync(r.CuentaCobroId, r.VersionFila, u, ct) : Task.FromResult(Result<string>.Falla(Error.NoAutorizado()));
}

[RequierePermiso("fac.administrar")]
public sealed record DescartarBorradorCommand(int CuentaCobroId, string? Motivo) : IRequest<Result>;
public sealed class DescartarBorradorValidator : AbstractValidator<DescartarBorradorCommand>
{
    public DescartarBorradorValidator() => RuleFor(x => x.Motivo).Must(m => m is { } t && t.Trim().Length is >= 10 and <= 500).WithErrorCode(CodigosFacturacion.Motivo).WithMessage("El motivo debe tener entre 10 y 500 caracteres.");
}
public sealed class DescartarBorradorHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<DescartarBorradorCommand, Result>
{
    public Task<Result> Handle(DescartarBorradorCommand r, CancellationToken ct)
        => Cartera.Superadmin(sesion, out var u) ? repo.DescartarBorradorAsync(r.CuentaCobroId, r.Motivo!.Trim(), u, ct) : Task.FromResult(Result.Falla(Error.NoAutorizado()));
}

[RequierePermiso("fac.administrar")]
public sealed record RegenerarBorradorCommand(int CuentaCobroId) : IRequest<Result>;
public sealed class RegenerarBorradorHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<RegenerarBorradorCommand, Result>
{
    public Task<Result> Handle(RegenerarBorradorCommand r, CancellationToken ct)
        => Cartera.Superadmin(sesion, out var u) ? repo.RegenerarBorradorAsync(r.CuentaCobroId, u, ct) : Task.FromResult(Result.Falla(Error.NoAutorizado()));
}

/// <summary>Anula (reversando antes sus aplicaciones) y, si se pide, genera la CC que la reemplaza. Se avisa al cliente con un correo ANULACION sin adjunto.</summary>
[RequierePermiso("fac.administrar")]
public sealed record AnularCuentaCobroCommand(int CuentaCobroId, string? Motivo, bool EmitirReemplazo) : IRequest<Result>;
public sealed class AnularCuentaCobroValidator : AbstractValidator<AnularCuentaCobroCommand>
{
    public AnularCuentaCobroValidator() => RuleFor(x => x.Motivo).Must(m => m is { } t && t.Trim().Length is >= 10 and <= 500).WithErrorCode(CodigosFacturacion.Motivo).WithMessage("El motivo debe tener entre 10 y 500 caracteres.");
}
public sealed class AnularCuentaCobroHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<AnularCuentaCobroCommand, Result>
{
    public Task<Result> Handle(AnularCuentaCobroCommand r, CancellationToken ct)
        => Cartera.Superadmin(sesion, out var u) ? repo.AnularAsync(r.CuentaCobroId, r.Motivo!.Trim(), r.EmitirReemplazo, u, ct) : Task.FromResult(Result.Falla(Error.NoAutorizado()));
}

// ---------- Envíos ----------
[RequierePermiso("fac.administrar")]
public sealed record ReintentarEnvioCommand(Guid EnvioId) : IRequest<Result>;
public sealed class ReintentarEnvioHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<ReintentarEnvioCommand, Result>
{
    public Task<Result> Handle(ReintentarEnvioCommand r, CancellationToken ct)
        => Cartera.Superadmin(sesion, out var u) ? repo.ReintentarEnvioAsync(r.EnvioId, u, ct) : Task.FromResult(Result.Falla(Error.NoAutorizado()));
}

[RequierePermiso("fac.administrar")]
public sealed record ReenviarCuentaCobroCommand(int CuentaCobroId) : IRequest<Result>;
public sealed class ReenviarCuentaCobroHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<ReenviarCuentaCobroCommand, Result>
{
    public Task<Result> Handle(ReenviarCuentaCobroCommand r, CancellationToken ct)
        => Cartera.Superadmin(sesion, out var u) ? repo.ReenviarAsync(r.CuentaCobroId, u, ct) : Task.FromResult(Result.Falla(Error.NoAutorizado()));
}

[RequierePermiso("fac.administrar")]
public sealed record MarcarEnvioRebotadoCommand(Guid EnvioId, string CodigoError) : IRequest<Result>;
public sealed class MarcarEnvioRebotadoHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<MarcarEnvioRebotadoCommand, Result>
{
    public Task<Result> Handle(MarcarEnvioRebotadoCommand r, CancellationToken ct)
        => Cartera.Superadmin(sesion, out var u) ? repo.MarcarEnvioRebotadoAsync(r.EnvioId, r.CodigoError, u, ct) : Task.FromResult(Result.Falla(Error.NoAutorizado()));
}

[RequierePermiso("fac.administrar")]
public sealed record AtenderAlertaCommand(int AlertaId) : IRequest<Result>;
public sealed class AtenderAlertaHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<AtenderAlertaCommand, Result>
{
    public Task<Result> Handle(AtenderAlertaCommand r, CancellationToken ct)
        => Cartera.Superadmin(sesion, out var u) ? repo.AtenderAlertaAsync(r.AlertaId, u, ct) : Task.FromResult(Result.Falla(Error.NoAutorizado()));
}

// ---------- Pagos ----------
[RequierePermiso("fac.administrar")]
public sealed record RegistrarPagoCommand(int EmpresaId, string Clase, string? Medio, DateOnly FechaPago, decimal ValorCop, string? Referencia, string? Observacion, bool AplicarAutomatico)
    : IRequest<Result<int>>;

public sealed class RegistrarPagoValidator : AbstractValidator<RegistrarPagoCommand>
{
    public RegistrarPagoValidator()
    {
        RuleFor(x => x.EmpresaId).GreaterThan(0).WithErrorCode(CodigosFacturacion.Empresa).WithMessage("Elija la empresa.");
        RuleFor(x => x.Clase).Must(c => c is "RECAUDO" or "RETENCION" or "OTRO").WithErrorCode(CodigosFacturacion.Pago).WithMessage("La clase debe ser RECAUDO, RETENCION u OTRO.");
        RuleFor(x => x.Medio).Must(m => m is null or "TRANSFERENCIA" or "CONSIGNACION" or "PSE" or "CHEQUE" or "OTRO").WithErrorCode(CodigosFacturacion.Pago).WithMessage("Medio de pago no válido.");
        RuleFor(x => x.Medio).NotNull().When(x => x.Clase == "RECAUDO").WithErrorCode(CodigosFacturacion.Pago).WithMessage("El recaudo exige el medio de pago.");
        RuleFor(x => x.ValorCop).GreaterThan(0).WithErrorCode(CodigosFacturacion.Pago).WithMessage("El valor debe ser mayor que cero.");
        RuleFor(x => x.Referencia).MaximumLength(60).Must(SinNumerosLargos).WithErrorCode(CodigosFacturacion.Pago)
            .WithMessage("La referencia es el número del comprobante; nunca números de cuenta ni de tarjeta (10 o más dígitos seguidos).");
        RuleFor(x => x.Observacion).MaximumLength(300);
    }

    private static bool SinNumerosLargos(string? r) => r is null || !System.Text.RegularExpressions.Regex.IsMatch(r, @"\d{10,}");
}

public sealed class RegistrarPagoHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<RegistrarPagoCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RegistrarPagoCommand r, CancellationToken ct)
    {
        if (!Cartera.Superadmin(sesion, out var u)) return Result<int>.Falla(Error.NoAutorizado());
        var pago = await repo.RegistrarPagoAsync(r, u, ct).ConfigureAwait(false);
        if (!pago.EsExitoso || !r.AplicarAutomatico) return pago;
        var aplicado = await repo.AplicarAutomaticoAsync(pago.Valor, u, ct).ConfigureAwait(false);   // FIFO por vencimiento; lo que sobra queda como saldo a favor
        return aplicado.EsExitoso ? pago : Result<int>.Falla(aplicado.Errores);
    }
}

[RequierePermiso("fac.administrar")]
public sealed record AplicarPagoCommand(int PagoId, IReadOnlyList<AplicacionSolicitada> Aplicaciones) : IRequest<Result>;

public sealed class AplicarPagoValidator : AbstractValidator<AplicarPagoCommand>
{
    public AplicarPagoValidator()
    {
        RuleFor(x => x.Aplicaciones).NotEmpty().WithErrorCode(CodigosFacturacion.Pago).WithMessage("Indique al menos una cuenta.");
        RuleForEach(x => x.Aplicaciones).Must(a => a.ValorCop > 0).WithErrorCode(CodigosFacturacion.Pago).WithMessage("Cada valor debe ser mayor que cero.");
        RuleFor(x => x.Aplicaciones).Must(l => l.Select(a => a.CuentaCobroId).Distinct().Count() == l.Count).WithErrorCode(CodigosFacturacion.Pago).WithMessage("Una cuenta no puede repetirse.");
    }
}

public sealed class AplicarPagoHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<AplicarPagoCommand, Result>
{
    public async Task<Result> Handle(AplicarPagoCommand r, CancellationToken ct)
    {
        if (!Cartera.Superadmin(sesion, out var u)) return Result.Falla(Error.NoAutorizado());
        foreach (var a in r.Aplicaciones)
        {
            var res = await repo.AplicarPagoAsync(r.PagoId, a.CuentaCobroId, a.ValorCop, u, ct).ConfigureAwait(false);
            if (!res.EsExitoso) return res;
        }
        return Result.Ok();
    }
}

[RequierePermiso("fac.administrar")]
public sealed record ReversarAplicacionCommand(int AplicacionId, string? Motivo) : IRequest<Result>;
public sealed class ReversarAplicacionValidator : AbstractValidator<ReversarAplicacionCommand>
{
    public ReversarAplicacionValidator() => RuleFor(x => x.Motivo).Must(m => m is { } t && t.Trim().Length is >= 10 and <= 300).WithErrorCode(CodigosFacturacion.Motivo).WithMessage("El motivo debe tener entre 10 y 300 caracteres.");
}
public sealed class ReversarAplicacionHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<ReversarAplicacionCommand, Result>
{
    public Task<Result> Handle(ReversarAplicacionCommand r, CancellationToken ct)
        => Cartera.Superadmin(sesion, out var u) ? repo.ReversarAplicacionAsync(r.AplicacionId, r.Motivo!.Trim(), u, ct) : Task.FromResult(Result.Falla(Error.NoAutorizado()));
}

[RequierePermiso("fac.administrar")]
public sealed record AnularPagoCommand(int PagoId, string? Motivo) : IRequest<Result>;
public sealed class AnularPagoValidator : AbstractValidator<AnularPagoCommand>
{
    public AnularPagoValidator() => RuleFor(x => x.Motivo).Must(m => m is { } t && t.Trim().Length is >= 10 and <= 300).WithErrorCode(CodigosFacturacion.Motivo).WithMessage("El motivo debe tener entre 10 y 300 caracteres.");
}
public sealed class AnularPagoHandler(ISesionUsuario sesion, IRepositorioFacturacion repo) : IRequestHandler<AnularPagoCommand, Result>
{
    public Task<Result> Handle(AnularPagoCommand r, CancellationToken ct)
        => Cartera.Superadmin(sesion, out var u) ? repo.AnularPagoAsync(r.PagoId, r.Motivo!.Trim(), u, ct) : Task.FromResult(Result.Falla(Error.NoAutorizado()));
}

// ---------- Descarga del PDF ----------
public sealed record ArchivoCuentaCobro(Stream Contenido, string NombreArchivo);

/// <summary>Descarga el PDF emitido. El cliente solo accede al de su empresa (RLS) y nunca a un borrador; cada descarga queda en <c>aud.Acceso</c>.</summary>
[RequierePermiso("fac.consultar")]
public sealed record DescargarCuentaCobroPdfQuery(int CuentaCobroId, string? Ip) : IRequest<Result<ArchivoCuentaCobro>>;

public sealed class DescargarCuentaCobroPdfHandler(ISesionUsuario sesion, IRepositorioFacturacion repo, IAlmacenDocumentosFacturacion almacen)
    : IRequestHandler<DescargarCuentaCobroPdfQuery, Result<ArchivoCuentaCobro>>
{
    public async Task<Result<ArchivoCuentaCobro>> Handle(DescargarCuentaCobroPdfQuery r, CancellationToken ct)
    {
        if (sesion.UsuarioId is not { } usuarioId) return Result<ArchivoCuentaCobro>.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        var pdf = await repo.ObtenerPdfAsync(r.CuentaCobroId, ct).ConfigureAwait(false);   // la RLS oculta lo de otras empresas
        if (pdf is null || (!sesion.EsSuperAdmin && !EstadosCuentaCobro.VisibleParaCliente(pdf.Value.Estado)))
            return Result<ArchivoCuentaCobro>.Falla(Error.NoEncontrado("La cuenta de cobro no existe."));
        if (pdf.Value.Ruta is null) return Result<ArchivoCuentaCobro>.Falla(Error.Conflicto("PDF_NO_DISPONIBLE", "El PDF todavía no está disponible."));
        var flujo = await almacen.AbrirAsync(pdf.Value.Ruta, ct).ConfigureAwait(false);
        if (flujo is null) return Result<ArchivoCuentaCobro>.Falla(Error.Conflicto("PDF_NO_DISPONIBLE", "El archivo ya no está disponible."));
        await repo.RegistrarDescargaPdfAsync(usuarioId, sesion.EmpresaId, r.CuentaCobroId, r.Ip, ct).ConfigureAwait(false);
        return new ArchivoCuentaCobro(flujo, $"{pdf.Value.Numero ?? "cuenta-de-cobro"}.pdf");
    }
}
