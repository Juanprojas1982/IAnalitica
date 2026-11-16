# PL-14 · Paquete ESE (sector público)

Estado: diseñado, no implementado. Módulo `ESE_PRESUPUESTO` (permiso `dash.ese_presupuesto`, sembrado). Script `1160_ese.sql`.
Preparación (2026-11-16): **fases 1 a 3 listas para implementar.** El origen se verificó en el código de SaludIPS (repo `SaludIPS2`, commit `0e05edb`): ver «Origen verificado». El cuadre con datos reales de la ESE es criterio de aceptación, no bloqueo. Fase 4 espera la validación del plan de cuentas con la ESE; fase 5, P6.
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
- Catálogos nuevos (`DefinicionesCatalogo`, modo `COMPLETA`): `rubros-presupuestales` (vigencia, codigo, nombre, tipo I/G, naturaleza, codigoCuipo, permiteMovimientos, codigoPadre) y `fuentes-financiacion`.
- Hechos: `presupuesto-publico` (`VENTANA` por `fecha`, suma de control `valor`, `ModuloRequerido: "ESE_PRESUPUESTO"`; requiere la verificación de PL-02 §1; **sí** pasa por el control de periodo cerrado, son hechos ejecutados) → `fact.PresupuestoMovimiento`; `contratos-publicos` (`COMPLETA`/`INCREMENTAL`) → `fact.ContratoPublico`.
- `etl.usp_ProcesarPresupuestoPublico`: `etapa` ∈ lista del `CHECK`; rubro obligatorio y existente en la vigencia (`REFERENCIA_INEXISTENTE:rubroCodigo`); rubro sin `PermiteMovimientos` → `VALOR_INVALIDO:rubroCodigo`; etapa de ingreso sobre rubro de gasto (y al revés) → `VALORES_INVALIDOS`. `Valor` admite negativos (reversiones).
- Nota: la spec (`03` §22) habla de `fact.PresupuestoGasto` y `fact.PresupuestoIngreso`; el DDL real es una sola tabla. **Manda el DDL.** Las etapas `TRASLADO_CRED/CONTRA` de la spec son `CREDITO/CONTRACREDITO` en el DDL.

### Fase 2 · Ejecución presupuestal
- `rpt.vw_EjecucionGasto` por vigencia, periodo, rubro y fuente: `ApropiacionInicial, Adiciones, Reducciones, Creditos, Contracreditos, ApropiacionDefinitiva, Cdp, Rp, Obligaciones, Pagos, SaldoPorComprometer` (definitiva − RP), `SaldoPorObligar`, `CuentasPorPagar` (obligaciones − pagos: definición presupuestal) y `CompromisosPorPagar` (RP − pagos: es lo que el reporte de SaludIPS llama «cuentas por pagar» y contra lo que se cuadra). El % de ejecución es RP ÷ definitiva, como en SaludIPS; la variante por CDP se expone como métrica aparte (`ese.pct_ejecucion_cdp`).
- `rpt.vw_EjecucionIngreso`: `AforoInicial, Adiciones, Reducciones, AforoDefinitivo, Reconocimientos, Recaudos, PorRecaudar`.
- `rpt.vw_EquilibrioPresupuestal`: ingresos recaudados y reconocidos vs. gastos comprometidos y obligados, por vigencia y periodo acumulado.
- Jerarquía de rubros: dimensión `rubro` (`dim.RubroPresupuestal`) y `rubro_nivel1` (primer segmento del código) para no listar miles de rubros.
- `ModuloEsePresupuesto`: métricas `ese.apropiacion_definitiva`, `ese.compromisos`, `ese.obligaciones`, `ese.pagos`, `ese.pct_ejecucion_gasto` (`KpiMeta`), `ese.aforo_definitivo`, `ese.reconocimientos`, `ese.recaudos`, `ese.pct_recaudo` (`KpiMeta`), `ese.cuentas_por_pagar`, `ese.equilibrio_recaudo_compromiso`. Todas `Acumulado: true` dentro de la vigencia → **el motor acumula desde el origen, no desde enero**: hace falta soporte de «acumulado por vigencia» (filtro fijo por vigencia de `PeriodoHasta`). Es un cambio del motor (`MotorMetricas.ArmarSql`): diseñarlo como bandera `AcumuladoAnual` en `DefinicionMetrica` (con `Acumulado` y `AcumuladoAnual` excluyentes, validado en `CatalogoAnalitico`): el motor ignora `PeriodoDesde` y agrega `periodo >= año de PeriodoHasta × 100 + 1`. PL-07 no la usa ni la necesita (resuelve su acumulado en el handler); los dos planes son independientes.
- Dashboards: `ese.ejecucion_gastos`, `ese.ejecucion_ingresos`, `ese.equilibrio`, `ese.cuentas_por_pagar`.

### Fase 3 · Contratación y calidad del reporte
- `ese.contratacion`: contratos por estado, ejecución vs. valor, pólizas por vencer (30/60 días), adiciones sobre el valor inicial. **Los umbrales legales de adición se definen con jurídico**; hasta entonces solo se muestra el porcentaje, sin semáforo.
- Reglas del auditor (una clase cada una, mismo motor, activas solo con el módulo): ESE-01 rubro sin CUIPO o sin fuente · ESE-02 cuenta sin `RubroFuenteId` donde la integración lo exige · ESE-03 comprobante con funcionalidad presupuestal sin tipo de comprobante presupuestal · ESE-04 EPS sin rubro de ingreso · ESE-05 presupuesto del año siguiente incompleto desde noviembre. ESE-02 a ESE-04 necesitan tablas en la foto `configuracion` (ampliar la lista blanca de `03` §21 y regenerar `saludips.*` con `generar-saludips.py` en un script nuevo).
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
| Infrastructure | `Ingesta/DefinicionesCatalogo.cs`, `Ingesta/Procesamiento.cs`, `Semantica/MotorMetricas.cs` |
| Módulos | `Modulos.Ese/ModuloEse.cs`, `Modulos.Auditor/Reglas.cs` |
| Pruebas | `EseTests.cs`, `DashboardsTests.cs`, `AuditorTests.cs`, `SemanticaYReportesTests.cs`, aislamiento |

Símbolos para `impact`: `DefinicionMetrica`, `MotorMetricas.ArmarSql`, `CatalogoAnalitico` (validación de métricas), `DefinicionesCatalogo`, `ModuloEse`, `CatalogoEntidades`, y en fase 4 `rpt.vw_EstadoResultados`/`rpt.vw_SaldoBalance` (texto: buscar todas las métricas que las usan).

## Pruebas
- Cadena presupuestal a mano: apropiación 1.000, adición 200, CDP 900, RP 800, obligación 600, pago 500 → definitiva 1.200, por comprometer 400, cuentas por pagar 100, ejecución 66,7 %.
- Reversión con signo negativo reduce la etapa.
- Vigencias: el acumulado de 2027 no arrastra 2026.
- Fase 4: una empresa comercial da exactamente las mismas cifras antes y después del cambio.
- Aislamiento por todas las vistas; reglas ESE no se evalúan sin el módulo.

## Riesgos
- **Sin datos para cuadrar.** La estructura y las reglas salen del código de SaludIPS, pero las tablas están vacías en la instalación revisada: ninguna cifra se muestra a la ESE antes del cuadre de la fase 0.2. Las pruebas de integración usan el caso a mano de abajo.
- Reservas presupuestales y vigencias expiradas: no modeladas (no hay etapa); se agregan si la fase 0 muestra que la ESE las usa.
- Licencia de QuestPDF con cliente público: confirmar que la restricción «sector público no califica» de D33 se refiere al licenciatario y no al cliente final antes de entregar PDF a una ESE.
- Contratación con una entidad pública: revisar con el abogado (P5) antes de ofertar.
