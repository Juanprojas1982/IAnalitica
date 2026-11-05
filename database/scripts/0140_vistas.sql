/* 0140 · Vistas rpt.* e ia.vw_ConsumoMes (antes que los procedimientos que las usan). */
/* ============================ VISTAS PARA DASHBOARDS ============================ */
-- Heredan RLS. Saldo = por naturaleza de la cuenta (regla SaludIPS); Utilidad = Crédito - Débito (ingresos +, costos/gastos -).
CREATE OR ALTER VIEW rpt.vw_EstadoResultados AS
SELECT a.EmpresaId, a.PeriodoKey, a.SedeId, a.CentroCostoId, c.CuentaId, c.Codigo, c.ClasePuc,
       ISNULL(h.GrupoEstadoResultados, 'SIN_HOMOLOGAR') AS GrupoEstadoResultados,
       SUM(CASE WHEN (c.Naturaleza = 'D' AND c.MovimientoContrario = 0) OR (c.Naturaleza = 'C' AND c.MovimientoContrario = 1)
                THEN a.Debito - a.Credito ELSE a.Credito - a.Debito END) AS Saldo,
       SUM(a.Credito - a.Debito) AS Utilidad
FROM fact.AsientoLinea a
JOIN dim.CuentaContable c      ON c.CuentaId = a.CuentaId
JOIN dim.TipoComprobante tc    ON tc.TipoComprobanteId = a.TipoComprobanteId
LEFT JOIN dim.CuentaHomologada h ON h.CuentaEstandarId = c.CuentaEstandarId
WHERE a.Estado = 1
  AND c.ClasePuc IN ('4', '5', '6', '7')
  AND tc.Clase <> 'CIERRE'
GROUP BY a.EmpresaId, a.PeriodoKey, a.SedeId, a.CentroCostoId, c.CuentaId, c.Codigo, c.ClasePuc, h.GrupoEstadoResultados;
GO
CREATE OR ALTER VIEW rpt.vw_EjecucionPresupuestal AS
SELECT COALESCE(r.EmpresaId, p.EmpresaId) AS EmpresaId, COALESCE(r.PeriodoKey, p.PeriodoKey) AS PeriodoKey,
       COALESCE(r.CuentaId, p.CuentaId) AS CuentaId, COALESCE(r.CentroCostoId, p.CentroCostoId) AS CentroCostoId,
       COALESCE(r.SedeId, p.SedeId) AS SedeId,
       ISNULL(r.Saldo, 0) AS Real, ISNULL(p.Valor, 0) AS Presupuesto,
       ISNULL(r.Saldo, 0) - ISNULL(p.Valor, 0) AS Desviacion,
       CASE WHEN ISNULL(p.Valor, 0) = 0 THEN NULL ELSE ISNULL(r.Saldo, 0) / p.Valor END AS PctEjecucion
FROM (SELECT EmpresaId, PeriodoKey, CuentaId, CentroCostoId, SedeId, SUM(Saldo) AS Saldo
      FROM rpt.vw_EstadoResultados GROUP BY EmpresaId, PeriodoKey, CuentaId, CentroCostoId, SedeId) r
FULL JOIN (SELECT EmpresaId, PeriodoKey, CuentaId, CentroCostoId, SedeId, SUM(ValorPresupuestado) AS Valor
           FROM fact.Presupuesto WHERE Version = 'APROBADO'
           GROUP BY EmpresaId, PeriodoKey, CuentaId, CentroCostoId, SedeId) p
  ON  r.EmpresaId = p.EmpresaId AND r.PeriodoKey = p.PeriodoKey AND r.CuentaId = p.CuentaId
  AND ISNULL(r.CentroCostoId, 0) = ISNULL(p.CentroCostoId, 0) AND ISNULL(r.SedeId, 0) = ISNULL(p.SedeId, 0);
GO
CREATE OR ALTER VIEW rpt.vw_AntiguedadCartera AS
SELECT c.EmpresaId, c.CorteFechaKey, c.TipoCartera, c.PagadorId, c.TerceroId, c.SedeId, c.RangoEdad,
       COUNT(*) AS Documentos, SUM(c.ValorOriginal) AS ValorOriginal, SUM(c.Saldo) AS Saldo
FROM fact.CarteraDocumento c
GROUP BY c.EmpresaId, c.CorteFechaKey, c.TipoCartera, c.PagadorId, c.TerceroId, c.SedeId, c.RangoEdad;
GO
CREATE OR ALTER VIEW rpt.vw_Facturacion AS
SELECT f.EmpresaId, f.FechaEmisionKey / 100 AS PeriodoKey, f.SedeId, f.PagadorId, f.ContratoId, f.LineaServicioId,
       f.CentroCostoId, f.ProfesionalId, f.UsuarioOpId,
       COUNT(DISTINCT f.NumeroDocumento) AS Facturas,
       SUM(f.Cantidad) AS Cantidad,
       SUM(f.ValorNeto) AS ValorFacturado,
       SUM(f.Descuento) AS Descuentos,
       SUM(f.CuotaModeradora) AS CuotasModeradoras,
       SUM(f.CostoDirecto) AS CostoDirecto
FROM fact.FacturaLinea f
WHERE f.Anulada = 0
GROUP BY f.EmpresaId, f.FechaEmisionKey / 100, f.SedeId, f.PagadorId, f.ContratoId, f.LineaServicioId,
         f.CentroCostoId, f.ProfesionalId, f.UsuarioOpId;
GO
CREATE OR ALTER VIEW rpt.vw_FacturasAnuladas AS
SELECT f.EmpresaId, f.FechaEmisionKey / 100 AS PeriodoKey, f.SedeId, f.UsuarioOpId,
       COUNT(DISTINCT f.NumeroDocumento) AS FacturasAnuladas, SUM(f.ValorNeto) AS ValorAnulado
FROM fact.FacturaLinea f
WHERE f.Anulada = 1
GROUP BY f.EmpresaId, f.FechaEmisionKey / 100, f.SedeId, f.UsuarioOpId;
GO
CREATE OR ALTER VIEW rpt.vw_FlujoCaja AS
SELECT t.EmpresaId, t.FechaKey, t.FechaKey / 100 AS PeriodoKey, t.CuentaBancariaId, t.SedeId, t.Concepto, t.FormaPago,
       SUM(CASE WHEN t.TipoMovimiento = 'INGRESO' THEN t.Valor ELSE 0 END) AS Ingresos,
       SUM(CASE WHEN t.TipoMovimiento = 'EGRESO'  THEN t.Valor ELSE 0 END) AS Egresos
FROM fact.TesoreriaMovimiento t
WHERE t.TipoMovimiento <> 'TRASLADO' AND t.Anulado = 0
GROUP BY t.EmpresaId, t.FechaKey, t.CuentaBancariaId, t.SedeId, t.Concepto, t.FormaPago;
GO
CREATE OR ALTER VIEW rpt.vw_InventarioMensual AS
SELECT m.EmpresaId, m.FechaKey / 100 AS PeriodoKey, m.AlmacenId, m.ArticuloId, m.LoteId,
       SUM(m.CantidadEntrada) AS CantidadEntrada, SUM(m.CantidadSalida) AS CantidadSalida,
       SUM(m.ValorEntrada) AS ValorEntrada, SUM(m.ValorSalida) AS ValorSalida
FROM fact.MovimientoInventario m
WHERE m.Estado = 1
GROUP BY m.EmpresaId, m.FechaKey / 100, m.AlmacenId, m.ArticuloId, m.LoteId;
GO
/* ============================ VISTAS DE DECISIÓN ============================ */
-- Rentabilidad por CC con distribución
CREATE OR ALTER VIEW rpt.vw_RentabilidadCentroCosto AS
WITH directo AS (
    SELECT EmpresaId, PeriodoKey, CentroCostoId,
           SUM(CASE WHEN ClasePuc = '4' THEN Saldo ELSE 0 END) AS Ingreso,
           SUM(CASE WHEN ClasePuc IN ('5','6','7') THEN Saldo ELSE 0 END) AS CostoDirecto
    FROM rpt.vw_EstadoResultados WHERE CentroCostoId IS NOT NULL
    GROUP BY EmpresaId, PeriodoKey, CentroCostoId),
recibido AS (
    SELECT EmpresaId, PeriodoKey, CentroCostoDestinoId AS CentroCostoId, SUM(Valor) AS Recibido
    FROM fact.CostoDistribuido GROUP BY EmpresaId, PeriodoKey, CentroCostoDestinoId),
entregado AS (
    SELECT EmpresaId, PeriodoKey, CentroCostoOrigenId AS CentroCostoId, SUM(Valor) AS Entregado
    FROM fact.CostoDistribuido GROUP BY EmpresaId, PeriodoKey, CentroCostoOrigenId)
SELECT d.EmpresaId, d.PeriodoKey, d.CentroCostoId, cc.TipoCC,
       d.Ingreso, d.CostoDirecto,
       ISNULL(r.Recibido, 0)  AS CostoRecibido,
       ISNULL(e.Entregado, 0) AS CostoEntregado,
       d.CostoDirecto + ISNULL(r.Recibido, 0) - ISNULL(e.Entregado, 0) AS CostoTotal,
       d.Ingreso - (d.CostoDirecto + ISNULL(r.Recibido, 0) - ISNULL(e.Entregado, 0)) AS Margen
FROM directo d
JOIN dim.CentroCosto cc ON cc.CentroCostoId = d.CentroCostoId
LEFT JOIN recibido r  ON r.EmpresaId = d.EmpresaId AND r.PeriodoKey = d.PeriodoKey AND r.CentroCostoId = d.CentroCostoId
LEFT JOIN entregado e ON e.EmpresaId = d.EmpresaId AND e.PeriodoKey = d.PeriodoKey AND e.CentroCostoId = d.CentroCostoId;
GO

-- Costo unitario aproximado por prestación (costo total del CC repartido por valor de tarifa: método por precio relativo).
-- Es una aproximación hasta tener la ficha de costo estándar por CUPS.
CREATE OR ALTER VIEW rpt.vw_MargenServicio AS
WITH cc AS (
    SELECT EmpresaId, PeriodoKey, CentroCostoId, CostoTotal FROM rpt.vw_RentabilidadCentroCosto),
pr AS (
    SELECT EmpresaId, FechaRealizacionKey / 100 AS PeriodoKey, CentroCostoId, ServicioId, PagadorId,
           SUM(Cantidad) AS Cantidad, SUM(ValorTotal) AS Ingreso, SUM(ISNULL(CostoInsumos, 0)) AS Insumos
    FROM fact.Prestacion WHERE Anulado = 0
    GROUP BY EmpresaId, FechaRealizacionKey / 100, CentroCostoId, ServicioId, PagadorId),
tot AS (
    SELECT EmpresaId, PeriodoKey, CentroCostoId, SUM(Ingreso) AS IngresoCC FROM pr GROUP BY EmpresaId, PeriodoKey, CentroCostoId)
SELECT pr.EmpresaId, pr.PeriodoKey, pr.CentroCostoId, pr.ServicioId, pr.PagadorId, pr.Cantidad, pr.Ingreso,
       CAST(cc.CostoTotal * pr.Ingreso / NULLIF(tot.IngresoCC, 0) AS decimal(19,4)) AS CostoAsignado,
       pr.Ingreso - CAST(cc.CostoTotal * pr.Ingreso / NULLIF(tot.IngresoCC, 0) AS decimal(19,4)) AS Margen
FROM pr
JOIN tot ON tot.EmpresaId = pr.EmpresaId AND tot.PeriodoKey = pr.PeriodoKey AND tot.CentroCostoId = pr.CentroCostoId
LEFT JOIN cc ON cc.EmpresaId = pr.EmpresaId AND cc.PeriodoKey = pr.PeriodoKey AND cc.CentroCostoId = pr.CentroCostoId;
GO

-- Agenda: ocupación, inasistencia y costo de capacidad ociosa
CREATE OR ALTER VIEW rpt.vw_AgendaOcupacion AS
WITH cap AS (
    SELECT EmpresaId, FechaKey, ProfesionalId, EspecialidadId, SedeId,
           SUM(CuposOfertados) AS Cupos, SUM(MinutosOfertados) AS Minutos
    FROM fact.AgendaCapacidad GROUP BY EmpresaId, FechaKey, ProfesionalId, EspecialidadId, SedeId),
cit AS (
    SELECT EmpresaId, FechaCitaKey AS FechaKey, ProfesionalId,
           SUM(CASE WHEN Estado <> 'CANCELADA' THEN 1 ELSE 0 END) AS Asignadas,
           SUM(CASE WHEN Estado = 'ASISTIO' THEN 1 ELSE 0 END)    AS Atendidas,
           SUM(CASE WHEN Estado = 'NO_ASISTIO' THEN 1 ELSE 0 END) AS Inasistencias,
           SUM(CASE WHEN Estado = 'CANCELADA' THEN 1 ELSE 0 END)  AS Canceladas
    FROM fact.Cita GROUP BY EmpresaId, FechaCitaKey, ProfesionalId),
hora AS (   -- costo por hora contratada del profesional en el mes (si está en nómina)
    SELECT n.EmpresaId, n.PeriodoKey, p.ProfesionalId,
           SUM(CASE WHEN n.EsCosto = 1 THEN n.Valor ELSE 0 END) / NULLIF(MAX(n.HorasContratoMes), 0) AS CostoHora
    FROM fact.NominaCosto n
    JOIN dim.Profesional p ON p.EmpresaId = n.EmpresaId AND p.TerceroCodigo = n.EmpleadoTerceroCodigo
    GROUP BY n.EmpresaId, n.PeriodoKey, p.ProfesionalId)
SELECT cap.EmpresaId, cap.FechaKey, cap.FechaKey / 100 AS PeriodoKey, cap.ProfesionalId, cap.EspecialidadId, cap.SedeId,
       cap.Cupos, ISNULL(cit.Asignadas, 0) AS Asignadas, ISNULL(cit.Atendidas, 0) AS Atendidas,
       ISNULL(cit.Inasistencias, 0) AS Inasistencias, ISNULL(cit.Canceladas, 0) AS Canceladas,
       CAST(ISNULL(cit.Asignadas, 0) * 1.0 / NULLIF(cap.Cupos, 0) AS decimal(7,4)) AS Ocupacion,
       -- cupos que no produjeron atención: no asignados + inasistidos
       cap.Cupos - ISNULL(cit.Atendidas, 0) AS CuposPerdidos,
       CAST((cap.Cupos - ISNULL(cit.Atendidas, 0)) * (cap.Minutos * 1.0 / NULLIF(cap.Cupos, 0)) / 60.0 * h.CostoHora AS decimal(19,2)) AS CostoCapacidadOciosa
FROM cap
LEFT JOIN cit  ON cit.EmpresaId = cap.EmpresaId AND cit.FechaKey = cap.FechaKey AND cit.ProfesionalId = cap.ProfesionalId
LEFT JOIN hora h ON h.EmpresaId = cap.EmpresaId AND h.PeriodoKey = cap.FechaKey / 100 AND h.ProfesionalId = cap.ProfesionalId;
GO

-- Ciclo del ingreso: dinero detenido por etapa y días entre etapas
CREATE OR ALTER VIEW rpt.vw_CicloIngreso AS
SELECT p.EmpresaId, p.SedeId, p.PagadorId, p.LineaServicioId, p.UsuarioOpId,
       CASE WHEN p.NumeroFactura IS NULL THEN 'SIN_FACTURAR'
            WHEN p.FechaRadicacion IS NULL THEN 'SIN_RADICAR'
            ELSE 'RADICADO' END AS Etapa,
       COUNT(*) AS Prestaciones,
       SUM(p.ValorTotal) AS Valor,
       AVG(CAST(DATEDIFF(DAY, COALESCE(p.FechaSalida, p.FechaRealizacion), p.FechaFactura) AS decimal(9,2))) AS DiasAtencionAFactura,
       AVG(CAST(DATEDIFF(DAY, p.FechaFactura, p.FechaRadicacion) AS decimal(9,2))) AS DiasFacturaARadicacion,
       AVG(CAST(DATEDIFF(DAY, COALESCE(p.FechaSalida, p.FechaRealizacion), SYSDATETIME()) AS decimal(9,2))) AS AntiguedadPromedio
FROM fact.Prestacion p
WHERE p.Anulado = 0
GROUP BY p.EmpresaId, p.SedeId, p.PagadorId, p.LineaServicioId, p.UsuarioOpId,
         CASE WHEN p.NumeroFactura IS NULL THEN 'SIN_FACTURAR' WHEN p.FechaRadicacion IS NULL THEN 'SIN_RADICAR' ELSE 'RADICADO' END;
GO
/* ============================ VISTA PARA EL PANEL DE CONSUMO ============================ */
CREATE OR ALTER VIEW ia.vw_ConsumoMes AS
SELECT cp.EmpresaId, cp.PeriodoKey, cp.CupoIncluidoCop,
       cp.ValorConsumoCop,
       CASE WHEN cp.ValorConsumoCop > cp.CupoIncluidoCop THEN cp.ValorConsumoCop - cp.CupoIncluidoCop ELSE 0 END AS ExcedenteCop,
       CASE WHEN cp.CupoIncluidoCop = 0 THEN NULL ELSE CAST(100.0 * cp.ValorConsumoCop / cp.CupoIncluidoCop AS decimal(7,2)) END AS PctCupo,
       ec.ExcedenteAutorizado, ec.TopeExcedenteCop,
       -- proyección lineal al cierre del mes
       CAST(cp.ValorConsumoCop * DAY(EOMONTH(GETDATE())) / NULLIF(DAY(GETDATE()), 0) AS decimal(16,2)) AS ProyeccionMesCop,
       cp.TokensEntrada, cp.TokensSalida, cp.TokensCache
FROM ia.ConsumoPeriodo cp
JOIN ia.EmpresaConfig ec ON ec.EmpresaId = cp.EmpresaId;
GO
