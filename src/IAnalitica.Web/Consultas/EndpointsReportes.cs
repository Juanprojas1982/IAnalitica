using IAnalitica.Application.Mediacion;
using IAnalitica.Application.Reportes;
using IAnalitica.Domain.Modulos;
using IAnalitica.Web.Components.Dashboards;
using Microsoft.AspNetCore.Antiforgery;

namespace IAnalitica.Web.Consultas;

/// <summary>Pedir, cancelar, regenerar y descargar reportes (spec 21). Todo exige sesión; pedir exige además <c>reportes.exportar</c>.</summary>
internal static class EndpointsReportes
{
    public static void MapReportes(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/reportes").RequireAuthorization();

        g.MapPost("/solicitar", async (HttpContext http, IAntiforgery af, ISender enviar, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var f = await http.Request.ReadFormAsync(ct);
            // Los filtros llegan ya resueltos por la página (sin valores por omisión escondidos)
            var filtros = new FiltrosDashboard(
                FiltrosPagina.ParsePeriodo(f["desde"]), FiltrosPagina.ParsePeriodo(f["hasta"]),
                Lista(f["sede"]), Lista(f["pagador"]), Lista(f["cc"]), FiltrosPagina.ParseCorte(f["corte"]));
            var r = await enviar.Send(new SolicitarReporteCommand(f["dashboard"].ToString(), f["formato"].ToString().ToUpperInvariant(), filtros), ct);
            return Results.Redirect(r.EsExitoso
                ? "/reportes" + (r.Valor.Reutilizado ? "?reutilizado=1" : "?nuevo=1")
                : "/reportes?error=" + Uri.EscapeDataString(r.Errores[0].Mensaje));
        });

        g.MapPost("/{id:guid}/cancelar", async (Guid id, HttpContext http, IAntiforgery af, ISender enviar, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var r = await enviar.Send(new CancelarReporteCommand(id), ct);
            return Results.Redirect(r.EsExitoso ? "/reportes" : "/reportes?error=" + Uri.EscapeDataString(r.Errores[0].Mensaje));
        });

        g.MapPost("/{id:guid}/regenerar", async (Guid id, HttpContext http, IAntiforgery af, ISender enviar, IRepositorioReportes reportes, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var anterior = await reportes.ObtenerAsync(id, ct);
            var parametros = anterior is null ? null : ParametrosReporte.DeJson(anterior.ParametrosJson);
            if (anterior is null || parametros is null) return Results.Redirect("/reportes?error=" + Uri.EscapeDataString("El reporte no existe."));
            var r = await enviar.Send(new SolicitarReporteCommand(parametros.Dashboard, anterior.Formato, parametros.Filtros), ct);
            return Results.Redirect(r.EsExitoso ? "/reportes?nuevo=1" : "/reportes?error=" + Uri.EscapeDataString(r.Errores[0].Mensaje));
        });

        g.MapGet("/{id:guid}/descargar", async (Guid id, HttpContext http, ISender enviar, CancellationToken ct) =>
        {
            var r = await enviar.Send(new DescargarReporteQuery(id, http.Connection.RemoteIpAddress?.ToString()), ct);
            if (!r.EsExitoso)
            {
                var e = r.Errores[0];
                return Results.Problem(statusCode: e.Codigo switch { "NO_ENCONTRADO" => 404, "NO_AUTORIZADO" => 403, _ => 409 }, title: e.Mensaje,
                    extensions: new Dictionary<string, object?> { ["codigo"] = e.Codigo });
            }
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(r.Valor.Contenido, r.Valor.TipoContenido, r.Valor.NombreArchivo);
        });
    }

    private static IReadOnlyList<int>? Lista(string? valor)
        => int.TryParse(valor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? new[] { n } : null;
}
