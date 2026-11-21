# UI-02 — KPI: variación en puntos porcentuales y tendencia

Rama: `portal/ui-02-kpi` desde `portal/ui-01-marca`. Capas: Application + Web.
Archivos: `src/IAnalitica.Application/Semantica/Contratos.cs`, `src/IAnalitica.Application/Semantica/Consultas.cs` (`ObtenerDatosWidgetHandler.CalcularAsync`), `src/IAnalitica.Web/Components/Dashboards/KpiVista.razor`, `Formato.cs`, `dashboards.css`, pruebas.
Símbolos para `impact` (upstream): `SerieWidget`, `ObtenerDatosWidgetHandler`, `KpiVista`, `Formato`.

## A. Variación en puntos para unidad `%` (error de cifra)
Hoy el margen de 18,7 % → 17,5 % se muestra como «−6,4 %». Lo correcto es «−1,2 pts».
- `SerieWidget` (en `Contratos.cs`): agregar una propiedad calculada, **sin tocar `Variacion`**, porque la usan pruebas y PL-07:
  ```csharp
  /// <summary>Diferencia absoluta frente al periodo anterior (para métricas en %, se lee en puntos porcentuales).</summary>
  public decimal? Diferencia => Resultado.Total is { } a && Anterior is { } b ? a - b : null;
  ```
- `Formato.cs`: `public static string Puntos(decimal diferencia) => (diferencia > 0 ? "+" : "") + diferencia.ToString("0.#", Co) + " pts";`
- `KpiVista.razor`: si `r.Unidad == Unidades.Porcentaje`, usar `Serie.Diferencia` (texto `Formato.Puntos`) para el texto, la flecha y `mejor/peor`. Así funciona también cuando `Anterior == 0`. En cualquier otra unidad no cambia nada.
- Texto: «▼ −1,2 pts frente al periodo anterior (18,7 %)».

## B. Tendencia (sparkline) en el KPI
### Application
- `Contratos.cs`:
  ```csharp
  public sealed record PuntoTendencia(int Periodo, decimal? Valor);
  ```
  En `SerieWidget`, propiedad **init** (no posicional: no rompe `new SerieWidget(r, a, m, s)` en pruebas ni PL-07):
  `public IReadOnlyList<PuntoTendencia> Tendencia { get; init; } = [];`
- `CalcularAsync`, dentro de `if (widget.Tipo == TipoWidget.Kpi)`, llamar a un nuevo `TendenciaAsync(metrica, request.Filtros, ct)`. Condiciones, todas necesarias:
  - la métrica tiene la dimensión `Dimensiones.Periodo`;
  - `!metrica.Acumulado`;
  - `PeriodoDesde` y `PeriodoHasta` no son nulos y abarcan ≥ 2 periodos.
  
  Consulta: `motor.EjecutarAsync(new ConsultaMetrica(metrica.Codigo, new[] { Dimensiones.Periodo }, f), ct)`. Mapear cada fila a `PuntoTendencia(int.Parse(clave), valor)`, ordenar por periodo y conservar los **últimos 24**.
  - **Verificar al implementar:** el formato de `Dimensiones[periodo].Clave` (se espera `yyyymm`, como `FechaKey/100`; la etiqueta es `2026-01`). Si no es un entero `yyyymm`, parsear desde lo que el motor entregue y documentarlo. No inventar.
  - **Verificar** que `EjecutarAsync` aplica la `Calculadora` cuando existe (márgenes). Si no la aplica con dimensiones, no calcular tendencia para métricas con `Calculadora` y dejarlo anotado en el PR.
- Construcción: `series.Add(new SerieWidget(resultado, anterior, meta, semaforo) { Tendencia = tendencia });`.
- Costo: una consulta más por KPI, dentro del mismo `cache.ObtenerOCrearAsync` (no cambia la clave de caché). Anotar en el PR.

### Web
- `KpiVista.razor`: si `Serie.Tendencia.Count(p => p.Valor is not null) >= 2`, debajo de la variación:
  ```razor
  <svg class="kpi-tendencia" viewBox="0 0 120 32" preserveAspectRatio="none" role="img" aria-label="@TextoTendencia">…</svg>
  ```
  - Una `polyline` con los puntos no nulos (x equiespaciado por índice del periodo, y escalado a [3, 29] entre min y max de la serie; si min == max, línea en y = 16). Usar `stroke: var(--naranja-osc)`, `stroke-width: 2`, `fill: none` y `vector-effect: non-scaling-stroke`.
  - Último punto: `circle r=2.5`.
  - `TextoTendencia` = `"Tendencia de {n} meses: de {Formato.Valor(primero)} ({etiqueta}) a {Formato.Valor(último)} ({etiqueta})"`. La etiqueta del periodo es `yyyy-mm`.
  - Formatear con `CultureInfo.InvariantCulture`, como el resto de SVG.
- `dashboards.css`: `.kpi-tendencia { width:100%; height:2rem; margin-top:.5rem; display:block; }`.

## Pruebas
- Application (`SemanticaYReportesTests`):
  - `Diferencia` con 18,7 y 17,5 da −1,2;
  - `Formato.Puntos` no aplica aquí (es de Web).
  - Handler de KPI con métrica por periodo y rango de 3 meses da `Tendencia.Count == 3` en orden.
  - Con `Acumulado = true` da vacía.
  - Sin rango da vacía.
  - Usar los dobles que ya usa el archivo para `IMotorMetricas`.
- Integración (`DashboardsWebTests`, tablero gerencial con enero y febrero): el HTML contiene `class="kpi-tendencia"`.
  - Si el seed tiene margen en ambos meses, afirmar el texto `pts`. Si no lo tiene, agregar el caso con datos mínimos.
  - Las afirmaciones actuales (`+100 %`, «frente al periodo anterior») siguen pasando.
- `DashboardsTests.cs:132` (`feb.Variacion == 1`) no cambia.

## Fuera de alcance
Metas en barra de progreso (el semáforo ya existe; PL-04 trae la pantalla de metas) y año anterior (PL-07).

## Docs
- `03` §17 «Renderizador»: «KPI: valor, variación (en puntos si la unidad es %), semáforo y tendencia de hasta 24 periodos».
- `docs/planes/PL-07_comparativos.md`: la nota ya está agregada.
