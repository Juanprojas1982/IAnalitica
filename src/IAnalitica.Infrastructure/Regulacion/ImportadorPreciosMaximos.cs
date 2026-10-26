using System.Globalization;
using System.Text.Json;
using Dapper;
using IAnalitica.Application.Regulacion;
using IAnalitica.Infrastructure.Persistencia;

namespace IAnalitica.Infrastructure.Regulacion;

/// <summary>
/// Carga una lista de precios máximos en <c>reg.PrecioMaximo</c> (global, sin RLS). Reimportar la misma circular reemplaza sus precios.
/// Una fila inválida (sin CUM válido o sin precio numérico) se cuenta como rechazada; no detiene la importación. Los factores de presentación que
/// trae la fuente entran como OFICIAL sin pisar un factor MANUAL ya validado.
/// </summary>
internal sealed class ImportadorPreciosMaximos(IFabricaConexiones conexiones) : IImportadorPreciosMaximos
{
    private const int TamanoLote = 5000;
    private sealed record Fila(string Cum, string? Principio, string? Presentacion, decimal Precio, decimal? PrecioUnidad, decimal? Unidades);

    public async Task<ResultadoImportacion> ImportarAsync(DatosCircular circular, MapeoPreciosMaximos mapeo, Stream json, CancellationToken ct)
    {
        var filas = new Dictionary<string, Fila>();          // un CUM por circular: gana la última fila
        var rechazados = 0;
        var muestras = new List<string>();
        var indice = 0;
        await foreach (var e in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(json, cancellationToken: ct).ConfigureAwait(false))
        {
            indice++;
            if (e.ValueKind != JsonValueKind.Object) throw new JsonException("Se esperaban objetos.");
            var fila = Leer(e, mapeo, out var motivo);
            if (fila is null) { rechazados++; if (muestras.Count < 5) muestras.Add($"fila {indice}: {motivo}"); }
            else filas[fila.Cum] = fila;
        }

        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await using var tx = await cn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var circularId = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            MERGE reg.Circular AS t USING (SELECT @Entidad AS Entidad, @Numero AS Numero, @Anio AS Anio) s ON t.Entidad = s.Entidad AND t.Numero = s.Numero AND t.Anio = s.Anio
            WHEN MATCHED THEN UPDATE SET Tema = @Tema, FechaExpedicion = @FechaExpedicion, VigenteDesde = @VigenteDesde, VigenteHasta = @VigenteHasta, UrlFuente = @UrlFuente, Notas = @Notas
            WHEN NOT MATCHED THEN INSERT (Entidad, Numero, Anio, Tema, FechaExpedicion, VigenteDesde, VigenteHasta, UrlFuente, Notas)
                 VALUES (@Entidad, @Numero, @Anio, @Tema, @FechaExpedicion, @VigenteDesde, @VigenteHasta, @UrlFuente, @Notas)
            OUTPUT inserted.CircularId;
            """, new
        {
            circular.Entidad, circular.Numero, circular.Anio, circular.Tema, FechaExpedicion = circular.FechaExpedicion.ToDateTime(TimeOnly.MinValue), VigenteDesde = circular.VigenteDesde.ToDateTime(TimeOnly.MinValue),
            VigenteHasta = circular.VigenteHasta?.ToDateTime(TimeOnly.MinValue), circular.UrlFuente, circular.Notas,
        }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition("DELETE reg.PrecioMaximo WHERE CircularId = @circularId", new { circularId }, tx, commandTimeout: 300, cancellationToken: ct)).ConfigureAwait(false);

        foreach (var lote in filas.Values.Chunk(TamanoLote))
        {
            var payload = JsonSerializer.Serialize(lote.Select(f => new { cum = f.Cum, principio = f.Principio, presentacion = f.Presentacion, precio = f.Precio, unidad = f.PrecioUnidad, factor = f.Unidades }));
            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT reg.PrecioMaximo (CircularId, Cum, Principio, Presentacion, PrecioMaximoPresentacion, PrecioMaximoUnidad, VigenteDesde, VigenteHasta)
                SELECT @circularId, j.cum, j.principio, j.presentacion, j.precio, j.unidad, c.VigenteDesde, c.VigenteHasta
                FROM OPENJSON(@payload) WITH (cum varchar(30) '$.cum', principio nvarchar(300) '$.principio', presentacion nvarchar(400) '$.presentacion', precio decimal(19,4) '$.precio', unidad decimal(19,4) '$.unidad') j
                CROSS JOIN reg.Circular c WHERE c.CircularId = @circularId;

                MERGE reg.FactorPresentacion AS t
                USING (SELECT cum, factor FROM OPENJSON(@payload) WITH (cum varchar(30) '$.cum', factor decimal(12,4) '$.factor') WHERE factor > 0) s ON t.Cum = s.cum
                WHEN MATCHED AND t.Fuente = 'OFICIAL' THEN UPDATE SET UnidadesPorPresentacion = s.factor
                WHEN NOT MATCHED THEN INSERT (Cum, UnidadesPorPresentacion, Fuente) VALUES (s.cum, s.factor, 'OFICIAL');
                """, new { circularId, payload }, tx, commandTimeout: 300, cancellationToken: ct)).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new ResultadoImportacion(circularId, filas.Count, rechazados, muestras);
    }

    public async Task RegistrarFactorAsync(string cum, decimal unidades, string validadoPor, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition("""
            MERGE reg.FactorPresentacion AS t USING (SELECT @cum AS Cum) s ON t.Cum = s.Cum
            WHEN MATCHED THEN UPDATE SET UnidadesPorPresentacion = @unidades, Fuente = 'MANUAL', ValidadoPor = @validadoPor, ValidadoEn = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Cum, UnidadesPorPresentacion, Fuente, ValidadoPor, ValidadoEn) VALUES (@cum, @unidades, 'MANUAL', @validadoPor, SYSUTCDATETIME());
            """, new { cum, unidades, validadoPor }, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static Fila? Leer(JsonElement e, MapeoPreciosMaximos m, out string motivo)
    {
        motivo = "";
        var cum = !string.IsNullOrWhiteSpace(m.CampoCum)
            ? Texto(e, m.CampoCum)
            : Entero(Texto(e, m.CampoExpediente)) is { } ex && Entero(Texto(e, m.CampoConsecutivo)) is { } co ? $"{ex}-{co}" : null;
        if (!FormatoCum.EsValido(cum)) { motivo = "CUM ausente o con formato inválido"; return null; }
        if (Numero(Texto(e, m.CampoPrecioPresentacion)) is not { } precio || precio <= 0) { motivo = "precio ausente o no numérico"; return null; }
        var unidad = Numero(Texto(e, m.CampoPrecioUnidad));
        var factor = Numero(Texto(e, m.CampoUnidadesPorPresentacion));
        return new Fila(cum!.Trim(), Cortar(Texto(e, m.CampoPrincipio), 300), Cortar(Texto(e, m.CampoPresentacion), 400), precio, unidad is > 0 ? unidad : null, factor is > 0 ? factor : null);
    }

    private static string? Texto(JsonElement e, string? campo)
        => campo is not null && e.TryGetProperty(campo, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null;

    // Número en formato invariante (punto decimal, sin separador de miles): lo que exporta Socrata. Cualquier otro formato se rechaza en vez de adivinarlo.
    private static decimal? Numero(string? t)
        => t is not null && decimal.TryParse(t.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static long? Entero(string? t) => t is not null && long.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    private static string? Cortar(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
