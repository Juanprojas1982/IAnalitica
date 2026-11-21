# PL-07 · Comparativos: mismo periodo del año anterior y acumulado del año

Estado: diseñado, no implementado. Sin script de BD. Sin decisiones pendientes: es el plan más barato y da valor con los datos de hoy.
Preparación (2026-11-16): **listo para implementar.**

## Objetivo
Que cada KPI muestre, además del valor, su variación frente al periodo anterior (ya existe), frente al mismo periodo del año anterior y el acumulado del año con su comparación.

## Situación actual
`ObtenerDatosWidgetHandler.CalcularAsync` calcula `anterior` con `PeriodoAnteriorAsync` (rango inmediatamente anterior de igual longitud; `null` si la métrica no tiene dimensión `periodo`, es `Acumulado` o no hay rango). `SerieWidget(resultado, anterior, meta, semaforo)`.

## Cambios
### Application `Semantica/Contratos.cs` (el record) y `Semantica/Consultas.cs` (el handler)
- `SerieWidget` (está en `Contratos.cs`, no en `Consultas.cs`): agregar `decimal? AnioAnterior = null`, `decimal? AcumuladoAnio = null`, `decimal? AcumuladoAnioAnterior = null` (parámetros opcionales al final para no romper llamadas) y las variaciones derivadas con la misma fórmula de `Variacion` (`(a − b) / |b|`, nula si `b` es 0 o falta).
- Solo para `TipoWidget.Kpi`:
  - `AnioAnterior`: mismos filtros con `PeriodoDesde − 100` y `PeriodoHasta − 100`. Para métricas `Acumulado` (balance) **sí aplica**: saldo al mismo mes del año anterior (el motor ignora `PeriodoDesde`).
  - `AcumuladoAnio`: `PeriodoDesde = año de PeriodoHasta × 100 + 1`, `PeriodoHasta` igual. No aplica a `Acumulado` (ya es saldo) → `null`.
  - `AcumuladoAnioAnterior`: lo mismo un año atrás.
- No aplica (todo `null`) a métricas sin dimensión `periodo` (cartera por `corte`, dinero detenido).
- Razones y promedios: el acumulado se recalcula con la expresión de la métrica sobre el rango (correcto para `SUM(a)/NULLIF(SUM(b),0)`); **nunca** promediar porcentajes mensuales.
- El acumulado del año se resuelve **aquí, en el handler**, cambiando `PeriodoDesde`; no se toca `MotorMetricas`. La bandera `AcumuladoAnual` del motor es de PL-14 (métricas presupuestales por vigencia) y es independiente: ningún plan espera al otro.
- Si la métrica es `AcumuladoAnual` (bandera de PL-14, si ya existe): se trata como `Acumulado` (`AcumuladoAnio` nulo; `AnioAnterior` sí aplica), y `PeriodoAnteriorAsync` también debe devolver `null` para ella.
- Costo: hasta 3 consultas más por KPI, cacheadas con el widget (misma clave). `MotorMetricas.EjecutarAsync` abre su propia conexión en cada llamada (verificado), así que se pueden lanzar con `Task.WhenAll`.

### Web
- `Components/Pages/Dashboards/Tablero.razor` (ahí se pinta el KPI): segunda línea «vs. año anterior ▲ 4,2 %» y tercera «Acumulado 2026: … (▲ … vs. 2025)». Color según `SentidoMejora` (reutilizar la lógica de la variación actual). Sin dato → no se pinta la línea.
- PDF/Excel de reportes: **fuera de este plan.** Verificado: los exportadores no usan `SerieWidget.Anterior` (ni siquiera exportan la variación actual), así que no hay sección a la que agregar columnas.

### Asistente IA
Sin cambios. Verificado: `HerramientasAsistente` consulta `IMotorMetricas` directamente y no usa `SerieWidget`; los comparativos no llegan al asistente. No cambiar el prompt.

## Archivos por capa
| Capa | Archivo |
|---|---|
| Application | `src/IAnalitica.Application/Semantica/Contratos.cs` (`SerieWidget`), `src/IAnalitica.Application/Semantica/Consultas.cs` (`ObtenerDatosWidgetHandler.CalcularAsync`) |
| Web | `src/IAnalitica.Web/Components/Pages/Dashboards/Tablero.razor` |
| Pruebas | `tests/IAnalitica.Application.Tests/SemanticaYReportesTests.cs`, `tests/.../Ingesta/DashboardsTests.cs`, `Web/DashboardsWebTests.cs` (bUnit o HTML) |

Símbolos para `impact`: `SerieWidget`, `ObtenerDatosWidgetHandler`, `DatosWidget`, `Tablero.razor`.

## Pruebas
- Ingresos 2025-03 = 100 y 2026-03 = 120 → año anterior 100, variación 20 %.
- Acumulado ene–mar 2026 vs. ene–mar 2025.
- Margen acumulado = Σ utilidad / Σ ingreso del rango (no promedio de márgenes).
- Balance: `AnioAnterior` con valor, `AcumuladoAnio` nulo.
- Cartera (sin periodo): los tres nulos y el componente no pinta líneas.
- Rango que cruza de año (2025-11 a 2026-02): el acumulado toma el año de `PeriodoHasta`.

## Riesgos
- Con menos de 13 meses cargados no hay año anterior. No hace falta una consulta aparte de «fecha mínima de datos»: sin dimensiones el motor ejecuta `SELECT SUM(…) FROM vista WHERE …` y, sobre cero filas, `SUM` y `SUM/NULLIF(SUM)` devuelven `NULL` → `Total` nulo → la línea no se pinta. Regla: **un comparativo nulo nunca se convierte en 0**. La excepción son las expresiones `COUNT(*)`/`COUNT(DISTINCT)` (dan 0 sobre cero filas): hoy ninguna métrica de KPI las usa; si aparece una, su comparativo en 0 se trata como «sin dato». Prueba: empresa con datos solo de 2026 → `AnioAnterior` nulo, no 0.
- `SerieWidget` es un record público usado por Web, reportes y posiblemente IA: revisar el `impact` completo.
- **(2026-11-21, UI-02)** Si UI-02 ya está fusionado: conservar `SerieWidget.Tendencia` y `Diferencia`. Para métricas con unidad `%`, mostrar «año anterior» y «acumulado» como diferencia en puntos (`Formato.Puntos`), no como variación relativa.
