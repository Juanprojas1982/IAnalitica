# PL-12 · Productividad y apoyo diagnóstico

Estado: diseñado, no implementado. Módulos `DIAGNOSTICO` y `NOMINA_COSTOS` (permisos sembrados). Script `1140_productividad.sql`.
**Depende de PL-10** (`prestaciones`, `nomina`).

## Objetivo
Producción por profesional y especialidad, costo de personal por centro y por hora, y —para centros de diagnóstico— volumen, oportunidad de entrega y repetición de estudios.

## Situación actual
`fact.Prestacion`, `fact.NominaCosto`, `fact.EstudioDiagnostico` existen. Entidad `estudios` rechazada. Sin vistas ni métricas. La instalación revisada tiene 97 mil estudios de imagen y laboratorio clínico vacío.

## Cambios
### 1. Entidad `estudios`
`INCREMENTAL`, `INICIAL`. `etl.usp_ProcesarEstudios`: upsert por `SourceKey`; `tipo` ∈ (`IMAGEN`, `LABORATORIO`); `prestacionSourceKey` obligatorio (no exigir que la prestación exista: pueden llegar en otro orden; dejar el enlace lógico).

### 2. Vistas (`1140_productividad.sql`)
- `rpt.vw_ProduccionProfesional`: `EmpresaId, PeriodoKey, SedeId, EspecialidadId, ProfesionalId, PagadorId, LineaServicioId, Prestaciones, Cantidad, ValorProducido, PrestacionesAnuladas`.
- `rpt.vw_CostoPersonal`: `EmpresaId, PeriodoKey, CentroCostoId, Cargo, CostoPersonal, Horas, HorasExtra` (solo si el concepto lo permite distinguir; si no, omitir `HorasExtra`), `Empleados` (conteo de códigos distintos). **Nunca expone `EmpleadoTerceroCodigo`.** Requisito de privacidad (`01` §3): si un grupo `periodo × centro × cargo` tiene menos de 3 empleados, se agrega al centro sin cargo («nómina sin nombres, solo agregados»). Umbral 3 = `[S]`, confirmar con el dueño.
- `rpt.vw_EstudiosDiagnostico`: `EmpresaId, PeriodoKey, SedeId, Tipo, ProfesionalId, Estudios, Entregados, DiasEntregaSuma, ImagenesRechazadas, ImagenesRepetidas`.

### 3. Métricas y dashboards
- `ModuloNominaCostos` (en `Modulos.Operacion`; permiso `dash.nomina_costos`, **solo gerencia**): `nomina.costo_personal`, `nomina.costo_hora` = `SUM(CostoPersonal)/NULLIF(SUM(Horas),0)`, `nomina.empleados`. Dashboard `nomina.costo_personal`.
- `ModuloProductividad` (o dentro de `AGENDA` si el dueño prefiere no crear módulo: **decisión**, la lista de módulos es cerrada en `03` §17 y no incluye PRODUCTIVIDAD → usar `AGENDA` para producción por profesional): `agenda.produccion_valor`, `agenda.prestaciones`, `agenda.pct_anuladas`. Dashboard `agenda.productividad`.
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
- Supresión de grupos pequeños: 2 empleados en un cargo → el cargo no aparece, el total del centro sí.
- Usuario `FINANCIERO` no ve `nomina.*` ni por dashboard ni por el asistente; `GERENTE` sí.
- Aislamiento por las tres vistas.

## Riesgos y supuestos `[S]`
- Horas laboradas: `mTurnosN` está vacío (brecha del HIS, `03` §18); `Horas` sale de la nómina (`horas`, `horasContratoMes`) y puede faltar para honorarios → `costo_hora` nulo, no 0.
- Fechas de informe y entrega de estudios: confirmar que la instalación las registra antes de publicar `dias_entrega`.
- «Productividad administrativa y tasa de error por usuario» (KPI D del catálogo) queda fuera: mezcla desempeño individual con datos de personas; requiere decisión del dueño.
