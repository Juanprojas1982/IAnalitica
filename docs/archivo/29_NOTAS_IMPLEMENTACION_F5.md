# Notas de implementación — F5 · Costos e IA interna

Complementa a `24_SPEC_MAESTRA.md` (§5.4 y §5.7), `07_spec_asistente_ia.md` y `10_hechos_operativos_y_costeo.sql` (mandan). Aquí: qué se implementó, qué quedó fuera, supuestos `[S]` y decisiones pendientes.

## 1. Qué incluye F5
| Entregable | Dónde | Cómo se verifica |
|---|---|---|
| Motor de costos validado: `etl.usp_CalcularBasesCosto` + `etl.usp_DistribuirCostos` (ya estaban en `0150`; no se modificaron) | `database/scripts/0150_procedimientos.sql` | `CostosTests`: cascada ADM→APO→productivos calculada a mano, **conservación del costo total**, idempotencia al recalcular, centro sin base conserva su costo, aislamiento entre empresas |
| Trabajo `CALCULAR_COSTOS` | `Infrastructure/Ingesta/Procesamiento.cs` | Se encola un trabajo por mes de la ventana (máx. 36) al cargar `asientos` o `facturas`, **solo si la empresa tiene reglas**; el worker corre ambos procedimientos |
| Reglas de distribución por el portal | `Application/Costos`, `Infrastructure/Auditoria/RepositorioCostos.cs`, página `/costos` | `CostosWebTests`: definir/cambiar/quitar, bases manuales rechazadas, reglas por defecto, recálculo por rango (máx. 24 meses); auditado en `aud.CambioAdministrativo` |
| Dashboard «Estado de resultados con distribución de costos» | `Modulos.Financiero` (`financiero.pyg_distribuido`) sobre `rpt.vw_RentabilidadCentroCosto` | `CostosTests.El_dashboard_…`: margen 2.900 = 4.000 − 1.100; por centro 525 y 2.375 |
| Asistente IA interno: orquestador | `Application/Asistente` (`EnviarMensajeAsistenteHandler`), `Infrastructure/Asistente/RepositorioAsistente.cs` | `AsistenteTests` (13) con un modelo simulado, sin proveedor real |
| Herramientas = métricas de los módulos activos | `Application/Asistente/Herramientas.cs`: `listar_metricas` y `consultar_metrica` sobre el motor semántico (RLS, permiso y módulo del usuario; máx. 200 filas) | `AsistenteTests` |
| Cupo y cobro (spec 07 §6) | Reserva → bucle → liquidación con `ia.usp_ReservarCuota` / `ia.usp_LiquidarReserva`; valoración con `ia.fn_TarifaVigente`; `ia.UsoLlamada` por iteración | Valor calculado a mano (6,2 COP), reserva liberada, `CUPO_AGOTADO` sin llamar al proveedor, tope de iteraciones, error del proveedor, modelo de ahorro |
| Seudonimización y salida segura | `Seudonimizador`, `SanitizadorSalida` | Los nombres de usuarios operativos y terceros viajan como «Usuario #id» y se re-hidratan solo para la empresa; el historial guardado conserva el alias; la salida pierde HTML y enlaces externos |
| Página `/asistente` (pruebas internas) | `Web/Components/Pages/Asistente.razor` | `AsistenteWebTests` |
| Prompt de sistema versionado | `database/scripts/1030_asistente_prompt.sql` | — |

Suite completa al cierre: 398 pruebas (60 dominio, 90 aplicación, 253 integración con SQL Server en contenedor) en verde.

## 2. Lo que NO se entregó (y por qué)
| Pendiente | Motivo |
|---|---|
| **Adaptador del proveedor (Anthropic)** | Sin clave ni aprobación de costo (D32): `IAsistenteChatClientFactory` devuelve `null` por defecto y el asistente responde «no hay un proveedor de IA configurado». Hay que registrar una fábrica que cree el `IChatClient` del paquete oficial `Anthropic` (13.3.0 existe en NuGet; no se instaló ni se probó). Los nombres de modelo salen de `ia.Modelo` |
| Streaming (SSE) y panel lateral con `Ctrl+K`, preguntas sugeridas, valoración 👍/👎 | Fase siguiente. La fase 1 es una página de pruebas internas |
| Área de parámetros del superadmin (tarifa, planes, TRM), consumo visible (barra del cupo), alertas al 70/90 % y estado de cuenta | La tarifa de venta y el cupo por plan son decisiones comerciales abiertas (spec 07 §11). Sin una fila en `ia.Tarifa` el asistente responde «falta configurar la tarifa». `ia.usp_CerrarPeriodo` existe pero nadie lo llama |
| Autorización de excedente desde el portal | Decisión pendiente (spec 07 §11.2) |
| Evaluaciones (spec 07 §9) y `ia.Modelo.EvaluacionAprobada` | Requieren el modelo real y un set de preguntas con respuestas de referencia |
| Caché de prompt, conteo de tokens exacto y resumen de historial | Dependen del proveedor. La reserva usa una estimación conservadora (3 caracteres por token) |
| Respaldo de modelo (`ModeloRespaldoId`) | Sin proveedor no hay fallo real que respaldar; el error se registra (`ia.UsoLlamada.Resultado = 'ERROR'`) y se informa sin detalles internos |
| Bases manuales de costos (m², kilos de ropa, porcentaje) | Falta la carga de sus valores (`fact.CostoBaseValor` manual, «por Excel» en el DDL). La UI solo permite bases calculadas |
| Clasificación de los centros de costo (`TipoCC`) | La homologación de `TipoOrigen` a `TipoCC` es `[S]` (05 §8.5) y hoy todo entra como PRODUCTIVO; por eso «reglas por defecto» solo sugiere algo después de clasificar |
| Entidades `nomina`, `prestaciones` y `movimientos-inventario` en la ingesta | Las bases COSTO_PERSONAL, ACTIVIDADES, ATENCIONES y CONSUMO_INSUMOS dependen de ellas; hoy solo la base INGRESO sale de lo que la ingesta carga. Los procedimientos están probados con esos hechos insertados directamente |

**Criterio de aceptación de F5** (spec 24: «conservación del costo; consumo de IA dentro del tope»): la conservación está probada (también la hace cumplir el procedimiento con `THROW 50010`) y el consumo dentro del tope está probado con tarifa y cupo sintéticos. «Motor de distribución con los pilotos» y «asistente en pruebas internas» necesitan datos y modelo reales: no se pueden cerrar aquí.

## 3. Supuestos `[S]` por verificar
1. **Permiso para configurar costos** = `admin.usuarios` (ADMIN_EMPRESA). La matriz de la spec no define un permiso propio para costos; si el dueño quiere uno (`costos.configurar`), se agrega al seed.
2. **Visibilidad del valor de cada respuesta**: `ia.EmpresaConfig.VisibilidadCosto = 'ADMIN'` por defecto → lo ve quien tenga `admin.usuarios`; con `'TODOS'`, cualquiera (spec 07 §11.3 sigue abierta).
3. **Rol permitido** (`ia.RolPermitido`): no se aplica; basta el permiso `ia.usar`. `ISesionUsuario` no expone los roles del usuario.
4. **Personas a seudonimizar** = todo valor de las dimensiones `usuario` y `tercero` (se asume que pueden ser personas naturales; no hay una marca por fila).
5. **Permiso de una métrica** = el permiso de los dashboards de su módulo (en la matriz, un permiso `dash.*` por módulo).
6. **Estimación de la reserva**: 3 caracteres por token + 1.500 tokens de herramientas, multiplicado por las iteraciones máximas. Es conservadora; la reserva sobrante se libera al liquidar.
7. **Fecha de hoy** = hora de Colombia (UTC−5) para elegir la tarifa; el periodo del cupo lo fija el procedimiento en UTC (`SYSUTCDATETIME()`), como estaba en el DDL.

## 4. Decisiones de diseño tomadas (reportar si no convienen)
- **Herramientas generadas del catálogo semántico** en lugar de las diez herramientas fijas de la spec 07 §4: la spec maestra (24 §5.7) dice «herramientas = métricas de los módulos activos». Dos herramientas genéricas cubren todas las métricas presentes y futuras y heredan RLS, permisos y límites del motor. Las herramientas específicas de la spec 07 (`pendiente_facturar`, `inventario`…) quedan para cuando existan sus métricas.
- **`IChatClient` de `Microsoft.Extensions.AI.Abstractions` 10.11.0**, con el bucle de herramientas escrito a mano (no `FunctionInvokingChatClient`) para poder reservar, medir y cortar por iteración.
- **El historial guardado lleva alias**, no nombres: vuelve al proveedor en cada turno. Si la empresa no autoriza guardar texto (`GuardarTextoConversacion = 0`), el intercambio se registra sin texto y la conversación no tiene memoria entre turnos.
- **Tarifa y cupo** salen de la base (`ia.Tarifa`, `ia.PlanComercial`/`EmpresaConfig`); el código no fija precios ni modelos. Los precios de `ia.Modelo` son los del script 0100 (a 2026-10-06) y siguen marcados «verificar antes de producción».
- **Distribución de costos**: no se cambió la lógica de los procedimientos; las pruebas la confirmaron. Una limitación conocida de su diseño: un centro sin destinos con base conserva su costo (se ve en la rentabilidad del propio centro).

## 5. Cómo probar
```bash
dotnet test        # 398 pruebas
dotnet test tests/IAnalitica.Integration.Tests --filter "FullyQualifiedName~Costos|FullyQualifiedName~Asistente"
```
Manual (costos): cargar `centros-costo`, `asientos` con `centroCostoCodigo` y `facturas`; en `/costos` definir reglas y recalcular; abrir el dashboard «Estado de resultados con distribución de costos» (permiso `dash.financiero`).
Manual (asistente): habilitar la empresa (`ia.EmpresaConfig.Habilitado = 1`, módulo `IA`), crear una fila en `ia.Tarifa`, registrar una `IAsistenteChatClientFactory` real y abrir `/asistente` con un usuario con `ia.usar`.

## 6. Ambigüedades para el equipo funcional
1. Reglas de distribución de cada piloto (P7 de la spec 24): qué centros reparten, en qué orden y con qué base.
2. Homologación de `TipoOrigen` de SaludIPS a administrativo / apoyo / productivo.
3. Tarifa de venta (COP por millón de tokens), cupo por plan y quién autoriza el excedente (spec 07 §11).
4. ¿Permiso propio para configurar costos?
