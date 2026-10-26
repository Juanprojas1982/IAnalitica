# Spec: Paquete ESE / sector público (IAnalitica)

Estado: diseño (Opus). Implementación: Sonnet. Piloto: la ESE con años de relación (D04).
**Corrección:** en documentos anteriores (`03`, `02`) el presupuesto público quedó «fuera del alcance gerencial». **Para una ESE es lo contrario: el presupuesto es la herramienta central de gestión y de control externo.**

## 1. Marco (validar con la ESE y su revisor)
| Tema | Referencia encontrada | Implicación |
|---|---|---|
| **Categorización del riesgo fiscal y financiero de las ESE** | MinSalud la hace cada año con la metodología de la Res. 2509 de 2012, modificada por la Res. 2249 de 2018. La Res. 1122 de 2025 usó datos a dic‑2024 reportados por SIHO hasta el 30‑abr‑2025; la **Res. 1020 de 2026** usó datos a dic‑2025. **Las ESE que no reportan a tiempo o con inconsistencias quedan en riesgo alto automáticamente** | Simular el indicador de riesgo **durante el año**, no enterarse en mayo. Controlar la calidad y oportunidad del reporte |
| Reporte SIHO | Decreto 2193 de 2004. SaludIPS ya tiene el reporte `RelacionCompromisos2193` | Completitud y consistencia antes del cierre |
| CUIPO | SaludIPS tiene `CUIPO01` a `CUIPO05` y los rubros con códigos CUIPO | Programación y ejecución de ingresos y gastos con la clasificación oficial |
| Contabilidad pública | Plan de cuentas de la Contaduría General (no el PUC privado). Por confirmar el marco normativo que aplica la ESE | Homologación de cuentas propia del paquete ESE |

## 2. Datos en SaludIPS (esquema `Presupuesto`, verificado en el diccionario)
`RubrosPresupuestales` (con códigos CUIPO, fuente de financiación y vigencia), `RubrosFuentes`, `Movimientos` y `MovimientosDetalles` (apropiación, adiciones, reducciones y traslados; **[S]** valores de `TipoMovimiento`), `CDPs`, `RPs`, `Obligaciones` y `Pagos`, cada uno con sus `*Imputaciones` (valor por rubro); `Reconocimientos`, `IngresosContables`, `IngresosTesoreria` (recaudos) y sus imputaciones; `Notas*` (reversiones y ajustes); `Contrataciones` (contratos, adiciones, prórrogas y pólizas); `ReversionesReconocimientos`.
En la instalación revisada estas tablas estaban vacías porque es otra institución. **Las columnas de las tablas `*Imputaciones` se verifican al implementar.**

## 3. Hechos nuevos (esquema `fact`, módulo ESE)
| Hecho | Grano | Origen |
|---|---|---|
| `fact.PresupuestoGasto` | rubro × fuente × fecha × etapa (APROPIACION_INICIAL, ADICION, REDUCCION, TRASLADO_CRED, TRASLADO_CONTRA, CDP, RP, OBLIGACION, PAGO, y reversiones con signo) | Movimientos, CDPs, RPs, Obligaciones, Pagos e imputaciones |
| `fact.PresupuestoIngreso` | rubro × fuente × fecha × etapa (AFORO_INICIAL, ADICION, REDUCCION, RECONOCIMIENTO, RECAUDO) | Movimientos, Reconocimientos, IngresosTesoreria e imputaciones |
| `dim.RubroPresupuestal` | rubro por vigencia (código, nombre, CUIPO, sección, tipo ingreso/gasto, funcionamiento/operación/inversión) | `RubrosPresupuestales` |
| `dim.FuenteFinanciacion` | fuente | `SIIFNet.FuentesFinanciaciones` |
| `fact.ContratoPublico` | contrato, adiciones, prórrogas, pólizas, valor comprometido | `Presupuesto.Contrataciones*` |

## 4. Dashboards ESE
1. **Ejecución de gastos:** apropiación definitiva, CDP, compromisos (RP), obligaciones, pagos, saldo por comprometer y por pagar, por rubro, fuente y mes. Alertas de rubros con déficit o sin ejecución.
2. **Ejecución de ingresos:** aforo definitivo, reconocimientos (facturado a EPS y otros), recaudos, y la brecha reconocido frente a recaudado por EPS y fuente.
3. **Equilibrio presupuestal:** recaudo frente a compromisos, reconocimiento frente a compromisos y proyección al cierre de la vigencia.
4. **Simulador de categorización de riesgo:** cálculo durante el año de los indicadores de la metodología oficial (fórmulas y umbrales **por cargar desde la Res. 2509 de 2012 y la 2249 de 2018**, versionados), semáforo proyectado y qué variable lo mueve.
5. **Cuentas por pagar presupuestales y reservas:** antigüedad y riesgo de vigencias expiradas.
6. **Calidad del reporte:** completitud de SIHO y CUIPO antes de los plazos, consistencias presupuesto-contabilidad-tesorería (un recaudo en tesorería sin reconocimiento, un pago sin obligación).
7. **Contratación:** contratos por estado, ejecución frente a valor, pólizas por vencer y adiciones que superan topes (umbrales **por confirmar** con jurídico).
8. Todos los dashboards de una IPS (ciclo del ingreso, cartera con EPS, agenda, costos), que aplican igual a la ESE.

## 5. Auditor de configuración: reglas propias de la ESE
| Código | Regla |
|---|---|
| ESE‑01 | Rubros de la vigencia **sin código CUIPO** o sin fuente de financiación |
| ESE‑02 | Cuenta contable con `RubroFuenteId` vacío donde la integración presupuesto-contabilidad lo exige |
| ESE‑03 | Tipos de comprobante con `FuncionalidadPresupuestal` sin su `TipoComprobantePresupuestalId` |
| ESE‑04 | Pagadores (EPS) sin rubro presupuestal de ingreso configurado (`mEmpresasRubrosPresupuestales`) |
| ESE‑05 | Configuración presupuestal del **año siguiente** incompleta desde noviembre |

## 6. Pendientes para la ESE piloto
1. El marco contable que aplica y la homologación de su plan de cuentas.
2. Las fórmulas exactas de la categorización (Res. 2509 de 2012 y 2249 de 2018), validadas con su jefe financiero.
3. Los tipos de movimiento presupuestal que usa en SaludIPS.
4. Los informes que hoy le exigen la junta, la Secretaría de Salud y los entes de control, para priorizar.
