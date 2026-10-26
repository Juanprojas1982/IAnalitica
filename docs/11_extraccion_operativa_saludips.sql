/* =====================================================================
   Extracción SaludIPS -> API: hechos operativos (complemento de 06_extraccion_saludips.sql)
   Destino: 10_hechos_operativos_y_costeo.sql. Lo ejecuta el conector (12_spec_conector_saludips.md)
   con login de solo lectura; recomendado publicarlos como vistas analitica.* versionadas.
   NO EJECUTADO. Campos marcados [S] se validan con el usuario funcional.
   Modo de carga: INCREMENTAL (upsert por sourceKey) salvo agenda (VENTANA) y nómina (VENTANA por periodo).
   ===================================================================== */
DECLARE @Desde date = '2026-09-01', @Hasta date = '2026-10-26',
        @HastaAgenda date = '2026-12-31',                 -- la agenda futura también se envía
        @Sal nvarchar(64) = N'<secreto-por-empresa>';
DECLARE @HastaEx datetime = DATEADD(DAY, 1, CAST(@Hasta AS datetime));

/* ---------------- catálogo: especialidades ---------------- */
SELECT CAST(r.CodReferencia AS varchar(20)) AS codigoOrigen, r.NomReferencia AS nombre
FROM dbo.rReferencias r WHERE r.CodClaseReferencia = 8;

/* ---------------- catálogo: profesionales (amplía el de 06) ---------------- */
SELECT p.IdPersonal AS codigoOrigen, p.NomPersonal AS nombre,
       CAST(p.CodEspecialidad AS varchar(20)) AS especialidadCodigo,
       CAST(p.TerceroId AS varchar(30)) AS terceroCodigo,          -- enlace con nómina
       CAST(CASE WHEN p.Activo = 1 THEN 1 ELSE 0 END AS bit) AS activo
FROM dbo.mPersonal p;

/* ---------------- agenda-capacidad (VENTANA [@Desde, @HastaAgenda]) ----------------
   dCitasHorarios es una plantilla: médico x día de semana x jornada, con hora de inicio,
   NoPacientes cupos de MinutosPaciente, vigente entre FechaHorario y FechaFinal
   (ver SaludIPSNet.BLL/SICitas/dCitasHorariosRepository.cs). MinutosPaciente = 100 y NoPacientes = 0
   es una marca especial del sistema y no es capacidad.
   [S] DiaHorario: se asume 1 = domingo ... 7 = sábado (el código aplica un "ajuste"); validar con:
     SELECT h.DiaHorario, DATENAME(WEEKDAY, c.FechaCita) AS dia, COUNT(*)
     FROM dbo.dCitasHorarios h JOIN dbo.dCitas c ON c.IdMedico = h.IdMedico AND c.Jornada = h.JornadaHorario
      AND c.FechaCita BETWEEN h.FechaHorario AND ISNULL(h.FechaFinal, '9999-12-31')
     GROUP BY h.DiaHorario, DATENAME(WEEKDAY, c.FechaCita) ORDER BY 1, 3 DESC;               */
WITH dias AS (
    SELECT TOP (DATEDIFF(DAY, @Desde, @HastaAgenda) + 1)
           DATEADD(DAY, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1, @Desde) AS d
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
SELECT CONVERT(char(8), dias.d, 112) AS fecha,
       h.IdMedico AS profesionalCodigo,
       CAST(MIN(h.CodEspecialidad) AS varchar(20)) AS especialidadCodigo,
       CAST(MIN(h.idsede) AS varchar(30)) AS sedeCodigo,
       h.JornadaHorario AS jornada,
       MIN(TRY_CONVERT(time(0), h.HoraHorario)) AS horaInicio,
       SUM(CAST(h.NoPacientes AS int)) AS cuposOfertados,
       SUM(CAST(h.NoPacientes AS int) * h.MinutosPaciente) / NULLIF(SUM(CAST(h.NoPacientes AS int)), 0) AS minutosPorCupo
FROM dbo.dCitasHorarios h
JOIN dias ON dias.d BETWEEN CAST(h.FechaHorario AS date) AND CAST(ISNULL(h.FechaFinal, '9999-12-31') AS date)
         AND (DATEDIFF(DAY, '19000107', dias.d) % 7) + 1 = h.DiaHorario          -- 1900-01-07 fue domingo
WHERE h.NoPacientes > 0 AND h.MinutosPaciente > 0
  AND NOT EXISTS (SELECT 1 FROM dbo.mCitasFestivo f WHERE CAST(f.FechaFestivo AS date) = dias.d)
GROUP BY dias.d, h.IdMedico, h.JornadaHorario;

/* ---------------- citas (INCREMENTAL) ---------------- */
SELECT 'C:' + CAST(c.EMConsecutivo AS varchar(12)) AS sourceKey,
       CASE WHEN c.Asistio = 1 THEN 'ASISTIO'
            WHEN c.FechaCita < CAST(GETDATE() AS date) THEN 'NO_ASISTIO'
            ELSE 'PENDIENTE' END AS estado,
       CAST(c.FechaCita AS date) AS fechaCita,
       TRY_CONVERT(time(0), c.HoraCita) AS horaCita,
       CAST(c.FechaUsuario AS date) AS fechaSolicitud,                 -- "Fecha Solicitud Usuario"
       c.FechaDigitacion AS fechaAsignacion,
       CAST(NULL AS datetime) AS fechaCancelacion, CAST(NULL AS varchar(60)) AS motivoCancelacion, CAST(NULL AS bit) AS reprogramada,
       TRY_CONVERT(time(0), c.HoraAtencion) AS horaLlegada,           -- [S] se fija al marcar asistencia
       a.FechaAtencion AS inicioAtencion,                               -- [S]
       a.FechaSalidaHC AS finAtencion,                                  -- [S]
       c.EstaConfirmado AS confirmada,
       c.IdMedico AS profesionalCodigo,
       CAST(c.CodEspecialidad AS varchar(20)) AS especialidadCodigo,
       CAST(c.idsede AS varchar(30)) AS sedeCodigo,
       CAST(c.IdEmpresa AS varchar(30)) AS pagadorCodigo,
       CAST(c.CodTipoConsulta AS varchar(30)) AS tipoConsulta,
       CAST(c.CodTipoSolicitud AS varchar(30)) AS tipoSolicitud,
       CONVERT(char(64), HASHBYTES('SHA2_256', @Sal + c.NoHistoria), 2) AS pacienteHash,
       'C:' + CAST(c.IdcUsuario AS varchar(10)) AS usuarioCodigo,
       CAST(c.IdAdmision AS varchar(30)) AS admisionSourceKey
FROM dbo.dCitas c
LEFT JOIN dbo.mAdmisiones a ON a.IdAdmision = c.IdAdmision
WHERE ISNULL(c.EstaActivo, 1) = 1                                       -- [S] significado de EstaActivo
  AND (c.FechaCita BETWEEN @Desde AND @HastaAgenda OR c.FechaDigitacion >= @Desde)
UNION ALL
SELECT 'X:' + CAST(x.EMConsecutivo AS varchar(12)), 'CANCELADA',
       CAST(x.FechaCita AS date), TRY_CONVERT(time(0), x.HoraCita),
       NULL, NULL,
       x.FechaCancela, CAST(x.CodCitasMotivoCancela AS varchar(60)), CAST(x.MoverCita AS bit),
       NULL, NULL, NULL, NULL,
       x.IdMedico, CAST(x.CodEspecialidad AS varchar(20)), CAST(x.IdSede AS varchar(30)), NULL, NULL, NULL,
       CONVERT(char(64), HASHBYTES('SHA2_256', @Sal + x.NoHistoria), 2),
       'C:' + CAST(x.IdcUsuario AS varchar(10)), NULL
FROM dbo.dCitasCanceladas x
WHERE x.FechaCancela >= @Desde OR x.FechaCita BETWEEN @Desde AND @HastaAgenda;

/* ---------------- prestaciones (INCREMENTAL: realizadas en la ventana o con eventos en la ventana) ---------------- */
SELECT CAST(s.IdServicio AS varchar(30)) AS sourceKey,
       CAST(s.IdAdmision AS varchar(30)) AS admisionSourceKey,
       ta.NomTipoAtencion AS tipoAtencion,
       a.FechaIngreso AS fechaIngreso,
       s.FechaOrdenado AS fechaOrden,
       s.FechaRealizado AS fechaRealizacion,
       a.FechaSalidaHC AS fechaSalida,
       fac.NoFactura AS numeroFactura,
       fac.FechaFactura AS fechaFactura,
       re.FechaEnvio AS fechaRadicacion,                                   -- [S] envío de la cuenta de cobro
       fac.Enlace AS enlaceFactura,                                         -- DETALLE | ADMISION
       CAST(CASE WHEN ISNULL(s.Anulado, 0) <> 0 THEN 1 ELSE 0 END AS bit) AS anulado,
       s.FechaAnulacion AS fechaAnulacion,
       'C:' + CAST(s.IdcUsuarioA AS varchar(10)) AS usuarioAnulaCodigo,
       CASE WHEN pp.CodProcedimiento IS NOT NULL THEN 'P:' ELSE 'S:' END + s.CodServicio AS servicioCodigo,
       CAST(s.CodTipoServicio AS varchar(30)) AS lineaServicioCodigo,
       cc.Codigo AS centroCostoCodigo,                                       -- [S] centroCostoID -> SIIFNet
       CAST(a.IdSede AS varchar(30)) AS sedeCodigo,
       CAST(a.IdEmpresa AS varchar(30)) AS pagadorCodigo,
       s.IdMedico AS profesionalCodigo,
       s.IdMedicoOrdena AS profesionalOrdenaCodigo,
       CAST(s.CodEspecialidad AS varchar(20)) AS especialidadCodigo,
       'C:' + CAST(s.IdcUsuario AS varchar(10)) AS usuarioCodigo,
       CAST(CASE WHEN NULLIF(LTRIM(s.NoAutorizacion), '') IS NULL THEN 0 ELSE 1 END AS bit) AS tieneAutorizacion,
       CAST(s.Cantidad AS decimal(18,4)) AS cantidad,
       CAST(ROUND(s.VrUnitario, 2) AS decimal(19,2)) AS valorUnitario,
       CAST(ROUND(s.VrTotal, 2) AS decimal(19,2)) AS valorTotal,
       CAST(ROUND(ISNULL(s.VrDescuento, 0), 2) AS decimal(19,2)) AS descuento,
       CAST(ROUND(tp.Valor, 2) AS decimal(19,2)) AS tarifaPactada,         -- [S] llave manual/plan
       CAST(ROUND(s.VrCosto, 2) AS decimal(19,2)) AS costoInsumos           -- [S] unitario o total
FROM dbo.mServicios s
JOIN dbo.mAdmisiones a ON a.IdAdmision = s.IdAdmision
LEFT JOIN dbo.pTipoAtencion ta ON ta.CodTipoAtencion = a.CodTipoAtencion
LEFT JOIN dbo.mEmpresas e ON e.IdEmpresa = a.IdEmpresa
LEFT JOIN dbo.pProcedimientos pp ON pp.CodProcedimiento = s.CodServicio
LEFT JOIN SIIFNet.CentrosCostos cc ON cc.Id = s.centroCostoID
OUTER APPLY (   -- factura: primero por detalle; si no hay, por admisión (regla de los reportes)
    SELECT TOP (1) x.NoFactura, x.FechaFactura, x.IdRipsEnvio, x.Enlace
    FROM (SELECT f.NoFactura, f.FechaFactura, f.IdRipsEnvio, 'DETALLE' AS Enlace, 1 AS Prioridad
          FROM dbo.dFacturasServicios d JOIN dbo.mFacturas f ON f.NoFactura = d.NoFactura AND f.IdSede = d.IdSede
          WHERE d.IdServicio = s.IdServicio AND ISNULL(f.Anulado, 0) = 0
          UNION ALL
          SELECT f.NoFactura, f.FechaFactura, f.IdRipsEnvio, 'ADMISION', 2
          FROM dbo.mFacturas f WHERE f.IdAdmision = s.IdAdmision AND ISNULL(f.Anulado, 0) = 0) x
    ORDER BY x.Prioridad, x.FechaFactura) fac
LEFT JOIN dbo.mRipsEnvios re ON re.IdRipsEnvio = fac.IdRipsEnvio
OUTER APPLY (SELECT TOP (1) t.Valor FROM dbo.pTarifasProcedimientos t
             WHERE t.CodProcedimiento = s.CodServicio AND t.CodManual = e.CodManual
               AND (t.CodPlan = s.CodPlan OR t.CodPlan IS NULL)
             ORDER BY CASE WHEN t.CodPlan = s.CodPlan THEN 0 ELSE 1 END) tp
WHERE (s.FechaRealizado >= @Desde AND s.FechaRealizado < @HastaEx)
   OR s.FechaAnulacion >= @Desde
   OR fac.FechaFactura >= @Desde
   OR re.FechaEnvio >= @Desde;

/* ---------------- estudios diagnósticos (INCREMENTAL) ---------------- */
SELECT 'R:' + CAST(r.IdServicio AS varchar(30)) AS sourceKey, 'IMAGEN' AS tipo,
       CAST(r.IdServicio AS varchar(30)) AS prestacionSourceKey,
       s.FechaRealizado AS fechaRealizacion, CAST(NULL AS datetime) AS fechaToma,
       r.FechaDigitacion AS fechaInforme, r.FechaEntregado AS fechaEntrega,
       CAST(r.Entregado AS bit) AS entregado,
       TRY_CONVERT(smallint, rr.Nombre) AS imagenesRechazadas,             -- [S] Id -> SIIFNet.Referencias
       TRY_CONVERT(smallint, rp.Nombre) AS imagenesRepetidas,             -- [S]
       LEFT(r.MotivoRechazoEstudio, 200) AS motivoRechazo,
       LEFT(r.MotivoRepeticionEstudio, 200) AS motivoRepeticion,
       rd.Nombre AS dosisReferencia,                                       -- [S]
       s.IdMedico AS profesionalInformaCodigo,                             -- [S]
       r.IdMedicoSolicita AS profesionalSolicitaCodigo,
       CAST(a.IdSede AS varchar(30)) AS sedeCodigo
FROM dbo.dLabRadiologia r
JOIN dbo.mServicios s ON s.IdServicio = r.IdServicio
JOIN dbo.mAdmisiones a ON a.IdAdmision = r.IdAdmision
LEFT JOIN SIIFNet.Referencias rr ON rr.Id = r.NoImagenesRechazadasId
LEFT JOIN SIIFNet.Referencias rp ON rp.Id = r.NoImagenesRepetidasId
LEFT JOIN SIIFNet.Referencias rd ON rd.Id = r.DosisRadiacionId
WHERE r.FechaDigitacion >= @Desde OR r.FechaEntregado >= @Desde OR s.FechaRealizado >= @Desde
UNION ALL
SELECT 'L:' + CAST(l.IdServicio AS varchar(30)) + '|' + l.CodLabExamen, 'LABORATORIO',
       CAST(l.IdServicio AS varchar(30)),
       s.FechaRealizado,
       DATEADD(MINUTE, ISNULL(DATEDIFF(MINUTE, 0, TRY_CONVERT(time(0), l.HoraTomaMuestra)), 0), CAST(CAST(l.FechaTomaMuestra AS date) AS datetime)),
       l.FechaDigitado,
       DATEADD(MINUTE, ISNULL(DATEDIFF(MINUTE, 0, TRY_CONVERT(time(0), l.HoraEntregaMuestra)), 0), CAST(CAST(l.FechaEntregaMuestra AS date) AS datetime)),
       CAST(CASE WHEN l.FechaEntregaMuestra IS NULL THEN 0 ELSE 1 END AS bit),
       NULL, NULL, NULL, NULL, NULL,
       l.IdMedico, s.IdMedicoOrdena, CAST(a.IdSede AS varchar(30))
FROM dbo.dLabResultados l
JOIN dbo.mServicios s ON s.IdServicio = l.IdServicio
JOIN dbo.mAdmisiones a ON a.IdAdmision = s.IdAdmision
WHERE l.FechaDigitado >= @Desde OR l.FechaTomaMuestra >= @Desde;

/* ---------------- nómina (VENTANA por periodo de liquidación) ----------------
   Dato sensible (salarios): en la plataforma solo agregados y roles de gerencia. Sin nombres.        */
SELECT CAST(h.Id AS varchar(30)) AS sourceKey,
       YEAR(d.FinalPeriodo) * 100 + MONTH(d.FinalPeriodo) AS periodo,
       c.Codigo AS contratoCodigo,
       CAST(e.TerceroId AS varchar(30)) AS empleadoTerceroCodigo,
       g.Nombre AS cargo,
       k.Codigo AS conceptoCodigo, h.NombreConcepto AS concepto,
       cu.Codigo AS cuentaCodigo,
       CAST(CASE WHEN LEFT(cu.Codigo, 1) IN ('5','6','7') THEN 1 ELSE 0 END AS bit) AS esCosto,
       cc.Codigo AS centroCostoCodigo,
       CAST(ROUND(ISNULL(h.Debito, 0) - ISNULL(h.Credito, 0), 2) AS decimal(19,2)) AS valor,   -- [S] signo
       CAST(h.HorasConcepto AS decimal(9,2)) AS horas,
       CAST(h.DiasDuracion AS decimal(9,2)) AS dias,
       CAST(h.HorasContratoMensuales AS decimal(9,2)) AS horasContratoMes
FROM Nomina.Historicos h
JOIN Nomina.DocumentosNominas d ON d.Id = h.DocumentoId
JOIN Nomina.Contratos c ON c.Id = h.ContratoId
JOIN Nomina.Empleados e ON e.Id = c.EmpleadoId
LEFT JOIN Nomina.Conceptos k ON k.Id = h.ConceptoId
LEFT JOIN SIIFNet.Cuentas cu ON cu.Id = COALESCE(h.CuentaContableId, k.CuentaContableId)
LEFT JOIN SIIFNet.CentrosCostos cc ON cc.Id = h.CentroCostoId
LEFT JOIN Nomina.Cargos g ON g.Id = h.CargoId
WHERE h.EstadoEntidad = 1 AND d.EstadoEntidad = 1
  AND d.FinalPeriodo >= @Desde AND d.FinalPeriodo < @HastaEx;

/* ---------------- envíos FE y RIPS (INCREMENTAL) ---------------- */
SELECT 'FE:' + CAST(x.Id AS varchar(12)) AS sourceKey, 'FE' AS canal, x.Tipo AS tipoDocumento, x.NoFactura AS numeroFactura,
       CAST(x.IdSede AS varchar(30)) AS sedeCodigo, x.FechaEnviado AS fechaEnvio,
       CASE ISNULL(x.Estado, 3) WHEN 0 THEN 'ENVIADA' WHEN 1 THEN 'CARGADA' WHEN 2 THEN 'ERROR' ELSE 'SIN_ENVIAR' END AS estado,
       x.CodigoUnico AS codigoUnico, CAST(ROUND(x.Valor, 2) AS decimal(19,2)) AS valor
FROM dbo.EnviosFacturaElectronica x
WHERE x.EstadoEntidad = 1 AND (x.FechaDigitacion >= @Desde OR x.FechaEnviado >= @Desde)
UNION ALL
SELECT 'RIPS:' + CAST(x.Id AS varchar(12)), 'RIPS', x.Tipo, x.NoFactura,
       CAST(x.IdSede AS varchar(30)), x.FechaEnviado,
       'R' + CAST(ISNULL(x.Estado, 255) AS varchar(3)),                     -- [S] estados RIPS
       x.CodigoUnico, CAST(ROUND(x.Valor, 2) AS decimal(19,2))
FROM dbo.EnviosRips x
WHERE x.EstadoEntidad = 1 AND (x.FechaDigitacion >= @Desde OR x.FechaEnviado >= @Desde);

/* ---------------- autorizaciones (INCREMENTAL) ---------------- */
SELECT d.Vigencia + '|' + CAST(d.ConAutorizacion AS varchar(12)) + '|' + CAST(d.EMConsecutivo AS varchar(12)) AS sourceKey,
       m.Fecha AS fechaSolicitud, d.FechaOrdenado AS fechaOrden, d.FechaDigitacion AS fechaRegistro,
       CAST(d.Estado AS varchar(20)) AS estado, d.NoAutorizacion AS numeroAutorizacion,
       'P:' + d.CodProcedimiento AS servicioCodigo, d.Cantidad AS cantidad,
       CAST(m.IdEmpresa AS varchar(30)) AS pagadorCodigo, CAST(m.IdSede AS varchar(30)) AS sedeCodigo,
       CAST(CASE WHEN m.Prioritario = 1 THEN 1 ELSE 0 END AS bit) AS prioritaria
FROM dbo.dAutorizaciones d
JOIN dbo.mAutorizaciones m ON m.ConAutorizacion = d.ConAutorizacion AND m.Vigencia = d.Vigencia AND m.IdSede = d.IdSede   -- [S] llave
WHERE d.FechaDigitacion >= @Desde OR m.Fecha >= @Desde;
