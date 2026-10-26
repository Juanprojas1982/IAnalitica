using System.Text.Json;
using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Mediacion;
using IAnalitica.Application.Reportes;
using IAnalitica.Application.Semantica;
using IAnalitica.Domain.Modulos;
using IAnalitica.Infrastructure.Ingesta;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IAnalitica.Infrastructure.Reportes;

/// <summary>
/// GENERAR_REPORTE (spec 21 §2): corre en el worker con la identidad de quien lo pidió (<c>rol_app</c> + su contexto → la misma RLS y los
/// mismos permisos que en pantalla), nunca con el rol del worker. Un reporte por usuario a la vez (applock); los demás esperan.
/// </summary>
internal sealed class ManejadorGenerarReporte(
    IServiceScopeFactory alcances, IFabricaConexionesEtl etl, IEnumerable<IExportadorReporte> exportadores, IAlmacenReportes almacen,
    IOptions<LimitesReportes> limites, TimeProvider reloj, ILogger<ManejadorGenerarReporte> log) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.GenerarReporte;

    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(trabajo.Payload ?? throw new InvalidOperationException("El trabajo no trae payload."));
        var solicitudId = doc.RootElement.GetProperty("solicitudId").GetGuid();
        var empresaId = trabajo.EmpresaId ?? throw new InvalidOperationException("El reporte no tiene empresa.");
        var usuarioId = trabajo.UsuarioId ?? throw new InvalidOperationException("El reporte no tiene solicitante.");

        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        var estado = await cn.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT Estado FROM rpt.SolicitudReporte WHERE SolicitudId = @solicitudId", new { solicitudId }, cancellationToken: ct)).ConfigureAwait(false);
        if (estado is not (EstadosReporte.Pendiente or EstadosReporte.EnProceso)) return (ResultadoTrabajo.Completado, TimeSpan.Zero);   // cancelado o ya resuelto

        var recurso = $"reporte:{usuarioId}";
        var bloqueo = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @recurso, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;", new { recurso }, cancellationToken: ct)).ConfigureAwait(false);
        if (bloqueo < 0) return (ResultadoTrabajo.Postergado, TimeSpan.FromSeconds(15));   // otro reporte del mismo usuario en curso
        try
        {
            await cn.ExecuteAsync(new CommandDefinition("UPDATE rpt.SolicitudReporte SET Estado = 'EN_PROCESO', IniciadoEn = SYSUTCDATETIME(), Progreso = 0 WHERE SolicitudId = @solicitudId", new { solicitudId }, cancellationToken: ct)).ConfigureAwait(false);
            try
            {
                await GenerarAsync(cn, solicitudId, empresaId, usuarioId, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Falló el reporte {Solicitud}", solicitudId);
                if (!trabajo.EsUltimoIntento)
                {
                    await cn.ExecuteAsync(new CommandDefinition("UPDATE rpt.SolicitudReporte SET Estado = 'PENDIENTE', Progreso = NULL WHERE SolicitudId = @solicitudId", new { solicitudId }, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
                    throw;
                }
                // Último intento: mensaje entendible, sin detalles internos; el detalle técnico queda en las trazas
                await cn.ExecuteAsync(new CommandDefinition(
                    "UPDATE rpt.SolicitudReporte SET Estado = 'ERROR', TerminadoEn = SYSUTCDATETIME(), Error = @error WHERE SolicitudId = @solicitudId",
                    new { solicitudId, error = "No fue posible generar el reporte. Intente de nuevo; si persiste, avise a soporte." }, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
                await NotificarAsync(empresaId, usuarioId, "REPORTE_ERROR", "No se pudo generar su reporte", ct: CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await cn.ExecuteAsync(new CommandDefinition("EXEC sp_releaseapplock @Resource = @recurso, @LockOwner = 'Session'", new { recurso }, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
        }
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }

    private async Task GenerarAsync(System.Data.Common.DbConnection cn, Guid solicitudId, int empresaId, int usuarioId, CancellationToken ct)
    {
        // Identidad del solicitante: sesión con sus permisos y contexto de BD de su empresa
        await using var alcance = alcances.CreateAsyncScope();
        var sp = alcance.ServiceProvider;
        sp.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeEmpresa(empresaId, usuarioId));
        sp.GetRequiredService<SesionTrabajo>().Establecer(usuarioId, empresaId, await sp.GetRequiredService<IPermisosUsuario>().ObtenerAsync(usuarioId, ct).ConfigureAwait(false));

        var solicitud = await sp.GetRequiredService<IRepositorioReportes>().ObtenerAsync(solicitudId, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("La solicitud no existe para ese usuario y empresa.");
        var parametros = ParametrosReporte.DeJson(solicitud.ParametrosJson) ?? throw new InvalidOperationException("Parámetros de reporte inválidos.");
        var exportador = exportadores.FirstOrDefault(e => e.Formato == solicitud.Formato) ?? throw new InvalidOperationException($"Formato sin exportador: {solicitud.Formato}.");

        var enviar = sp.GetRequiredService<ISender>();
        var tablero = await enviar.Send(new ObtenerDashboardQuery(parametros.Dashboard), ct).ConfigureAwait(false);
        if (!tablero.EsExitoso) throw new InvalidOperationException("El solicitante ya no puede ver el dashboard: " + tablero.Errores[0].Codigo);

        var secciones = new List<SeccionReporte>();
        foreach (var w in tablero.Valor.Widgets)
        {
            var datos = await enviar.Send(new ObtenerDatosWidgetQuery(parametros.Dashboard, w.Codigo, parametros.Filtros, Completo: true), ct).ConfigureAwait(false);
            if (!datos.EsExitoso) throw new InvalidOperationException($"No se pudo calcular el widget {w.Codigo}: {datos.Errores[0].Codigo}");
            secciones.Add(ConstructorSecciones.Desde(datos.Valor, limites.Value.MaxFilasPorSeccion));
        }

        var version = await sp.GetRequiredService<IVersionCarga>().ObtenerAsync(empresaId, ct).ConfigureAwait(false);
        var marca = await cn.QuerySingleAsync<(string RazonSocial, string ColorPrimario)>(new CommandDefinition(
            "SELECT RazonSocial, ColorPrimario FROM seg.Empresa WHERE EmpresaId = @empresaId", new { empresaId }, cancellationToken: ct)).ConfigureAwait(false);
        var opciones = (await enviar.Send(new ListarOpcionesFiltrosQuery(), ct).ConfigureAwait(false)).Valor;

        var reporte = new ReporteDashboard(marca.RazonSocial, tablero.Valor.Definicion.Titulo, tablero.Valor.NombreModulo,
            DescribirFiltros(parametros.Filtros, opciones), reloj.GetUtcNow().UtcDateTime, version, secciones, marca.ColorPrimario);
        var bytes = exportador.Exportar(reporte);

        var ruta = await almacen.GuardarAsync(empresaId, solicitudId, exportador.Extension, bytes, ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE rpt.SolicitudReporte SET Estado = 'LISTO', BlobRuta = @ruta, TamanoBytes = @tamano, VersionCarga = @version, Progreso = 100,
                   TerminadoEn = SYSUTCDATETIME(), ExpiraEn = DATEADD(DAY, @dias, SYSUTCDATETIME()), Error = NULL
            WHERE SolicitudId = @solicitudId
            """, new { solicitudId, ruta, tamano = (long)bytes.Length, version, dias = limites.Value.CaducidadDias }, cancellationToken: ct)).ConfigureAwait(false);
        await NotificarAsync(empresaId, usuarioId, "REPORTE_LISTO", $"Su reporte «{tablero.Valor.Definicion.Titulo}» está listo", ct).ConfigureAwait(false);
    }

    private async Task NotificarAsync(int empresaId, int usuarioId, string tipo, string titulo, CancellationToken ct)
    {
        await using var alcance = alcances.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeEmpresa(empresaId, usuarioId));
        await alcance.ServiceProvider.GetRequiredService<INotificaciones>().NotificarAsync(empresaId, usuarioId, tipo, titulo, "/reportes", ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<(string, string)> DescribirFiltros(FiltrosDashboard f, OpcionesFiltros? o)
    {
        var lista = new List<(string, string)>();
        if (f.PeriodoDesde is { } d && f.PeriodoHasta is { } h) lista.Add(("Periodo", d == h ? Dimensiones.EtiquetaPeriodo(d) : $"{Dimensiones.EtiquetaPeriodo(d)} a {Dimensiones.EtiquetaPeriodo(h)}"));
        if (f.Corte is { } c) lista.Add(("Corte", Dimensiones.EtiquetaCorte(c)));
        void Nombres(string titulo, IReadOnlyList<int>? ids, IReadOnlyList<OpcionFiltro>? opciones)
        {
            if (ids is not { Count: > 0 }) return;
            lista.Add((titulo, string.Join(", ", ids.Select(i => opciones?.FirstOrDefault(x => x.Id == i)?.Nombre ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        }
        Nombres("Sede", f.Sedes, o?.Sedes); Nombres("Pagador", f.Pagadores, o?.Pagadores); Nombres("Centro de costo", f.CentrosCosto, o?.CentrosCosto);
        if (lista.Count == 0) lista.Add(("Filtros", "ninguno"));
        return lista;
    }
}

/// <summary>Vence los reportes viejos: marca EXPIRADO y borra el archivo (spec 21 §4: 7 días).</summary>
public sealed class ExpiradorReportes(IFabricaConexionesEtl etl, IAlmacenReportes almacen, ILogger<ExpiradorReportes> log)
{
    public async Task<int> ExpirarAsync(CancellationToken ct)
    {
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        var vencidos = (await cn.QueryAsync<(Guid Id, string? Ruta)>(new CommandDefinition(
            "SELECT TOP (500) SolicitudId, BlobRuta FROM rpt.SolicitudReporte WHERE Estado = 'LISTO' AND ExpiraEn < SYSUTCDATETIME()", cancellationToken: ct)).ConfigureAwait(false)).ToList();
        foreach (var (id, ruta) in vencidos)
        {
            try { if (ruta is not null) await almacen.EliminarAsync(ruta, ct).ConfigureAwait(false); }
            catch (Exception e) { log.LogWarning(e, "No se pudo borrar el archivo del reporte {Id}", id); continue; }
            await cn.ExecuteAsync(new CommandDefinition("UPDATE rpt.SolicitudReporte SET Estado = 'EXPIRADO', BlobRuta = NULL WHERE SolicitudId = @id", new { id }, cancellationToken: ct)).ConfigureAwait(false);
        }
        return vencidos.Count;
    }
}
