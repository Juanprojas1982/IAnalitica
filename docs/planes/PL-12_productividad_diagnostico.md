# PL-12 · Productividad y apoyo diagnóstico

Estado: diseñado, no implementado. Módulos `DIAGNOSTICO` y `NOMINA_COSTOS` (permisos sembrados). Script `1140_productividad.sql`.
Preparación (2026-11-16): **listo para implementar.** DP-10 (D61: umbral 3, sin productividad individual) y DP-12 (D63: dentro de `AGENDA`) aprobadas.
**Depende de PL-10** (`prestaciones`, `nomina`) y de PL-11 (dimensiones `profesional` y `especialidad`, y su seudonimización). Decisiones: DP-10 y DP-12.

## Objetivo
Producción por profesional y especialidad, costo de personal por centro y por hora, y —para centros de diagnóstico— volumen, oportunidad de entrega y repetición de estudios.

## Situación actual
`fact.Prestacion`, `fact.NominaCosto`, `fact.EstudioDiagnostico` existen. Entidad `estudios` rechazada. Sin vistas ni métricas. La instalación revisada tiene 97 mil estudios de imagen y laboratorio clínico vacío.

## Cambios
### 1. Entidad `estudios`
`INCREMENTAL`, `INICIAL`. `etl.usp_ProcesarEstudios`: upsert por `SourceKey`; `tipo` ∈ (`IMAGEN`, `LABORATORIO`); `prestacionSourceKey` obligatorio (no exigir que la prestación exista: pueden llegar en otro orden; dejar el enlace lógico).

### 2. Vistas (`1140_productividad.sql`)
- `rpt.vw_ProduccionProfesional`: `EmpresaId, PeriodoKey, SedeId, EspecialidadId, ProfesionalId, PagadorId, LineaServicioId, Prestaciones, Cantidad, ValorProducido, PrestacionesAnuladas`.
- `rpt.vw_CostoPersonal`: `EmpresaId, PeriodoKey, CentroCostoId, Cargo, CostoPersonal, Horas`. Sin `HorasExtra` en v1: el contrato no trae una marca que distinga el concepto de hora extra (decidido aquí para no dejarlo a criterio de quien implemente).
  - `CostoPersonal` = Σ `Valor` con `EsCosto = 1`.
  - `Horas` = **horas contratadas**: por empleado, periodo y centro se toma `MAX(HorasContratoMes)` y luego se suma entre empleados (CTE interna). **No** es Σ `Horas` de las líneas: `fact.NominaCosto` tiene una fila por concepto y cada una repite sus horas, así que sumarlas las cuenta varias veces. Es el mismo criterio que ya usa `rpt.vw_AgendaOcupacion` para `CostoHora`. `[S]`: confirmar con datos reales que `horasContratoMes` viene lleno (P2).
  - **Nunca expone `EmpleadoTerceroCodigo` ni un conteo de empleados** (un conteo distinto no es aditivo: sumarlo entre periodos o centros lo infla). Requisito de privacidad (`01` §3, «nómina sin nombres, solo agregados»), umbral 3 (D61):
  - Un grupo `periodo × centro × cargo` con menos de 3 empleados no se muestra con su cargo: pasa a `Cargo = NULL` («otros cargos»).
  - **Supresión complementaria:** si el grupo «otros cargos» resultante de un `periodo × centro` tiene menos de 3 empleados, **todo** el centro de ese periodo se publica sin cargo. Si no, el cargo oculto se deduce restando los visibles del total.
  - Un centro con menos de 3 empleados en el periodo se publica igual a nivel de centro (el costo por centro ya es visible en la contabilidad); lo que se protege es el cruce con el cargo.
  - El conteo se hace dentro de la vista (CTE) y no sale como columna.
- `rpt.vw_EstudiosDiagnostico`: `EmpresaId, PeriodoKey, SedeId, Tipo, ProfesionalId, Estudios, Entregados, EstudiosConEntrega, DiasEntregaSuma, ImagenesRechazadas, ImagenesRepetidas`. `PeriodoKey` sale de `FechaRealizacion` (la tabla no tiene llave de fecha; sin fecha de realización el estudio no entra a la vista y se cuenta en una prueba). `ProfesionalId` = `ProfesionalInformaId`. `DiasEntregaSuma` y `EstudiosConEntrega` solo con `FechaEntrega` informada.

### 3. Métricas y dashboards
- `ModuloNominaCostos` (en `Modulos.Operacion`; permiso `dash.nomina_costos`, **solo gerencia**): `nomina_costos.costo_personal`, `nomina_costos.costo_hora` = `SUM(CostoPersonal)/NULLIF(SUM(Horas),0)` (rotulado «por hora contratada»). Sin métrica de número de empleados en v1 (no aditiva y facilita la deducción). Dashboard `nomina_costos.costo_personal`.
  - **El prefijo es `nomina_costos.`, no `nomina.`** (así estaba antes): `CatalogoAnalitico` exige que el código de la métrica empiece por el código del módulo en minúsculas y falla **al arrancar los tres hosts** si no. El módulo es `NOMINA_COSTOS`.
- Producción por profesional dentro de `ModuloAgenda` (D63: la lista de módulos es un `CHECK` cerrado en `0020` y no incluye PRODUCTIVIDAD; crear el módulo exigiría script y permiso nuevos): `agenda.produccion_valor`, `agenda.prestaciones`, `agenda.pct_anuladas`. Dashboard `agenda.productividad`.
- `ModuloDiagnostico`: `diagnostico.estudios`, `diagnostico.dias_entrega` = `SUM(DiasEntregaSuma)/NULLIF(SUM(EstudiosConEntrega),0)` (`KpiMeta: "DIAS_ENTREGA_ESTUDIO"`), `diagnostico.pct_repetidos`, `diagnostico.pct_rechazados`. Dashboard `diagnostico.resumen`.
- Dimensiones: `profesional`, `especialidad` (de PL-11), `cargo`, `tipo_estudio`. `cargo` es texto libre de la nómina: con grupos pequeños puede identificar a una persona; por eso existe la supresión de arriba y **no** se agrega a la lista de seudónimos (no es una llave de persona).
- IA: `profesional` seudonimizado; las métricas `nomina_costos.*` no quedan disponibles al asistente para roles sin `dash.nomina_costos` (verificado: `HerramientasAsistente` filtra por permiso del módulo y módulo activo; dejar la prueba).
- Composición: los módulos nuevos se agregan a `ModuloOperacion.Modulos` (creado en PL-11); no hay que volver a tocar los hosts.

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1140_productividad.sql` |
| Domain | `Modulos/Dimensiones.cs` |
| Application | `Ingesta/Entidades.cs` |
| Infrastructure | `Ingesta/Procesamiento.cs` |
| Módulo | `Modulos.Operacion/*` |
| Pruebas | `ProductividadTests.cs`, `DashboardsTests.cs`, `AsistenteTests.cs`, aislamiento |

Símbolos para `impact`: `CatalogoEntidades`, `ProcesadorCargas.AplicarAsync`, `ModuloOperacion`, `Dimensiones.Todas`.

## Pruebas
- Producción por profesional contra un caso a mano; anuladas fuera del valor.
- Supresión de grupos pequeños: centro con cargos de 5, 4 y 2 empleados → el de 2 pasa a «otros cargos», que queda con 2 (< 3) → el centro entero sale sin cargo. Con cargos de 5, 2 y 2 → «otros cargos» tiene 4 y se muestran el cargo de 5 y «otros cargos».
- Usuario `FINANCIERO` no ve `nomina_costos.*` ni por dashboard ni por el asistente; `GERENTE` sí.
- Costo por hora: empleado con 3 conceptos de costo (salario, auxilio, aportes) y 240 horas contratadas → `Horas` = 240, no 720.
- El catálogo arranca (`CatalogoAnalitico.Construir` con todos los módulos) sin errores de prefijo ni de permiso.
- Aislamiento por las tres vistas.

## Riesgos y supuestos `[S]`
- Horas laboradas: `mTurnosN` está vacío (brecha del HIS, `03` §18); `Horas` sale de `horasContratoMes` de la nómina y puede faltar para honorarios → `costo_hora` nulo, no 0. Es costo por hora **contratada**, no laborada.
- Fechas de informe y entrega de estudios: confirmar que la instalación las registra antes de publicar `dias_entrega`.
- «Productividad administrativa y tasa de error por usuario» (KPI D del catálogo) queda fuera por decisión del dueño (D61): mezcla desempeño individual con datos de personas.
