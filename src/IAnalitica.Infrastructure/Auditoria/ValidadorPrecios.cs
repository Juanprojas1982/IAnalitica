using System.Data;
using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure.Ingesta;
using Microsoft.Extensions.Logging;

namespace IAnalitica.Infrastructure.Auditoria;

public sealed record ResumenValidacionPrecios(int Evaluadas, int SinLimiteComparable, int Nuevos, int Corregidos);

/// <summary>
/// Validador de precios máximos de medicamentos (spec 15 §2). Corre con rol_etl sobre la copia <c>saludips.*</c> de la empresa y el catálogo
/// regulatorio global <c>reg.*</c>; deja hallazgos en <c>fact.HallazgoTarifario</c> con el mismo ciclo que el auditor de configuración.
/// <list type="bullet">
/// <item>R1: tarifa del pagador por encima del precio máximo vigente del CUM. CRÍTICA solo si la comparación de unidades está validada; si no, MEDIA («por verificar»).</item>
/// <item>R2: la tarifa quedará por encima del precio máximo de una circular futura (ALTA / MEDIA).</item>
/// <item>R5: tarifa por debajo del costo de compra (ALTA).</item>
/// <item>R6: CUM con formato inválido o con todos sus registros INVIMA vencidos (ALTA). El CUM vacío lo reporta TAR-06.</item>
/// </list>
/// Supuestos [S]: la tarifa de SaludIPS (<c>pTarifasSuministros.Valor</c>) y el costo (<c>pSuministros.Costo</c>) están por unidad de consumo; el pagador
/// toma la tarifa de su manual (<c>mEmpresas.CodManual</c>). El límite por unidad es <c>PrecioMaximoUnidad</c> si la fuente lo trae, o el precio por presentación
/// dividido por las unidades por presentación (<c>reg.FactorPresentacion</c>); sin ninguno de los dos no se compara.
/// </summary>
public sealed class ValidadorPrecios(IFabricaConexionesEtl etl, TimeProvider reloj, ILogger<ValidadorPrecios> log)
{
    private sealed record Candidato(string Regla, string Severidad, string ClaveEntidad, string? NombreEntidad, string? Cum, int? PagadorId,
        decimal? ValorObservado, decimal? ValorLimite, decimal? ExcesoUnitario, decimal? UnidadesAfectadas, decimal? MontoEnRiesgo, int? CircularId, bool FactorValidado, string? Detalle);
    private sealed record Existente(long HallazgoId, string Regla, string ClaveEntidad, string Estado);

    private const string Calculo = """
        SELECT ts.CodSuministro, e.IdEmpresa, e.NomEmpresa, CAST(ts.Valor AS decimal(19,4)) AS Tarifa, su.NomSuministro,
               NULLIF(LTRIM(RTRIM(su.CodCum)), N'') AS Cum, CAST(su.Costo AS decimal(19,4)) AS Costo
        INTO #t
        FROM saludips.dbo_pTarifasSuministros ts
        JOIN saludips.dbo_mEmpresas e ON e.EmpresaId = ts.EmpresaId AND e.CodManual = ts.CodManual AND e.EstadoEntidad = 1
        JOIN saludips.dbo_pSuministros su ON su.EmpresaId = ts.EmpresaId AND su.CodSuministro = ts.CodSuministro
        WHERE ts.EmpresaId = @EmpresaId AND ts.Valor > 0;

        -- Límite vigente y límite de la próxima circular, por CUM, ya en unidad de consumo
        SELECT x.Cum, x.Momento, x.CircularId, x.VigenteDesde, x.LimUnidad, x.Validado
        INTO #lim
        FROM (
            SELECT pm.Cum, CASE WHEN pm.VigenteDesde <= @Hoy THEN 'VIGENTE' ELSE 'FUTURO' END AS Momento, pm.CircularId, pm.VigenteDesde,
                   COALESCE(pm.PrecioMaximoUnidad, pm.PrecioMaximoPresentacion / NULLIF(f.UnidadesPorPresentacion, 0)) AS LimUnidad,
                   CAST(CASE WHEN pm.PrecioMaximoUnidad IS NOT NULL OR f.Fuente = 'OFICIAL' OR f.ValidadoEn IS NOT NULL THEN 1 ELSE 0 END AS bit) AS Validado,
                   ROW_NUMBER() OVER (PARTITION BY pm.Cum, CASE WHEN pm.VigenteDesde <= @Hoy THEN 0 ELSE 1 END
                                      ORDER BY CASE WHEN pm.VigenteDesde <= @Hoy THEN pm.VigenteDesde END DESC, CASE WHEN pm.VigenteDesde > @Hoy THEN pm.VigenteDesde END ASC, pm.PrecioMaximoId DESC) AS rn
            FROM reg.PrecioMaximo pm
            LEFT JOIN reg.FactorPresentacion f ON f.Cum = pm.Cum
            WHERE (pm.VigenteDesde <= @Hoy AND (pm.VigenteHasta IS NULL OR pm.VigenteHasta >= @Hoy)) OR pm.VigenteDesde > @Hoy
        ) x WHERE x.rn = 1 AND x.LimUnidad IS NOT NULL;

        SELECT n.Regla, n.Severidad, n.ClaveEntidad, n.NombreEntidad, n.Cum, dp.PagadorId, n.ValorObservado, n.ValorLimite, n.ExcesoUnitario,
               imp.Unidades AS UnidadesAfectadas, CAST(CASE WHEN n.ExcesoUnitario > 0 THEN n.ExcesoUnitario * imp.Unidades END AS decimal(19,4)) AS MontoEnRiesgo,
               n.CircularId, n.FactorValidado, n.Detalle
        FROM (
            -- R1: por encima del máximo vigente
            SELECT 'R1' AS Regla, CASE WHEN l.Validado = 1 THEN 'CRITICA' ELSE 'MEDIA' END AS Severidad,
                   t.CodSuministro + N'|' + CAST(t.IdEmpresa AS nvarchar(20)) AS ClaveEntidad, t.NomSuministro + N' · ' + t.NomEmpresa AS NombreEntidad,
                   t.Cum, t.IdEmpresa, t.Tarifa AS ValorObservado, l.LimUnidad AS ValorLimite, t.Tarifa - l.LimUnidad AS ExcesoUnitario, l.CircularId, l.Validado AS FactorValidado,
                   CAST(CASE WHEN l.Validado = 1 THEN NULL ELSE N'Por verificar: las unidades de la tarifa y del precio máximo no están validadas.' END AS nvarchar(400)) AS Detalle
            FROM #t t JOIN #lim l ON l.Cum = t.Cum AND l.Momento = 'VIGENTE' WHERE t.Tarifa > l.LimUnidad
            UNION ALL
            -- R2: quedará por encima del máximo de una circular futura (y hoy no lo supera)
            SELECT 'R2', CASE WHEN f.Validado = 1 THEN 'ALTA' ELSE 'MEDIA' END,
                   t.CodSuministro + N'|' + CAST(t.IdEmpresa AS nvarchar(20)), t.NomSuministro + N' · ' + t.NomEmpresa,
                   t.Cum, t.IdEmpresa, t.Tarifa, f.LimUnidad, t.Tarifa - f.LimUnidad, f.CircularId, f.Validado,
                   N'Entra en vigencia el ' + CONVERT(nvarchar(10), f.VigenteDesde, 23) + N' (en ' + CAST(DATEDIFF(DAY, @Hoy, f.VigenteDesde) AS nvarchar(10)) + N' días)'
            FROM #t t JOIN #lim f ON f.Cum = t.Cum AND f.Momento = 'FUTURO'
            LEFT JOIN #lim v ON v.Cum = t.Cum AND v.Momento = 'VIGENTE'
            WHERE t.Tarifa > f.LimUnidad AND (v.Cum IS NULL OR t.Tarifa <= v.LimUnidad)
            UNION ALL
            -- R5: tarifa por debajo del costo de compra
            SELECT 'R5', 'ALTA', t.CodSuministro + N'|' + CAST(t.IdEmpresa AS nvarchar(20)), t.NomSuministro + N' · ' + t.NomEmpresa,
                   t.Cum, t.IdEmpresa, t.Tarifa, t.Costo, t.Costo - t.Tarifa, NULL, CAST(1 AS bit), N'Pérdida por unidad: tarifa menor que el costo registrado en el suministro.'
            FROM #t t WHERE t.Costo > 0 AND t.Tarifa < t.Costo
        ) n
        LEFT JOIN dim.Pagador dp ON dp.EmpresaId = @EmpresaId AND dp.CodigoOrigen = CAST(n.IdEmpresa AS varchar(30))
        OUTER APPLY (SELECT SUM(fl.Cantidad) AS Unidades FROM fact.FacturaLinea fl JOIN dim.Articulo a ON a.ArticuloId = fl.ArticuloId
                     WHERE fl.EmpresaId = @EmpresaId AND dp.PagadorId IS NOT NULL AND fl.PagadorId = dp.PagadorId AND a.CodigoCum = n.Cum
                       AND fl.FechaEmisionKey >= @Desde30 AND fl.Anulada = 0 AND fl.TipoDocumento = 'FACTURA') imp
        """;

    private const string SinLimite = """
        SELECT COUNT(DISTINCT t.CodSuministro) FROM #t t WHERE t.Cum IS NOT NULL AND NOT EXISTS (SELECT 1 FROM #lim l WHERE l.Cum = t.Cum)
        """;

    private const string R6 = """
        SELECT 'R6' AS Regla, 'ALTA' AS Severidad, su.CodSuministro AS ClaveEntidad, su.NomSuministro AS NombreEntidad, LTRIM(RTRIM(su.CodCum)) AS Cum, CAST(NULL AS int) AS PagadorId,
               CAST(NULL AS decimal(19,4)) AS ValorObservado, CAST(NULL AS decimal(19,4)) AS ValorLimite, CAST(NULL AS decimal(19,4)) AS ExcesoUnitario,
               CAST(NULL AS decimal(19,4)) AS UnidadesAfectadas, CAST(NULL AS decimal(19,4)) AS MontoEnRiesgo, CAST(NULL AS int) AS CircularId, CAST(1 AS bit) AS FactorValidado,
               CAST(CASE WHEN LTRIM(RTRIM(su.CodCum)) NOT LIKE '[0-9]%-[0-9]%' OR LTRIM(RTRIM(su.CodCum)) LIKE '%[^0-9-]%' OR LTRIM(RTRIM(su.CodCum)) LIKE '%-%-%' OR RIGHT(LTRIM(RTRIM(su.CodCum)), 1) = '-'
                         THEN N'El CUM no tiene el formato expediente-consecutivo.' ELSE N'Todos los registros INVIMA del artículo están vencidos.' END AS nvarchar(400)) AS Detalle
        FROM saludips.dbo_pSuministros su
        WHERE su.EmpresaId = @EmpresaId AND LTRIM(RTRIM(ISNULL(su.CodCum, N''))) <> N''
          AND (LTRIM(RTRIM(su.CodCum)) NOT LIKE '[0-9]%-[0-9]%' OR LTRIM(RTRIM(su.CodCum)) LIKE '%[^0-9-]%' OR LTRIM(RTRIM(su.CodCum)) LIKE '%-%-%' OR RIGHT(LTRIM(RTRIM(su.CodCum)), 1) = '-'
               OR EXISTS (SELECT 1 FROM saludips.Almacen_ArticulosBase ab
                          WHERE ab.EmpresaId = @EmpresaId AND ab.CodigoCUM = LTRIM(RTRIM(su.CodCum))
                            AND EXISTS (SELECT 1 FROM saludips.Almacen_RegistrosInvima ri WHERE ri.EmpresaId = @EmpresaId AND ri.ArticuloId = ab.Id)
                            AND NOT EXISTS (SELECT 1 FROM saludips.Almacen_RegistrosInvima ri WHERE ri.EmpresaId = @EmpresaId AND ri.ArticuloId = ab.Id AND (ri.FechaVigencia IS NULL OR ri.FechaVigencia >= @Hoy))))
        """;

    public async Task<ResumenValidacionPrecios> EjecutarAsync(int empresaId, CancellationToken ct)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var hoy = ahora.AddHours(-5).Date;   // Colombia: UTC-5 todo el año
        var p = new { EmpresaId = empresaId, Hoy = hoy, Desde30 = int.Parse(hoy.AddDays(-30).ToString("yyyyMMdd")) };
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);

        // Una sola ejecución: las tablas temporales viven en esta conexión
        await using var lector = await cn.QueryMultipleAsync(new CommandDefinition(Calculo + "\n" + SinLimite, p, commandTimeout: 600, cancellationToken: ct)).ConfigureAwait(false);
        var candidatos = (await lector.ReadAsync<Candidato>().ConfigureAwait(false)).ToList();
        var sinLimite = await lector.ReadSingleAsync<int>().ConfigureAwait(false);
        candidatos.AddRange(await cn.QueryAsync<Candidato>(new CommandDefinition(R6, p, commandTimeout: 600, cancellationToken: ct)).ConfigureAwait(false));

        int nuevos = 0, corregidos = 0;
        foreach (var regla in new[] { "R1", "R2", "R5", "R6" })
        {
            var (n, c) = await SincronizarAsync(cn, empresaId, regla, candidatos.Where(x => x.Regla == regla).ToList(), ahora, ct).ConfigureAwait(false);
            nuevos += n; corregidos += c;
        }
        log.LogInformation("Validador de precios, empresa {Empresa}: {Nuevos} nuevos, {Corregidos} corregidos, {SinLimite} suministros con CUM sin límite comparable.", empresaId, nuevos, corregidos, sinLimite);
        return new ResumenValidacionPrecios(candidatos.Count, sinLimite, nuevos, corregidos);
    }

    private static async Task<(int Nuevos, int Corregidos)> SincronizarAsync(IDbConnection cn, int empresaId, string regla, IReadOnlyList<Candidato> detectados, DateTime ahora, CancellationToken ct)
    {
        var porClave = detectados.GroupBy(d => d.ClaveEntidad).ToDictionary(g => g.Key, g => g.First());
        var existentes = (await cn.QueryAsync<Existente>(new CommandDefinition("""
            SELECT HallazgoId, Regla, ClaveEntidad, Estado FROM fact.HallazgoTarifario WITH (UPDLOCK) WHERE EmpresaId = @empresaId AND Regla = @regla
            """, new { empresaId, regla }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var abiertos = existentes.Where(e => e.Estado is "NUEVO" or "ABIERTO").ToDictionary(e => e.ClaveEntidad);
        var suprimidos = existentes.Where(e => e.Estado is "ACEPTADO" or "FALSO_POSITIVO").Select(e => e.ClaveEntidad).ToHashSet();

        int nuevos = 0, corregidos = 0;
        using var tx = cn.BeginTransaction();
        foreach (var (clave, d) in porClave)
        {
            if (suprimidos.Contains(clave)) continue;
            var parametros = new
            {
                empresaId, regla, clave, d.Severidad, d.NombreEntidad, d.Cum, d.PagadorId, d.ValorObservado, d.ValorLimite, d.ExcesoUnitario, d.UnidadesAfectadas, d.MontoEnRiesgo,
                d.CircularId, d.FactorValidado, d.Detalle, ahora,
            };
            if (abiertos.ContainsKey(clave))
                await cn.ExecuteAsync(new CommandDefinition("""
                    UPDATE fact.HallazgoTarifario SET Estado = 'ABIERTO', Severidad = @Severidad, NombreEntidad = @NombreEntidad, Cum = @Cum, PagadorId = @PagadorId,
                           ValorObservado = @ValorObservado, ValorLimite = @ValorLimite, ExcesoUnitario = @ExcesoUnitario, UnidadesAfectadas = @UnidadesAfectadas, MontoEnRiesgo = @MontoEnRiesgo,
                           CircularId = @CircularId, FactorValidado = @FactorValidado, Detalle = @Detalle, UltimaDeteccion = @ahora
                    WHERE EmpresaId = @empresaId AND Regla = @regla AND ClaveEntidad = @clave AND Estado IN ('NUEVO','ABIERTO')
                    """, parametros, tx, cancellationToken: ct)).ConfigureAwait(false);
            else
            {
                await cn.ExecuteAsync(new CommandDefinition("""
                    INSERT fact.HallazgoTarifario (EmpresaId, Regla, Severidad, ClaveEntidad, NombreEntidad, Cum, PagadorId, ValorObservado, ValorLimite, ExcesoUnitario, UnidadesAfectadas,
                                                   MontoEnRiesgo, CircularId, FactorValidado, Detalle, DetectadoEn, UltimaDeteccion)
                    VALUES (@empresaId, @regla, @Severidad, @clave, @NombreEntidad, @Cum, @PagadorId, @ValorObservado, @ValorLimite, @ExcesoUnitario, @UnidadesAfectadas,
                            @MontoEnRiesgo, @CircularId, @FactorValidado, @Detalle, @ahora, @ahora)
                    """, parametros, tx, cancellationToken: ct)).ConfigureAwait(false);
                nuevos++;
            }
        }
        foreach (var (clave, e) in abiertos)
        {
            if (porClave.ContainsKey(clave)) continue;
            await cn.ExecuteAsync(new CommandDefinition("UPDATE fact.HallazgoTarifario SET Estado = 'CORREGIDO', CerradoEn = @ahora WHERE EmpresaId = @empresaId AND HallazgoId = @id",
                new { empresaId, id = e.HallazgoId, ahora }, tx, cancellationToken: ct)).ConfigureAwait(false);
            corregidos++;
        }
        tx.Commit();
        return (nuevos, corregidos);
    }
}

/// <summary>VALIDAR_PRECIOS: corre el validador de una empresa tras cargar su foto de configuración.</summary>
internal sealed class ManejadorValidarPrecios(ValidadorPrecios validador) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.ValidarPrecios;

    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        await validador.EjecutarAsync(trabajo.EmpresaId ?? throw new InvalidOperationException("Falta la empresa."), ct).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
}
