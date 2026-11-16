# PL-14 · Paquete ESE (sector público)

Estado: diseñado, no implementado. Módulo `ESE_PRESUPUESTO` (permiso `dash.ese_presupuesto`, sembrado). Script `1160_ese.sql`.
Es el paquete de la **ESE piloto**. Decisión del dueño (2026-11-16): el sector público entra al alcance porque aún no se ha salido con ningún piloto.

## Objetivo
Dar a una ESE lo que usa para gestionarse y rendir cuentas: ejecución presupuestal de gastos e ingresos, equilibrio y proyección, cuentas por pagar presupuestales, contratación, calidad del reporte y —al final— simulación de la categorización de riesgo. Además, que los tableros financieros funcionen con su plan de cuentas.

## Situación actual
- `fact.PresupuestoMovimiento` (una tabla con `Etapa`), `fact.ContratoPublico`, `dim.RubroPresupuestal`, `dim.FuenteFinanciacion` existen (`0040`, `0070`). `ModuloEse.cs` vacío.
- **No hay extracción escrita** para el esquema `Presupuesto` de SaludIPS (`docs/06` y `docs/11` no lo cubren) y esas tablas están **vacías en la instalación revisada**: las columnas de `*Imputaciones` y los valores de `TipoMovimiento` son `[S]`.
- Balance y P&G usan reglas del PUC comercial; el plan de la Contaduría (CGN) está «por validar».
- Fórmulas de categorización de riesgo: pendiente P6 (la ESE piloto debe validarlas).

## Fases (cada una es un PR)
### Fase 0 · Levantamiento con la ESE piloto (sin código de producto)
1. Obtener una copia anonimizada o acceso de lectura a una base con datos presupuestales reales.
2. Confirmar: tipos de movimiento presupuestal, estructura de imputaciones, marco contable, informes que exigen junta, Secretaría de Salud y entes de control (los cuatro «pendientes con la ESE» de `03` §22).
3. Escribir `docs/12_extraccion_presupuesto_saludips.sql` (NO ejecutado → ejecutado y cuadrado contra `CUIPO01`–`CUIPO05` y `RelacionCompromisos2193`).
**Sin la fase 0 no se implementa la fase 1**: el diseño de abajo es el esperado y puede cambiar.

### Fase 1 · Ingesta
- Catálogos nuevos (`DefinicionesCatalogo`, modo `COMPLETA`): `rubros-presupuestales` (vigencia, codigo, nombre, tipo I/G, naturaleza, codigoCuipo, permiteMovimientos, codigoPadre) y `fuentes-financiacion`.
- Hechos: `presupuesto-publico` (`VENTANA` por `fecha`, suma de control `valor`, `ModuloRequerido: "ESE_PRESUPUESTO"`; requiere la verificación de PL-02 §1; **sí** pasa por el control de periodo cerrado, son hechos ejecutados) → `fact.PresupuestoMovimiento`; `contratos-publicos` (`COMPLETA`/`INCREMENTAL`) → `fact.ContratoPublico`.
- `etl.usp_ProcesarPresupuestoPublico`: `etapa` ∈ lista del `CHECK`; rubro obligatorio y existente en la vigencia (`REFERENCIA_INEXISTENTE:rubroCodigo`); rubro sin `PermiteMovimientos` → `VALOR_INVALIDO:rubroCodigo`; etapa de ingreso sobre rubro de gasto (y al revés) → `VALORES_INVALIDOS`. `Valor` admite negativos (reversiones).
- Nota: la spec (`03` §22) habla de `fact.PresupuestoGasto` y `fact.PresupuestoIngreso`; el DDL real es una sola tabla. **Manda el DDL.** Las etapas `TRASLADO_CRED/CONTRA` de la spec son `CREDITO/CONTRACREDITO` en el DDL.

### Fase 2 · Ejecución presupuestal
- `rpt.vw_EjecucionGasto` por vigencia, periodo, rubro y fuente: `ApropiacionInicial, Adiciones, Reducciones, Creditos, Contracreditos, ApropiacionDefinitiva, Cdp, Rp, Obligaciones, Pagos, SaldoPorComprometer` (definitiva − RP), `SaldoPorObligar`, `CuentasPorPagar` (obligaciones − pagos).
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
- **El mayor: diseñar sin datos.** Tablas vacías en la instalación revisada; todo lo de imputaciones es `[S]`.
- Reservas presupuestales y vigencias expiradas: no modeladas (no hay etapa); se agregan si la fase 0 muestra que la ESE las usa.
- Licencia de QuestPDF con cliente público: confirmar que la restricción «sector público no califica» de D33 se refiere al licenciatario y no al cliente final antes de entregar PDF a una ESE.
- Contratación con una entidad pública: revisar con el abogado (P5) antes de ofertar.
