using System.Globalization;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Facturacion;
using IAnalitica.Application.Mediacion;
using Microsoft.AspNetCore.Antiforgery;

namespace IAnalitica.Web.Consultas;

/// <summary>
/// Facturación del SaaS (08 §32.5). Las acciones de administración exigen <c>fac.administrar</c> (solo el superadmin; además lo verifica cada handler y
/// la RLS). La descarga del PDF exige <c>fac.consultar</c>, lee con RLS, registra en <c>aud.Acceso</c> y responde sin caché.
/// </summary>
internal static class EndpointsFacturacion
{
    private const string Base = "/admin/facturacion";

    private delegate Task<Result> Accion(HttpContext http, ISender enviar, IFormCollection form, CancellationToken ct);

    public static void MapFacturacion(this IEndpointRouteBuilder app)
    {
        var cliente = app.MapGroup("/facturacion").RequireAuthorization("fac.consultar");
        cliente.MapGet("/{id:int}/pdf", async (int id, HttpContext http, ISender enviar, CancellationToken ct) =>
        {
            var r = await enviar.Send(new DescargarCuentaCobroPdfQuery(id, http.Connection.RemoteIpAddress?.ToString()), ct);
            if (!r.EsExitoso)
            {
                var e = r.Errores[0];
                return Results.Problem(statusCode: e.Codigo switch { "NO_ENCONTRADO" => 404, "NO_AUTORIZADO" => 403, _ => 409 }, title: e.Mensaje,
                    extensions: new Dictionary<string, object?> { ["codigo"] = e.Codigo });
            }
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(r.Valor.Contenido, "application/pdf", r.Valor.NombreArchivo);
        });

        // Contactos de facturación: los mantiene el administrador de la empresa (la cuenta de cobro se envía a ellos). La empresa sale de la sesión.
        var contactos = app.MapGroup("/facturacion/contactos").RequireAuthorization("admin.usuarios");
        contactos.MapPost("/", async (HttpContext http, IAntiforgery af, ISender enviar, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var f = await http.Request.ReadFormAsync(ct);
            var r = await enviar.Send(new GuardarContactoFacturacionCommand(f["tipo"].ToString(), f["nombre"].ToString(), f["email"].ToString()), ct);
            return Results.Redirect(r.EsExitoso ? "/facturacion/contactos?ok=" + Uri.EscapeDataString("Contacto guardado.") : "/facturacion/contactos?error=" + Uri.EscapeDataString(r.Errores[0].Mensaje));
        });
        contactos.MapPost("/{id:int}/quitar", async (int id, HttpContext http, IAntiforgery af, ISender enviar, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var r = await enviar.Send(new QuitarContactoFacturacionCommand(id), ct);
            return Results.Redirect(r.EsExitoso ? "/facturacion/contactos?ok=" + Uri.EscapeDataString("Contacto quitado.") : "/facturacion/contactos?error=" + Uri.EscapeDataString(r.Errores[0].Mensaje));
        });

        var g = app.MapGroup(Base).RequireAuthorization("fac.administrar");

        // ---- cuentas
        Post(g, "/cuentas/{id:int}/emitir", "Cuenta emitida.", "", async (h, s, f, ct) => Quitar(await s.Send(new EmitirCuentaCobroCommand(Id(h), null), ct)));
        Post(g, "/cuentas/{id:int}/descartar", "Borrador descartado.", "", (h, s, f, ct) => s.Send(new DescartarBorradorCommand(Id(h), f["motivo"]), ct));
        Post(g, "/cuentas/{id:int}/regenerar", "Se recalculará el borrador en unos segundos.", "", (h, s, f, ct) => s.Send(new RegenerarBorradorCommand(Id(h)), ct));
        Post(g, "/cuentas/{id:int}/anular", "Cuenta anulada. Se avisará al cliente.", "", (h, s, f, ct) => s.Send(new AnularCuentaCobroCommand(Id(h), f["motivo"], f["reemplazo"] == "on"), ct));
        Post(g, "/cuentas/{id:int}/reenviar", "Reenvío programado.", "", (h, s, f, ct) => s.Send(new ReenviarCuentaCobroCommand(Id(h)), ct));

        // ---- envíos y alertas
        Post(g, "/envios/{id:guid}/reintentar", "Reintento programado.", "", (h, s, f, ct) => s.Send(new ReintentarEnvioCommand(Guid.Parse((string)h.Request.RouteValues["id"]!)), ct));
        Post(g, "/envios/{id:guid}/rebotado", "Envío marcado como rebotado.", "", (h, s, f, ct) => s.Send(new MarcarEnvioRebotadoCommand(Guid.Parse((string)h.Request.RouteValues["id"]!), "REBOTE_MANUAL"), ct));
        Post(g, "/alertas/{id:int}/atender", "Alerta atendida.", "", (h, s, f, ct) => s.Send(new AtenderAlertaCommand(Id(h)), ct));

        // ---- pagos
        Post(g, "/pagos", "Pago registrado.", "/pagos", async (h, s, f, ct) =>
        {
            if (!int.TryParse(f["empresa"], out var empresa) || !decimal.TryParse(f["valor"], NumberStyles.Number, CultureInfo.InvariantCulture, out var valor) || !DateOnly.TryParse(f["fecha"], out var fecha))
                return Result.Falla(Error.Validacion("DATOS_INVALIDOS", "Revise la empresa, el valor y la fecha."));
            var medio = f["medio"].ToString();
            return Quitar(await s.Send(new RegistrarPagoCommand(empresa, f["clase"].ToString(), medio.Length == 0 ? null : medio, fecha, valor,
                NullSiVacio(f["referencia"]), NullSiVacio(f["observacion"]), f["automatico"] == "on"), ct));
        });
        Post(g, "/pagos/{id:int}/aplicar", "Pago aplicado.", "/pagos", (h, s, f, ct) =>
        {
            if (!int.TryParse(f["cuenta"], out var cuenta) || !decimal.TryParse(f["valor"], NumberStyles.Number, CultureInfo.InvariantCulture, out var valor))
                return Task.FromResult(Result.Falla(Error.Validacion("DATOS_INVALIDOS", "Revise la cuenta y el valor.")));
            return s.Send(new AplicarPagoCommand(Id(h), [new AplicacionSolicitada(cuenta, valor)]), ct);
        });
        Post(g, "/pagos/{id:int}/anular", "Pago anulado.", "/pagos", (h, s, f, ct) => s.Send(new AnularPagoCommand(Id(h), f["motivo"]), ct));
        Post(g, "/aplicaciones/{id:int}/reversar", "Aplicación reversada.", "/pagos", (h, s, f, ct) => s.Send(new ReversarAplicacionCommand(Id(h), f["motivo"]), ct));

        // ---- configuración comercial
        Post(g, "/configuracion/canon", "Canon guardado.", "/configuracion", (h, s, f, ct) =>
        {
            if (!short.TryParse(f["plan"], out var plan) || !DateOnly.TryParse(f["desde"], out var desde) || !decimal.TryParse(f["canon"], NumberStyles.Number, CultureInfo.InvariantCulture, out var canon))
                return Task.FromResult(Result.Falla(Error.Validacion("DATOS_INVALIDOS", "Revise el plan, la fecha y el canon.")));
            return s.Send(new GuardarCanonPlanCommand(plan, desde, canon), ct);
        });
        Post(g, "/configuracion/condicion", "Condición comercial guardada.", "/configuracion", async (h, s, f, ct) =>
        {
            if (!int.TryParse(f["empresa"], out var empresa) || !short.TryParse(f["plan"], out var plan) || !DateOnly.TryParse(f["desde"], out var desde))
                return Result.Falla(Error.Validacion("DATOS_INVALIDOS", "Revise la empresa, el plan y la fecha."));
            decimal? canon = decimal.TryParse(f["canon"], NumberStyles.Number, CultureInfo.InvariantCulture, out var c) ? c : null;
            short? dias = short.TryParse(f["dias"], out var d) ? d : null;
            bool? aprobacion = f["aprobacion"].ToString() switch { "1" => true, "0" => false, _ => null };
            return Quitar(await s.Send(new GuardarCondicionComercialCommand(empresa, desde, plan, canon, dias, aprobacion, f["facturable"] == "on", NullSiVacio(f["referencia"])), ct));
        });
        Post(g, "/configuracion/parametro", "Parámetro guardado.", "/configuracion", (h, s, f, ct) => s.Send(new GuardarParametroFacturacionCommand(f["clave"].ToString(), f["valor"].ToString()), ct));
        Post(g, "/configuracion/correo/probar", "La conexión SMTP funciona.", "/configuracion", async (h, s, f, ct) =>
        {
            var r = await s.Send(new ProbarConexionCorreoCommand(), ct);
            if (!r.EsExitoso) return Result.Falla(r.Errores);
            return r.Valor.Resultado == ResultadoCorreo.Aceptado
                ? Result.Ok()
                : Result.Falla(Error.Conflicto("CORREO", "La conexión SMTP no funcionó (" + (r.Valor.CodigoError ?? "sin código") + ")."));
        });
    }

    private static void Post(RouteGroupBuilder g, string ruta, string ok, string destino, Accion accion)
        => g.MapPost(ruta, async (HttpContext http, IAntiforgery af, ISender enviar, CancellationToken ct) =>
        {
            await af.ValidateRequestAsync(http);
            var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(ct) : new FormCollection(new());
            var r = await accion(http, enviar, form, ct);
            return Results.Redirect(r.EsExitoso
                ? $"{Base}{destino}?ok={Uri.EscapeDataString(ok)}"
                : $"{Base}{destino}?error={Uri.EscapeDataString(r.Errores[0].Mensaje)}");
        });

    private static int Id(HttpContext http) => int.Parse((string)http.Request.RouteValues["id"]!, CultureInfo.InvariantCulture);
    private static string? NullSiVacio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static Result Quitar(Result r) => r.EsExitoso ? Result.Ok() : Result.Falla(r.Errores);
}
