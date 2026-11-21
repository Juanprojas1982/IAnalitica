# UI-04 — Acabado de gráficos (línea, cascada, dona, apiladas, barras)

Rama: `portal/ui-04-graficos` desde `portal/ui-02-kpi`. Capa: Web.
Archivos: `Components/Dashboards/LineaVista.razor`, `CascadaVista.razor`, `DonaVista.razor`, `ApiladasVista.razor`, `wwwroot/dashboards.css`.
Símbolos para `impact`: cada componente (`LineaVista`, `CascadaVista`, `DonaVista`, `ApiladasVista`) y `Paleta`.

## Objetivo
Llevar los gráficos al nivel del mockup del landing (`landing/dist/assets/dash-gerencial.webp`) sin cambiar los datos, la accesibilidad ni el marcado que afirman las pruebas.

## Línea (`LineaVista.razor`)
- **Área bajo la serie 0**, solo si `min >= 0`:
  - `<defs><linearGradient id="g-@Datos.Dashboard-@Datos.Widget" x1="0" y1="0" x2="0" y2="1">` con dos `stop`: `Paleta.De(0)` con opacidad .18 y la misma con opacidad 0.
  - `<path>` cerrado desde el primer punto no nulo hasta el último, bajando a `Y(min)`. Va antes de las `polyline` y con `aria-hidden="true"`.
  - Si la serie 0 tiene huecos (`null`), no se dibuja el área.
  - El id incluye dashboard y widget porque los ids deben ser únicos en la página.
- `polyline`: agregar `stroke-linejoin="round" stroke-linecap="round"`.
- Puntos: `r=3` normales. El último punto no nulo de cada serie con `r=4.5`, `stroke="#fff" stroke-width="2"`.
- Etiqueta del último valor **solo de la serie 0**:
  - `Svg.Texto` con `Formato.Corto(v, unidad)` encima del punto (`y − 10`).
  - Ancla `end` si es el último índice; clase `etiqueta valor`.
- No cambiar `<details class="datos">`, la tabla ni el `role="img"`.

## Cascada (`CascadaVista.razor`)
- Total con `Paleta.Total` (berenjena, UI-01). Positivo y negativo sin cambios.
- **Conectores:** entre la barra `i` y la `i+1`, una `line` horizontal en `Y(items[i].Hasta)` desde el borde derecho de la barra `i` hasta el borde izquierdo de la `i+1`, con `class="conector"`. Para la última barra de paso, el conector llega al total.
- CSS: `.grafico .conector { stroke: var(--gris); stroke-width:1; stroke-dasharray:3 3; }`.
- `rect` con `rx="2"`.

## Dona (`DonaVista.razor`)
- Total central con unidad: `Formato.Corto(total, resultado.Unidad)`.
  - Debajo, segundo `text` «Total», clase `dona-rotulo` (`font-size:10px; fill: var(--tx-suave)`).
  - Ajustar y: total en 100, rótulo en 116.
- Leyenda: el valor de cada sector con `Formato.Valor(valor, unidad)` en lugar de `Formato.Numero`. El porcentaje no cambia.
- Separación entre sectores: restar 1,5 al largo del trazo cuando hay más de un sector (`largo − 1.5`, mínimo 0). El `stroke-dashoffset` se sigue calculando con el largo real.
- `stroke-width` 24.

## Barras apiladas (`ApiladasVista.razor`)
- `stroke="#fff" stroke-width="1"` en cada `rect` (separa segmentos).
- Leyenda: verificar que existe. Si no, agregar `figcaption.leyenda` como en `LineaVista`.

## Barras (`BarrasVista.razor`, HTML)
Sin cambios de marcado (las pruebas afirman `style="width:NN%"`). Solo CSS:
- `.barra-pista { height:.7rem; border-radius:999px; }` y `.barra { border-radius:999px; }`;
- `.barras li` con `padding:.3rem 0`;
- `.barra-etiqueta` con `overflow:hidden; text-overflow:ellipsis; white-space:nowrap`.

## CSS común
- `@media (prefers-reduced-motion: no-preference)`: entrada suave de `.widget` (`opacity` 0→1 en 200 ms). Nada más se anima.
- `.grafico .etiqueta.valor { fill: var(--tx); }`.

## Pruebas
- Integración (`DashboardsWebTests`): todas las afirmaciones actuales pasan.
  - Agregar en el gerencial: el HTML contiene `linearGradient`.
  - Agregar en el P&G (cascada) o en el gerencial (si la cascada está ahí): el HTML contiene `class="conector"`.
  - Agregar en el ciclo del ingreso (dona): contiene `dona-rotulo`.
- Capturas antes/después (ver índice) en 1440 y 390 px.

## Fuera de alcance
Embudo y mapa de calor (ver índice); cambiar los gráficos a otra librería.
