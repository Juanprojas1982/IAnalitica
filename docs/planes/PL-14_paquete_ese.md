# PL-14 · Paquete ESE (sector público)

Estado: diseñado, no implementado. Módulo `ESE_PRESUPUESTO` (permiso `dash.ese_presupuesto`, sembrado). Script `1160_ese.sql`.
Preparación (2026-11-16): **fases 1 y 2 listas para implementar; fase 3, solo las reglas del auditor.** El origen presupuestal se verificó en el código de SaludIPS (repo `SaludIPS2`, commit `0e05edb`): ver «Origen verificado». El cuadre con datos reales de la ESE es criterio de aceptación, no bloqueo. **`contratos-publicos` y el tablero de contratación no están listos:** el contrato de esa entidad no tiene campos ni origen definidos (P9; ver fase 1). Fase 4 espera la validación del plan de cuentas con la ESE; fase 5, P6.
Revisado contra el código de este repo el 2026-11-16: prefijo de las métricas, catálogo de rubros con llave compuesta y campos de `presupuesto-publico`.
Es el paquete de la **ESE piloto**. Decisión del dueño (2026-11-16): el sector público entra al alcance porque aún no se ha salido con ningún piloto.

## Objetivo
Dar a una ESE lo que usa para gestionarse y rendir cuentas: ejecución presupuestal de gastos e ingresos, equilibrio y proyección, cuentas por pagar presupuestales, contratación, calidad del reporte y —al final— simulación de la categorización de riesgo. Además, que los tableros financieros funcionen con su plan de cuentas.

## Situación actual
- `fact.PresupuestoMovimiento` (una tabla con `Etapa`), `fact.ContratoPublico`, `dim.RubroPresupuestal`, `dim.FuenteFinanciacion` existen (`0040`, `0070`). `ModuloEse.cs` vacío.
- **No hay extracción escrita** para el esquema `Presupuesto` de SaludIPS (`docs/06` y `docs/11` no lo cubren; el conector `SaludIPSNet.Analitica/Sql` llega hasta `A50`). Las tablas están **vacías en la instalación revisada**, pero la estructura (36 tablas en `docs/fuentes-saludips/diccionario_columnas.csv`), los enumerados y las reglas de cálculo están en el código de SaludIPS: ya no son `[S]`.
- Balance y P&G usan reglas del PUC comercial; el plan de la Contaduría (CGN) está «por validar».
- Fórmulas de categorización de riesgo: pendiente P6 (la ESE piloto debe validarlas).

## Fases (cada una es un PR)
### Fase 0 · Extracción (sin código de producto en este repo)
1. Escribir en el repo `SaludIPS2` la función del conector (`SaludIPSNet.Analitica/Sql/A60_fn_presupuesto.sql`, siguiendo `A20`/`A30`) y dejar copia de referencia en `docs/12_extraccion_presupuesto_saludips.sql`, con las reglas de «Origen verificado».
2. Cuadre (criterio de aceptación, cuando haya una base con datos): la ejecución por rubro y fuente debe dar lo mismo que los reportes de SaludIPS `presupuestoEjecucionPresupuestalGastos*` e `…Ingresos*`, y que `CUIPO01`–`CUIPO05` y `RelacionCompromisos2193`.
3. Con la ESE piloto quedan solo: marco contable y homologación del plan de cuentas (fase 4), informes que exigen junta y entes de control, y fórmulas de riesgo (P6, fase 5).

### Origen verificado (código de SaludIPS)
Fuentes: `SaludIPSNet.Entities/Adds/EnumPresupuesto.cs`, `SaludIPSNet.Entities/Presupuesto/RubroFuente.cs` (métodos `Valor*`), `Reports.BLL/Presupuesto/PresupuestoEjecucionPresupuestalGastosHelper.cs`.

**Enumerados** (se guardan como `tinyint`):
- `Movimientos.TipoMovimiento`: 1 Adición · 2 Reducción · 3 Traslado. Aplazamiento y desplazamiento están comentados en el código: no existen.
- `Movimientos.TipoDocumento`: 1 Acuerdo · 2 Decreto · 3 Resolución.
- `RubrosPresupuestales.Naturaleza`: 1 Ingreso · 2 Gasto → `dim.RubroPresupuestal.Tipo` `I`/`G`.
- `RubrosFuentes.TipoSaldo`: 1 saldo inicial · 2 adición.
- `EstadoEntidad`: 0 Pendiente · **1 Confirmado** · otro = anulado. SaludIPS solo suma `EstadoEntidad = 1`, y lo exige en **cada nivel** de la cadena.
- `SituacionFondos`: 0 sin · 1 con. `Pagos.TipoEgreso`: 0 vigencia actual · 1 vigencia pasada · 2 sin movimiento presupuestal.

**Grano:** todo se imputa a `RubrosFuentes` (rubro × fuente). `Vigencia` = `RubrosPresupuestales.Vigencia`.

| Etapa (`fact.PresupuestoMovimiento`) | Origen | Valor | Fecha |
|---|---|---|---|
| `APROPIACION_INICIAL` (rubro de gasto) / `AFORO_INICIAL` (rubro de ingreso) | `RubrosFuentes` | `SaldoInicial` | 1 de enero de la vigencia |
| `ADICION` | `MovimientosDetalles` con `Movimientos.TipoMovimiento = 1` | `ValorIngreso` | `MovimientosDetalles.Fecha` |
| `REDUCCION` | ídem, `TipoMovimiento = 2` | `ValorEgreso` | ídem |
| `CREDITO` | ídem, `TipoMovimiento = 3` | `ValorIngreso` | ídem |
| `CONTRACREDITO` | ídem, `TipoMovimiento = 3` | `ValorEgreso` | ídem |
| `CDP` | `CDPsImputaciones` (`RubroFuenteId` directo) | `Valor` | `CDPs.Fecha` |
| `RP` | `RPsImputaciones` → `CDPImputacionId` | `Valor` | `RPs.Fecha` |
| `OBLIGACION` | `ObligacionesImputaciones` → `RPImputacionId` | `Valor` | `Obligaciones.Fecha` |
| `PAGO` | `PagosImputaciones` → `ObligacionImputacionId` | `Valor` | `Pagos.Fecha` |
| `RECONOCIMIENTO` | `ReconocimientosImputaciones` (`RubroFuenteId`) | `Valor` | `Reconocimientos.Fecha` |
| `RECAUDO` | `IngresosTesoreriaImputaciones` → `ReconocimientoImputacionId` | `Valor` | `IngresosTesoreria.Fecha` |

**Notas (reversiones): misma etapa, valor negativo, con la fecha de la nota.**
- `NotasPresupuestalesCDPsDetalles.ValorNota` → `CDP`; `…RPsDetalles` → `RP`; `…ObligacionesDetalles` → `OBLIGACION`; `…PagosDetalles` → `PAGO`; `…RecaudosDetalles.ValorNota` → `RECAUDO`.
- Ingresos: `NotasPresupuestalesIngresosDetalles.ValorGlosaEPS` → `RECONOCIMIENTO`, con `NotasPresupuestalesIngresos.FechaTramite` (no `Fecha`).

**Fórmulas con las que cuadra SaludIPS:**
- Definitivo = `SaldoInicial` + Σ `ValorIngreso` − Σ `ValorEgreso` de los movimientos confirmados (equivale a inicial + adiciones − reducciones + créditos − contracréditos).
- Total de cada etapa = Σ imputaciones − Σ notas.
- % de ejecución = RP ÷ definitivo (variante «con CDP»: CDP ÷ definitivo). Saldo por ejecutar = definitivo − RP (o − CDP).
- **«Cuentas por pagar» del reporte de SaludIPS = RP − pagos**, no obligaciones − pagos.

**Reglas de la extracción:**
- `sourceKey` = prefijo de tabla + `Id` de la fila (`MOV-I:`/`MOV-E:` para el lado ingreso y egreso de un mismo detalle, `CDP:`, `RP:`, `OBL:`, `PAG:`, `REC:`, `ING:`, `NCDP:`, `NRP:`, `NOBL:`, `NPAG:`, `NING:`, `NREC:`, `INI:` + `RubroFuenteId`).
- Los valores son `float` en SaludIPS: redondear a 4 decimales al extraer.
- `documento` = número del documento (`NumeroRP`, `NumeroObligacion`, `NumeroPago`, `NumeroReconocimiento`, `NumeroComprobante`, `Consecutivo`, `NumeroNota`); `terceroCodigo` desde `TerceroId` de RP, obligación, pago, reconocimiento e ingreso de tesorería.

**Sigue siendo `[S]`** (el código no lo resuelve):
- `SaldoInicial` de un rubro-fuente con `TipoSaldo = 2` (adición): el reporte de SaludIPS lo suma como saldo inicial sin mirar el tipo ni la fecha. Se replica (apropiación inicial) para cuadrar; confirmar con la ESE si debe verse como adición.
- `Presupuesto.IngresosContables*` no interviene en las fórmulas leídas: no se extrae hasta saber qué representa en el flujo de la ESE.
- Pagos de vigencia pasada (`TipoEgreso = 1`): el reporte de SaludIPS no los separa. Se extraen como `PAGO` igual que los demás; no se modelan reservas ni cuentas por pagar de vigencias anteriores hasta el cuadre con datos reales.


### Fase 1 · Ingesta
- Catálogos nuevos, modo `COMPLETA`:
  - `fuentes-financiacion` (codigoOrigen*, nombre*): cabe en `DefinicionesCatalogo` tal cual (`dim.FuenteFinanciacion`, llave `CodigoOrigen`).
  - `rubros-presupuestales` (vigencia*, codigo*, nombre*, tipo* I/G, naturaleza, codigoCuipo, permiteMovimientos*, codigoPadre): **no cabe en `DefinicionesCatalogo`.** `DefCatalogo` admite una sola columna llave (`ClaveSql`/`ClaveJson`) y `dim.RubroPresupuestal` es única por `(EmpresaId, Vigencia, Codigo)` y no tiene `CodigoOrigen`. Se procesa con un SP propio, `etl.usp_ProcesarRubrosPresupuestales` (upsert por la llave compuesta, **sin borrar ni inactivar**: la tabla no tiene bandera de activo y los hechos la referencian), con su `case` en `ProcesadorCargas.AplicarAsync` y `EsCatalogo: true` en `CatalogoEntidades`. Duplicado por `vigencia|codigo` → `DUPLICADO_EN_CARGA`; `tipo` fuera de `I`/`G` → `VALOR_INVALIDO:tipo`.
- Hecho `presupuesto-publico` (`VENTANA` por `fecha`, suma de control `valor`, `ModuloRequerido: "ESE_PRESUPUESTO"`; requiere la verificación de PL-02 §1; **sí** pasa por el control de periodo cerrado, son hechos ejecutados) → `fact.PresupuestoMovimiento`.
  - Campos (el contrato `02` §15 solo lo nombra en «a añadir»; quedan definidos aquí y se copian a `02` al cerrar el plan): `sourceKey*, vigencia*, fecha*, rubroCodigo*, fuenteCodigo, etapa*, valor*, documento, terceroCodigo`.
  - La suma de control con valores negativos (notas) es la suma algebraica: `CerrarAsync` suma la columna tal cual.
  - Los movimientos `APROPIACION_INICIAL`/`AFORO_INICIAL` llevan fecha 1 de enero. Con la ventana diaria normal del conector (desde el primer periodo contable abierto) quedan fuera a partir de febrero: si la extracción los envía igual, se rechazan con `FUERA_DE_VENTANA`; si no los envía, un cambio en `SaldoInicial` nunca llega. Requisito para la extracción: **en `presupuesto-publico` la ventana empieza siempre el 1 de enero de la vigencia** (se recarga la vigencia en curso completa; el volumen presupuestal es pequeño). Documentarlo en `02` §16 y en `A60`. Consecuencia para PL-05 fase 2: un cierre duro de un mes de la vigencia en curso bloquearía esta entidad; al diseñar esa fase, o `presupuesto-publico` se declara `ExentaDeCierre`, o el conector la recorta igual que las demás y asume que la apropiación inicial ya no cambia.
- Hecho `contratos-publicos` → `fact.ContratoPublico`: **no listo.** El contrato no tiene campos definidos (P9) y «Origen verificado» no cubre la contratación. En el diccionario de SaludIPS existen tablas `Presupuesto.Contratacion*` (`ContratacionAdicionesRPs`, `ContratacionPolizas`, …), pero su relación con los campos de la tabla destino (`ValorInicial`, `ValorAdiciones`, `ValorComprometido`, `PolizaVence`, `Estado`) no se ha leído en el código de SaludIPS. Paso previo: repetir para contratación la verificación que se hizo para el presupuesto. Hasta entonces la entidad sigue en `ENTIDAD_NO_SOPORTADA`.
- `etl.usp_ProcesarPresupuestoPublico`: `etapa` ∈ lista del `CHECK`; rubro obligatorio y existente en la vigencia (`REFERENCIA_INEXISTENTE:rubroCodigo`); rubro sin `PermiteMovimientos` → `VALOR_INVALIDO:rubroCodigo`; etapa de ingreso sobre rubro de gasto (y al revés) → `VALORES_INVALIDOS`. `Valor` admite negativos (reversiones).
- Nota: la spec (`03` §22) habla de `fact.PresupuestoGasto` y `fact.PresupuestoIngreso`; el DDL real es una sola tabla. **Manda el DDL.** Las etapas `TRASLADO_CRED/CONTRA` de la spec son `CREDITO/CONTRACREDITO` en el DDL.

### Fase 2 · Ejecución presupuestal
- `rpt.vw_EjecucionGasto` por vigencia, periodo, rubro y fuente: `ApropiacionInicial, Adiciones, Reducciones, Creditos, Contracreditos, ApropiacionDefinitiva, Cdp, Rp, Obligaciones, Pagos, SaldoPorComprometer` (definitiva − RP), `SaldoPorObligar`, `CuentasPorPagar` (obligaciones − pagos: definición presupuestal) y `CompromisosPorPagar` (RP − pagos: es lo que el reporte de SaludIPS llama «cuentas por pagar» y contra lo que se cuadra). El % de ejecución es RP ÷ definitiva, como en SaludIPS; la variante por CDP se expone como métrica aparte (`ese_presupuesto.pct_ejecucion_cdp`).
- `rpt.vw_EjecucionIngreso`: `AforoInicial, Adiciones, Reducciones, AforoDefinitivo, Reconocimientos, Recaudos, PorRecaudar`.
- `rpt.vw_EquilibrioPresupuestal`: ingresos recaudados y reconocidos vs. gastos comprometidos y obligados, por vigencia y periodo acumulado.
- Jerarquía de rubros: dimensión `rubro` (`dim.RubroPresupuestal`) y `rubro_nivel1` (primer segmento del código) para no listar miles de rubros.
- `ModuloEsePresupuesto` (código `ESE_PRESUPUESTO`): métricas `ese_presupuesto.apropiacion_definitiva`, `.compromisos`, `.obligaciones`, `.pagos`, `.pct_ejecucion_gasto` (`KpiMeta: "EJECUCION_GASTO_ESE"`), `.pct_ejecucion_cdp`, `.aforo_definitivo`, `.reconocimientos`, `.recaudos`, `.pct_recaudo` (`KpiMeta: "PCT_RECAUDO_ESE"`), `.cuentas_por_pagar`, `.compromisos_por_pagar`, `.equilibrio_recaudo_compromiso`.
  - **El prefijo es `ese_presupuesto.`, no `ese.`** (así estaba antes): `CatalogoAnalitico` exige que el código empiece por el código del módulo en minúsculas y que el permiso del dashboard sea `dash.ese_presupuesto`; si no, los tres hosts no arrancan.
  - Todas acumuladas dentro de la vigencia → **`Acumulado: true` acumula desde el origen, no desde enero**: hace falta «acumulado por vigencia». Es un cambio del motor (`MotorMetricas.ArmarSql`): bandera `AcumuladoAnual` en `DefinicionMetrica` (parámetro opcional al final; `Acumulado` y `AcumuladoAnual` excluyentes, validado en `CatalogoAnalitico.ValidarMetrica`; exige dimensión `periodo`). Con ella el motor ignora `PeriodoDesde` y agrega `periodo >= @inicioAnio`, donde `@inicioAnio` = año de `PeriodoHasta` × 100 + 1. **Sin `PeriodoHasta`** el motor no tiene reloj: usa el año del mayor periodo de la fuente para la empresa (`(SELECT MAX(periodo) / 100 * 100 + 1 FROM fuente WHERE EmpresaId = @empresaId)`), el mismo recurso que ya usa para `corte`.
  - `ObtenerDatosWidgetHandler.PeriodoAnteriorAsync` debe devolver `null` también para `AcumuladoAnual` (hoy solo mira `Acumulado`): comparar un acumulado contra «el rango anterior de igual longitud» no significa nada. Si PL-07 ya está mezclado, sus comparativos la tratan como `Acumulado`.
  - PL-07 no usa la bandera ni la necesita (resuelve su acumulado en el handler); los dos planes son independientes.
- Dashboards: `ese_presupuesto.ejecucion_gastos`, `ese_presupuesto.ejecucion_ingresos`, `ese_presupuesto.equilibrio`, `ese_presupuesto.cuentas_por_pagar`.
- **Composición (verificado):** `ModuloEse.cs` hoy solo expone `Ensamblado`. Agregar `ModuloEse.Modulos` y concatenarla en las siete llamadas a `AddCapaSemantica` (tres hosts y cuatro en pruebas; lista en el índice).

### Fase 3 · Contratación y calidad del reporte
- `ese_presupuesto.contratacion` (**espera el contrato de `contratos-publicos`, ver fase 1; no implementar todavía**): contratos por estado, ejecución vs. valor, pólizas por vencer (30/60 días), adiciones sobre el valor inicial. **Los umbrales legales de adición se definen con jurídico**; hasta entonces solo se muestra el porcentaje, sin semáforo.
- Reglas del auditor (una clase cada una, mismo motor, activas solo con el módulo; esta parte sí está lista): ESE-01 rubro sin CUIPO o sin fuente · ESE-02 cuenta sin `RubroFuenteId` donde la integración lo exige · ESE-03 comprobante con funcionalidad presupuestal sin tipo de comprobante presupuestal · ESE-04 EPS sin rubro de ingreso · ESE-05 presupuesto del año siguiente incompleto desde noviembre. ESE-02 a ESE-04 necesitan tablas en la foto `configuracion` (ampliar la lista blanca de `03` §21 y regenerar `saludips.*` con `generar-saludips.py` en un script nuevo): no producen hallazgos hasta que el conector de la ESE envíe la foto ampliada. ESE-01 y ESE-05 salen de `dim.RubroPresupuestal` (fase 1) y no dependen de la foto.
- Consistencia presupuesto–contabilidad–tesorería: pagos presupuestales del mes vs. salidas de disponible (PL-01) como control, sin afirmar que deban ser iguales.

### Fase 4 · Plan de cuentas público en los tableros financieros
- Homologación por empresa: `dim.CuentaHomologada` ya da `GrupoBalance` y `GrupoEstadoResultados`. Falta el **marco**: parámetro por empresa `PLAN_CUENTAS` (`COMERCIAL` | `CGN`) y, para CGN, una tabla de homologación sembrada tras validar con la ESE (clases de ingreso, gasto y costo del catálogo de la Contaduría — **no asumir que 4/5/6/7 significan lo mismo**).
- `rpt.vw_EstadoResultados` y `rpt.vw_SaldoBalance`: hoy filtran por `ClasePuc IN (...)` fijo. Cambio: resolver la clase lógica (INGRESO, GASTO, COSTO, ACTIVO, PASIVO, PATRIMONIO) desde la homologación y caer al PUC comercial si no hay. Es el cambio más sensible del plan: afecta a todos los clientes → pruebas de regresión completas de `DashboardsTests` y cuadre idéntico para una empresa comercial antes y después.

### Fase 5 · Simulador de categorización de riesgo (bloqueado por P6)
- Fórmulas y umbrales **cargados como datos versionados** (`reg.FormulaRiesgo`, `reg.UmbralRiesgo` con norma y vigencia), nunca en código. Las resoluciones citadas en el contexto (2509/2012, 2249/2018, 1122/2025, 1020/2026) están «a validar».
- `IMetricaCalculada` que evalúa las variables y devuelve categoría, valor de cada variable y cuál la mueve.
- Aviso fijo: «simulación; la categorización oficial la emite el Ministerio».

## Archivos por capa (fases 1–3)
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1160_ese.sql` (+ uno por fase posterior) |
| Domain | `Modulos/IModuloAnalitico.cs` (`AcumuladoAnual`), `Modulos/Dimensiones.cs` |
| Application | `Ingesta/Entidades.cs` |
| Application | `Semantica/Consultas.cs` (`PeriodoAnteriorAsync`) |
| Infrastructure | `Ingesta/DefinicionesCatalogo.cs` (solo `fuentes-financiacion`), `Ingesta/Procesamiento.cs` (`case` de rubros y de `presupuesto-publico`), `Semantica/MotorMetricas.cs` |
| Módulos | `Modulos.Ese/ModuloEse.cs`, `Modulos.Auditor/Reglas.cs` |
| Hosts y pruebas | las siete llamadas a `AddCapaSemantica` |
| Pruebas | `EseTests.cs`, `DashboardsTests.cs`, `AuditorTests.cs`, `SemanticaYReportesTests.cs`, `Domain.Tests/CatalogoAnaliticoTests.cs` (`AcumuladoAnual`), aislamiento |

Símbolos para `impact`: `DefinicionMetrica`, `MotorMetricas.ArmarSql`, `CatalogoAnalitico` (validación de métricas), `ObtenerDatosWidgetHandler`, `DefinicionesCatalogo`, `ProcesadorCargas.AplicarAsync`, `ModuloEse`, `CatalogoEntidades`, `AddCapaSemantica`, y en fase 4 `rpt.vw_EstadoResultados`/`rpt.vw_SaldoBalance` (texto: buscar todas las métricas que las usan).

## Pruebas
- Cadena presupuestal a mano: apropiación 1.000, adición 200, CDP 900, RP 800, obligación 600, pago 500 → definitiva 1.200, por comprometer 400, cuentas por pagar 100, ejecución 66,7 %.
- Reversión con signo negativo reduce la etapa.
- Vigencias: el acumulado de 2027 no arrastra 2026; sin `PeriodoHasta` toma la última vigencia con datos; una métrica con `Acumulado` y `AcumuladoAnual` a la vez impide arrancar.
- Rubros: el mismo código en dos vigencias son dos filas; recargar el catálogo no borra rubros con movimientos.
- Carga de junio con ventana desde el 1 de enero: la apropiación inicial se reemplaza (un cambio de 1.000 a 1.100 se refleja) y no hay rechazos `FUERA_DE_VENTANA`.
- El catálogo arranca con el módulo (`ese_presupuesto.*`, `dash.ese_presupuesto`).
- Fase 4: una empresa comercial da exactamente las mismas cifras antes y después del cambio.
- Aislamiento por todas las vistas; reglas ESE no se evalúan sin el módulo.

## Riesgos
- **Sin datos para cuadrar.** La estructura y las reglas salen del código de SaludIPS, pero las tablas están vacías en la instalación revisada: ninguna cifra se muestra a la ESE antes del cuadre de la fase 0.2. Las pruebas de integración usan el caso a mano de abajo.
- Reservas presupuestales y vigencias expiradas: no modeladas (no hay etapa); se agregan si la fase 0 muestra que la ESE las usa.
- Licencia de QuestPDF con cliente público: confirmar que la restricción «sector público no califica» de D33 se refiere al licenciatario y no al cliente final antes de entregar PDF a una ESE.
- Contratación con una entidad pública: revisar con el abogado (P5) antes de ofertar.
