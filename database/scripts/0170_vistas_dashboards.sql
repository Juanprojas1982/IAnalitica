/* 0170 · Vistas de los dashboards de la fase 1 (F3): financiero, ciclo del ingreso y cartera.
   Heredan la RLS de los hechos. Todas exponen EmpresaId: el motor de métricas filtra por la empresa de la sesión además de la RLS (doble defensa).
   Las que ya existían en 0140 se redefinen con CREATE OR ALTER agregando columnas (sin quitar ninguna). */

-- P&G: se agregan Grupo (2 primeros dígitos del PUC), Ingreso y CostoGasto (para márgenes con SUM(a)/NULLIF(SUM(b),0))
CREATE OR ALTER VIEW rpt.vw_EstadoResultados AS
SELECT a.EmpresaId, a.PeriodoKey, a.SedeId, a.CentroCostoId, c.CuentaId, c.Codigo, c.ClasePuc,
       ISNULL(h.GrupoEstadoResultados, 'SIN_HOMOLOGAR') AS GrupoEstadoResultados,
       SUM(CASE WHEN (c.Naturaleza = 'D' AND c.MovimientoContrario = 0) OR (c.Naturaleza = 'C' AND c.MovimientoContrario = 1)
                THEN a.Debito - a.Credito ELSE a.Credito - a.Debito END) AS Saldo,
       SUM(a.Credito - a.Debito) AS Utilidad,
       LEFT(c.Codigo, 2) AS Grupo,
       SUM(CASE WHEN c.ClasePuc = '4' THEN a.Credito - a.Debito ELSE 0 END) AS Ingreso,
       SUM(CASE WHEN c.ClasePuc <> '4' THEN a.Debito - a.Credito ELSE 0 END) AS CostoGasto
FROM fact.AsientoLinea a
JOIN dim.CuentaContable c      ON c.CuentaId = a.CuentaId
JOIN dim.TipoComprobante tc    ON tc.TipoComprobanteId = a.TipoComprobanteId
LEFT JOIN dim.CuentaHomologada h ON h.CuentaEstandarId = c.CuentaEstandarId
WHERE a.Estado = 1
  AND c.ClasePuc IN ('4', '5', '6', '7')
  AND tc.Clase <> 'CIERRE'
GROUP BY a.EmpresaId, a.PeriodoKey, a.SedeId, a.CentroCostoId, c.CuentaId, c.Codigo, c.ClasePuc, h.GrupoEstadoResultados;
GO

-- Balance: saldo por periodo y cuenta (clases 1, 2 y 3). El saldo a una fecha es la suma de los periodos hasta ella.
-- GrupoBalance sale de la homologación (dim.CuentaHomologada) y, si falta, de los 2 primeros dígitos del PUC comercial. [S] 24 §12 / notas F3.
CREATE OR ALTER VIEW rpt.vw_SaldoBalance AS
WITH m AS (
    SELECT a.EmpresaId, a.PeriodoKey, a.SedeId, c.CuentaId, c.Codigo, c.ClasePuc, LEFT(c.Codigo, 2) AS Grupo, h.GrupoBalance AS GrupoHomologado,
           SUM(CASE WHEN (c.Naturaleza = 'D' AND c.MovimientoContrario = 0) OR (c.Naturaleza = 'C' AND c.MovimientoContrario = 1)
                    THEN a.Debito - a.Credito ELSE a.Credito - a.Debito END) AS Saldo
    FROM fact.AsientoLinea a
    JOIN dim.CuentaContable c ON c.CuentaId = a.CuentaId
    LEFT JOIN dim.CuentaHomologada h ON h.CuentaEstandarId = c.CuentaEstandarId
    WHERE a.Estado = 1 AND c.ClasePuc IN ('1', '2', '3')
    GROUP BY a.EmpresaId, a.PeriodoKey, a.SedeId, c.CuentaId, c.Codigo, c.ClasePuc, h.GrupoBalance),
g AS (
    SELECT m.*, COALESCE(m.GrupoHomologado,
        CASE WHEN m.ClasePuc = '1' THEN CASE WHEN m.Grupo IN ('11', '12', '13', '14') THEN 'ACTIVO_CORRIENTE' ELSE 'ACTIVO_NO_CORRIENTE' END
             WHEN m.ClasePuc = '2' THEN CASE WHEN m.Grupo IN ('21', '22', '23', '24', '25') THEN 'PASIVO_CORRIENTE' ELSE 'PASIVO_NO_CORRIENTE' END
             ELSE 'PATRIMONIO' END) AS GrupoBalance
    FROM m)
SELECT EmpresaId, PeriodoKey, SedeId, CuentaId, Codigo, ClasePuc, Grupo, GrupoBalance, Saldo,
       CASE WHEN GrupoBalance = 'ACTIVO_CORRIENTE' THEN Saldo ELSE 0 END AS ActivoCorriente,
       CASE WHEN ClasePuc = '1' THEN Saldo ELSE 0 END AS ActivoTotal,
       CASE WHEN GrupoBalance = 'PASIVO_CORRIENTE' THEN Saldo ELSE 0 END AS PasivoCorriente,
       CASE WHEN ClasePuc = '2' THEN Saldo ELSE 0 END AS PasivoTotal,
       CASE WHEN ClasePuc = '3' THEN Saldo ELSE 0 END AS Patrimonio,
       CASE WHEN Grupo = '11' AND ClasePuc = '1' THEN Saldo ELSE 0 END AS Disponible,
       CASE WHEN Grupo = '14' AND ClasePuc = '1' THEN Saldo ELSE 0 END AS Inventarios,
       CASE WHEN GrupoBalance = 'ACTIVO_CORRIENTE' AND Grupo <> '14' THEN Saldo ELSE 0 END AS ActivoCorrienteSinInv,
       CASE WHEN GrupoBalance = 'ACTIVO_CORRIENTE' THEN Saldo WHEN GrupoBalance = 'PASIVO_CORRIENTE' THEN -Saldo ELSE 0 END AS CapitalTrabajo
FROM g;
GO

-- Cartera por edades: se agrega SaldoMas90 (> 90 días) para el porcentaje de cartera vencida
CREATE OR ALTER VIEW rpt.vw_AntiguedadCartera AS
SELECT c.EmpresaId, c.CorteFechaKey, c.TipoCartera, c.PagadorId, c.TerceroId, c.SedeId, c.RangoEdad,
       COUNT(*) AS Documentos, SUM(c.ValorOriginal) AS ValorOriginal, SUM(c.Saldo) AS Saldo,
       SUM(CASE WHEN c.RangoEdad IN ('91-120', '121-150', '151-180', '181-360', '>360') THEN c.Saldo ELSE 0 END) AS SaldoMas90
FROM fact.CarteraDocumento c
GROUP BY c.EmpresaId, c.CorteFechaKey, c.TipoCartera, c.PagadorId, c.TerceroId, c.SedeId, c.RangoEdad;
GO

-- DSO por pagador: saldo de cuentas por cobrar al corte frente a lo facturado en los 90 días anteriores (DSO = saldo / facturado × 90)
CREATE OR ALTER VIEW rpt.vw_CarteraPagador AS
WITH k AS (
    SELECT DISTINCT EmpresaId, CorteFechaKey, PagadorId FROM fact.CarteraDocumento WHERE PagadorId IS NOT NULL AND TipoCartera = 'CXC')
SELECT k.EmpresaId, k.CorteFechaKey, k.PagadorId,
       s.SaldoCxC, s.SaldoCxCMas90, ISNULL(f.Facturado90, 0) AS Facturado90
FROM k
CROSS APPLY (SELECT DATEADD(DAY, -90, CONVERT(date, CAST(k.CorteFechaKey AS char(8)), 112)) AS Desde) d
CROSS APPLY (SELECT SUM(c.Saldo) AS SaldoCxC,
                    SUM(CASE WHEN c.RangoEdad IN ('91-120', '121-150', '151-180', '181-360', '>360') THEN c.Saldo ELSE 0 END) AS SaldoCxCMas90
             FROM fact.CarteraDocumento c
             WHERE c.EmpresaId = k.EmpresaId AND c.CorteFechaKey = k.CorteFechaKey AND c.PagadorId = k.PagadorId AND c.TipoCartera = 'CXC') s
OUTER APPLY (SELECT SUM(fl.ValorNeto) AS Facturado90
             FROM fact.FacturaLinea fl
             WHERE fl.EmpresaId = k.EmpresaId AND fl.PagadorId = k.PagadorId AND fl.Anulada = 0
               AND fl.FechaEmisionKey > YEAR(d.Desde) * 10000 + MONTH(d.Desde) * 100 + DAY(d.Desde)
               AND fl.FechaEmisionKey <= k.CorteFechaKey) f;
GO

-- Conciliación: saldo de la cartera calculada frente al saldo contable de las cuentas con DetalleCartera (2 CxC, 1 CxP) a la fecha de corte
CREATE OR ALTER VIEW rpt.vw_ConciliacionCartera AS
WITH k AS (SELECT DISTINCT EmpresaId, CorteFechaKey FROM fact.CarteraDocumento),
t AS (SELECT 'CXC' AS TipoCartera, 2 AS Detalle UNION ALL SELECT 'CXP', 1)
SELECT k.EmpresaId, k.CorteFechaKey, t.TipoCartera,
       ISNULL(cart.Saldo, 0) AS SaldoCartera,
       ISNULL(lib.Saldo, 0) AS SaldoLibro,
       ISNULL(cart.Saldo, 0) - ISNULL(lib.Saldo, 0) AS Diferencia
FROM k CROSS JOIN t
OUTER APPLY (SELECT SUM(c.Saldo) AS Saldo FROM fact.CarteraDocumento c
             WHERE c.EmpresaId = k.EmpresaId AND c.CorteFechaKey = k.CorteFechaKey AND c.TipoCartera = t.TipoCartera) cart
OUTER APPLY (SELECT SUM(CASE WHEN cu.Naturaleza = 'D' THEN a.Debito - a.Credito ELSE a.Credito - a.Debito END) AS Saldo
             FROM fact.AsientoLinea a JOIN dim.CuentaContable cu ON cu.CuentaId = a.CuentaId
             WHERE a.EmpresaId = k.EmpresaId AND a.Estado = 1 AND a.FechaKey <= k.CorteFechaKey AND cu.DetalleCartera = t.Detalle) lib;
GO

-- Factura electrónica y radicación, por documento (una factura = una fila)
CREATE OR ALTER VIEW rpt.vw_EnvioFacturas AS
WITH d AS (
    SELECT f.EmpresaId, f.SedeId, f.NumeroDocumento, MIN(f.PagadorId) AS PagadorId, MIN(f.FechaEmisionKey) AS FechaEmisionKey,
           MAX(f.FechaRadicacionKey) AS FechaRadicacionKey, MAX(f.EstadoFE) AS EstadoFE, SUM(f.ValorNeto) AS Valor
    FROM fact.FacturaLinea f
    WHERE f.Anulada = 0
    GROUP BY f.EmpresaId, f.SedeId, f.NumeroDocumento)
SELECT d.EmpresaId, d.FechaEmisionKey / 100 AS PeriodoKey, d.SedeId, d.PagadorId,
       CASE d.EstadoFE WHEN 0 THEN 'ENVIADA' WHEN 1 THEN 'CARGADA' WHEN 2 THEN 'ERROR' WHEN 3 THEN 'SIN_ENVIAR' ELSE 'SIN_DATO' END AS EstadoFe,
       1 AS Facturas, d.Valor AS Valor,
       CASE WHEN d.EstadoFE IN (2, 3) THEN 1 ELSE 0 END AS FacturasConProblemaFe,
       CASE WHEN d.FechaRadicacionKey IS NULL THEN 1 ELSE 0 END AS FacturasSinRadicar,
       CASE WHEN d.FechaRadicacionKey IS NULL THEN d.Valor ELSE 0 END AS ValorSinRadicar,
       DATEDIFF(DAY, fe.Fecha, fr.Fecha) AS DiasARadicacion
FROM d
JOIN dim.Fecha fe ON fe.FechaKey = d.FechaEmisionKey
LEFT JOIN dim.Fecha fr ON fr.FechaKey = d.FechaRadicacionKey;
GO

-- Re-trabajo de facturación: lo anulado frente a lo emitido
CREATE OR ALTER VIEW rpt.vw_Retrabajo AS
SELECT f.EmpresaId, f.FechaEmisionKey / 100 AS PeriodoKey, f.SedeId, f.UsuarioOpId,
       COUNT(DISTINCT CASE WHEN f.Anulada = 0 THEN f.NumeroDocumento END) AS Facturas,
       COUNT(DISTINCT CASE WHEN f.Anulada = 1 THEN f.NumeroDocumento END) AS FacturasAnuladas,
       SUM(CASE WHEN f.Anulada = 0 THEN f.ValorNeto ELSE 0 END) AS ValorFacturado,
       SUM(CASE WHEN f.Anulada = 1 THEN f.ValorNeto ELSE 0 END) AS ValorAnulado,
       SUM(f.ValorNeto) AS ValorEmitido
FROM fact.FacturaLinea f
GROUP BY f.EmpresaId, f.FechaEmisionKey / 100, f.SedeId, f.UsuarioOpId;
GO
