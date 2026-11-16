# PL-11 · Agendas y oportunidad

Estado: diseñado, no implementado. Módulo `AGENDA` (permiso `dash.agenda`, ya sembrado). Script `1130_agenda.sql`.
Depende de PL-10 solo para el costo de la capacidad ociosa (nómina); el resto es independiente.

## Objetivo
Responder: ¿qué tan llenas están las agendas?, ¿cuánto se pierde por inasistencia y cancelación?, ¿cuántos días espera un paciente?, ¿cuánto cuesta la capacidad que no se usa?

## Situación actual
- `fact.AgendaCapacidad` y `fact.Cita` existen (`0060`); `rpt.vw_AgendaOcupacion` existe (`0140`). `fact.Cita` ya calcula `DiasOportunidad` (desde la solicitud: definición normativa) y `DiasDesdeAsignacion` (lo que mide `SIPS_014`).
- `ModuloOperacion.cs` está vacío. Entidades `agenda-capacidad` y `citas` rechazadas. Extracción en `docs/11`, no ejecutada.

## Cambios
### 1. Entidades
- `agenda-capacidad`: `VENTANA` (incluye fechas futuras), sin suma de control (o `cuposOfertados`). **Excluirla del control de periodo cerrado** (PL-05) y permitir `ventanaHasta` futura: revisar que ningún validador lo impida.
- `citas`: `INCREMENTAL`, `INICIAL`; sin suma de control. `pacienteHash` obligatorio si viene paciente; **rechazar la carga si una fila trae nombre o documento** (ampliar `ReglasPrivacidad`).

### 2. Procedimientos (`1130_agenda.sql`)
- `etl.usp_ProcesarAgendaCapacidad`: reemplaza la ventana por `fecha`. Llave natural `fecha + profesional + jornada + horaInicio` (confirmar con la PK de `0060` antes de escribir). `cuposOfertados ≥ 0`, `minutosPorCupo` entre 1 y 480.
- `etl.usp_ProcesarCitas`: upsert por `SourceKey`. `estado` normalizado a `ASIGNADA`, `ASISTIO`, `NO_ASISTIO`, `CANCELADA` (la vista usa esos literales; el mapeo desde SaludIPS es `[S]` y vive en la extracción, no aquí). Fuera de lista → `ESTADO_INVALIDO`.

### 3. Vistas
- `rpt.vw_AgendaOcupacion`: `CREATE OR ALTER` agregando `Minutos`, `MinutosPerdidos` y `CostoHora` como columnas (hoy solo expone el costo ya multiplicado), sin quitar ninguna.
- `rpt.vw_CitasOportunidad` (nueva): `EmpresaId, PeriodoKey, SedeId, EspecialidadId, ProfesionalId, PagadorId, TipoConsulta, Citas, DiasOportunidadSuma, CitasConSolicitud, Canceladas, Reprogramadas, Confirmadas, Inasistencias`. Los promedios se obtienen como `SUM/NULLIF(SUM)`.

### 4. Módulo `ModuloAgenda` (en `IAnalitica.Modulos.Operacion`)
Métricas: `agenda.cupos`, `agenda.asignadas`, `agenda.atendidas`, `agenda.ocupacion_pct` (`KpiMeta: "OCUPACION_AGENDA"`), `agenda.inasistencia_pct` (`KpiMeta: "PCT_INASISTENCIA"`), `agenda.cancelacion_pct`, `agenda.cupos_perdidos`, `agenda.costo_capacidad_ociosa` (`ModulosAdicionales: NOMINA_COSTOS` en el widget), `agenda.dias_oportunidad` (`KpiMeta: "DIAS_OPORTUNIDAD"`), `agenda.confirmacion_pct`.
Dimensiones nuevas: `especialidad` (`dim.Especialidad`), `profesional` (`dim.Profesional`; **se seudonimiza en la IA**, agregar a la lista de dimensiones seudonimizadas junto a `usuario` y `tercero`).
Dashboards: `agenda.ocupacion` (KPI + línea por periodo + barras por especialidad + tabla por profesional) y `agenda.oportunidad` (días por especialidad, inasistencia y cancelación por pagador).
Registrar el módulo en los tres hosts (ver cómo se compone `ModuloIngreso.Modulos`).

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1130_agenda.sql` |
| Domain | `Modulos/Dimensiones.cs` |
| Application | `Ingesta/Entidades.cs`, `Ingesta/Comandos.cs`, seudonimización del asistente |
| Infrastructure | `Ingesta/Procesamiento.cs` |
| Módulo | `Modulos.Operacion/ModuloOperacion.cs` |
| Pruebas | `AgendaTests.cs` (nuevo), `DashboardsTests.cs`, `AsistenteTests.cs` (seudónimo de profesional), aislamiento |

Símbolos para `impact`: `CatalogoEntidades`, `ReglasPrivacidad.Revisar`, `ProcesadorCargas.AplicarAsync`, `ModuloOperacion`, `Dimensiones.Todas`, el seudonimizador del asistente.

## Pruebas
- Caso: 100 cupos, 80 asignadas, 60 atendidas, 15 inasistencias, 5 canceladas → ocupación 80 %, inasistencia 18,75 % (15/80), cupos perdidos 40.
- Oportunidad: solicitud 1-mar, cita 11-mar → 10 días; cita sin fecha de solicitud no entra al promedio.
- Agenda futura se acepta; recarga de la ventana elimina horarios retirados.
- Sin nómina: el costo de capacidad ociosa queda nulo y el widget lo dice, no muestra 0.
- Aislamiento por ambas vistas.

## Riesgos y supuestos `[S]`
- `HoraLlegada`, `InicioAtencion`, `FinAtencion` son `[S]`: **no** publicar espera en sala ni puntualidad en este plan.
- `dCitasHorarios` es una plantilla semanal: la expansión a fechas la hace la extracción; festivos y bloqueos pueden inflar la capacidad (regla AGE-04 del auditor).
- La unión capacidad↔citas es por profesional y fecha: citas de un profesional sin horario ese día no aparecen (documentar; medir cuántas son en el piloto).
- La instalación revisada es ambulatoria con 212 mil citas: volumen manejable; confirmar en IPS mayores.
