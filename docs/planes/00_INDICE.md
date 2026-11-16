# Planes de implementación — brechas del analista financiero

Estado: **diseñados, NO implementados.** No se implementa nada hasta orden explícita del dueño. Fecha: 2026-11-16.
Diseño según D30; implementación con Sonnet, un plan = una rama = un PR. Un cambio de diseño durante la implementación vuelve a diseño.

## Hallazgos del análisis que cambian el alcance
1. **Las tablas de hechos ya existen** (`fact.TesoreriaMovimiento`, `fact.ConciliacionBancaria`, `fact.Glosa`, `fact.AdmisionPendienteFactura`, `fact.Presupuesto`, `etl.PeriodoCierre`, `cfg.KpiMeta`). Falta el procesador, las vistas, las métricas y la pantalla. No hay DDL de tablas grandes por crear.
2. **El flujo de caja histórico y el recaudo de cartera salen de `asientos`, que ya se ingieren.** La extracción de `recibos` (`docs/06`) solo trae `mRecibos` (caja: copagos, particulares); los pagos de EPS entran por contabilidad. Por eso PL-01 no depende del conector.
3. **`rpt.vw_CicloIngreso` depende de `fact.Prestacion`**, que no se ingiere. «Dinero detenido por etapa» se rediseña sobre cartera + facturas + foto de pendientes (PL-03).
4. **`dim.MotivoGlosa` no tiene semillas** y es global: «glosas por causa» necesita guardar el código de origen (PL-02).
5. **`EntidadIngesta.ModuloRequerido` existe pero nadie lo verifica** (PL-02 lo hace cumplir).
6. **`cfg.KpiMeta` se lee (semáforo) pero no hay pantalla para escribirla**; las notificaciones con `UsuarioId NULL` no se cuentan en la campana (PL-04).
7. **Cerrar un periodo en la plataforma puede tumbar la carga nocturna**: la ventana del conector se calcula con los cierres de SaludIPS, no con los de IAnalitica → `409 PERIODO_CERRADO` (PL-05, requiere decisión).
8. **Los abonos sin cruzar desaparecen de la cartera**: `etl.usp_GenerarCorteCartera` descarta líneas con `ValorOriginal = 0` (un crédito en una cuenta CxC de naturaleza débito). Hoy solo se ven como diferencia en la conciliación (PL-09).
9. El comparativo contra el periodo anterior **ya existe** en los KPI; falta año anterior y acumulado del año (PL-07).

## Planes y orden
| # | Plan | Depende de | Script DbUp | Conector | Bloqueado por |
|---|---|---|---|---|---|
| PL-01 | Flujo de caja, recaudo de cartera, caja y conciliación bancaria | — | 1050 | Parte B y C | DP-1 |
| PL-02 | Glosas | — | 1060 | Sí | DP-2 |
| PL-03 | Atendido no facturado y dinero detenido por etapa | PL-02 (opcional) | 1070 | Sí (foto) | DP-3 |
| PL-04 | Metas de KPI y alertas | — | 1080 | No | DP-4 |
| PL-05 | Cierre de periodo | PL-04 | 1085 | Sí (ventana) | DP-5 |
| PL-06 | Presupuesto vs. real | — | 1090 | No | DP-4, DP-6 |
| PL-07 | Comparativos: año anterior y acumulado | — | — | No | — |
| PL-08 | Deterioro estimado de cartera | — | 1100 | No | DP-7 |
| PL-09 | Recaudo sin aplicar (anticipos y giros sin cruzar) | PL-01 | 1110 | No | DP-8, datos reales |

Orden sugerido: PL-07 → PL-01 (parte A) → PL-04 → PL-02 → PL-03 → PL-06 → PL-01 (B, C) → PL-08 → PL-05 → PL-09.
Razón: primero lo que da valor con datos ya ingeridos y sin decisiones pendientes; PL-05 y PL-09 al final por riesgo.
El número 1042 queda reservado para D50 (excedente IA). Un script por plan; si un plan necesita otro, usa el siguiente número libre de su decena.

## Decisiones del dueño que bloquean (no inventar la respuesta)
| # | Decisión | Propuesta | Bloquea |
|---|---|---|---|
| DP-1 | ¿Qué cuentas son «disponible»? | `dim.CuentaContable.EsBanco = 1` o grupo 11 del PUC comercial `[S]`; ESE (plan CGN) por validar | PL-01 A |
| DP-2 | Codificación de motivos de glosa que usa cada piloto y norma vigente (Res. 3047/2008 anexo 6 vs. Res. 2284/2023: **verificar**) | Guardar el código de origen; clasificar después | PL-02 (solo la clasificación) |
| DP-3 | Retención de fotos diarias de pendientes | Diarias 35 días + última de cada mes | PL-03 |
| DP-4 | Permisos nuevos `metas.configurar` y `presupuesto.cargar` (amplía P10) o reutilizar `admin.usuarios` como hace `/costos` | Reutilizar `admin.usuarios` `[S]` hasta cerrar P10 | PL-04, PL-06 |
| DP-5 | Quién cierra periodos y qué pasa con la ventana del conector | Ver PL-05 §Riesgo | PL-05 |
| DP-6 | Formato de carga del presupuesto por portal | CSV (sin paquetes nuevos); Excel después | PL-06 |
| DP-7 | Política de deterioro (porcentaje por edad vs. pérdida esperada) y quién la parametriza | Porcentaje por rango de edad, por empresa, etiquetado «estimado, no contable» | PL-08 |
| DP-8 | Cómo registra SaludIPS anticipos y giros sin identificar (cuenta, relación) | Validar con equipo funcional y datos reales | PL-09 |

## Reglas para quien implemente (además de `01` §0)
- Antes de editar cualquier símbolo: `impact` de GitNexus (upstream) y reportar riesgo; HIGH/CRITICAL se avisa antes de tocar. Antes de commit: `detect-changes --scope all`. Cada plan lista los símbolos a analizar.
- Nunca editar un script DbUp aplicado: script nuevo con el número indicado. Vistas con `CREATE OR ALTER` **sin quitar columnas**.
- Procedimientos `etl.usp_Procesar*`: copiar el patrón de `etl.usp_ProcesarFacturas` (`0160_ingesta.sql`): tabla temporal con `COLLATE DATABASE_DEFAULT`, validación por fila con primera causa, `etl.Rechazo` sin contenido, `#ok`, una transacción, salidas `@Ins/@Upd/@Del/@Rech`.
- Toda vista nueva expone `EmpresaId` (el motor filtra por empresa además de la RLS) y se llama `rpt.vw_*` (lista blanca por patrón en `CatalogoAnalitico`).
- Una métrica = una vista. Razones entre dos hechos se resuelven con una vista `UNION ALL` al mismo grano.
- Definición de terminado de `01` §0.7 completa: sin advertencias, prueba unitaria, **prueba de aislamiento por empresa**, integración con Testcontainers, métrica financiera cuadrada contra un caso conocido, `[S]` listados en el PR, docs de `contexto/` actualizadas (02 §5 entidades aceptadas, 03 §6 estado, 06 §10 pendientes).
- Ningún recurso pago ni paquete NuGet nuevo en estos planes. Si aparece la necesidad, parar (D32).

## Fuera de estos planes (y por qué)
- **Cuadre con datos reales (P1, P2):** no es código; sigue siendo lo primero para mostrar cifras a un cliente.
- **Ingesta de `nomina`, `prestaciones`, `inventario`, `TipoCC`, bases manuales:** ya es el pendiente técnico 2–3; merece su propio diseño.
- **Rentabilidad por contrato de capitación/PGP:** el contrato de ingesta no trae población afiliada y `CostoDirecto` es `[S]` (P2).
- **Conciliación de cartera con el saldo que reconoce la EPS (Circular 030 de 2013, por verificar):** no hay fuente del saldo del pagador.
- **Flujo de caja proyectado a 13 semanas:** esbozado en PL-01 §Fase siguiente; necesita patrón de recaudo real para calibrar.
