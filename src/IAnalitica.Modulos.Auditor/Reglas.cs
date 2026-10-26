using IAnalitica.Domain.Auditoria;

namespace IAnalitica.Modulos.Auditor;

// Las 10 reglas de la fase 1 (spec 16 §7). «Activo» = EstadoEntidad = 1, igual que el extractor de pagadores (docs/06).
// Los supuestos [S] de cada regla están en el comentario y en cfg.ReglaConfiguracion.PorVerificar.

/// <summary>CON-01. Pagador activo sin cuentas contables en la vigencia actual (mEmpresasCuentasContables). [S] basta con la cuenta por cobrar.</summary>
public sealed class Con01() : ReglaSql("CON-01", "Contabilidad", Severidad.Critica, $"""
    SELECT CAST(e.IdEmpresa AS nvarchar(200)) AS ClaveEntidad, e.NomEmpresa AS NombreEntidad,
           N'Sin cuentas contables en ' + CAST(@Anio AS nvarchar(4)) AS ValorEncontrado, N'Cuentas contables de la vigencia' AS ValorEsperado,
           imp.Cant AS ImpactoCantidad, imp.Valor AS ImpactoValor, N'Facturas y valor de los últimos 30 días del pagador' AS ImpactoDetalle, CAST(NULL AS varchar(8)) AS Severidad
    FROM saludips.dbo_mEmpresas e
    {ImpactoFacturacion("e.IdEmpresa")}
    WHERE e.EmpresaId = @EmpresaId AND e.EstadoEntidad = 1
      AND NOT EXISTS (SELECT 1 FROM saludips.dbo_mEmpresasCuentasContables c
                      WHERE c.EmpresaId = @EmpresaId AND c.IdEmpresa = e.IdEmpresa AND c.Vigencia = @Anio
                        AND c.CuentaPorCobrarId IS NOT NULL AND ISNULL(c.EstadoEntidad, 1) = 1)
    """);

/// <summary>
/// CON-02. Desde el 1 de noviembre: configuración del año siguiente incompleta. Fase 1 cubre pagadores (cuentas), tipos de comprobante con cuentas
/// este año y sin cuentas el próximo, y cConfiguracionAnual. Crítica desde diciembre.
/// </summary>
public sealed class Con02() : ReglaSql("CON-02", "Contabilidad", Severidad.Alta, """
    SELECT N'mEmpresas:' + CAST(e.IdEmpresa AS nvarchar(40)) AS ClaveEntidad, e.NomEmpresa AS NombreEntidad,
           N'Sin cuentas contables en ' + CAST(@Anio + 1 AS nvarchar(4)) AS ValorEncontrado, N'Cuentas contables de la vigencia' AS ValorEsperado,
           CAST(NULL AS int) AS ImpactoCantidad, CAST(NULL AS decimal(19,4)) AS ImpactoValor,
           N'El 1 de enero esta facturación quedará sin contabilizar' AS ImpactoDetalle, @Sev AS Severidad
    FROM saludips.dbo_mEmpresas e
    WHERE e.EmpresaId = @EmpresaId AND e.EstadoEntidad = 1
      AND EXISTS (SELECT 1 FROM saludips.dbo_mEmpresasCuentasContables c WHERE c.EmpresaId = @EmpresaId AND c.IdEmpresa = e.IdEmpresa AND c.Vigencia = @Anio)
      AND NOT EXISTS (SELECT 1 FROM saludips.dbo_mEmpresasCuentasContables c WHERE c.EmpresaId = @EmpresaId AND c.IdEmpresa = e.IdEmpresa AND c.Vigencia = @Anio + 1)
    UNION ALL
    SELECT N'TiposComprobantes:' + CAST(tc.Id AS nvarchar(40)), ISNULL(t.Prefijo + N' ', N'') + ISNULL(t.Nombre, N''),
           N'Sin cuentas en ' + CAST(@Anio + 1 AS nvarchar(4)), N'Cuentas del comprobante para la vigencia',
           NULL, NULL, N'El 1 de enero estos comprobantes no contabilizarán', @Sev
    FROM (SELECT DISTINCT TipoComprobanteId AS Id FROM saludips.SIIFNet_TiposComprobantesCuentasContables WHERE EmpresaId = @EmpresaId AND Vigencia = @Anio) tc
    LEFT JOIN saludips.SIIFNet_TiposComprobantes t ON t.EmpresaId = @EmpresaId AND t.Id = tc.Id
    WHERE NOT EXISTS (SELECT 1 FROM saludips.SIIFNet_TiposComprobantesCuentasContables n WHERE n.EmpresaId = @EmpresaId AND n.TipoComprobanteId = tc.Id AND n.Vigencia = @Anio + 1)
    UNION ALL
    SELECT N'cConfiguracionAnual', N'Configuración anual (cConfiguracionAnual)', N'Sin registros de ' + CAST(@Anio + 1 AS nvarchar(4)), N'Parámetros del año siguiente',
           NULL, NULL, N'Parámetros anuales del año siguiente sin cargar', @Sev
    WHERE EXISTS (SELECT 1 FROM saludips.dbo_cConfiguracionAnual WHERE EmpresaId = @EmpresaId AND AnoParametro = @Anio)
      AND NOT EXISTS (SELECT 1 FROM saludips.dbo_cConfiguracionAnual WHERE EmpresaId = @EmpresaId AND AnoParametro = @Anio + 1)
    """)
{
    protected override bool Aplica(ContextoAuditoria ctx) => ctx.Hoy.Month >= (int)ctx.Umbral("DesdeMes", 11);

    protected override IReadOnlyDictionary<string, object?> Parametros(ContextoAuditoria ctx)
        => new Dictionary<string, object?> { ["Sev"] = ctx.Hoy.Month >= (int)ctx.Umbral("CriticaDesdeMes", 12) ? "CRITICA" : "ALTA" };
}

/// <summary>CON-04. Cuenta por cobrar (normal o radicada) de pagadores activos sin DetalleCartera = 2. La parte de CxP queda para la fase 2 (no se sabe qué tabla la referencia) [S].</summary>
public sealed class Con04() : ReglaSql("CON-04", "Contabilidad", Severidad.Alta, """
    SELECT N'Cuentas:' + cu.Codigo AS ClaveEntidad, cu.Codigo + N' ' + ISNULL(cu.Nombre, N'') AS NombreEntidad,
           N'DetalleCartera = ' + ISNULL(CAST(cu.DetalleCartera AS nvarchar(10)), N'(vacío)') AS ValorEncontrado, N'DetalleCartera = 2' AS ValorEsperado,
           COUNT(DISTINCT e.IdEmpresa) AS ImpactoCantidad, CAST(NULL AS decimal(19,4)) AS ImpactoValor,
           N'Pagadores activos cuya cartera no aparece' AS ImpactoDetalle, CAST(NULL AS varchar(8)) AS Severidad
    FROM saludips.dbo_mEmpresasCuentasContables c
    JOIN saludips.dbo_mEmpresas e ON e.EmpresaId = c.EmpresaId AND e.IdEmpresa = c.IdEmpresa AND e.EstadoEntidad = 1
    CROSS APPLY (VALUES (c.CuentaPorCobrarId), (c.CuentaPorCobrarRadicadaId)) v(CuentaId)
    JOIN saludips.SIIFNet_Cuentas cu ON cu.EmpresaId = c.EmpresaId AND cu.Id = v.CuentaId
    WHERE c.EmpresaId = @EmpresaId AND c.Vigencia = @Anio AND ISNULL(c.EstadoEntidad, 1) = 1 AND ISNULL(cu.DetalleCartera, 0) <> 2
    GROUP BY cu.Codigo, cu.Nombre, cu.DetalleCartera
    """);

/// <summary>FE-01. Resolución DIAN vencida o que vence en menos de N días. Solo la vigente más reciente por prefijo y modalidad [S: TipoResolucionId no distingue nada].</summary>
public sealed class Fe01() : ReglaSql("FE-01", "Factura electrónica", Severidad.Alta, $"""
    SELECT N'AutorizacionesDIAN:' + CAST(a.Id AS nvarchar(40)) AS ClaveEntidad,
           ISNULL(a.Prefijo, N'(sin prefijo)') + N' · resolución ' + ISNULL(CAST(CAST(a.NumeroResolucion AS bigint) AS nvarchar(60)), N'?') AS NombreEntidad,
           N'Vence el ' + CONVERT(nvarchar(10), a.FechaFinVigencia, 23) AS ValorEncontrado, N'Vigente por más de ' + CAST(@DiasAviso AS nvarchar(10)) + N' días' AS ValorEsperado,
           CAST(NULL AS int) AS ImpactoCantidad, CAST(NULL AS decimal(19,4)) AS ImpactoValor, CAST(NULL AS nvarchar(400)) AS ImpactoDetalle,
           CASE WHEN a.FechaFinVigencia < @Hoy THEN 'CRITICA' ELSE 'ALTA' END AS Severidad
    FROM saludips.dbo_AutorizacionesDIAN a
    WHERE a.EmpresaId = @EmpresaId AND ISNULL(a.EstadoEntidad, 1) = 1 AND a.FechaFinVigencia IS NOT NULL
      AND a.FechaFinVigencia < DATEADD(DAY, @DiasAviso, @Hoy)
      AND NOT EXISTS (SELECT 1 FROM saludips.dbo_AutorizacionesDIAN b
                      WHERE b.EmpresaId = @EmpresaId AND ISNULL(b.EstadoEntidad, 1) = 1 AND ISNULL(b.Prefijo, N'') = ISNULL(a.Prefijo, N'') AND ISNULL(CAST(b.Modalidad AS int), -1) = ISNULL(CAST(a.Modalidad AS int), -1)
                        AND (b.FechaFinVigencia > a.FechaFinVigencia OR (b.FechaFinVigencia = a.FechaFinVigencia AND b.Id > a.Id)))
    """)
{
    protected override IReadOnlyDictionary<string, object?> Parametros(ContextoAuditoria ctx)
        => new Dictionary<string, object?> { ["DiasAviso"] = (int)ctx.Umbral("DiasAviso", 30) };
}

/// <summary>FE-02. Numeración usada por encima de N % del rango (por defecto 90 %). [S] NumeracionActual es el último número usado.</summary>
public sealed class Fe02() : ReglaSql("FE-02", "Factura electrónica", Severidad.Alta, """
    SELECT N'AutorizacionesDIAN:' + CAST(a.Id AS nvarchar(40)) AS ClaveEntidad,
           ISNULL(a.Prefijo, N'(sin prefijo)') + N' · resolución ' + ISNULL(CAST(CAST(a.NumeroResolucion AS bigint) AS nvarchar(60)), N'?') AS NombreEntidad,
           N'Usado ' + CAST(CAST(100.0 * (a.NumeracionActual - a.NumeracionDesde + 1) / (a.NumeracionHasta - a.NumeracionDesde + 1) AS decimal(5,1)) AS nvarchar(10)) + N' % del rango' AS ValorEncontrado,
           N'Menos de ' + CAST(@Porcentaje AS nvarchar(10)) + N' % usado' AS ValorEsperado,
           CAST(a.NumeracionHasta - a.NumeracionActual AS int) AS ImpactoCantidad, CAST(NULL AS decimal(19,4)) AS ImpactoValor,
           N'Números disponibles' AS ImpactoDetalle, CAST(NULL AS varchar(8)) AS Severidad
    FROM saludips.dbo_AutorizacionesDIAN a
    WHERE a.EmpresaId = @EmpresaId AND ISNULL(a.EstadoEntidad, 1) = 1
      AND a.NumeracionHasta > a.NumeracionDesde AND a.NumeracionActual IS NOT NULL
      AND 100.0 * (a.NumeracionActual - a.NumeracionDesde + 1) / (a.NumeracionHasta - a.NumeracionDesde + 1) > @Porcentaje
      AND NOT EXISTS (SELECT 1 FROM saludips.dbo_AutorizacionesDIAN b
                      WHERE b.EmpresaId = @EmpresaId AND ISNULL(b.EstadoEntidad, 1) = 1 AND ISNULL(b.Prefijo, N'') = ISNULL(a.Prefijo, N'') AND ISNULL(CAST(b.Modalidad AS int), -1) = ISNULL(CAST(a.Modalidad AS int), -1)
                        AND (b.FechaFinVigencia > a.FechaFinVigencia OR (b.FechaFinVigencia = a.FechaFinVigencia AND b.Id > a.Id)))
    """)
{
    protected override IReadOnlyDictionary<string, object?> Parametros(ContextoAuditoria ctx)
        => new Dictionary<string, object?> { ["Porcentaje"] = ctx.Umbral("PorcentajeAviso", 90) };
}

/// <summary>FE-03. Sede que facturó en los últimos 30 días y no tiene una resolución DIAN activa asignada (AutorizacionesDIANSedesComprobantes). [S] el SedeId de SaludIPS es el código de origen de dim.Sede; el tipo de comprobante no se puede cruzar con los hechos.</summary>
public sealed class Fe03() : ReglaSql("FE-03", "Factura electrónica", Severidad.Critica, """
    SELECT N'Sede:' + s.CodigoOrigen AS ClaveEntidad, s.Nombre AS NombreEntidad,
           N'Factura sin resolución DIAN asignada' AS ValorEncontrado, N'Al menos una resolución activa para la sede' AS ValorEsperado,
           f.Cant AS ImpactoCantidad, f.Valor AS ImpactoValor, N'Facturas y valor de los últimos 30 días en la sede' AS ImpactoDetalle, CAST(NULL AS varchar(8)) AS Severidad
    FROM dim.Sede s
    CROSS APPLY (SELECT COUNT(DISTINCT fl.NumeroDocumento) AS Cant, SUM(fl.ValorNeto) AS Valor FROM fact.FacturaLinea fl
                 WHERE fl.EmpresaId = @EmpresaId AND fl.SedeId = s.SedeId AND fl.FechaEmisionKey >= @Desde30 AND fl.Anulada = 0 AND fl.TipoDocumento = 'FACTURA') f
    WHERE s.EmpresaId = @EmpresaId AND f.Cant > 0
      AND NOT EXISTS (SELECT 1 FROM saludips.dbo_AutorizacionesDIANSedesComprobantes sc
                      JOIN saludips.dbo_AutorizacionesDIAN a ON a.EmpresaId = sc.EmpresaId AND a.Id = sc.AutorizacionDIANId AND ISNULL(a.EstadoEntidad, 1) = 1
                      WHERE sc.EmpresaId = @EmpresaId AND ISNULL(sc.EstadoEntidad, 1) = 1 AND CAST(sc.SedeId AS varchar(30)) = s.CodigoOrigen)
    """);

/// <summary>TAR-01. Pagador activo sin manual tarifario. [S] CodManual NULL o 0 significa «sin manual».</summary>
public sealed class Tar01() : ReglaSql("TAR-01", "Tarifas", Severidad.Critica, $"""
    SELECT CAST(e.IdEmpresa AS nvarchar(200)) AS ClaveEntidad, e.NomEmpresa AS NombreEntidad,
           N'Sin manual tarifario' AS ValorEncontrado, N'Manual tarifario asignado' AS ValorEsperado,
           imp.Cant AS ImpactoCantidad, imp.Valor AS ImpactoValor, N'Facturas y valor de los últimos 30 días del pagador' AS ImpactoDetalle, CAST(NULL AS varchar(8)) AS Severidad
    FROM saludips.dbo_mEmpresas e
    {ImpactoFacturacion("e.IdEmpresa")}
    WHERE e.EmpresaId = @EmpresaId AND e.EstadoEntidad = 1 AND ISNULL(e.CodManual, 0) = 0
    """);

/// <summary>
/// TAR-02. Por pagador activo con manual: procedimientos del portafolio, de los tipos de servicio que el pagador puede usar (TipoServiciosXEmpresa),
/// sin tarifa mayor que cero en su manual y plan (pTarifasProcedimientos). Replica la idea de SIPS_198. [S] el cruce es por CodManual y, si el pagador lo tiene, CodPlan.
/// </summary>
public sealed class Tar02() : ReglaSql("TAR-02", "Tarifas", Severidad.Alta, """
    SELECT CAST(e.IdEmpresa AS nvarchar(200)) AS ClaveEntidad, e.NomEmpresa AS NombreEntidad,
           CAST(x.Faltan AS nvarchar(20)) + N' procedimientos sin tarifa' AS ValorEncontrado, N'Tarifa en el manual ' + CAST(e.CodManual AS nvarchar(10)) + ISNULL(N' y plan ' + CAST(e.CodPlan AS nvarchar(10)), N'') AS ValorEsperado,
           x.Faltan AS ImpactoCantidad, CAST(NULL AS decimal(19,4)) AS ImpactoValor, N'Procedimientos del portafolio sin tarifa' AS ImpactoDetalle, CAST(NULL AS varchar(8)) AS Severidad
    FROM saludips.dbo_mEmpresas e
    CROSS APPLY (
        SELECT COUNT(DISTINCT pf.CodProcedimiento) AS Faltan
        FROM saludips.dbo_TipoServiciosXEmpresa t
        JOIN saludips.dbo_pPortafolio pf ON pf.EmpresaId = t.EmpresaId AND pf.CodTipoServicio = t.CodTipoServicio
        WHERE t.EmpresaId = @EmpresaId AND t.IdEmpresa = e.IdEmpresa
          AND NOT EXISTS (SELECT 1 FROM saludips.dbo_pTarifasProcedimientos tp
                          WHERE tp.EmpresaId = @EmpresaId AND tp.CodProcedimiento = pf.CodProcedimiento AND tp.CodManual = e.CodManual
                            AND (e.CodPlan IS NULL OR tp.CodPlan = e.CodPlan) AND tp.Valor > 0)) x
    WHERE e.EmpresaId = @EmpresaId AND e.EstadoEntidad = 1 AND ISNULL(e.CodManual, 0) <> 0 AND x.Faltan > 0
    """);

/// <summary>TAR-06. Procedimientos sin CUPS y suministros sin CUM. Un hallazgo por tipo (no uno por ítem) para no inundar la lista.</summary>
public sealed class Tar06() : ReglaSql("TAR-06", "Tarifas", Severidad.Alta, """
    SELECT N'pProcedimientos.CodCups' AS ClaveEntidad, N'Procedimientos sin CUPS' AS NombreEntidad,
           CAST(COUNT(*) AS nvarchar(20)) + N' de ' + CAST((SELECT COUNT(*) FROM saludips.dbo_pProcedimientos WHERE EmpresaId = @EmpresaId) AS nvarchar(20)) AS ValorEncontrado,
           N'Todos con código CUPS' AS ValorEsperado, COUNT(*) AS ImpactoCantidad, CAST(NULL AS decimal(19,4)) AS ImpactoValor,
           N'Ejemplos: ' + ISNULL((SELECT STRING_AGG(CAST(z.CodProcedimiento AS nvarchar(40)), N', ') FROM (SELECT TOP (5) CodProcedimiento FROM saludips.dbo_pProcedimientos
                                    WHERE EmpresaId = @EmpresaId AND LTRIM(RTRIM(ISNULL(CodCups, N''))) = N'' ORDER BY CodProcedimiento) z), N'') AS ImpactoDetalle, CAST(NULL AS varchar(8)) AS Severidad
    FROM saludips.dbo_pProcedimientos WHERE EmpresaId = @EmpresaId AND LTRIM(RTRIM(ISNULL(CodCups, N''))) = N''
    HAVING COUNT(*) > 0
    UNION ALL
    SELECT N'pSuministros.CodCum', N'Suministros sin CUM',
           CAST(COUNT(*) AS nvarchar(20)) + N' de ' + CAST((SELECT COUNT(*) FROM saludips.dbo_pSuministros WHERE EmpresaId = @EmpresaId) AS nvarchar(20)),
           N'Todos con código CUM', COUNT(*), NULL,
           N'Ejemplos: ' + ISNULL((SELECT STRING_AGG(CAST(z.CodSuministro AS nvarchar(40)), N', ') FROM (SELECT TOP (5) CodSuministro FROM saludips.dbo_pSuministros
                                    WHERE EmpresaId = @EmpresaId AND LTRIM(RTRIM(ISNULL(CodCum, N''))) = N'' ORDER BY CodSuministro) z), N''), NULL
    FROM saludips.dbo_pSuministros WHERE EmpresaId = @EmpresaId AND LTRIM(RTRIM(ISNULL(CodCum, N''))) = N''
    HAVING COUNT(*) > 0
    """);

/// <summary>USU-01. Usuario no bloqueado cuyo empleado (AspNetUsers.TerceroId → Nomina.Empleados) tiene contratos y todos con FechaSalida pasada. [S] bloqueado = Bloqueado = 1 o bloqueo temporal vigente.</summary>
public sealed class Usu01() : ReglaSql("USU-01", "Seguridad", Severidad.Critica, """
    SELECT N'AspNetUsers:' + CAST(u.Id AS nvarchar(100)) AS ClaveEntidad, ISNULL(u.UserName, CAST(u.Id AS nvarchar(100))) AS NombreEntidad,
           N'Activo; empleado retirado el ' + CONVERT(nvarchar(10), s.UltimaSalida, 23) AS ValorEncontrado, N'Usuario bloqueado' AS ValorEsperado,
           CAST(NULL AS int) AS ImpactoCantidad, CAST(NULL AS decimal(19,4)) AS ImpactoValor, CAST(NULL AS nvarchar(400)) AS ImpactoDetalle, CAST(NULL AS varchar(8)) AS Severidad
    FROM saludips.dbo_AspNetUsers u
    JOIN saludips.Nomina_Empleados em ON em.EmpresaId = u.EmpresaId AND em.TerceroId = u.TerceroId
    CROSS APPLY (SELECT MAX(c.FechaSalida) AS UltimaSalida, COUNT(*) AS Contratos,
                        SUM(CASE WHEN c.FechaSalida IS NULL OR c.FechaSalida >= @Hoy THEN 1 ELSE 0 END) AS Vigentes
                 FROM saludips.Nomina_Contratos c WHERE c.EmpresaId = u.EmpresaId AND c.EmpleadoId = em.Id) s
    WHERE u.EmpresaId = @EmpresaId AND u.TerceroId IS NOT NULL AND s.Contratos > 0 AND s.Vigentes = 0
      AND ISNULL(u.Bloqueado, 0) = 0 AND (u.LockoutEndDateUtc IS NULL OR u.LockoutEndDateUtc <= @Ahora)
    """);

public static class ExtensionesContexto
{
    /// <summary>Umbral configurado para la regla (cfg.ReglaConfiguracion / por empresa) o el valor por defecto.</summary>
    public static decimal Umbral(this ContextoAuditoria ctx, string nombre, decimal porDefecto)
        => ctx.Umbrales.TryGetValue(nombre, out var v) ? v : porDefecto;
}
