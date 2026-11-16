# Planes de implementación — producto completo antes del primer piloto

Estado: **diseñados, NO implementados.** No se implementa nada hasta orden explícita del dueño. Fecha: 2026-11-16.
Alcance (decisión del dueño, 2026-11-16): como aún no hay ningún piloto en operación, entra **todo lo que falta**, incluido el sector público (ESE). PL-01 a PL-09 cubren al analista financiero; PL-10 a PL-17, el resto de lo que el producto promete.
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
| PL-10 | Ingesta operativa (prestaciones, nómina, inventario) y costos confiables | — | 1120 | Sí | P2 |
| PL-11 | Agendas y oportunidad | PL-10 (solo costo ocioso) | 1130 | Sí | — |
| PL-12 | Productividad y apoyo diagnóstico | PL-10 | 1140 | Sí | DP-10 |
| PL-13 | Farmacia, inventario y validador de precios completo | PL-10 | 1150 | Sí | Confirmación regulatoria, P9 |
| PL-14 | Paquete ESE (presupuesto público, plan de cuentas público, riesgo) | PL-07 (motor) | 1160 | Sí | Fase 0 con la ESE, P6 |
| PL-15 | Grupos de instituciones (vista consolidada) | PL-04 (sesión de sistema) | 1170 | No | DP-9 |
| PL-16 | Asistente IA: proveedor real y salida a clientes | PL-04 (alertas de cupo) | 1180, 1042 | No | D32, P11, P5 |
| PL-17 | Auditor de configuración, fase 2 | — | 1190 | Parcial | — |

### Orden sugerido (por oleadas)
1. **Sin conector ni decisiones:** PL-07 → PL-01 (parte A) → PL-17 (lotes 1 y 3) → PL-04.
2. **Ciclo del ingreso completo:** PL-02 → PL-03 → PL-06 → PL-01 (B, C).
3. **Operación:** PL-10 → PL-11 → PL-12.
4. **Por piloto:** PL-13 (operador farmacéutico) y PL-14 (ESE), en paralelo, cada uno tras su levantamiento.
5. **Riesgo alto o bloqueados:** PL-16 → PL-08 → PL-05 → PL-15 → PL-09.

Razón: primero lo que da valor con datos ya ingeridos; después lo que exige ampliar el conector; al final lo que cambia seguridad, cuesta dinero o depende de datos reales.

### Advertencia de alcance
Son 17 planes. El riesgo «alcance creciente» (`06` §11) deja de ser teórico: ninguna fase se ha cerrado con datos reales y F6 sigue bloqueada por P1. **Incluir todo en el plan no es construir todo antes de salir.** Recomendación: fijar un corte de salida por piloto (qué planes necesita cada uno) y dejar el resto en cola.
| Piloto | Mínimo para salir |
|---|---|
| IPS privada | Lo ya construido + PL-07, PL-01 A, PL-02, PL-03, PL-04 |
| Operador farmacéutico | Lo ya construido + PL-10 (inventario), PL-13 A y B |
| ESE | Lo ya construido + PL-14 fases 0–2 y 4 |
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
| DP-9 | Modelo de acceso para grupos (contradice D11) | Opción A de PL-15: el usuario sigue en una empresa; un servicio consulta empresa por empresa; la RLS no cambia | PL-15 |
| DP-10 | Umbral mínimo de empleados para mostrar costo de personal por cargo, y si se publica productividad individual de administrativos | 3 empleados; productividad individual fuera | PL-12 |
| DP-11 | Corte de salida por piloto (ver «Advertencia de alcance») | La tabla de arriba | Orden de todo |

## Reglas para quien implemente (además de `01` §0)
- Antes de editar cualquier símbolo: `impact` de GitNexus (upstream) y reportar riesgo; HIGH/CRITICAL se avisa antes de tocar. Antes de commit: `detect-changes --scope all`. Cada plan lista los símbolos a analizar.
- Nunca editar un script DbUp aplicado: script nuevo con el número indicado. Vistas con `CREATE OR ALTER` **sin quitar columnas**.
- Procedimientos `etl.usp_Procesar*`: copiar el patrón de `etl.usp_ProcesarFacturas` (`0160_ingesta.sql`): tabla temporal con `COLLATE DATABASE_DEFAULT`, validación por fila con primera causa, `etl.Rechazo` sin contenido, `#ok`, una transacción, salidas `@Ins/@Upd/@Del/@Rech`.
- Toda vista nueva expone `EmpresaId` (el motor filtra por empresa además de la RLS) y se llama `rpt.vw_*` (lista blanca por patrón en `CatalogoAnalitico`).
- Una métrica = una vista. Razones entre dos hechos se resuelven con una vista `UNION ALL` al mismo grano.
- Definición de terminado de `01` §0.7 completa: sin advertencias, prueba unitaria, **prueba de aislamiento por empresa**, integración con Testcontainers, métrica financiera cuadrada contra un caso conocido, `[S]` listados en el PR, docs de `contexto/` actualizadas (02 §5 entidades aceptadas, 03 §6 estado, 06 §10 pendientes).
- Ningún recurso pago ni paquete NuGet nuevo en estos planes. Si aparece la necesidad, parar (D32).

## Fuera de estos planes (y por qué)
- **Cuadre con datos reales (P1, P2) y despliegue (F6):** no es diseño; sigue siendo lo primero para mostrar cifras a un cliente.
- **Rentabilidad por contrato de capitación/PGP:** el contrato de ingesta no trae población afiliada.
- **Conciliación de cartera con el saldo que reconoce la EPS (Circular 030 de 2013, por verificar):** no hay fuente del saldo del pagador.
- **Flujo de caja proyectado a 13 semanas:** esbozado en PL-01; necesita patrón de recaudo real para calibrar.
- **Hospitalario y quirúrgico (camas, triage, quirófano), benchmarking entre IPS, pronóstico:** fase 3 de la spec; la instalación revisada no tiene esos datos.
- **MIPRES y SISMED:** sus plazos y emisores están «por verificar» en el propio contexto (PL-13).
- **Conector para un HIS distinto de SaludIPS:** existe el contrato canónico; un conector nuevo depende de cada sistema.
- **Editor de dashboards (`cfg.Dashboard`), comentarios sobre datos, informe gerencial programado:** fase 2 de la capa semántica; sin plan todavía.
