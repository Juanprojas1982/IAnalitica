using System.Globalization;
using IAnalitica.Application.Costos;
using IAnalitica.Application.Mediacion;
using Microsoft.AspNetCore.Antiforgery;

namespace IAnalitica.Web.Consultas;

/// <summary>Reglas de distribución de costos y recálculo. Exige sesión; el permiso <c>admin.usuarios</c> lo valida cada comando.</summary>
internal static class EndpointsCostos
{
    public static void MapCostos(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/costos").RequireAuthorization();

        g.MapPost("/regla", async (HttpContext http, IAntiforgery af, ISender enviar, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var f = await http.Request.ReadFormAsync(ct);
            if (!int.TryParse(f["centro"], NumberStyles.None, CultureInfo.InvariantCulture, out var centro)
                || !short.TryParse(f["orden"], NumberStyles.None, CultureInfo.InvariantCulture, out var orden)
                || !int.TryParse(f["desde"], NumberStyles.None, CultureInfo.InvariantCulture, out var desde))
                return Volver("error", "Revise el centro, el orden y el periodo (aaaamm).");
            var r = await enviar.Send(new DefinirReglaCostoCommand(centro, orden, f["base"].ToString(), desde), ct);
            return r.EsExitoso ? Volver("ok", "Regla guardada.") : Volver("error", r.Errores[0].Mensaje);
        });

        g.MapPost("/regla/quitar", async (HttpContext http, IAntiforgery af, ISender enviar, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var f = await http.Request.ReadFormAsync(ct);
            if (!int.TryParse(f["centro"], NumberStyles.None, CultureInfo.InvariantCulture, out var centro) || !int.TryParse(f["desde"], NumberStyles.None, CultureInfo.InvariantCulture, out var desde))
                return Volver("error", "Solicitud no válida.");
            var r = await enviar.Send(new QuitarReglaCostoCommand(centro, desde), ct);
            return r.EsExitoso ? Volver("ok", "Regla eliminada.") : Volver("error", r.Errores[0].Mensaje);
        });

        g.MapPost("/defecto", async (HttpContext http, IAntiforgery af, ISender enviar, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var f = await http.Request.ReadFormAsync(ct);
            if (!int.TryParse(f["desde"], NumberStyles.None, CultureInfo.InvariantCulture, out var desde)) return Volver("error", "Indique el periodo (aaaamm).");
            var r = await enviar.Send(new AplicarReglasPorDefectoCommand(desde), ct);
            return r.EsExitoso ? Volver("ok", r.Valor == 0 ? "Todos los centros clasificados ya tienen regla." : $"Se crearon {r.Valor} reglas por defecto.") : Volver("error", r.Errores[0].Mensaje);
        });

        g.MapPost("/recalcular", async (HttpContext http, IAntiforgery af, ISender enviar, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var f = await http.Request.ReadFormAsync(ct);
            if (!int.TryParse(f["desde"], NumberStyles.None, CultureInfo.InvariantCulture, out var desde) || !int.TryParse(f["hasta"], NumberStyles.None, CultureInfo.InvariantCulture, out var hasta))
                return Volver("error", "Indique los periodos (aaaamm).");
            var r = await enviar.Send(new RecalcularCostosCommand(desde, hasta), ct);
            return r.EsExitoso ? Volver("ok", r.Valor == 0 ? "Ya había cálculos en cola para esos periodos." : $"Quedaron en cola {r.Valor} periodos; el resultado aparece en el dashboard en unos minutos.") : Volver("error", r.Errores[0].Mensaje);
        });
    }

    private static IResult Volver(string clave, string mensaje) => Results.Redirect($"/costos?{clave}={Uri.EscapeDataString(mensaje)}");
}
