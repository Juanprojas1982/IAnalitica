# PL-11 · Agendas y oportunidad

Estado: diseñado, no implementado. Módulo `AGENDA` (permiso `dash.agenda`, ya sembrado). Script `1130_agenda.sql`.
Preparación (2026-11-16): **listo para implementar.**
Depende de PL-10 solo para el costo de la capacidad ociosa (nómina); el resto es independiente.

## Objetivo
Responder: ¿qué tan llenas están las agendas?, ¿cuánto se pierde por inasistencia y cancelación?, ¿cuántos días espera un paciente?, ¿cuánto cuesta la capacidad que no se usa?

## Situación actual
- `fact.AgendaCapacidad` y `fact.Cita` existen (`0060`); `rpt.vw_AgendaOcupacion` existe (`0140`). `fact.Cita` ya calcula `DiasOportunidad` (desde la solicitud: definición normativa) y `DiasDesdeAsignacion` (lo que mide `SIPS_014`).
- `ModuloOperacion.cs` está vacío. Entidades `agenda-capacidad` y `citas` rechazadas. Extracción en `docs/11`, no ejecutada.

## Cambios
### 1. Entidades
- `agenda-capacidad`: `VENTANA` (incluye fechas futuras), suma de control `cuposOfertados`. **Excluirla del control de periodo cerrado** con `ExentaDeCierre: true` (el control ya existe en `IniciarCargaHandler`, no nace con PL-05; ver índice, «Mecanismos compartidos»). `ventanaHasta` futura ya se acepta: `IniciarCargaCommandValidator` no limita fechas (verificado); el único tope es `dim.Fecha` (hasta 2035) → fecha fuera de rango `FECHA_INVALIDA`.
- `citas`: `INCREMENTAL`, `INICIAL`; sin suma de control. **Rechazar la carga si una fila trae `nombre`, `documento` o `identificacion`** (ampliar `ReglasPrivacidad.Revisar`, que hoy solo revisa `terceros`; ver PL-10 §2 para el mensaje por entidad). `pacienteHash`, si viene, debe tener 64 caracteres hexadecimales (`char(64)` en la tabla) → si no, `VALOR_INVALIDO:pacienteHash`.

### 2. Procedimientos (`1130_agenda.sql`)
- `etl.usp_ProcesarAgendaCapacidad`: reemplaza la ventana por `fecha`. Llave: la PK real de `0060` es `(EmpresaId, FechaKey, ProfesionalId, Jornada)` — **sin** `horaInicio` (verificado; manda el DDL). Dos franjas del mismo profesional, día y jornada se **agregan** en una fila: `CuposOfertados` = Σ, `HoraInicio` = la menor, `MinutosPorCupo` = Σ(cupos × minutos) / Σ cupos redondeado (`MinutosOfertados` es columna calculada y debe conservar el total; si el redondeo lo altera en más de 1 minuto por cupo → rechazo `VALOR_INVALIDO:minutosPorCupo`). Franjas con `especialidadCodigo` o `sedeCodigo` distintos dentro de la misma llave → `DUPLICADO_EN_CARGA` (no se puede elegir una). Cambiar la PK para incluir la hora sería otro script y otra decisión. `cuposOfertados ≥ 0`, `minutosPorCupo` entre 1 y 480.
- `etl.usp_ProcesarCitas`: upsert por `SourceKey`. `estado` ∈ `ASISTIO`, `NO_ASISTIO`, `PENDIENTE`, `CANCELADA`: son los valores del `CHECK` de `fact.Cita` (`0060`). **`ASIGNADA` no existe en el DDL** (una versión anterior de este plan lo decía): la cita asignada y aún no atendida es `PENDIENTE`. La vista cuenta como «asignadas» todo lo que no es `CANCELADA`. El mapeo desde SaludIPS es `[S]` y vive en la extracción, no aquí. Fuera de lista → `ESTADO_INVALIDO`.

### 3. Vistas
- `rpt.vw_AgendaOcupacion`: `CREATE OR ALTER` agregando `Minutos`, `MinutosPerdidos` y `CostoHora` como columnas (hoy solo expone el costo ya multiplicado), sin quitar ninguna.
  - **Defecto de la vista actual que se corrige aquí:** la capacidad se agrupa por profesional, fecha, **especialidad y sede**, pero las citas se unen solo por profesional y fecha. Un profesional con jornada de mañana en la sede A y de tarde en la sede B produce dos filas de capacidad y **cada una recibe todas sus citas del día** (asignadas y atendidas se duplican). Corrección: la capacidad se agrupa por profesional y fecha; `EspecialidadId` y `SedeId` de la fila son los de la jornada con más cupos (empate: la jornada menor). Las columnas se conservan.
- `rpt.vw_CitasOportunidad` (nueva): `EmpresaId, PeriodoKey, SedeId, EspecialidadId, ProfesionalId, PagadorId, TipoConsulta, Citas, DiasOportunidadSuma, CitasConSolicitud, Canceladas, Reprogramadas, Confirmadas, Inasistencias`. Los promedios se obtienen como `SUM/NULLIF(SUM)`.

### 4. Módulo `ModuloAgenda` (en `IAnalitica.Modulos.Operacion`)
Métricas: `agenda.cupos`, `agenda.asignadas`, `agenda.atendidas`, `agenda.ocupacion_pct` (`KpiMeta: "OCUPACION_AGENDA"`), `agenda.inasistencia_pct` (`KpiMeta: "PCT_INASISTENCIA"`), `agenda.cancelacion_pct`, `agenda.cupos_perdidos`, `agenda.costo_capacidad_ociosa` (`ModulosAdicionales: NOMINA_COSTOS` en el widget), `agenda.dias_oportunidad` (`KpiMeta: "DIAS_OPORTUNIDAD"`), `agenda.confirmacion_pct`.
Dimensiones nuevas: `especialidad` (`dim.Especialidad`), `profesional` (`dim.Profesional`; **se seudonimiza en la IA**, agregar a la lista de dimensiones seudonimizadas junto a `usuario` y `tercero`).
Dashboards: `agenda.ocupacion` (KPI + línea por periodo + barras por especialidad + tabla por profesional) y `agenda.oportunidad` (días por especialidad, inasistencia y cancelación por pagador).
Las fórmulas, sobre columnas que la vista ya tiene: ocupación = `100.0*SUM(Asignadas)/NULLIF(SUM(Cupos),0)`; inasistencia = `100.0*SUM(Inasistencias)/NULLIF(SUM(Asignadas),0)`; cancelación = `100.0*SUM(Canceladas)/NULLIF(SUM(Citas),0)` sobre `vw_CitasOportunidad`; días de oportunidad = `SUM(DiasOportunidadSuma)/NULLIF(SUM(CitasConSolicitud),0)`. No usar la columna `Ocupacion` de la vista en una métrica (es una razón por fila: promediarla sería promedio de promedios).
**Composición (verificado):** `ModuloOperacion.cs` hoy solo expone `Ensamblado`. Agregar `ModuloOperacion.Modulos` (lista de `IModuloAnalitico`, como `ModuloIngreso.Modulos`) y concatenarla en **las siete llamadas a `AddCapaSemantica`**: `Api/Program.cs`, `Web/Program.cs`, `Worker/Program.cs` y, en pruebas, `Infra/BaseDeDatosFixture.cs`, `Ingesta/AyudaIngesta.cs` y `Ingesta/ReportesTests.cs` (dos veces). Si falta un host, el tablero existe en uno y no en otro.
Seudonimización: en `Application/Asistente/Seudonimizacion.cs`, agregar `["profesional"] = "Profesional"` al diccionario `Prefijos` (hoy solo `usuario` y `tercero`).

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1130_agenda.sql` |
| Domain | `Modulos/Dimensiones.cs` |
| Application | `Ingesta/Entidades.cs`, `Ingesta/Comandos.cs` (`ReglasPrivacidad`, `RecibirLoteHandler`), `Asistente/Seudonimizacion.cs` |
| Infrastructure | `Ingesta/Procesamiento.cs` |
| Módulo | `Modulos.Operacion/ModuloOperacion.cs` (+ `ModuloAgenda.cs`) |
| Hosts | `Api/Program.cs`, `Web/Program.cs`, `Worker/Program.cs` (composición) |
| Pruebas | `AgendaTests.cs` (nuevo), `DashboardsTests.cs`, `AsistenteTests.cs` (seudónimo de profesional), `BaseDeDatosFixture.cs`, `AyudaIngesta.cs`, `ReportesTests.cs` (composición), aislamiento |

Símbolos para `impact`: `CatalogoEntidades`, `ReglasPrivacidad.Revisar`, `RecibirLoteHandler`, `ProcesadorCargas.AplicarAsync`, `ModuloOperacion`, `Dimensiones.Todas`, `Seudonimizador`, `AddCapaSemantica`.

## Pruebas
- Caso: 100 cupos; 85 citas: 60 `ASISTIO`, 15 `NO_ASISTIO`, 5 `PENDIENTE` y 5 `CANCELADA` → asignadas 80, ocupación 80 %, inasistencia 18,75 % (15/80), cupos perdidos 40.
- Profesional con mañana en la sede A (10 cupos) y tarde en la sede B (6 cupos) y 12 citas ese día → una fila con 16 cupos y 12 asignadas (no 24), en la sede A.
- `estado = 'ASIGNADA'` → `ESTADO_INVALIDO`.
- Oportunidad: solicitud 1-mar, cita 11-mar → 10 días; cita sin fecha de solicitud no entra al promedio.
- Agenda futura se acepta; recarga de la ventana elimina horarios retirados.
- Dos franjas en la misma jornada (8:00 con 10 cupos de 20 min y 10:00 con 5 de 20 min) → una fila con 15 cupos y 300 minutos; con sedes distintas → rechazo.
- `agenda-capacidad` con un mes cerrado en la ventana se acepta (`ExentaDeCierre`); `citas` no cambia.
- Sin nómina: el costo de capacidad ociosa queda nulo y el widget lo dice, no muestra 0.
- Aislamiento por ambas vistas.

## Riesgos y supuestos `[S]`
- `HoraLlegada`, `InicioAtencion`, `FinAtencion` son `[S]`: **no** publicar espera en sala ni puntualidad en este plan.
- `dCitasHorarios` es una plantilla semanal: la expansión a fechas la hace la extracción; festivos y bloqueos pueden inflar la capacidad (regla AGE-04 del auditor).
- La unión capacidad↔citas es por profesional y fecha: citas de un profesional sin horario ese día no aparecen (documentar; medir cuántas son en el piloto).
- La instalación revisada es ambulatoria con 212 mil citas: volumen manejable; confirmar en IPS mayores.
