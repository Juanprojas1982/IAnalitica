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
- `rpt.vw_CostoPersonal`: `EmpresaId, PeriodoKey, CentroCostoId, Cargo, CostoPersonal, Horas, HorasExtra` (solo si el concepto lo permite distinguir; si no, omitir `HorasExtra`), **Nunca expone `EmpleadoTerceroCodigo` ni un conteo de empleados** (un conteo distinto no es aditivo: sumarlo entre periodos o centros lo infla). Requisito de privacidad (`01` §3, «nómina sin nombres, solo agregados»), umbral 3 (D61):
  - Un grupo `periodo × centro × cargo` con menos de 3 empleados no se muestra con su cargo: pasa a `Cargo = NULL` («otros cargos»).
  - **Supresión complementaria:** si el grupo «otros cargos» resultante de un `periodo × centro` tiene menos de 3 empleados, **todo** el centro de ese periodo se publica sin cargo. Si no, el cargo oculto se deduce restando los visibles del total.
  - Un centro con menos de 3 empleados en el periodo se publica igual a nivel de centro (el costo por centro ya es visible en la contabilidad); lo que se protege es el cruce con el cargo.
  - El conteo se hace dentro de la vista (CTE) y no sale como columna.
- `rpt.vw_EstudiosDiagnostico`: `EmpresaId, PeriodoKey, SedeId, Tipo, ProfesionalId, Estudios, Entregados, DiasEntregaSuma, ImagenesRechazadas, ImagenesRepetidas`.

### 3. Métricas y dashboards
- `ModuloNominaCostos` (en `Modulos.Operacion`; permiso `dash.nomina_costos`, **solo gerencia**): `nomina.costo_personal`, `nomina.costo_hora` = `SUM(CostoPersonal)/NULLIF(SUM(Horas),0)`. Sin métrica de número de empleados en v1 (no aditiva y facilita la deducción). Dashboard `nomina.costo_personal`.
- Producción por profesional dentro de `ModuloAgenda` (D63: la lista de módulos es un `CHECK` cerrado en `0020` y no incluye PRODUCTIVIDAD; crear el módulo exigiría script y permiso nuevos): `agenda.produccion_valor`, `agenda.prestaciones`, `agenda.pct_anuladas`. Dashboard `agenda.productividad`.
- `ModuloDiagnostico`: `diagnostico.estudios`, `diagnostico.dias_entrega` (`KpiMeta: "DIAS_ENTREGA_ESTUDIO"`), `diagnostico.pct_repetidos`, `diagnostico.pct_rechazados`. Dashboard `diagnostico.resumen`.
- Dimensiones: `profesional`, `especialidad` (de PL-11), `cargo`, `tipo_estudio`.
- IA: `profesional` seudonimizado; las métricas `nomina.*` no deben quedar disponibles al asistente para roles sin `dash.nomina_costos` (ya lo hereda del permiso: probarlo).

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
- Usuario `FINANCIERO` no ve `nomina.*` ni por dashboard ni por el asistente; `GERENTE` sí.
- Aislamiento por las tres vistas.

## Riesgos y supuestos `[S]`
- Horas laboradas: `mTurnosN` está vacío (brecha del HIS, `03` §18); `Horas` sale de la nómina (`horas`, `horasContratoMes`) y puede faltar para honorarios → `costo_hora` nulo, no 0.
- Fechas de informe y entrega de estudios: confirmar que la instalación las registra antes de publicar `dias_entrega`.
- «Productividad administrativa y tasa de error por usuario» (KPI D del catálogo) queda fuera por decisión del dueño (D61): mezcla desempeño individual con datos de personas.
