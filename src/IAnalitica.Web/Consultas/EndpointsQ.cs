using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using IAnalitica.Application.Semantica;
using IAnalitica.Web.Components.Dashboards;

namespace IAnalitica.Web.Consultas;

/// <summary>
/// Endpoints de consulta del portal (24 §7.2), autenticados por cookie. Misma seguridad que las páginas: permiso por módulo,
/// módulo activo y RLS. El EmpresaId sale de la sesión; nunca de la URL.
/// </summary>
internal static class EndpointsQ
{
    public static void MapConsultasQ(this IEndpointRouteBuilder app)
    {
        var q = app.MapGroup("/q").RequireAuthorization();

        q.MapGet("/dashboards", async (ISender enviar, CancellationToken ct) =>
            Responder(await enviar.Send(new ListarDashboardsQuery(), ct)));

        q.MapGet("/dashboards/{codigo}", async (string codigo, ISender enviar, CancellationToken ct) =>
        {
            var r = await enviar.Send(new ObtenerDashboardQuery(codigo), ct);
            return r.EsExitoso
                ? Results.Ok(new
                {
                    codigo = r.Valor.Definicion.Codigo, titulo = r.Valor.Definicion.Titulo, modulo = r.Valor.Definicion.Modulo, version = r.Valor.Definicion.Version,
                    widgets = r.Valor.Widgets.Select(w => new { w.Codigo, w.Titulo, tipo = w.Tipo.ToString(), w.Metricas, w.Dimensiones, w.Fila, w.Columna, w.Ancho, w.Alto, w.DrillDashboard }),
                })
                : Problema(r.Errores);
        });

        q.MapGet("/widgets/{dashboard}/{widget}/datos", async (string dashboard, string widget, HttpContext http, TimeProvider reloj, ISender enviar, CancellationToken ct) =>
        {
            var c = http.Request.Query;
            var pagina = new FiltrosPagina(c["desde"], c["hasta"], Entero(c["sede"]), Entero(c["pagador"]), Entero(c["cc"]), c["corte"]);
            var filtros = pagina.ParaMotor(reloj.GetUtcNow().UtcDateTime.AddHours(-5));
            return Responder(await enviar.Send(new ObtenerDatosWidgetQuery(dashboard, widget, filtros), ct));
        });
    }

    private static int? Entero(string? s) => int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    private static IResult Responder<T>(Result<T> r) => r.EsExitoso ? Results.Ok(r.Valor) : Problema(r.Errores);

    private static IResult Problema(IReadOnlyList<Error> errores)
    {
        var e = errores[0];
        var estado = e.Codigo switch { "NO_ENCONTRADO" => 404, "NO_AUTORIZADO" => 403, _ => 409 };
        return Results.Problem(statusCode: estado, title: e.Mensaje, extensions: new Dictionary<string, object?> { ["codigo"] = e.Codigo });
    }
}
