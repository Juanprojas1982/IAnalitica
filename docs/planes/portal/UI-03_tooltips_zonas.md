# UI-03 — Tooltips propios y zonas de lectura (mejora progresiva)

Rama: `portal/ui-03-tooltips` desde `portal/ui-04-graficos`. Capa: Web.
Archivos: `wwwroot/js/graficos.js` (nuevo), `Components/App.razor`, `Components/Dashboards/LineaVista.razor`, `ApiladasVista.razor`, `wwwroot/dashboards.css`.

## Objetivo
Leer un valor al pasar el mouse o tocar, sin el retardo ni el estilo del `<title>` nativo. Sin JS todo sigue igual: el `<title>` nativo y «Ver los datos» siguen ahí.

## Restricciones
- CSP: `script-src 'self'` → archivo externo, **sin** `onclick`, sin `eval` y sin estilos en atributos creados por JS salvo `style.left/top` (permitido: `style-src 'unsafe-inline'` ya existe).
- No hay `blazor.web.js` ni navegación mejorada: un `DOMContentLoaded` basta. Aun así, se usa **delegación en `document`** para no depender de él.
- Sin dependencias. ES2017, sin módulos. Menos de 3 KB sin minificar está bien. No minificar.

## `wwwroot/js/graficos.js`
Comportamiento:
1. Un único `div.tip` creado al primer uso y agregado a `body`, con `aria-hidden="true"`. Es duplicado visual: el lector de pantalla ya tiene el `<title>`/`aria-label`.
2. `pointerover` en `document`:
   - Buscar `e.target.closest('figure.grafico svg [data-tip], figure.grafico svg *')`.
   - Subir hasta el primer elemento que tenga `data-tip`, o un hijo directo `<title>` que **no** sea el `<title>` raíz del `svg`.
   - Si tiene `<title>` y no `data-tip`: copiar el texto a `data-tip` y a `aria-label`, y **quitar** el `<title>` para que no salga además el tooltip nativo.
   - Mostrar `.tip` con ese texto.
3. `pointermove`: posicionar `.tip` a 12 px del puntero (`clientX/clientY` + `scrollX/scrollY`), sin salirse del viewport (voltear a la izquierda o arriba si hace falta).
4. `pointerout` (cuando sale del elemento), `scroll` y `keydown Escape`: ocultar.
5. Táctil: `pointerover` también se dispara al tocar. Un toque fuera de un elemento con tip lo oculta.

## Zonas de lectura en líneas (`LineaVista.razor`)
- Después de las series, por cada categoría `i`: un `rect class="zona"` de ancho `(W−ML−MR)/max(1, n−1)` centrado en `X(i)` (recortado a `[ML, W−MR]`), alto `H−MT−MB` desde `MT`, `fill="transparent"`.
- Hijo `<title>`: `"{etiqueta} · {serie1}: {valor1} · {serie2}: {valor2}"` (todas las series, `Formato.Valor`; «—» si es nulo).
- Las zonas van **encima** de los círculos: el tooltip por categoría reemplaza el del punto. Se conservan los `<title>` de los círculos (lectores de pantalla y sin JS).
- CSS: `.grafico .zona:hover { fill: rgba(51,51,51,.05); }`. Es una banda vertical que marca la categoría; funciona sin JS.

## Apiladas (`ApiladasVista.razor`)
Igual que en línea, una `rect.zona` por categoría con el total y el desglose de la columna en su `<title>`. Va **debajo** de los segmentos (primero en el DOM), para que el segmento siga teniendo su propio tooltip y la zona cubra el espacio vacío sobre la barra.

## CSS
```css
.tip { position:absolute; z-index:1080; pointer-events:none; max-width:18rem; padding:.4rem .6rem; border-radius:var(--radio-btn);
       background:var(--tx); color:#fff; font-size:.8rem; line-height:1.3; box-shadow:var(--sombra); }
.tip[hidden] { display:none; }
```

## `App.razor`
`<script src="@Assets["js/graficos.js"]"></script>` después de `copiar.js`.

## Pruebas
- Integración (`DashboardsWebTests`):
  - el tablero gerencial contiene `js/graficos.js` y `class="zona"`;
  - el `<title>` de una zona contiene las dos series del widget de líneas (texto exacto según el seed).
- Manual con Playwright (script en el scratchpad, no versionado):
  1. pasar el mouse sobre una zona del gráfico de líneas;
  2. verificar que aparece `.tip` visible con el texto;
  3. verificar que no hay errores en la consola;
  4. verificar que la CSP no bloquea nada (escuchar `securitypolicyviolation`).
  
  Captura del tooltip en el PR.

## Fuera de alcance
Teclado sobre los puntos (la tabla «Ver los datos» cubre WCAG), zoom, brush y ocultar series desde la leyenda.

## Docs
- `03` §17 «Renderizador»: «SSR+SVG con mejora progresiva (`js/graficos.js`: tooltips y zonas)».
- `06` §13: la fila «Dashboards con MudBlazor/ApexCharts…» queda «SSR + estado en URL + SVG; JS propio solo para tooltips (sin librerías)».
