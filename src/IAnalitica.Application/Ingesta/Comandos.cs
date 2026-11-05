using System.Text.Json;
using FluentValidation;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using IAnalitica.Ingesta.Contracts;

namespace IAnalitica.Application.Ingesta;

/// <summary>
/// Límites del contrato 05 §1. Las filas por lote y los bytes los hace cumplir la API antes de llegar aquí (413); se repiten para el dominio.
/// </summary>
public static class LimitesIngesta
{
    public const int MaxFilasPorLote = 5000;
    public const int MaxBytesLote = 10 * 1024 * 1024;
    public const int MaxRechazosConsulta = 1000;
}

// ---------- POST /v1/cargas ----------
public sealed record IniciarCargaCommand(SolicitudCarga Solicitud, string? TraceParent) : IRequest<Result<long>>;

public sealed class IniciarCargaCommandValidator : AbstractValidator<IniciarCargaCommand>
{
    public IniciarCargaCommandValidator()
    {
        RuleFor(x => x.Solicitud.Entidad).Must(e => CatalogoEntidades.Buscar(e) is not null)
            .WithErrorCode(CodigosError.EntidadNoSoportada).WithMessage("La entidad no está soportada por la plataforma.");
        RuleFor(x => x).Must(x => CatalogoEntidades.Buscar(x.Solicitud.Entidad)?.Modos.Contains(x.Solicitud.Modo) ?? true)
            .WithName("Modo").WithErrorCode(CodigosError.ContratoInvalido).WithMessage("El modo no es válido para la entidad.");
        RuleFor(x => x).Must(x => x.Solicitud.VentanaDesde is not null && x.Solicitud.VentanaHasta is not null)
            .When(x => x.Solicitud.Modo is Modos.Ventana or Modos.Inicial)
            .WithName("Ventana").WithErrorCode(CodigosError.ContratoInvalido).WithMessage("VENTANA e INICIAL exigen ventanaDesde y ventanaHasta.");
        RuleFor(x => x).Must(x => x.Solicitud.VentanaDesde!.Value.Date <= x.Solicitud.VentanaHasta!.Value.Date)
            .When(x => x.Solicitud.VentanaDesde is not null && x.Solicitud.VentanaHasta is not null)
            .WithName("Ventana").WithErrorCode(CodigosError.ContratoInvalido).WithMessage("ventanaDesde no puede ser posterior a ventanaHasta.");
        RuleFor(x => x).Must(x => x.Solicitud.Corte is not null)
            .When(x => x.Solicitud.Modo == Modos.Foto)
            .WithName("Corte").WithErrorCode(CodigosError.ContratoInvalido).WithMessage("FOTO exige corte.");
        RuleFor(x => x.Solicitud.VersionAgente).MaximumLength(20).WithErrorCode(CodigosError.ContratoInvalido).WithMessage("versionAgente admite máximo 20 caracteres.");
        RuleFor(x => x.Solicitud.VersionFuente).MaximumLength(30).WithErrorCode(CodigosError.ContratoInvalido).WithMessage("versionFuente admite máximo 30 caracteres.");
    }
}

public sealed class IniciarCargaHandler(ISesionUsuario sesion, IRepositorioCargas cargas) : IRequestHandler<IniciarCargaCommand, Result<long>>
{
    public async Task<Result<long>> Handle(IniciarCargaCommand request, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId) return Result<long>.Falla(Error.NoAutorizado("El token no identifica una empresa."));
        var s = request.Solicitud;
        var desde = s.VentanaDesde is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null;
        var hasta = s.VentanaHasta is { } h ? DateOnly.FromDateTime(h) : (DateOnly?)null;

        // 409: los hechos de un periodo cerrado en la plataforma no se reemplazan (contrato 05 §7)
        if (s.Modo is Modos.Ventana or Modos.Inicial && CatalogoEntidades.Buscar(s.Entidad) is { EsCatalogo: false }
            && await cargas.PeriodoCerradoAsync(empresaId, desde!.Value, hasta!.Value, ct).ConfigureAwait(false))
            return Result<long>.Falla(Error.Conflicto(CodigosError.PeriodoCerrado, "La ventana incluye un periodo cerrado en la plataforma."));

        var id = await cargas.CrearAsync(empresaId,
            new NuevaCarga(s.Entidad, s.Modo, desde, hasta, s.Corte is { } c ? DateOnly.FromDateTime(c) : null, s.VersionAgente, s.VersionFuente, request.TraceParent), ct)
            .ConfigureAwait(false);
        return id;
    }
}

// ---------- POST /v1/cargas/{id}/lotes/{n} ----------
/// <param name="FilasJson">Texto JSON de la lista «filas» tal como llegó (no se reserializa).</param>
public sealed record RecibirLoteCommand(long CargaId, int Numero, int Filas, string FilasJson) : IRequest<Result<RespuestaLote>>;

public sealed class RecibirLoteCommandValidator : AbstractValidator<RecibirLoteCommand>
{
    public RecibirLoteCommandValidator()
    {
        RuleFor(x => x.Numero).GreaterThanOrEqualTo(1).WithErrorCode(CodigosError.ContratoInvalido).WithMessage("El número de lote empieza en 1.");
        RuleFor(x => x.Filas).GreaterThanOrEqualTo(1).WithErrorCode(CodigosError.ContratoInvalido).WithMessage("El lote no trae filas.");
        RuleFor(x => x.Filas).LessThanOrEqualTo(LimitesIngesta.MaxFilasPorLote).WithErrorCode(CodigosError.LoteGrande)
            .WithMessage("El lote admite máximo 5000 filas.");
    }
}

public sealed class RecibirLoteHandler(ISesionUsuario sesion, IRepositorioCargas cargas) : IRequestHandler<RecibirLoteCommand, Result<RespuestaLote>>
{
    public async Task<Result<RespuestaLote>> Handle(RecibirLoteCommand request, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId) return Result<RespuestaLote>.Falla(Error.NoAutorizado("El token no identifica una empresa."));
        var carga = await cargas.ObtenerAsync(request.CargaId, ct).ConfigureAwait(false);
        if (carga is null) return Result<RespuestaLote>.Falla(Error.NoEncontrado("La carga no existe."));
        if (carga.Estado != EstadosCarga.Abierta)
            return Result<RespuestaLote>.Falla(Error.Conflicto(CodigosError.CargaNoAbierta, "La carga ya no recibe lotes."));

        var violaciones = ReglasPrivacidad.Revisar(carga.Entidad, request.FilasJson);
        if (violaciones.Count > 0)
        {
            await cargas.RechazarAsync(carga.CargaId, "Datos personales de pacientes en la carga (contrato 05 §4).", ct).ConfigureAwait(false);
            return Result<RespuestaLote>.Falla(new Error(CodigosError.ValidacionNegocio,
                "La carga se rechazó: un tercero paciente trae nombre. Los pacientes solo viajan con identificación anonimizada."));
        }

        await cargas.GuardarLoteAsync(empresaId, carga.CargaId, carga.Entidad, request.Numero, request.Filas, request.FilasJson, ct).ConfigureAwait(false);
        return new RespuestaLote { FilasRecibidas = request.Filas };
    }
}

/// <summary>Reglas que se verifican al recibir el lote, antes de guardarlo (contrato 05 §4).</summary>
public static class ReglasPrivacidad
{
    /// <summary>Terceros con <c>esPaciente = true</c> que traen <c>nombre</c>: devuelve su <c>codigoOrigen</c> (nunca el nombre).</summary>
    public static IReadOnlyList<string> Revisar(string entidad, string filasJson)
    {
        var infractores = new List<string>();
        if (entidad != "terceros") return infractores;
        using var doc = JsonDocument.Parse(filasJson);
        foreach (var fila in doc.RootElement.EnumerateArray())
        {
            if (fila.ValueKind != JsonValueKind.Object) continue;
            var paciente = fila.TryGetProperty("esPaciente", out var p) && p.ValueKind == JsonValueKind.True;
            var traeNombre = fila.TryGetProperty("nombre", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString());
            if (paciente && traeNombre)
                infractores.Add(fila.TryGetProperty("codigoOrigen", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "?");
        }
        return infractores;
    }
}

// ---------- POST /v1/cargas/{id}/cerrar ----------
public sealed record CerrarCargaCommand(long CargaId, CierreCarga Cierre, string? TraceParent) : IRequest<Result>;

public sealed class CerrarCargaCommandValidator : AbstractValidator<CerrarCargaCommand>
{
    public CerrarCargaCommandValidator()
    {
        RuleFor(x => x.Cierre.TotalLotes).GreaterThanOrEqualTo(0).WithErrorCode(CodigosError.ContratoInvalido).WithMessage("totalLotes no puede ser negativo.");
        RuleFor(x => x.Cierre.TotalFilas).GreaterThanOrEqualTo(0).WithErrorCode(CodigosError.ContratoInvalido).WithMessage("totalFilas no puede ser negativo.");
    }
}

public sealed class CerrarCargaHandler(ISesionUsuario sesion, IRepositorioCargas cargas) : IRequestHandler<CerrarCargaCommand, Result>
{
    public async Task<Result> Handle(CerrarCargaCommand request, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId) return Result.Falla(Error.NoAutorizado("El token no identifica una empresa."));
        var carga = await cargas.ObtenerAsync(request.CargaId, ct).ConfigureAwait(false);
        if (carga is null) return Result.Falla(Error.NoEncontrado("La carga no existe."));
        var entidad = CatalogoEntidades.Buscar(carga.Entidad)!;

        // Reintento tras perder la respuesta: cerrar es idempotente y repite el resultado de la primera vez.
        switch (carga.Estado)
        {
            case EstadosCarga.Cerrada or EstadosCarga.Procesando or EstadosCarga.Ok:
                return Result.Ok();
            case EstadosCarga.Rechazada when carga.Error?.StartsWith("CARGA_DUPLICADA", StringComparison.Ordinal) == true:
                return Result.Falla(Error.Conflicto(CodigosError.CargaDuplicada, "Ya existe una carga idéntica procesada."));
            case EstadosCarga.Rechazada:
                return Result.Falla(new Error(CodigosError.ControlNoCuadra, carga.Error ?? "La carga fue rechazada."));
            case EstadosCarga.Error:
                return Result.Falla(Error.Conflicto(CodigosError.CargaNoAbierta, "La carga terminó con error de procesamiento."));
        }

        var r = await cargas.CerrarAsync(empresaId, carga, request.Cierre, entidad.ColumnasSumaControl, request.TraceParent, ct).ConfigureAwait(false);
        return r.Resultado switch
        {
            ResultadoCierre.Encolada => Result.Ok(),
            ResultadoCierre.Duplicada => Result.Falla(Error.Conflicto(CodigosError.CargaDuplicada, r.Detalle ?? "Ya existe una carga idéntica procesada.")),
            ResultadoCierre.NoAbierta => Result.Falla(Error.Conflicto(CodigosError.CargaNoAbierta, "La carga ya no está abierta.")),
            _ => Result.Falla(new Error(CodigosError.ControlNoCuadra, r.Detalle ?? "Los totales o la suma de control no cuadran con lo recibido.")),
        };
    }
}

// ---------- GET ----------
public sealed record ConsultarCargaQuery(long CargaId) : IRequest<Result<EstadoCarga>>;
public sealed record ConsultarRechazosQuery(long CargaId) : IRequest<Result<IReadOnlyList<RechazoFila>>>;
public sealed record ConsultarEstadoQuery : IRequest<Result<EstadoPlataforma>>;

public sealed class ConsultarCargaHandler(IRepositorioCargas cargas) : IRequestHandler<ConsultarCargaQuery, Result<EstadoCarga>>
{
    public async Task<Result<EstadoCarga>> Handle(ConsultarCargaQuery request, CancellationToken ct)
        => await cargas.EstadoCargaAsync(request.CargaId, ct).ConfigureAwait(false) is { } e
            ? e
            : Result<EstadoCarga>.Falla(Error.NoEncontrado("La carga no existe."));
}

public sealed class ConsultarRechazosHandler(IRepositorioCargas cargas) : IRequestHandler<ConsultarRechazosQuery, Result<IReadOnlyList<RechazoFila>>>
{
    public async Task<Result<IReadOnlyList<RechazoFila>>> Handle(ConsultarRechazosQuery request, CancellationToken ct)
    {
        if (await cargas.ObtenerAsync(request.CargaId, ct).ConfigureAwait(false) is null)
            return Result<IReadOnlyList<RechazoFila>>.Falla(Error.NoEncontrado("La carga no existe."));
        return Result<IReadOnlyList<RechazoFila>>.Ok(await cargas.RechazosAsync(request.CargaId, LimitesIngesta.MaxRechazosConsulta, ct).ConfigureAwait(false));
    }
}

public sealed class ConsultarEstadoHandler(ISesionUsuario sesion, IRepositorioCargas cargas) : IRequestHandler<ConsultarEstadoQuery, Result<EstadoPlataforma>>
{
    public async Task<Result<EstadoPlataforma>> Handle(ConsultarEstadoQuery request, CancellationToken ct)
        => sesion.EmpresaId is { } e
            ? await cargas.EstadoPlataformaAsync(e, ct).ConfigureAwait(false)
            : Result<EstadoPlataforma>.Falla(Error.NoAutorizado("El token no identifica una empresa."));
}
