using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using IAnalitica.Application.Ingesta;
using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Ingesta.Contracts;
using Microsoft.Data.SqlClient;

namespace IAnalitica.Infrastructure.Ingesta;

/// <summary>Cargas y lotes con el contexto del token (<c>rol_app</c>, RLS). El cuerpo de las filas nunca se registra.</summary>
internal sealed partial class RepositorioCargas(IFabricaConexiones conexiones) : IRepositorioCargas
{
    private const decimal Tolerancia = 0.01m;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex Identificador();

    public async Task<long> CrearAsync(int empresaId, NuevaCarga c, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT etl.Carga (EmpresaId, Entidad, Modo, VentanaDesde, VentanaHasta, Corte, Origen, VersionAgente, VersionFuente, TraceParent)
            OUTPUT inserted.CargaId
            VALUES (@empresaId, @Entidad, @Modo, @VentanaDesde, @VentanaHasta, @Corte, 'api', @VersionAgente, @VersionFuente, @TraceParent)
            """, new
        {
            empresaId, c.Entidad, c.Modo,
            VentanaDesde = c.VentanaDesde?.ToDateTime(TimeOnly.MinValue), VentanaHasta = c.VentanaHasta?.ToDateTime(TimeOnly.MinValue),
            Corte = c.Corte?.ToDateTime(TimeOnly.MinValue), c.VersionAgente, c.VersionFuente, c.TraceParent,
        }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<DatosCarga?> ObtenerAsync(long cargaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.QuerySingleOrDefaultAsync<DatosCarga>(new CommandDefinition(
            "SELECT CargaId, EmpresaId, Entidad, Modo, Estado, Error FROM etl.Carga WHERE CargaId = @cargaId", new { cargaId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> PeriodoCerradoAsync(int empresaId, DateOnly desde, DateOnly hasta, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM etl.PeriodoCierre
            WHERE EmpresaId = @empresaId AND Estado = 'CERRADO' AND PeriodoKey BETWEEN @d AND @h
            """, new { empresaId, d = desde.Year * 100 + desde.Month, h = hasta.Year * 100 + hasta.Month }, cancellationToken: ct)).ConfigureAwait(false) > 0;
    }

    public async Task GuardarLoteAsync(int empresaId, long cargaId, string entidad, int numero, int filas, string filasJson, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await using var tx = await cn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var p = new DynamicParameters();
        p.Add("cargaId", cargaId); p.Add("empresaId", empresaId); p.Add("entidad", entidad); p.Add("numero", numero); p.Add("filas", filas);
        p.Add("json", filasJson, System.Data.DbType.String, size: -1);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE stg.Payload WITH (UPDLOCK, SERIALIZABLE) SET Filas = @filas, Json = @json, RecibidoEn = SYSUTCDATETIME()
            WHERE CargaId = @cargaId AND NumeroLote = @numero;
            IF @@ROWCOUNT = 0
                INSERT stg.Payload (CargaId, EmpresaId, Entidad, NumeroLote, Filas, Json) VALUES (@cargaId, @empresaId, @entidad, @numero, @filas, @json);
            """, p, tx, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task RechazarAsync(long cargaId, string motivo, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE etl.Carga SET Estado = 'RECHAZADA', Error = @motivo, FinalizadaEn = SYSUTCDATETIME() WHERE CargaId = @cargaId AND Estado = 'ABIERTA';
            DELETE stg.Payload WHERE CargaId = @cargaId;
            """, new { cargaId, motivo }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<RespuestaCierre> CerrarAsync(int empresaId, DatosCarga carga, CierreCarga cierre, string[] columnasSumaControl, string? traceParent, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await using var tx = await cn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var fila = await cn.QuerySingleOrDefaultAsync<FilaCarga>(new CommandDefinition("""
            SELECT Estado, Modo, VentanaDesde AS Desde, VentanaHasta AS Hasta, Corte FROM etl.Carga WITH (UPDLOCK, ROWLOCK) WHERE CargaId = @id
            """, new { id = carga.CargaId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (fila is null || fila.Estado != EstadosCarga.Abierta) return new RespuestaCierre(ResultadoCierre.NoAbierta);

        // 1. Control de lotes y filas: lotes 1..N sin huecos y el total declarado
        var recibido = await cn.QuerySingleAsync<ControlLotes>(new CommandDefinition(
            "SELECT COUNT(*) AS Lotes, MAX(NumeroLote) AS MaxLote, ISNULL(SUM(Filas), 0) AS Filas FROM stg.Payload WHERE CargaId = @id",
            new { id = carga.CargaId }, tx, cancellationToken: ct)).ConfigureAwait(false);

        string? detalle = null;
        if (recibido.Lotes != cierre.TotalLotes || (recibido.Lotes > 0 && recibido.MaxLote != recibido.Lotes))
            detalle = $"Lotes recibidos: {recibido.Lotes}; declarados: {cierre.TotalLotes}. Faltan lotes o hay huecos en la numeración.";
        else if (recibido.Filas != cierre.TotalFilas)
            detalle = $"Filas recibidas: {recibido.Filas}; declaradas: {cierre.TotalFilas}.";

        // 2. Suma de control (Σ columnas de la entidad sobre lo recibido)
        if (detalle is null && columnasSumaControl.Length > 0)
        {
            if (cierre.SumaControl is not { } declarada)
                detalle = "La entidad exige sumaControl.";
            else
            {
                var suma = await SumaRecibidaAsync(cn, tx, carga.CargaId, columnasSumaControl, ct).ConfigureAwait(false);
                if (Math.Abs(suma - declarada) > Tolerancia)
                    detalle = $"sumaControl declarada {declarada:0.##}; recibida {suma:0.##}.";
            }
        }

        if (detalle is not null)
        {
            await FinalizarSinProcesarAsync(cn, tx, carga.CargaId, cierre, EstadosCarga.Rechazada, detalle, null, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new RespuestaCierre(ResultadoCierre.ControlNoCuadra, detalle);
        }

        // 3. Hash de la carga (entidad, modo, ventana y contenido de cada lote en orden). Se combinan los hash de los lotes:
        //    nunca se concatena el contenido completo en memoria.
        var hashes = (await cn.QueryAsync<byte[]>(new CommandDefinition(
            "SELECT HASHBYTES('SHA2_256', Json) FROM stg.Payload WHERE CargaId = @id ORDER BY NumeroLote",
            new { id = carga.CargaId }, tx, commandTimeout: 300, cancellationToken: ct))).ToList();
        using var sha = SHA256.Create();
        var cabecera = $"{carga.Entidad}|{fila.Modo}|{fila.Desde:yyyy-MM-dd}|{fila.Hasta:yyyy-MM-dd}|{fila.Corte:yyyy-MM-dd}|{hashes.Count}";
        sha.TransformBlock(Encoding.UTF8.GetBytes(cabecera), 0, Encoding.UTF8.GetByteCount(cabecera), null, 0);
        foreach (var h in hashes) sha.TransformBlock(h, 0, h.Length, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hash = sha.Hash!;

        var original = await cn.QueryFirstOrDefaultAsync<long?>(new CommandDefinition("""
            SELECT TOP (1) CargaId FROM etl.Carga
            WHERE EmpresaId = @empresaId AND Entidad = @entidad AND HashPayload = @hash AND Estado = 'OK' AND CargaId <> @id
            """, new { empresaId, entidad = carga.Entidad, hash, id = carga.CargaId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (original is { } previa)
        {
            // Idempotencia (contrato 05 §7): el conector la trata como «sin cambios». /v1/estado la cuenta para su marca de agua.
            var msg = $"CARGA_DUPLICADA de la carga {previa}";
            await FinalizarSinProcesarAsync(cn, tx, carga.CargaId, cierre, EstadosCarga.Rechazada, msg, null, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new RespuestaCierre(ResultadoCierre.Duplicada, "La carga es idéntica a la " + previa + ".");
        }

        // 4. Cerrar y encolar en la misma transacción (outbox)
        var prioridad = CatalogoEntidades.Buscar(carga.Entidad) is { EsCatalogo: true } ? 3 : (carga.Entidad == "asientos" ? 5 : 6);
        await cn.ExecuteAsync(new CommandDefinition("""
            UPDATE etl.Carga SET Estado = 'CERRADA', TotalLotes = @lotes, TotalFilas = @filas, SumaControl = @suma, HashPayload = @hash,
                   FilasRecibidas = @filas, CerradaEn = SYSUTCDATETIME() WHERE CargaId = @id;
            INSERT etl.Trabajo (Tipo, EmpresaId, ClaveExclusion, Prioridad, Payload, TraceParent)
            VALUES ('PROCESAR_CARGA', @empresaId, @clave, @prioridad, @payload, @trace);
            """, new
        {
            id = carga.CargaId, lotes = cierre.TotalLotes, filas = cierre.TotalFilas, suma = cierre.SumaControl, hash, empresaId, prioridad,
            clave = $"EMP:{empresaId}|CARGA:{carga.CargaId}", payload = $"{{\"cargaId\":{carga.CargaId}}}", trace = traceParent,
        }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new RespuestaCierre(ResultadoCierre.Encolada);
    }

    private sealed record FilaCarga(string Estado, string Modo, DateTime? Desde, DateTime? Hasta, DateTime? Corte);
    private sealed record ControlLotes(int Lotes, int? MaxLote, int Filas);

    private static async Task<decimal> SumaRecibidaAsync(SqlConnection cn, System.Data.Common.DbTransaction tx, long cargaId, string[] columnas, CancellationToken ct)
    {
        if (columnas.Any(c => !Identificador().IsMatch(c))) throw new InvalidOperationException("Columna de suma de control no válida.");   // constantes del catálogo
        var with = string.Join(", ", columnas.Select((c, i) => $"c{i} decimal(19,4) '$.{c}'"));
        var suma = string.Join(" + ", columnas.Select((_, i) => $"ISNULL(r.c{i}, 0)"));
        var sql = $"SELECT ISNULL(SUM({suma}), 0) FROM stg.Payload p CROSS APPLY OPENJSON(p.Json) WITH ({with}) r WHERE p.CargaId = @id";
        return await cn.ExecuteScalarAsync<decimal>(new CommandDefinition(sql, new { id = cargaId }, tx, commandTimeout: 600, cancellationToken: ct)).ConfigureAwait(false);
    }

    private static Task FinalizarSinProcesarAsync(SqlConnection cn, System.Data.Common.DbTransaction tx, long id, CierreCarga cierre, string estado, string error, byte[]? hash, CancellationToken ct)
        => cn.ExecuteAsync(new CommandDefinition("""
            UPDATE etl.Carga SET Estado = @estado, Error = @error, TotalLotes = @lotes, TotalFilas = @filas, SumaControl = @suma,
                   CerradaEn = SYSUTCDATETIME(), FinalizadaEn = SYSUTCDATETIME() WHERE CargaId = @id;
            DELETE stg.Payload WHERE CargaId = @id;
            """, new { id, estado, error, lotes = cierre.TotalLotes, filas = cierre.TotalFilas, suma = cierre.SumaControl }, tx, cancellationToken: ct));

    public async Task<EstadoCarga?> EstadoCargaAsync(long cargaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.QuerySingleOrDefaultAsync<EstadoCarga>(new CommandDefinition("""
            SELECT CargaId, Entidad, Estado, ISNULL(FilasInsertadas, 0) AS FilasInsertadas, ISNULL(FilasActualizadas, 0) AS FilasActualizadas,
                   ISNULL(FilasEliminadas, 0) AS FilasEliminadas, ISNULL(FilasRechazadas, 0) AS FilasRechazadas, Error
            FROM etl.Carga WHERE CargaId = @cargaId
            """, new { cargaId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RechazoFila>> RechazosAsync(long cargaId, int maximo, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<RechazoFila>(new CommandDefinition(
            "SELECT TOP (@maximo) SourceKey, Motivo FROM etl.Rechazo WHERE CargaId = @cargaId ORDER BY RechazoId", new { cargaId, maximo }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<EstadoPlataforma> EstadoPlataformaAsync(int empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        var nit = await cn.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT Nit FROM seg.Empresa WHERE EmpresaId = @empresaId", new { empresaId }, cancellationToken: ct)).ConfigureAwait(false);

        // «facturas» con modo INCREMENTAL son las anulaciones: el conector las vigila como «facturas-anulaciones».
        // Una carga idéntica a una ya procesada (CARGA_DUPLICADA) cuenta: confirma que los datos siguen al día.
        var marcas = await cn.QueryAsync<MarcaAgua>(new CommandDefinition("""
            WITH c AS (
                SELECT CASE WHEN Entidad = 'facturas' AND Modo = 'INCREMENTAL' THEN 'facturas-anulaciones' ELSE Entidad END AS Entidad,
                       CargaId, ISNULL(FinalizadaEn, CerradaEn) AS Cuando,
                       ROW_NUMBER() OVER (PARTITION BY CASE WHEN Entidad = 'facturas' AND Modo = 'INCREMENTAL' THEN 'facturas-anulaciones' ELSE Entidad END
                                          ORDER BY ISNULL(FinalizadaEn, CerradaEn) DESC, CargaId DESC) AS rn
                FROM etl.Carga
                WHERE EmpresaId = @empresaId AND (Estado = 'OK' OR (Estado = 'RECHAZADA' AND Error LIKE 'CARGA_DUPLICADA%')))
            SELECT Entidad, Cuando AS UltimaCargaOkEn, CargaId FROM c WHERE rn = 1 ORDER BY Entidad
            """, new { empresaId }, cancellationToken: ct)).ConfigureAwait(false);
        return new EstadoPlataforma { Empresa = nit, Entidades = marcas.ToList() };
    }
}
