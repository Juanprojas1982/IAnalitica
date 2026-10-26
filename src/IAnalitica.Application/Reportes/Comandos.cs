using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using IAnalitica.Application.Semantica;
using IAnalitica.Domain.Modulos;
using Microsoft.Extensions.Options;

namespace IAnalitica.Application.Reportes;

public static class CodigosReporte
{
    public const string LimiteEnCola = "LIMITE_REPORTES_EN_COLA";
    public const string FormatoInvalido = "FORMATO_INVALIDO";
    public const string NoCancelable = "REPORTE_NO_CANCELABLE";
}

/// <summary>Parámetros de un reporte de dashboard, tal como se guardan en <c>rpt.SolicitudReporte.ParametrosJson</c>.</summary>
public sealed record ParametrosReporte(string Dashboard, FiltrosDashboard Filtros)
{
    public const string Tipo = "DASHBOARD";

    public string AJson() => JsonSerializer.Serialize(new
    {
        dashboard = Dashboard,
        filtros = new { Filtros.PeriodoDesde, Filtros.PeriodoHasta, Filtros.Sedes, Filtros.Pagadores, Filtros.CentrosCosto, Filtros.Corte },
    });

    public static ParametrosReporte? DeJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;
        if (!raiz.TryGetProperty("dashboard", out var d) || d.GetString() is not { } dashboard) return null;
        var f = raiz.GetProperty("filtros");
        int? Entero(string n) => f.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        IReadOnlyList<int>? Lista(string n) => f.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetInt32()).ToList() : null;
        return new ParametrosReporte(dashboard, new FiltrosDashboard(Entero("PeriodoDesde"), Entero("PeriodoHasta"), Lista("Sedes"), Lista("Pagadores"), Lista("CentrosCosto"), Entero("Corte")));
    }

    public string Hash(int usuarioId, string formato)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{usuarioId}|{formato}|{AJson()}")));
}

// ---------- Pedir un reporte ----------
[RequierePermiso("reportes.exportar")]
public sealed record SolicitarReporteCommand(string Dashboard, string Formato, FiltrosDashboard Filtros) : IRequest<Result<SolicitudCreada>>;

public sealed class SolicitarReporteCommandValidator : AbstractValidator<SolicitarReporteCommand>
{
    public SolicitarReporteCommandValidator()
    {
        RuleFor(x => x.Formato).Must(f => FormatosReporte.Todos.Contains(f)).WithErrorCode(CodigosReporte.FormatoInvalido).WithMessage("Formato no válido: use PDF, XLSX o CSV.");
        RuleFor(x => x.Dashboard).NotEmpty().WithErrorCode("DASHBOARD_REQUERIDO").WithMessage("Indique el dashboard.");
    }
}

public sealed class SolicitarReporteHandler(
    CatalogoAnalitico catalogo, ISesionUsuario sesion, IModulosEmpresa modulos, IVersionCarga version, IRepositorioReportes reportes, IOptions<LimitesReportes> limites)
    : IRequestHandler<SolicitarReporteCommand, Result<SolicitudCreada>>
{
    public async Task<Result<SolicitudCreada>> Handle(SolicitarReporteCommand request, CancellationToken ct)
    {
        var empresaId = sesion.EmpresaId!.Value;
        var usuarioId = sesion.UsuarioId!.Value;

        // El dashboard debe ser uno que el usuario puede ver (permiso del módulo, módulo activo)
        var visible = await new VisibilidadDashboards(catalogo, sesion, modulos).ResolverAsync(request.Dashboard, ct).ConfigureAwait(false);
        if (!visible.EsExitoso) return Result<SolicitudCreada>.Falla(visible.Errores);

        var parametros = new ParametrosReporte(request.Dashboard, request.Filtros);
        var v = await version.ObtenerAsync(empresaId, ct).ConfigureAwait(false);
        var hash = parametros.Hash(usuarioId, request.Formato);

        // Reutilización: mismo usuario, parámetros y versión de carga en las últimas horas → se entrega el archivo existente
        var existente = await reportes.BuscarReutilizableAsync(usuarioId, hash, v, limites.Value.ReutilizarHoras, ct).ConfigureAwait(false);
        if (existente is not null) return new SolicitudCreada(existente.SolicitudId, true, 0);

        if (await reportes.ContarActivasAsync(usuarioId, ct).ConfigureAwait(false) >= limites.Value.EnColaPorUsuario)
            return Result<SolicitudCreada>.Falla(Error.Conflicto(CodigosReporte.LimiteEnCola,
                $"Ya tiene {limites.Value.EnColaPorUsuario} reportes en cola o generándose. Espere a que terminen o cancele alguno."));

        var id = await reportes.CrearAsync(empresaId, usuarioId, new NuevaSolicitud(ParametrosReporte.Tipo, request.Formato, parametros.AJson(), hash), v, ct).ConfigureAwait(false);
        return new SolicitudCreada(id, false, await reportes.PosicionEnColaAsync(id, ct).ConfigureAwait(false));
    }
}

// ---------- Mis reportes ----------
public sealed record ListarMisReportesQuery(bool TodosLosDeLaEmpresa = false) : IRequest<Result<IReadOnlyList<SolicitudReporte>>>;

public sealed class ListarMisReportesHandler(ISesionUsuario sesion, IRepositorioReportes reportes) : IRequestHandler<ListarMisReportesQuery, Result<IReadOnlyList<SolicitudReporte>>>
{
    public async Task<Result<IReadOnlyList<SolicitudReporte>>> Handle(ListarMisReportesQuery request, CancellationToken ct)
    {
        if (sesion.UsuarioId is not { } usuarioId || sesion.EmpresaId is null) return Result<IReadOnlyList<SolicitudReporte>>.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        // El administrador de la empresa ve los de toda su empresa (spec 21 §3); los demás, solo los suyos
        var todos = request.TodosLosDeLaEmpresa && sesion.TienePermiso("admin.usuarios");
        return Result<IReadOnlyList<SolicitudReporte>>.Ok(await reportes.ListarAsync(usuarioId, todos, 100, ct).ConfigureAwait(false));
    }
}

public sealed record CancelarReporteCommand(Guid SolicitudId) : IRequest<Result>;

public sealed class CancelarReporteHandler(ISesionUsuario sesion, IRepositorioReportes reportes) : IRequestHandler<CancelarReporteCommand, Result>
{
    public async Task<Result> Handle(CancelarReporteCommand request, CancellationToken ct)
    {
        if (sesion.UsuarioId is not { } usuarioId) return Result.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        return await reportes.CancelarAsync(request.SolicitudId, usuarioId, ct).ConfigureAwait(false)
            ? Result.Ok()
            : Result.Falla(Error.Conflicto(CodigosReporte.NoCancelable, "Solo se puede cancelar un reporte que sigue en cola."));
    }
}

// ---------- Descargar ----------
public sealed record ArchivoReporte(Stream Contenido, string NombreArchivo, string TipoContenido);

public sealed record DescargarReporteQuery(Guid SolicitudId, string? Ip) : IRequest<Result<ArchivoReporte>>;

public sealed class DescargarReporteHandler(ISesionUsuario sesion, IRepositorioReportes reportes, IAlmacenReportes almacen, IEnumerable<IExportadorReporte> exportadores)
    : IRequestHandler<DescargarReporteQuery, Result<ArchivoReporte>>
{
    public async Task<Result<ArchivoReporte>> Handle(DescargarReporteQuery request, CancellationToken ct)
    {
        if (sesion.UsuarioId is not { } usuarioId || sesion.EmpresaId is not { } empresaId) return Result<ArchivoReporte>.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        var s = await reportes.ObtenerAsync(request.SolicitudId, ct).ConfigureAwait(false);   // la RLS ya limita a la empresa
        // Solo su dueño, o el administrador de la empresa. Para cualquiera más, el reporte «no existe».
        if (s is null || (s.UsuarioId != usuarioId && !sesion.TienePermiso("admin.usuarios"))) return Result<ArchivoReporte>.Falla(Error.NoEncontrado("El reporte no existe."));
        if (s.Estado != EstadosReporte.Listo || s.BlobRuta is null) return Result<ArchivoReporte>.Falla(Error.Conflicto("REPORTE_NO_LISTO", "El reporte todavía no está listo o ya no está disponible."));
        if (s.ExpiraEn is { } vence && vence < DateTime.UtcNow) return Result<ArchivoReporte>.Falla(Error.Conflicto("REPORTE_EXPIRADO", "El reporte expiró. Vuelva a generarlo."));

        var flujo = await almacen.AbrirAsync(s.BlobRuta, ct).ConfigureAwait(false);
        if (flujo is null) return Result<ArchivoReporte>.Falla(Error.Conflicto("REPORTE_EXPIRADO", "El archivo ya no está disponible. Vuelva a generarlo."));
        await reportes.RegistrarDescargaAsync(usuarioId, empresaId, s.SolicitudId, request.Ip, ct).ConfigureAwait(false);

        var exportador = exportadores.First(e => e.Formato == s.Formato);
        var parametros = ParametrosReporte.DeJson(s.ParametrosJson);
        return new ArchivoReporte(flujo, $"{(parametros?.Dashboard ?? "reporte").Replace('.', '-')}-{s.SolicitadoEn:yyyyMMdd-HHmm}.{exportador.Extension}", exportador.TipoContenido);
    }
}
