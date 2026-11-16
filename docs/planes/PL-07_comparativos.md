# PL-07 · Comparativos: mismo periodo del año anterior y acumulado del año

Estado: diseñado, no implementado. Sin script de BD. Sin decisiones pendientes: es el plan más barato y da valor con los datos de hoy.

## Objetivo
Que cada KPI muestre, además del valor, su variación frente al periodo anterior (ya existe), frente al mismo periodo del año anterior y el acumulado del año con su comparación.

## Situación actual
`ObtenerDatosWidgetHandler.CalcularAsync` calcula `anterior` con `PeriodoAnteriorAsync` (rango inmediatamente anterior de igual longitud; `null` si la métrica no tiene dimensión `periodo`, es `Acumulado` o no hay rango). `SerieWidget(resultado, anterior, meta, semaforo)`.

## Cambios
### Application `Semantica/Consultas.cs`
- `SerieWidget`: agregar `decimal? AnioAnterior`, `decimal? AcumuladoAnio`, `decimal? AcumuladoAnioAnterior` (parámetros opcionales al final para no romper llamadas).
- Solo para `TipoWidget.Kpi`:
  - `AnioAnterior`: mismos filtros con `PeriodoDesde − 100` y `PeriodoHasta − 100`. Para métricas `Acumulado` (balance) **sí aplica**: saldo al mismo mes del año anterior (el motor ignora `PeriodoDesde`).
  - `AcumuladoAnio`: `PeriodoDesde = año de PeriodoHasta × 100 + 1`, `PeriodoHasta` igual. No aplica a `Acumulado` (ya es saldo) → `null`.
  - `AcumuladoAnioAnterior`: lo mismo un año atrás.
- No aplica (todo `null`) a métricas sin dimensión `periodo` (cartera por `corte`, dinero detenido).
- Razones y promedios: el acumulado se recalcula con la expresión de la métrica sobre el rango (correcto para `SUM(a)/NULLIF(SUM(b),0)`); **nunca** promediar porcentajes mensuales.
- El acumulado del año se resuelve **aquí, en el handler**, cambiando `PeriodoDesde`; no se toca `MotorMetricas`. La bandera `AcumuladoAnual` del motor es de PL-14 (métricas presupuestales por vigencia) y es independiente: ningún plan espera al otro.
- Costo: hasta 3 consultas más por KPI, cacheadas con el widget (misma clave). Lanzarlas con `Task.WhenAll` solo si la conexión lo permite; si no, en secuencia.

### Web
- Componente del KPI (`Components/Pages/Dashboards/…`): segunda línea «vs. año anterior ▲ 4,2 %» y tercera «Acumulado 2026: … (▲ … vs. 2025)». Color según `SentidoMejora` (reutilizar la lógica de la variación actual). Sin dato → no se pinta la línea.
- PDF/Excel de reportes (`GeneradorReportes`): agregar las columnas a la sección de KPI si la sección ya exporta `anterior`; si no, dejarlo fuera y anotarlo.

### Asistente IA
Verificar si las herramientas genéricas exponen `SerieWidget`; si lo hacen, los campos nuevos viajan solos. No cambiar el prompt.

## Archivos por capa
| Capa | Archivo |
|---|---|
| Application | `src/IAnalitica.Application/Semantica/Consultas.cs` |
| Web | componente KPI en `src/IAnalitica.Web/Components/Pages/Dashboards/` |
| Infrastructure | `Reportes/GeneradorReportes.cs` (solo si aplica) |
| Pruebas | `tests/IAnalitica.Application.Tests/SemanticaYReportesTests.cs`, `tests/.../Ingesta/DashboardsTests.cs`, `Web/DashboardsWebTests.cs` (bUnit o HTML) |

Símbolos para `impact`: `SerieWidget`, `ObtenerDatosWidgetHandler`, `DatosWidget`, componente KPI.

## Pruebas
- Ingresos 2025-03 = 100 y 2026-03 = 120 → año anterior 100, variación 20 %.
- Acumulado ene–mar 2026 vs. ene–mar 2025.
- Margen acumulado = Σ utilidad / Σ ingreso del rango (no promedio de márgenes).
- Balance: `AnioAnterior` con valor, `AcumuladoAnio` nulo.
- Cartera (sin periodo): los tres nulos y el componente no pinta líneas.
- Rango que cruza de año (2025-11 a 2026-02): el acumulado toma el año de `PeriodoHasta`.

## Riesgos
- Con menos de 13 meses cargados, el año anterior da 0 y la variación engaña: si el periodo comparado no tiene **ninguna** carga, devolver `null`, no 0. Usar la fecha mínima de datos de la empresa (consulta barata a `fact.AsientoLinea`/`etl.Carga`, cacheada).
- `SerieWidget` es un record público usado por Web, reportes y posiblemente IA: revisar el `impact` completo.
