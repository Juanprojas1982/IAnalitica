# SIPS_108 vs SIPS_550_1: por qué no devuelven las mismas facturas

Fuente: `docs/fuentes-saludips/reportes3.csv` (la única instalación que trae SIPS_108, SIPS_550 y SIPS_550_1 juntos). Análisis **solo del SQL**: no se ejecutó contra ninguna base. `SIPS_550_2` no existe en los archivos.

## 1. Qué hace cada uno
| | SIPS_108 «Reporte general de facturación» | SIPS_550_1 «Procedimientos y medicamentos FACTURADOS» |
|---|---|---|
| Grano | **Factura** (GROUP BY; una fila por factura) | **Servicio** (una fila por procedimiento o medicamento) |
| Fecha | `mFacturas.FechaFactura` | `mFacturas.FechaFactura` (igual) |
| Anuladas | factura y servicio no anulados | ídem |
| Filtro de sede | `mAdmisiones.IdSede` (en la versión de reportes3) | ídem |
| Cruces | factura → admisión → servicios → paciente → empresa → usuario | Los de 108 **más ~12 cruces INNER extra** (ciudad, sede, médico, especialidad, tipo de servicio, procedimiento, **tarifa del manual**, usuario del servicio, tipo de atención, `dServicios`) y, para medicamentos, artículo base, suministro y su tarifa |
| Empresa | la de la **factura** (`mFacturas.IdEmpresa`) | la de la **admisión** (`mAdmisiones.IdEmpresa`) |
| Usuario | quien hizo la **factura** (`mFacturas.IdcUsuario`) | quien cargó el **servicio** (`ms.IdcUsuario`) |
| Clase de servicio | todas | dos ramas con `UNION ALL`: `codclaseservicio <> 4` y `= 4` |

SIPS_550 (sin `_1`) es otro reporte: filtra por `mServicios.FechaRealizado` y usa LEFT JOIN a `mFacturas`, así que incluye servicios sin factura y facturas de otros periodos. No debe coincidir con 108.

## 2. Causa principal: 550_1 pierde facturas por INNER JOIN
Un INNER JOIN descarta el servicio completo cuando el dato relacionado no existe. En 108 esos cruces no existen. Una factura de 108 desaparece de 550_1 cuando **todos** sus servicios fallan alguno de estos cruces; si solo fallan algunos, aparece con menos líneas y el total difiere:

1. **Sin tarifa en el manual de la empresa** (la más probable): `ptarifasprocedimientos` / `pTarifasSuministros` se cruzan por `codmanual = mEmpresas.codmanual`. Si la tarifa no existe en el manual **actual** de la empresa (se borró, nunca se cargó, o el pagador cambió de manual después de facturar), el servicio sale de 550_1. Es la regla TAR-02 del auditor de IAnalitica.
2. Servicio cuyo `codservicio` ya no está en `pProcedimientos` / `pSuministros`.
3. **Médico nulo** o sin especialidad en `rReferencias` clase 8. Para medicamentos usa el médico de la admisión (`a.idmedico`), no el del servicio.
4. Medicamento sin `dServicios.ArticuloId` o con artículo ausente en `Almacen.ArticulosBase`.
5. `codclaseservicio` **NULL**: queda fuera de las dos ramas (`<>4` y `=4` son falsos con NULL).
6. Servicio sin fila en `dServicios`; usuario del servicio, tipo de servicio, tipo de atención, sede o ciudad inexistentes.

## 3. Causas que van en sentido contrario o alteran totales
- **Factura en 550_1 y no en 108** (menos frecuente): 108 une `cUsuarios` por el usuario de la **factura** y `mEmpresas` por la empresa de la **factura**; si ese usuario es nulo o la empresa de la factura no existe, se pierde en 108. En la versión de `reportes1` además hay INNER JOIN a `mContratos` (empresa sin contrato) y en otras versiones no existe el filtro de sede.
- **Filas repetidas en 550_1**: `LEFT JOIN dHCPacienteResultados` (varios resultados por servicio), `dCirugias` y `EnviosFacturaElectronica` con `Estado = 1` más de una vez multiplican el servicio. 108 agrupa y no se infla, pero 550_1 sí: los totales no cuadran aunque estén las mismas facturas.
- **Admisión con dos facturas no anuladas**: ambos reportes cruzan por admisión, así que cada factura arrastra **todos** los servicios de la admisión. 108 lo delata en la columna `DiFerencia` (≠ 0).
- **Borde de hora**: ambos usan `'@Fecha Final' + ' 23:59'` (equivale a 23:59:00). Las facturas de la **última** fecha entre 23:59:01 y 23:59:59 se pierden en los dos; la versión de `reportes1` de 108 usa `23:59:59` y sí las trae.
- **Versiones distintas**: hay tres textos de SIPS_108 (reportes1, 3 y 4) y dos de SIPS_550. Confirme cuál versión imprime cada instalación; no son idénticas.

## 4. Cómo identificar el problema con sus datos
Ejecute este script en la base de SaludIPS (solo lectura). Ajuste las fechas. El resultado 1 cuenta servicios perdidos por causa; el 2 lista las facturas afectadas.

```sql
SET DATEFORMAT DMY;
DECLARE @Desde datetime = '01/01/2026', @Hasta datetime = '31/01/2026 23:59:59';

SELECT f.NoFactura, f.IdAdmision, f.IdEmpresa AS EmpFactura, a.IdEmpresa AS EmpAdmision, f.IdcUsuario AS UsuFactura,
       ms.IdServicio, ms.CodServicio, ms.CodClaseServicio, ms.VrTotal,
       CASE WHEN ms.CodClaseServicio IS NULL THEN 1 ELSE 0 END                                                 AS ClaseNula,
       CASE WHEN ds.IdServicio IS NULL THEN 1 ELSE 0 END                                                       AS SinDServicios,
       CASE WHEN c.CodCiudad IS NULL THEN 1 ELSE 0 END                                                         AS SinCiudad,
       CASE WHEN ca.IdSede IS NULL THEN 1 ELSE 0 END                                                           AS SinSede,
       CASE WHEN mp.IdPersonal IS NULL THEN 1 ELSE 0 END                                                       AS SinMedico,
       CASE WHEN mp.IdPersonal IS NOT NULL AND esp.CodReferencia IS NULL THEN 1 ELSE 0 END                     AS SinEspecialidad,
       CASE WHEN ts.CodTipoServicio IS NULL THEN 1 ELSE 0 END                                                  AS SinTipoServicio,
       CASE WHEN cu.IdcUsuario IS NULL THEN 1 ELSE 0 END                                                       AS SinUsuarioServicio,
       CASE WHEN ta.CodTipoAtencion IS NULL THEN 1 ELSE 0 END                                                  AS SinTipoAtencion,
       CASE WHEN ISNULL(ms.CodClaseServicio,0) <> 4 AND pp.CodProcedimiento IS NULL THEN 1 ELSE 0 END          AS SinProcedimiento,
       CASE WHEN ISNULL(ms.CodClaseServicio,0) <> 4 AND pp.CodProcedimiento IS NOT NULL AND tp.CodProcedimiento IS NULL THEN 1 ELSE 0 END AS SinTarifaProcEnManual,
       CASE WHEN ms.CodClaseServicio = 4 AND ps.CodSuministro IS NULL THEN 1 ELSE 0 END                        AS SinSuministro,
       CASE WHEN ms.CodClaseServicio = 4 AND ps.CodSuministro IS NOT NULL AND pts.CodSuministro IS NULL THEN 1 ELSE 0 END AS SinTarifaSumEnManual,
       CASE WHEN ms.CodClaseServicio = 4 AND ar.Id IS NULL THEN 1 ELSE 0 END                                   AS SinArticuloBase
INTO #servicios
FROM mFacturas f
JOIN mAdmisiones a ON a.IdAdmision = f.IdAdmision
JOIN mServicios ms ON ms.IdAdmision = a.IdAdmision AND ms.Anulado = 0
JOIN mEmpresas e   ON e.IdEmpresa = a.IdEmpresa
LEFT JOIN rCiudades c        ON c.CodCiudad = a.CodCiudad
LEFT JOIN cAdministracion ca ON ca.IdSede = a.IdSede
LEFT JOIN dServicios ds      ON ds.IdServicio = ms.IdServicio
LEFT JOIN mPersonal mp       ON mp.IdPersonal = CASE WHEN ms.CodClaseServicio = 4 THEN a.IdMedico ELSE ms.IdMedico END
LEFT JOIN rReferencias esp   ON esp.CodClaseReferencia = 8 AND esp.CodReferencia = mp.CodEspecialidad
LEFT JOIN pTipoServicio ts   ON ts.CodTipoServicio = ms.CodTipoServicio
LEFT JOIN cUsuarios cu       ON cu.IdcUsuario = ms.IdcUsuario
LEFT JOIN pTipoAtencion ta   ON ta.CodTipoAtencion = a.CodTipoAtencion
LEFT JOIN pProcedimientos pp ON pp.CodProcedimiento = ms.CodServicio AND ISNULL(ms.CodClaseServicio,0) <> 4
LEFT JOIN pTarifasProcedimientos tp ON tp.CodProcedimiento = pp.CodProcedimiento AND tp.CodManual = e.CodManual
LEFT JOIN pSuministros ps    ON ps.CodSuministro = ms.CodServicio AND ms.CodClaseServicio = 4
LEFT JOIN pTarifasSuministros pts ON pts.CodSuministro = ps.CodSuministro AND pts.CodManual = e.CodManual
LEFT JOIN Almacen.ArticulosBase ar ON ar.Id = ds.ArticuloId
WHERE f.Anulado = 0 AND f.FechaFactura BETWEEN @Desde AND @Hasta;

-- 1) Servicios que SIPS_550_1 pierde, por causa (un servicio puede tener varias)
SELECT Causa, COUNT(*) AS Servicios, COUNT(DISTINCT NoFactura) AS Facturas, SUM(VrTotal) AS Valor
FROM #servicios
CROSS APPLY (VALUES ('ClaseNula',ClaseNula),('SinDServicios',SinDServicios),('SinCiudad',SinCiudad),('SinSede',SinSede),('SinMedico',SinMedico),
  ('SinEspecialidad',SinEspecialidad),('SinTipoServicio',SinTipoServicio),('SinUsuarioServicio',SinUsuarioServicio),('SinTipoAtencion',SinTipoAtencion),
  ('SinProcedimiento',SinProcedimiento),('SinTarifaProcEnManual',SinTarifaProcEnManual),('SinSuministro',SinSuministro),
  ('SinTarifaSumEnManual',SinTarifaSumEnManual),('SinArticuloBase',SinArticuloBase)) v(Causa, Marca)
WHERE Marca = 1 GROUP BY Causa ORDER BY Servicios DESC;

-- 2) Facturas que desaparecen por completo de 550_1 (todos sus servicios se pierden) o quedan incompletas
SELECT NoFactura, COUNT(*) AS Servicios,
       SUM(CASE WHEN ClaseNula+SinDServicios+SinCiudad+SinSede+SinMedico+SinEspecialidad+SinTipoServicio+SinUsuarioServicio+SinTipoAtencion
                     +SinProcedimiento+SinTarifaProcEnManual+SinSuministro+SinTarifaSumEnManual+SinArticuloBase = 0 THEN 1 ELSE 0 END) AS ServiciosEn550_1,
       SUM(VrTotal) AS ValorServicios
FROM #servicios GROUP BY NoFactura
HAVING SUM(CASE WHEN ClaseNula+SinDServicios+SinCiudad+SinSede+SinMedico+SinEspecialidad+SinTipoServicio+SinUsuarioServicio+SinTipoAtencion
                     +SinProcedimiento+SinTarifaProcEnManual+SinSuministro+SinTarifaSumEnManual+SinArticuloBase = 0 THEN 1 ELSE 0 END) < COUNT(*)
ORDER BY ServiciosEn550_1, NoFactura;

-- 3) Facturas que SIPS_108 puede perder (usuario o empresa de la factura inexistente) o con empresa distinta a la de la admisión
SELECT f.NoFactura, f.IdEmpresa, a.IdEmpresa AS EmpAdmision, f.IdcUsuario
FROM mFacturas f JOIN mAdmisiones a ON a.IdAdmision = f.IdAdmision
WHERE f.Anulado = 0 AND f.FechaFactura BETWEEN @Desde AND @Hasta
  AND (NOT EXISTS (SELECT 1 FROM cUsuarios u WHERE u.IdcUsuario = f.IdcUsuario) OR NOT EXISTS (SELECT 1 FROM mEmpresas e WHERE e.IdEmpresa = f.IdEmpresa) OR f.IdEmpresa <> a.IdEmpresa);

-- 4) Admisiones con más de una factura vigente (ambos reportes repiten los servicios en cada factura)
SELECT IdAdmision, COUNT(*) AS Facturas FROM mFacturas WHERE Anulado = 0 AND FechaFactura BETWEEN @Desde AND @Hasta GROUP BY IdAdmision HAVING COUNT(*) > 1;

-- 5) Facturas del último día entre 23:59:01 y 23:59:59 (se pierden con el filtro '... 23:59')
SELECT NoFactura, FechaFactura FROM mFacturas WHERE Anulado = 0 AND FechaFactura > DATEADD(MINUTE, 1439, CAST(CAST(@Hasta AS date) AS datetime)) AND FechaFactura <= @Hasta;

DROP TABLE #servicios;
```
Qué mirar: si el resultado 1 muestra `SinTarifaProcEnManual` / `SinTarifaSumEnManual`, la diferencia se corrige cargando la tarifa en el manual del pagador (o reportando que el reporte exige tarifa vigente). Si domina `SinMedico`, `ClaseNula` o `SinArticuloBase`, es calidad de datos en el servicio.

## 5. Recomendación
Para cuadrar facturas, use 108 como universo (es el que no depende de maestros) y 550_1 solo para detalle. Si 550_1 debe ser fiel a la factura, sus cruces con tarifa, especialidad, usuario, ciudad y sede deberían ser LEFT JOIN, y los resultados/CUFE deberían agregarse en subconsultas para no multiplicar filas. Es un cambio al reporte de SaludIPS: no lo apliqué.
