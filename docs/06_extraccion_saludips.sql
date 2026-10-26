/* =====================================================================
   Extracción SaludIPS -> API de ingesta (contrato v1, ver 05_contrato_api_ingesta.md)
   Se ejecuta en la BD de SaludIPS con un login de SOLO LECTURA.
   Recomendado: publicar cada SELECT como vista en un esquema `analitica` desplegado con
   SQLServerScriptsRunner, versionado junto con SaludIPS. El agente solo lee esas vistas.

   Parámetros (el agente los pasa como SqlParameter; aquí declarados para pruebas):
     @Desde/@Hasta  ventana por fecha del hecho (inclusive)
     @Corte         fecha de corte para fotos (admisiones pendientes)
     @Sal           secreto por empresa para anonimizar pacientes (nunca sale del agente)
   Reglas de origen: ver 03_analisis_reportes_SaludIPS.md y 24_SPEC_MAESTRA.md §5.1.
   NO SE HA EJECUTADO: validar contra una copia de SaludIPS.
   ===================================================================== */
DECLARE @Desde date = '2026-09-01', @Hasta date = '2026-10-26', @Corte date = '2026-10-26',
        @Sal nvarchar(64) = N'<secreto-por-empresa>';
DECLARE @HastaEx datetime = DATEADD(DAY, 1, CAST(@Hasta AS datetime));

/* ============================ CATÁLOGOS (completos) ============================ */

-- sedes
SELECT CAST(s.Id AS varchar(30)) AS codigoOrigen, s.Nombre AS nombre, s.NombreCorto AS nombreCorto,
       CAST(CASE WHEN s.EstadoEntidad = 1 THEN 1 ELSE 0 END AS bit) AS activa
FROM SIIFNet.Sedes s;

-- lineas-servicio (pTipoServicio)
SELECT CAST(t.CodTipoServicio AS varchar(30)) AS codigoOrigen, t.NomTipoServicio AS nombre
FROM dbo.pTipoServicio t;

-- centros-costo (vigencia más reciente por código)
SELECT x.Codigo AS codigoOrigen, x.Nombre AS nombre, p.Codigo AS codigoPadre, x.Nivel AS nivel,
       x.TipoCentroCosto AS tipoOrigen, x.ManejaMovimientos AS manejaMovimientos,
       un.Nombre AS unidadNegocio, CAST(CASE WHEN x.EstadoEntidad = 1 THEN 1 ELSE 0 END AS bit) AS activo
FROM (SELECT c.*, ROW_NUMBER() OVER (PARTITION BY c.Codigo ORDER BY c.Vigencia DESC) AS rn
      FROM SIIFNet.CentrosCostos c) x
LEFT JOIN SIIFNet.CentrosCostos p ON p.Id = x.CentroCostoPadreId
LEFT JOIN SIIFNet.Referencias un ON un.Id = x.UnidadNegocioId
WHERE x.rn = 1;

-- cuentas (NormatividadId = 1, vigencia más reciente por código)
SELECT x.Codigo AS codigo, x.Nombre AS nombre, x.Nivel AS nivel, p.Codigo AS codigoPadre,
       CASE x.Naturaleza WHEN 0 THEN 'D' ELSE 'C' END AS naturaleza,
       x.MovimientoContrario AS movimientoContrario,
       x.DetalleCartera AS detalleCartera,                       -- 0 no, 1 CxP, 2 CxC
       CAST(CASE WHEN x.UsoCuenta = 2 THEN 1 ELSE 0 END AS bit) AS esBanco,
       x.ManejaCentroCosto AS manejaCentroCosto, x.ManejaMovimientos AS aceptaMovimiento
FROM (SELECT c.*, ROW_NUMBER() OVER (PARTITION BY c.Codigo ORDER BY c.Vigencia DESC) AS rn
      FROM SIIFNet.Cuentas c WHERE c.NormatividadId = 1) x
LEFT JOIN SIIFNet.Cuentas p ON p.Id = x.CuentaPadreId
WHERE x.rn = 1;

-- tipos-comprobante (claseSugerida: el portal permite corregirla por empresa)
SELECT CAST(t.Id AS varchar(30)) AS codigoOrigen, t.Prefijo AS prefijo, t.Nombre AS nombre,
       CASE COALESCE(b.Prefijo, t.Prefijo)
            WHEN 'CSI0' THEN 'SALDO_INICIAL' WHEN 'CDC0' THEN 'CIERRE'
            WHEN 'TEB'  THEN 'TRASLADO'      WHEN 'CNOM' THEN 'NOMINA'
            ELSE 'NORMAL' END AS claseSugerida
FROM SIIFNet.TiposComprobantes t
LEFT JOIN SIIFNet.TiposComprobantes b ON b.Id = t.TipoComprobanteBaseId;

-- terceros (INCREMENTAL por FechaDigitacion; pacientes anonimizados)
SELECT CAST(t.Id AS varchar(30)) AS codigoOrigen,
       CASE t.TipoPersona WHEN 1 THEN 'N' ELSE 'J' END AS tipoPersona,   -- POR VALIDAR valores de TipoPersona
       CASE WHEN pac.TerceroId IS NOT NULL
            THEN CONVERT(varchar(64), HASHBYTES('SHA2_256', @Sal + t.Identificacion), 2)
            ELSE t.Identificacion END AS identificacion,
       CASE WHEN pac.TerceroId IS NOT NULL THEN NULL ELSE t.DigitoVerificacion END AS digitoVerificacion,
       CASE WHEN pac.TerceroId IS NOT NULL THEN NULL ELSE COALESCE(t.NombreCompleto, t.RazonSocial) END AS nombre,
       CAST(CASE WHEN pac.TerceroId IS NOT NULL THEN 1 ELSE 0 END AS bit) AS esPaciente,
       t.ClaseTercero AS claseOrigen
FROM SIIFNet.Terceros t
LEFT JOIN (SELECT DISTINCT TerceroId FROM dbo.mPacientes WHERE TerceroId IS NOT NULL) pac ON pac.TerceroId = t.Id
WHERE t.FechaDigitacion >= @Desde;            -- en carga INICIAL: sin filtro

-- pagadores (mEmpresas)
SELECT CAST(e.IdEmpresa AS varchar(30)) AS codigoOrigen, e.NomEmpresa AS nombre,
       e.CodInstitucionAdm AS codigoAdministradora,
       CAST(i.TerceroId AS varchar(30)) AS terceroCodigo,         -- confirmado: mEmpresas -> mInstituciones -> TerceroId
       CAST(CASE WHEN e.IdEmpresa IN (SELECT IdEmpresaParticulares FROM dbo.cAdministracion) THEN 1 ELSE 0 END AS bit) AS esParticular,
       CAST(CASE WHEN e.EstadoEntidad = 1 THEN 1 ELSE 0 END AS bit) AS activo
FROM dbo.mEmpresas e
LEFT JOIN dbo.mInstituciones i ON i.CodInstitucion = e.CodInstitucionAdm;

-- contratos (mContratos)
SELECT CAST(c.IdContrato AS varchar(30)) AS codigoOrigen, c.NoContrato AS numeroContrato,
       (SELECT TOP (1) CAST(e.IdEmpresa AS varchar(30)) FROM dbo.mEmpresas e WHERE e.IdContrato = c.IdContrato ORDER BY e.IdEmpresa) AS pagadorCodigo,
       CASE c.CodTipoContrato WHEN 1 THEN 'EVENTO' WHEN 2 THEN 'CAPITACION' WHEN 3 THEN 'PGP' WHEN 4 THEN 'PAQUETE' ELSE 'OTRO' END AS modalidad,  -- confirmado en SIPS_706
       CAST(c.FechaContrato AS date) AS fechaInicio, CAST(c.FechaFinaliza AS date) AS fechaFin,
       CAST(ROUND(c.VrContrato, 2) AS decimal(19,2)) AS valorContrato
FROM dbo.mContratos c;

-- servicios (procedimientos 'P:' y suministros 'S:')
SELECT 'P:' + p.CodProcedimiento AS codigoOrigen, p.NomProcedimiento AS nombre, p.CodCups AS codigoEstandar, 'PROCEDIMIENTO' AS tipo
FROM dbo.pProcedimientos p
UNION ALL
SELECT 'S:' + s.CodSuministro, s.NomSuministro, s.CodCum, 'SUMINISTRO'
FROM dbo.pSuministros s;

-- articulos (almacén)
SELECT a.Codigo AS codigoOrigen, a.Nombre AS nombre, l.Nombre AS linea, g.Nombre AS grupo, sg.Nombre AS subgrupo,
       gen.Nombre AS generico, lab.Nombre AS laboratorio, um.Nombre AS unidadMedida, a.CodigoCUM AS codigoCum
FROM Almacen.ArticulosBase a
JOIN Almacen.SubGrupos sg ON sg.Id = a.SubGrupoId
JOIN Almacen.Grupos g ON g.Id = sg.GrupoId
JOIN Almacen.Lineas l ON l.Id = g.LineaId
LEFT JOIN SIIFNet.Referencias gen ON gen.Id = a.ArticuloGenericoId
LEFT JOIN SIIFNet.Referencias lab ON lab.Id = a.LaboratorioId
LEFT JOIN SIIFNet.Referencias um  ON um.Id  = a.UnidadMedidaId;

-- lotes
SELECT CAST(lt.Id AS varchar(30)) AS codigoOrigen, a.Codigo AS articuloCodigo, lt.NumeroLote AS numeroLote,
       CAST(lt.FechaVencimiento AS date) AS fechaVencimiento, ri.RegistroSanitario AS registroSanitario
FROM Almacen.Lotes lt
JOIN Almacen.ArticulosBase a ON a.Id = lt.ArticuloId
LEFT JOIN Almacen.RegistrosInvima ri ON ri.Id = lt.RegistroInvimaId;

-- almacenes (bodegas)
SELECT b.Codigo AS codigoOrigen, b.Nombre AS nombre,
       (SELECT TOP (1) CAST(pb.IdSede AS varchar(30)) FROM dbo.pBodegas pb WHERE pb.CodBodegaSIIFNet = b.Codigo) AS sedeCodigo,
       CAST(CASE WHEN b.EstadoEntidad = 1 THEN 1 ELSE 0 END AS bit) AS activo
FROM Almacen.Bodegas b;

-- profesionales
SELECT p.IdPersonal AS codigoOrigen, p.NomPersonal AS nombre, esp.NomReferencia AS especialidad,
       CAST(CASE WHEN p.Activo = 1 THEN 1 ELSE 0 END AS bit) AS activo
FROM dbo.mPersonal p
LEFT JOIN dbo.rReferencias esp ON esp.CodClaseReferencia = 8 AND esp.CodReferencia = p.CodEspecialidad;

-- usuarios-operativos ('C:' legado cUsuarios, 'A:' AspNetUsers de SIIFNet). Sin claves ni firmas.
SELECT 'C:' + CAST(u.IdcUsuario AS varchar(10)) AS codigoOrigen, COALESCE(u.NombresCompletos, u.Usuario) AS nombre,
       CAST(CASE WHEN u.Activo = 1 THEN 1 ELSE 0 END AS bit) AS activo
FROM dbo.cUsuarios u
UNION ALL
SELECT 'A:' + a.Id, a.UserName, CAST(CASE WHEN a.Bloqueado = 1 THEN 0 ELSE 1 END AS bit)
FROM dbo.AspNetUsers a;

-- cuentas-bancarias (bancos 'B:' y cajas 'C:')
SELECT 'B:' + CAST(bc.Id AS varchar(10)) AS codigoOrigen, 'BANCO' AS tipo, bt.NombreCompleto AS entidad,
       '****' + RIGHT(bc.NumCuentaBancaria, 4) AS numeroEnmascarado, cu.Codigo AS cuentaCodigo
FROM SIIFNet.BancosCuentas bc
JOIN SIIFNet.Terceros bt ON bt.Id = bc.BancoId
LEFT JOIN SIIFNet.Cuentas cu ON cu.Id = bc.CuentaId
UNION ALL
SELECT 'C:' + c.CodCaja, 'CAJA', COALESCE(c.NomCaja, c.CodCaja), NULL, c.IdCuenta     -- POR VALIDAR: IdCuenta = código PUC
FROM dbo.pCajas c;

/* ============================ HECHOS (ventana) ============================ */

-- asientos  (estado 1 confirmado, 2 anulado; EstadoEntidad 0 no se envía => se elimina en la ventana)
SELECT CAST(td.Id AS varchar(30)) AS sourceKey,
       CAST(td.dContabilidadRelacionadoId AS varchar(30)) AS relacionadoSourceKey,
       CAST(tm.Id AS varchar(30)) AS movimientoSourceKey,
       CAST(tm.FechaMovimiento AS date) AS fecha,
       CAST(tm.TipoComprobanteId AS varchar(30)) AS tipoComprobanteCodigo,
       tm.NumComprobante AS numeroComprobante,
       td.NumDocumento AS numDocumento,
       cu.Codigo AS cuentaCodigo,
       cc.Codigo AS centroCostoCodigo,
       CAST(tm.SedeId AS varchar(30)) AS sedeCodigo,
       CAST(td.TerceroId AS varchar(30)) AS terceroCodigo,
       CASE WHEN td.BancoCuentaId IS NOT NULL THEN 'B:' + CAST(td.BancoCuentaId AS varchar(10))
            WHEN td.CodCajaId IS NOT NULL THEN 'C:' + td.CodCajaId END AS cuentaBancariaCodigo,
       art.Codigo AS articuloCodigo,
       'A:' + au.Id AS usuarioCodigo,
       CAST(ROUND(td.ValorDebito, 2)  AS decimal(19,2)) AS debito,
       CAST(ROUND(td.ValorCredito, 2) AS decimal(19,2)) AS credito,
       CAST(td.FechaCartera AS date) AS fechaCartera,
       CAST(td.FechaVencimientoPago AS date) AS fechaVencimiento,
       CAST(CASE WHEN tm.EstadoEntidad = 1 AND td.EstadoEntidad = 1 THEN 1 ELSE 2 END AS tinyint) AS estado,
       LEFT(td.DetalleRegistro, 300) AS descripcion
FROM SIIFNet.mMovimientos tm
JOIN SIIFNet.mMovimientosDetalles td ON td.mMovimientoId = tm.Id
JOIN SIIFNet.Cuentas cu ON cu.Id = td.CuentaId
LEFT JOIN SIIFNet.CentrosCostos cc ON cc.Id = td.CentroCostoId
LEFT JOIN Almacen.MovimientosAlmacenDetalles mad ON mad.Id = td.MovimientoAlmacenDetalleId
LEFT JOIN Almacen.ArticulosBase art ON art.Id = mad.ArticuloId
LEFT JOIN dbo.AspNetUsers au      -- misma equivalencia guid <-> Id que usa MOVIMIENTOSCONTABLESGENERAL
       ON au.Id = CAST(CAST(CONVERT(bigint, CONVERT(varbinary(8), '0x' + RIGHT(REPLICATE('0', 8) + SUBSTRING(CONVERT(nvarchar(36), tm.UsuarioID), 1, 8), 8), 1)) AS bigint) AS varchar(20))
       OR au.Id = CAST(tm.UsuarioID AS varchar(36))
WHERE tm.EstadoEntidad IN (1, 2) AND td.EstadoEntidad IN (1, 2)
  AND tm.FechaMovimiento >= @Desde AND tm.FechaMovimiento < @HastaEx;

-- facturas (grano: servicio facturado; enlace por dFacturasServicios, NO por IdAdmision)
-- OJO: ningún reporte de SaludIPS usa dFacturasServicios. Validar su cobertura (02_mapeo, riesgo 1)
-- antes de adoptarla; si hay facturas sin detalle, volver al prorrateo por IdAdmision de SIPS_4.
SELECT f.NoFactura + '|' + CAST(f.IdSede AS varchar(10)) + '|' + CAST(s.IdServicio AS varchar(12)) AS sourceKey,
       f.NoFactura AS numeroDocumento,
       CAST(f.IdAdmision AS varchar(30)) AS admisionSourceKey,
       CAST(f.FechaFactura AS date) AS fechaEmision,
       CAST(s.FechaRealizado AS date) AS fechaPrestacion,
       CAST(re.FechaEnvio AS date) AS fechaEnvioRips,
       re.NoCuentaCobro AS cuentaCobro,
       CAST(f.IdEmpresa AS varchar(30)) AS pagadorCodigo,
       CAST(a.IdContrato AS varchar(30)) AS contratoCodigo,
       CAST(f.IdSede AS varchar(30)) AS sedeCodigo,
       s.CodBodega AS almacenCodigo,
       cc.Codigo AS centroCostoCodigo,
       CAST(s.CodTipoServicio AS varchar(30)) AS lineaServicioCodigo,
       CASE WHEN pp.CodProcedimiento IS NOT NULL THEN 'P:' ELSE 'S:' END + s.CodServicio AS servicioCodigo,
       s.IdMedico AS profesionalCodigo,
       'C:' + CAST(f.IdcUsuario AS varchar(10)) AS usuarioCodigo,
       CAST(s.Cantidad AS decimal(18,4)) AS cantidad,
       CAST(ROUND(s.VrTotal, 2) AS decimal(19,2)) AS valorServicio,
       CAST(ROUND(ISNULL(s.VrDescuento, 0), 2) AS decimal(19,2)) AS descuento,
       CAST(ROUND(ISNULL(s.VrCuotaModeradora, 0), 2) AS decimal(19,2)) AS cuotaModeradora,
       CAST(ROUND(ISNULL(s.VrIvaUnitario, 0) * s.Cantidad, 2) AS decimal(19,2)) AS iva,
       CAST(ROUND(CASE WHEN f.CodPaquete <> '0' THEN ISNULL(pq.VrTotal, 0)
                       ELSE f.VrTotal * s.VrTotal / NULLIF(f.VrServicios, 0) END, 2) AS decimal(19,2)) AS valorNeto,
       CAST(ROUND(s.VrCosto, 2) AS decimal(19,2)) AS vrCostoOrigen,          -- POR VALIDAR: ¿unitario o total?
       CAST(CASE WHEN ISNULL(f.Anulado, 0) <> 0 OR ISNULL(s.Anulado, 0) <> 0 THEN 1 ELSE 0 END AS bit) AS anulada,
       fe.Estado AS estadoFE
FROM dbo.mFacturas f
JOIN dbo.dFacturasServicios dfs ON dfs.NoFactura = f.NoFactura AND dfs.IdSede = f.IdSede
JOIN dbo.mServicios s ON s.IdServicio = dfs.IdServicio
JOIN dbo.mAdmisiones a ON a.IdAdmision = f.IdAdmision
LEFT JOIN dbo.mRipsEnvios re ON re.IdRipsEnvio = f.IdRipsEnvio
LEFT JOIN SIIFNet.CentrosCostos cc ON cc.Id = s.centroCostoID                -- POR VALIDAR vs CodCentroCosto
LEFT JOIN dbo.pProcedimientos pp ON pp.CodProcedimiento = s.CodServicio
LEFT JOIN dbo.dFacturasPaq pq ON pq.NoFactura = f.NoFactura AND pq.IdServicio = s.IdServicio
OUTER APPLY (SELECT TOP (1) ISNULL(e.Estado, 3) AS Estado FROM dbo.EnviosFacturaElectronica e
             WHERE e.NoFactura = f.NoFactura AND e.Tipo = 1 ORDER BY e.Id DESC) fe
WHERE f.FechaFactura >= @Desde AND f.FechaFactura < @HastaEx;
-- Carga INCREMENTAL complementaria: mismas columnas con
--   WHERE f.FechaAnulacion >= @Desde   (anulaciones de facturas fuera de la ventana)

-- recibos (tesorería: una fila por recibo x forma de pago; enlace por CodCaja + NoRecibo)
SELECT r.CodCaja + '|' + CAST(r.NoRecibo AS varchar(12)) + '|' + ISNULL(CAST(d.Id AS varchar(12)), '0') AS sourceKey,
       CAST(r.FechaRecibo AS date) AS fecha,
       'C:' + r.CodCaja AS cuentaBancariaCodigo,
       'INGRESO' AS tipoMovimiento,
       CAST(r.CodTipoConcepto AS varchar(10)) AS conceptoCodigo, tc.NomTipoConcepto AS concepto,
       fp.Nombre AS formaPago,
       CAST(ad.IdEmpresa AS varchar(30)) AS pagadorCodigo,
       CAST(ad.IdSede AS varchar(30)) AS sedeCodigo,
       'C:' + CAST(r.IdcUsuario AS varchar(10)) AS usuarioCodigo,
       CAST(r.NoRecibo AS varchar(30)) AS numeroDocumento,
       r.NoFactura AS numeroFactura,
       CAST(ROUND(COALESCE(d.ValorPago, r.VrRecibo), 2) AS decimal(19,2)) AS valor,
       CAST(CASE WHEN ISNULL(r.Anulado, 0) <> 0 THEN 1 ELSE 0 END AS bit) AS anulado
FROM dbo.mRecibos r
LEFT JOIN dbo.dRecibosFormasPagos d ON d.CodCaja = r.CodCaja AND d.NoRecibo = r.NoRecibo AND d.EstadoEntidad = 1
LEFT JOIN SIIFNet.FormasPagos fp ON fp.Id = d.FormaPagoId
LEFT JOIN dbo.pTipoConcepto tc ON tc.CodTipoConcepto = r.CodTipoConcepto
LEFT JOIN dbo.mAdmisiones ad ON ad.IdAdmision = r.IdAdmision
WHERE r.FechaRecibo >= @Desde AND r.FechaRecibo < @HastaEx;

-- inventario
SELECT CAST(td.Id AS varchar(30)) AS sourceKey,
       CAST(tm.Fecha AS date) AS fecha,
       CAST(tm.TipoComprobanteId AS varchar(30)) AS tipoComprobanteCodigo,
       COALESCE(tm.Consecutivo, tm.NumeroComprobante) AS numeroComprobante,
       b.Codigo AS almacenCodigo, a.Codigo AS articuloCodigo, CAST(td.LoteId AS varchar(30)) AS loteCodigo,
       cc.Codigo AS centroCostoCodigo, CAST(tm.TerceroId AS varchar(30)) AS terceroCodigo,
       CAST(ISNULL(td.CantidadEntrada, 0) AS decimal(18,4)) AS cantidadEntrada,
       CAST(ISNULL(td.CantidadSalida, 0)  AS decimal(18,4)) AS cantidadSalida,
       CAST(ROUND(td.Costo, 4) AS decimal(19,4)) AS costoUnitario,
       CAST(ISNULL(td.PorcentajeDescuento, 0) AS decimal(9,4)) AS porcentajeDescuento,
       CAST(ISNULL(td.PorcentajeIVA, 0) AS decimal(9,4)) AS porcentajeIva,
       CAST(ROUND(ISNULL(td.CantidadEntrada, 0) * td.Costo * (1 - ISNULL(td.PorcentajeDescuento, 0) / 100), 2) AS decimal(19,2)) AS valorEntrada,
       CAST(ROUND(ISNULL(td.CantidadSalida, 0)  * td.Costo * (1 - ISNULL(td.PorcentajeDescuento, 0) / 100), 2) AS decimal(19,2)) AS valorSalida,
       CAST(ROUND(td.CostoPromedio, 4) AS decimal(19,4)) AS costoPromedio,
       CAST(CASE WHEN tm.EstadoEntidad = 1 AND td.EstadoEntidad = 1 THEN 1 ELSE 2 END AS tinyint) AS estado
FROM Almacen.MovimientosAlmacen tm
JOIN Almacen.MovimientosAlmacenDetalles td ON td.MovimientoAlmacenId = tm.Id
JOIN Almacen.ArticulosBase a ON a.Id = td.ArticuloId
JOIN Almacen.Bodegas b ON b.Id = td.BodegaId
LEFT JOIN SIIFNet.CentrosCostos cc ON cc.Id = tm.CentroCostoId
WHERE tm.EstadoEntidad IN (1, 2) AND td.EstadoEntidad IN (1, 2)
  AND tm.Fecha >= @Desde AND tm.Fecha < @HastaEx;

-- conciliaciones (periodos de la ventana)
SELECT 'B:' + CAST(c.EntidadBancariaId AS varchar(10)) AS cuentaBancariaCodigo,
       c.Vigencia * 100 + c.Mes AS periodo,
       CAST(ROUND(c.SaldoInicial, 2)  AS decimal(19,2)) AS saldoInicial,
       CAST(ROUND(c.SaldoLibro, 2)    AS decimal(19,2)) AS saldoLibro,
       CAST(ROUND(c.SaldoExtracto, 2) AS decimal(19,2)) AS saldoExtracto,
       CAST(ROUND(c.Diferencia, 2)    AS decimal(19,2)) AS diferencia,
       CAST(CASE WHEN c.EstadoEntidad = 1 THEN 1 ELSE 0 END AS bit) AS cerrada
FROM Tesoreria.ConciliacionesBancarias c
WHERE c.Vigencia * 100 + c.Mes BETWEEN YEAR(@Desde) * 100 + MONTH(@Desde) AND YEAR(@Hasta) * 100 + MONTH(@Hasta);

-- admisiones-pendientes (foto al @Corte; regla SIPS_37: admisión sin factura vigente)
SELECT CAST(a.IdAdmision AS varchar(30)) AS admisionSourceKey,
       CAST(a.FechaIngreso AS date) AS fechaIngreso,
       CAST(sa.FechaSalida AS date) AS fechaSalida,
       CAST(a.IdEmpresa AS varchar(30)) AS pagadorCodigo,
       CAST(a.IdSede AS varchar(30)) AS sedeCodigo,
       ta.NomTipoAtencion AS tipoAtencion,
       a.IdMedico AS profesionalCodigo,
       'C:' + CAST(a.IdcUsuario AS varchar(10)) AS usuarioCodigo,
       CAST(ROUND(v.ValorServicios, 2) AS decimal(19,2)) AS valorServicios,
       DATEDIFF(DAY, COALESCE(sa.FechaSalida, a.FechaIngreso), @Corte) AS diasSinFacturar
FROM dbo.mAdmisiones a
JOIN dbo.pTipoAtencion ta ON ta.CodTipoAtencion = a.CodTipoAtencion
LEFT JOIN dbo.mSalidas sa ON sa.IdAdmision = a.IdAdmision
CROSS APPLY (SELECT SUM(s.VrTotal) AS ValorServicios FROM dbo.mServicios s
             WHERE s.IdAdmision = a.IdAdmision AND ISNULL(s.Anulado, 0) = 0) v
WHERE ISNULL(a.Anulado, 0) = 0
  AND a.FechaIngreso >= DATEADD(MONTH, -12, @Corte) AND a.FechaIngreso < DATEADD(DAY, 1, CAST(@Corte AS datetime))
  AND v.ValorServicios > 0
  AND NOT EXISTS (SELECT 1 FROM dbo.mFacturas f WHERE f.IdAdmision = a.IdAdmision AND ISNULL(f.Anulado, 0) = 0);

-- glosas (solo si la IPS usa el módulo: seg.EmpresaModulo GLOSAS)
SELECT CAST(g.IdGlosa AS varchar(30)) AS sourceKey, g.NoFactura AS numeroFactura,
       CAST(g.Fecha AS date) AS fechaGlosa, CAST(g.FechaConciliacion AS date) AS fechaRespuesta,
       CAST(f.IdEmpresa AS varchar(30)) AS pagadorCodigo, CAST(g.IdSede AS varchar(30)) AS sedeCodigo,
       CAST(g.CodGlosa AS varchar(10)) AS motivoCodigo,
       CAST(ROUND(ISNULL(it.Glosado, g.Valor), 2) AS decimal(19,2)) AS valorGlosado,
       CAST(ROUND(ISNULL(it.Aceptado, 0), 2)  AS decimal(19,2)) AS valorAceptado,
       CAST(ROUND(ISNULL(it.Rechazado, 0), 2) AS decimal(19,2)) AS valorRechazado,
       CAST(g.Estado AS varchar(10)) AS estadoOrigen,
       CAST(CASE WHEN ISNULL(g.Anulado, 0) <> 0 THEN 1 ELSE 0 END AS bit) AS anulada
FROM dbo.dGlosas g
LEFT JOIN dbo.mFacturas f ON f.NoFactura = g.NoFactura AND f.IdSede = g.IdSede
OUTER APPLY (SELECT SUM(i.VrGlosado) AS Glosado, SUM(i.Aceptada) AS Aceptado, SUM(i.Rechazada) AS Rechazado
             FROM dbo.dGlosasItems i WHERE i.IdGlosa = g.IdGlosa) it
WHERE g.Fecha >= @Desde AND g.Fecha < @HastaEx;
