# Planes de implementación — producto completo antes del primer piloto

Estado: **diseñados y revisados contra el DDL y el código; decisiones del dueño registradas el 2026-11-16 (D54–D65 en `06` §12). NO implementados.** La columna «Listo» de la tabla dice qué puede empezar; la implementación de cada plan arranca con orden explícita del dueño. Fecha: 2026-11-16.
Alcance (decisión del dueño, 2026-11-16): como aún no hay ningún piloto en operación, entra **todo lo que falta**, incluido el sector público (ESE). PL-01 a PL-09 cubren al analista financiero; PL-10 a PL-17, el resto de lo que el producto promete.
Diseño según D30; implementación con Sonnet, un plan = una rama = un PR. Un cambio de diseño durante la implementación vuelve a diseño.

## Hallazgos del análisis que cambian el alcance
1. **Las tablas de hechos ya existen** (`fact.TesoreriaMovimiento`, `fact.ConciliacionBancaria`, `fact.Glosa`, `fact.AdmisionPendienteFactura`, `fact.Presupuesto`, `etl.PeriodoCierre`, `cfg.KpiMeta`). Falta el procesador, las vistas, las métricas y la pantalla. No hay DDL de tablas grandes por crear.
2. **El flujo de caja histórico y el recaudo de cartera salen de `asientos`, que ya se ingieren.** La extracción de `recibos` (`docs/06`) solo trae `mRecibos` (caja: copagos, particulares); los pagos de EPS entran por contabilidad. Por eso PL-01 no depende del conector.
3. **`rpt.vw_CicloIngreso` depende de `fact.Prestacion`**, que no se ingiere. «Dinero detenido por etapa» se rediseña sobre cartera + facturas + foto de pendientes (PL-03).
4. **`dim.MotivoGlosa` no tiene semillas** y es global: «glosas por causa» necesita guardar el código de origen (PL-02).
5. **`EntidadIngesta.ModuloRequerido` existe pero nadie lo verifica** (PL-02 lo hace cumplir).
6. **`cfg.KpiMeta` se lee (semáforo) pero no hay pantalla para escribirla**; las notificaciones con `UsuarioId NULL` no se cuentan en la campana (PL-04).
7. **Cerrar un periodo en la plataforma puede tumbar la carga nocturna**: la ventana del conector se calcula con los cierres de SaludIPS, no con los de IAnalitica → `409 PERIODO_CERRADO` (PL-05; resuelto con D57: cierre blando primero).
8. **Los abonos sin cruzar desaparecen de la cartera**: `etl.usp_GenerarCorteCartera` descarta líneas con `ValorOriginal = 0` (un crédito en una cuenta CxC de naturaleza débito). Hoy solo se ven como diferencia en la conciliación (PL-09).
9. El comparativo contra el periodo anterior **ya existe** en los KPI; falta año anterior y acumulado del año (PL-07).

## Planes y orden
| # | Plan | Depende de | Script DbUp | Conector | Listo | Qué falta |
|---|---|---|---|---|---|---|
| PL-01 | Flujo de caja, recaudo de cartera, caja y conciliación bancaria | PL-02 (solo B y C: verificación de módulo) | 1050 | Parte B y C | **Sí** | — |
| PL-02 | Glosas | — | 1060 | Sí | **Sí** | Clasificación de motivos (DP-2): el tablero sale con códigos crudos |
| PL-03 | Atendido no facturado y dinero detenido por etapa | PL-02 (opcional) | 1070 | Sí (foto) | **Sí** | — |
| PL-04 | Metas de KPI y alertas | — | 1080 | No | **Sí** | — |
| PL-05 | Cierre de periodo | PL-04 (trabajo `ALERTAS` y sesión de sistema) | 1085 | Fase 2 (ventana) | **Fase 1 sí** | Fase 2 (cierre duro): conector actualizado en la empresa |
| PL-06 | Presupuesto vs. real | — | 1090 | No | **Sí** | — |
| PL-07 | Comparativos: año anterior y acumulado | — | — | No | **Sí** | — |
| PL-08 | Deterioro estimado de cartera | PL-04 (versión de configuración en la caché) | 1100 | No | **Sí** | — |
| PL-09 | Recaudo sin aplicar (anticipos y giros sin cruzar) | PL-01 | 1110 | No | No | DP-8 y diagnóstico con datos reales (paso 0) |
| PL-10 | Ingesta operativa (prestaciones, nómina, inventario) y costos confiables | PL-02 (verificación de módulo, nómina) | 1120 | Sí | **Sí** | P2 solo impide usar `CostoInsumos` |
| PL-11 | Agendas y oportunidad | PL-10 (solo costo ocioso) | 1130 | Sí | **Sí** | — |
| PL-12 | Productividad y apoyo diagnóstico | PL-10, PL-11 (dimensiones `profesional`, `especialidad`) | 1140 | Sí | **Sí** | — |
| PL-13 | Farmacia, inventario y validador de precios completo | PL-10 | 1150 | Sí | **Parte A sí** | B: contrato de `ordenes-externas` (P9). C: confirmación regulatoria |
| PL-14 | Paquete ESE (presupuesto público, plan de cuentas público, riesgo) | PL-02 (verificación de módulo) | 1160 | Sí | No | Fase 0 con la ESE (datos reales); P6 para la fase 5 |
| PL-15 | Grupos de instituciones (vista consolidada) | PL-04 (sesión de sistema) | 1170 | No | **Sí** | Para dar de alta un grupo real: consentimiento contractual (P5) |
| PL-16 | Asistente IA: proveedor real y salida a clientes | PL-04 (alertas de cupo) | 1180, 1042 (D44) | No | No | D32 (costo y clave), P11, P5 |
| PL-17 | Auditor de configuración, fase 2 | — | 1190 | Parcial | **Lotes 1 y 3 sí** | Resto de lotes: medir falsos positivos con el piloto; reglas `[S]` nacen `PorVerificar` |

«Listo» significa que el diseño no tiene decisiones abiertas y se puede escribir y probar con Testcontainers. **No** significa que haya datos: las entidades que envía el conector (PL-01 B/C, PL-02, PL-03, PL-10, PL-11, PL-12) dependen de extracciones de `docs/06` y `docs/11` que no se han ejecutado contra una base real (P2).

### Orden sugerido (por oleadas)
1. **Sin conector:** PL-07 → PL-17 (lotes 1 y 3) → PL-01 parte A → PL-04.
2. **Ciclo del ingreso completo:** PL-02 → PL-03 → PL-06 → PL-01 (B, C).
3. **Operación:** PL-10 → PL-11 → PL-12 → PL-13 parte A.
4. **Configuración y seguridad:** PL-08 → PL-05 fase 1 → PL-15.
5. **Esperan algo externo:** PL-09 (datos reales), PL-13 B y C (contrato y regulatorio), PL-14 (fase 0 con la ESE), PL-16 (D32, P11), PL-05 fase 2 (conector), PL-17 lotes 2 y 4–6 (medición con piloto).

Razón: primero lo que da valor con datos ya ingeridos; después lo que exige ampliar el conector; al final lo que cambia seguridad, cuesta dinero o depende de datos reales.

### Advertencia de alcance
Son 17 planes. El riesgo «alcance creciente» (`06` §11) deja de ser teórico: ninguna fase se ha cerrado con datos reales y F6 sigue bloqueada por P1. **Incluir todo en el plan no es construir todo antes de salir.** Recomendación: fijar un corte de salida por piloto (qué planes necesita cada uno) y dejar el resto en cola.
| Piloto | Mínimo para salir |
|---|---|
| IPS privada | Lo ya construido + PL-07, PL-01 A, PL-02, PL-03, PL-04 |
| Operador farmacéutico | Lo ya construido + PL-10 (inventario), PL-13 A y B |
| ESE | Lo ya construido + PL-14 fases 0–2 y 4 |
El número 1042 está reservado para `1042_ia_periodo_colombia.sql` (D44, `08` §31.2): es el requisito previo del paso 10 de facturación (excedente IA, D50), no el paso 10 mismo; si el paso 10 necesita DDL propio usa 1043. Un script por plan; si un plan necesita otro, usa el siguiente número libre de su decena.

## Decisiones del dueño
Aprobadas el 2026-11-16 y registradas en `06` §12. Lo que una decisión no dice **no se inventa**: vuelve a diseño.
| # | Decisión | Resuelto | Registro |
|---|---|---|---|
| DP-1 | ¿Qué cuentas son «disponible»? | `dim.CuentaContable.EsBanco = 1` o grupo 11 del PUC comercial. Saldos iniciales (`TipoComprobante.Clase = 'SALDO_INICIAL'`) en columna propia, fuera de las entradas. ESE (plan CGN): sigue por validar en PL-14 | D54 |
| DP-3 | Retención de fotos diarias de pendientes | Diarias 35 días + la última de cada mes | D55 |
| DP-4 | Permiso para metas, presupuesto y deterioro | Reutilizar `admin.usuarios` hasta cerrar P10 | D56 |
| DP-5 | Cierre de periodo | **Blando ahora, duro después.** Fase 1: cierre blando (no bloquea la ingesta; guarda foto de los KPI y avisa si cambian). Fase 2: cierre duro por empresa, solo con su conector actualizado. Cierra quien tenga `admin.usuarios` | D57 |
| DP-6 | Formato de carga del presupuesto por portal | CSV; Excel después | D58 |
| DP-7 | Política de deterioro | Porcentaje por rango de edad y por empresa, con excepción por pagador; etiquetado «estimado, no contable». Aprueba el plan como alcance nuevo fuera de la spec | D59 |
| DP-9 | Modelo de acceso para grupos | Opción A: el usuario sigue en una empresa (D11 intacta), un servicio consulta empresa por empresa y la RLS no cambia. La membresía del grupo se lee con una consulta fija por la conexión técnica (`rol_etl`), única excepción y solo sobre `seg.GrupoEmpresa`/`seg.UsuarioGrupo` | D60 |
| DP-10 | Costo de personal por cargo | Umbral de 3 empleados con supresión complementaria; sin productividad individual de administrativos | D61 |
| DP-11 | Corte de salida por piloto | La tabla de «Advertencia de alcance» | D62 |
| DP-12 | Producción por profesional | Dentro de `AGENDA`; no se crea el módulo `PRODUCTIVIDAD` | D63 |
| DP-13 | Periodo que evalúa `ALERTAS` | Último mes completo; KPI por corte, sobre el último corte | D64 |
| — | `inventario` sin `ModuloRequerido` | Se recibe aunque la empresa no tenga `INVENTARIO` (alimenta el costeo); los tableros sí exigen el módulo | D65 |

### Siguen abiertas (no dependen solo del dueño)
| # | Qué falta | Quién | Bloquea |
|---|---|---|---|
| DP-2 | Codificación de motivos de glosa de cada piloto y norma vigente (Res. 3047/2008 anexo 6 vs. Res. 2284/2023: **verificar**) | Equipo funcional | Solo la clasificación de PL-02; el plan sale con el código de origen |
| DP-8 | Cómo registra SaludIPS anticipos y giros sin identificar | Equipo funcional + datos reales | PL-09 |
| D32, P11, P5 | Costo y clave del proveedor de IA, tarifa y cupo, documentos legales | Dueño, abogado | PL-16 |
| Fase 0 ESE, P6 | Datos presupuestales reales y fórmulas de riesgo | ESE piloto | PL-14 |
| P9 | Contrato de `ordenes-externas` | Equipo funcional | PL-13 B |
| Confirmación regulatoria | Circulares de precios, Res. 1604/2013, plazos | Área regulatoria | PL-13 C |

## Reglas para quien implemente (además de `01` §0)
- Antes de editar cualquier símbolo: `impact` de GitNexus (upstream) y reportar riesgo; HIGH/CRITICAL se avisa antes de tocar. Antes de commit: `detect-changes --scope all`. Cada plan lista los símbolos a analizar.
- Nunca editar un script DbUp aplicado: script nuevo con el número indicado. Vistas con `CREATE OR ALTER` **sin quitar columnas**.
- Procedimientos `etl.usp_Procesar*`: copiar el patrón de `etl.usp_ProcesarFacturas` (`0160_ingesta.sql`): tabla temporal con `COLLATE DATABASE_DEFAULT`, validación por fila con primera causa, `etl.Rechazo` sin contenido, `#ok`, una transacción, salidas `@Ins/@Upd/@Del/@Rech`.
- Toda vista nueva expone `EmpresaId` (el motor filtra por empresa además de la RLS) y se llama `rpt.vw_*` (lista blanca por patrón en `CatalogoAnalitico`).
- Una métrica = una vista. Razones entre dos hechos se resuelven con una vista `UNION ALL` al mismo grano.
- Definición de terminado de `01` §0.7 completa: sin advertencias, prueba unitaria, **prueba de aislamiento por empresa**, integración con Testcontainers, métrica financiera cuadrada contra un caso conocido, `[S]` listados en el PR, docs de `contexto/` actualizadas (02 §5 entidades aceptadas, 03 §6 estado, 06 §10 pendientes).
- Ningún recurso pago ni paquete NuGet nuevo en estos planes. Si aparece la necesidad, parar (D32).

## Mecanismos compartidos (se crean una vez; el plan que llegue primero los crea)
| Mecanismo | Lo crea | Lo usan | Definición |
|---|---|---|---|
| Verificación de `ModuloRequerido` en `IniciarCargaHandler` | PL-02 | PL-01 B/C, PL-10 (nómina), PL-14 | PL-02 §1 |
| Sesión de sistema por empresa en el worker | PL-04 | PL-15, PL-16 | PL-04 §2 «Sesión de sistema» |
| Versión de configuración en la clave de caché de widgets | PL-04 | PL-08 | PL-04 §1 |
| `EntidadIngesta.ExentaDeCierre` (la entidad no pasa por el control de periodo cerrado) | PL-06 o PL-11, el primero | PL-06 (`presupuesto`), PL-11 (`agenda-capacidad`) | Propiedad `bool ExentaDeCierre = false` al final del record; `IniciarCargaHandler` la consulta junto a `EsCatalogo`. El control **ya existe hoy** para todo hecho en `VENTANA`/`INICIAL`; no nace con PL-05 |
| Bandera `AcumuladoAnual` en `DefinicionMetrica` | PL-14 fase 2 | Solo PL-14 | PL-07 **no** la usa: resuelve el acumulado del año en el handler |

Si un plan se implementa antes que el que crea su mecanismo, lo crea él con la misma definición y el otro lo reutiliza.

### Archivos compartidos
`Ingesta/Entidades.cs`, `Ingesta/Procesamiento.cs` (`AplicarAsync`), `Modulos/Dimensiones.cs` y `Modulos.Ingreso/ModuloIngreso.cs` los tocan casi todos los planes. «Un plan = una rama» se mantiene, pero **las ramas se abren desde `main` ya actualizado y se mezclan de una en una**; no se trabajan dos planes en paralelo sobre esos archivos.

## Reglas de diseño que salieron de la revisión (2026-11-16)
- **Promedios:** nunca `AVG(col)` sobre una vista ya agregada (promedio de promedios). La vista expone suma y conteo, y la métrica es `SUM(suma)/NULLIF(SUM(conteo),0)`.
- **Conteos distintos** (empleados, pacientes) no son aditivos: no se publican como `SUM` de una columna preagregada.
- **RLS:** `9000_rls.sql` solo cubre tablas con columna `EmpresaId`. Toda tabla nueva con datos de una empresa lleva `EmpresaId`, aunque sea derivable por FK.
- **`Acumulado`** aplica a la métrica entera: una expresión no puede mezclar un saldo acumulado con un flujo del periodo. Para eso, `IMetricaCalculada`.
- **`Prioridad` de la cola** solo ordena dentro de un mismo tipo de trabajo; no garantiza orden entre tipos.

## Fuera de estos planes (y por qué)
- **Cuadre con datos reales (P1, P2) y despliegue (F6):** no es diseño; sigue siendo lo primero para mostrar cifras a un cliente.
- **Rentabilidad por contrato de capitación/PGP:** el contrato de ingesta no trae población afiliada.
- **Conciliación de cartera con el saldo que reconoce la EPS (Circular 030 de 2013, por verificar):** no hay fuente del saldo del pagador.
- **Flujo de caja proyectado a 13 semanas:** esbozado en PL-01; necesita patrón de recaudo real para calibrar.
- **Hospitalario y quirúrgico (camas, triage, quirófano), benchmarking entre IPS, pronóstico:** fase 3 de la spec; la instalación revisada no tiene esos datos.
- **MIPRES y SISMED:** sus plazos y emisores están «por verificar» en el propio contexto (PL-13).
- **Conector para un HIS distinto de SaludIPS:** existe el contrato canónico; un conector nuevo depende de cada sistema.
- **Editor de dashboards (`cfg.Dashboard`), comentarios sobre datos, informe gerencial programado:** fase 2 de la capa semántica; sin plan todavía.
