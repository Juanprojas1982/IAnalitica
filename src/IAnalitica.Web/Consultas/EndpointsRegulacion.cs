using System.Globalization;
using IAnalitica.Application.Mediacion;
using IAnalitica.Application.Regulacion;
using Microsoft.AspNetCore.Antiforgery;

namespace IAnalitica.Web.Consultas;

/// <summary>Catálogo regulatorio global (spec 15 §2.4): importar una lista de precios máximos y registrar factores de presentación. Solo <c>admin.catalogos</c> (el comando lo valida).</summary>
internal static class EndpointsRegulacion
{
    public static void MapRegulacion(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/regulacion").RequireAuthorization("admin.catalogos");

        g.MapPost("/precios-maximos", async (HttpContext http, IAntiforgery af, ISender enviar, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var f = await http.Request.ReadFormAsync(ct);
            var archivo = f.Files["archivo"];
            if (archivo is null || archivo.Length == 0) return Volver("error", "Adjunte el archivo JSON de la lista de precios.");
            if (!DateOnly.TryParseExact(f["expedicion"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expedicion)
                || !DateOnly.TryParseExact(f["vigenteDesde"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var desde)
                || !short.TryParse(f["anio"], NumberStyles.None, CultureInfo.InvariantCulture, out var anio))
                return Volver("error", "Revise las fechas (aaaa-mm-dd) y el año de la circular.");
            DateOnly? hasta = DateOnly.TryParseExact(f["vigenteHasta"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var h) ? h : null;

            await using var flujo = archivo.OpenReadStream();
            var circular = new DatosCircular(f["entidad"].ToString().Trim(), f["numero"].ToString().Trim(), anio, f["tema"].ToString().Trim(), expedicion, desde, hasta, f["url"].ToString().Trim(),
                string.IsNullOrWhiteSpace(f["notas"]) ? null : f["notas"].ToString().Trim());
            var r = await enviar.Send(new ImportarPreciosMaximosCommand(circular, flujo), ct);
            return r.EsExitoso
                ? Volver("ok", $"Importados {r.Valor.Importados:N0} precios; rechazados {r.Valor.Rechazados:N0}." + (r.Valor.Muestras.Count > 0 ? " " + string.Join("; ", r.Valor.Muestras) : ""))
                : Volver("error", r.Errores[0].Mensaje);
        });

        g.MapPost("/factores", async (HttpContext http, IAntiforgery af, ISender enviar, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var f = await http.Request.ReadFormAsync(ct);
            if (!decimal.TryParse(f["unidades"], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var unidades))
                return Volver("error", "Las unidades por presentación deben ser un número (use punto decimal).");
            var r = await enviar.Send(new RegistrarFactorPresentacionCommand(f["cum"].ToString(), unidades, f["validadoPor"].ToString()), ct);
            return r.EsExitoso ? Volver("ok", "Factor registrado y validado.") : Volver("error", r.Errores[0].Mensaje);
        });
    }

    private static IResult Volver(string clave, string mensaje) => Results.Redirect($"/admin/regulacion?{clave}={Uri.EscapeDataString(mensaje)}");
}
