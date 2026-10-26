using System.Diagnostics;
using System.Text.Json;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Ingesta;
using IAnalitica.Application.Mediacion;
using IAnalitica.Ingesta.Contracts;

namespace IAnalitica.Api;

/// <summary>Endpoints /v1 del contrato 05 §2. El cuerpo de los lotes nunca se registra.</summary>
internal static class Endpoints
{
    /// <summary>Lote máximo: 10 MB comprimido (Kestrel lo hace cumplir) y, descomprimido, 64 MB como defensa contra bombas de compresión.</summary>
    private const long MaxBytesComprimido = LimitesIngesta.MaxBytesLote;
    private const long MaxBytesDescomprimido = 64L * 1024 * 1024;

    public static void MapIngesta(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1").RequireAuthorization("ingesta").RequireRateLimiting("ingesta");

        v1.MapPost("/cargas", async (SolicitudCarga solicitud, ISender sender, CancellationToken ct) =>
        {
            var r = await sender.Send(new IniciarCargaCommand(solicitud, Activity.Current?.Id), ct);
            return r.EsExitoso
                ? Results.Created($"/v1/cargas/{r.Valor}", new CargaCreada { CargaId = r.Valor })
                : Errores.AProblema(r.Errores);
        });

        v1.MapPost("/cargas/{cargaId:long}/lotes/{numero:int}", async (long cargaId, int numero, HttpRequest req, ISender sender, CancellationToken ct) =>
        {
            if (req.ContentLength > MaxBytesComprimido)
                return Errores.Problema(StatusCodes.Status413PayloadTooLarge, CodigosError.LoteGrande, "El lote supera 10 MB.");

            JsonDocument doc;
            try
            {
                var bytes = await LeerLimitadoAsync(req.Body, MaxBytesDescomprimido, ct);
                if (bytes is null) return Errores.Problema(StatusCodes.Status413PayloadTooLarge, CodigosError.LoteGrande, "El lote descomprimido es demasiado grande.");
                doc = JsonDocument.Parse(bytes);
            }
            catch (JsonException)
            {
                return Errores.Problema(StatusCodes.Status400BadRequest, CodigosError.ContratoInvalido, "El cuerpo no es un JSON válido.");
            }

            using (doc)
            {
                if (doc.RootElement is not { ValueKind: JsonValueKind.Object } raiz || !raiz.TryGetProperty("filas", out var filas) || filas.ValueKind != JsonValueKind.Array)
                    return Errores.Problema(StatusCodes.Status400BadRequest, CodigosError.ContratoInvalido, "El cuerpo debe ser {\"filas\": [...]}.");
                if (filas.EnumerateArray().Any(f => f.ValueKind != JsonValueKind.Object))
                    return Errores.Problema(StatusCodes.Status400BadRequest, CodigosError.ContratoInvalido, "Cada fila debe ser un objeto JSON.");

                var r = await sender.Send(new RecibirLoteCommand(cargaId, numero, filas.GetArrayLength(), filas.GetRawText()), ct);
                return r.EsExitoso ? Results.Accepted(value: r.Valor) : Errores.AProblema(r.Errores);
            }
        });

        v1.MapPost("/cargas/{cargaId:long}/cerrar", async (long cargaId, CierreCarga cierre, ISender sender, CancellationToken ct) =>
        {
            var r = await sender.Send(new CerrarCargaCommand(cargaId, cierre, Activity.Current?.Id), ct);
            return r.EsExitoso ? Results.Accepted() : Errores.AProblema(r.Errores);
        });

        v1.MapGet("/cargas/{cargaId:long}", async (long cargaId, ISender sender, CancellationToken ct) =>
            Respuesta(await sender.Send(new ConsultarCargaQuery(cargaId), ct)));

        v1.MapGet("/cargas/{cargaId:long}/rechazos", async (long cargaId, ISender sender, CancellationToken ct) =>
            Respuesta(await sender.Send(new ConsultarRechazosQuery(cargaId), ct)));

        v1.MapGet("/estado", async (ISender sender, CancellationToken ct) =>
            Respuesta(await sender.Send(new ConsultarEstadoQuery(), ct)));
    }

    private static IResult Respuesta<T>(Result<T> r) => r.EsExitoso ? Results.Ok(r.Valor) : Errores.AProblema(r.Errores);

    /// <summary>Lee el cuerpo ya descomprimido hasta el tope; null si lo supera.</summary>
    private static async Task<byte[]?> LeerLimitadoAsync(Stream cuerpo, long max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await cuerpo.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > max) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }
}
