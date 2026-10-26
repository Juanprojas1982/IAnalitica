using IAnalitica.Application.Auditoria;
using IAnalitica.Application.Mediacion;
using Microsoft.AspNetCore.Antiforgery;

namespace IAnalitica.Web.Consultas;

/// <summary>Cambiar el estado de un hallazgo del auditor (aceptar, falso positivo, reabrir). Exige sesión; el permiso <c>hallazgos.gestionar</c> lo valida el comando.</summary>
internal static class EndpointsAuditor
{
    public static void MapAuditor(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/auditor").RequireAuthorization();

        g.MapPost("/hallazgos/{id:long}/estado", async (long id, HttpContext http, IAntiforgery af, ISender enviar, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var f = await http.Request.ReadFormAsync(ct);
            var r = await enviar.Send(new CambiarEstadoHallazgoCommand(id, f["accion"].ToString().ToUpperInvariant(), f["justificacion"].ToString()), ct);
            var cerrados = f["cerrados"] == "1" ? "&cerrados=1" : "";
            return Results.Redirect(r.EsExitoso ? "/auditor?ok=1" + cerrados : "/auditor?error=" + Uri.EscapeDataString(r.Errores[0].Mensaje) + cerrados);
        });
    }
}
