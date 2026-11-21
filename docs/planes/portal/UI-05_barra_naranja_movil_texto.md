# UI-05 — Barra naranja, menú en móvil y tamaño de texto en gráficos

Rama: `portal/ui-05-barra-graficos` desde `portal/ui-03-tooltips`. Capa: Web.
Archivos: `Components/Layout/MainLayout.razor`, `wwwroot/app.css`, `Components/Dashboards/WidgetVista.razor`, `LineaVista.razor`, `CascadaVista.razor`, `ApiladasVista.razor`, `Components/Pages/Dashboards/Tablero.razor`, `wwwroot/dashboards.css`, pruebas, `docs/contexto/06_plan_pendientes_decisiones.md`.
Símbolos para `impact`: `MainLayout`, `WidgetVista`, `LineaVista`, `CascadaVista`, `ApiladasVista`, `Tablero`. Son Razor, así que hay que confirmarlos con búsqueda de texto.
Decisiones del dueño (2026-11-21): barra superior **naranja**; render cerrado como SSR + JS propio (**D66**).

## A. Barra superior naranja
- Color: `#D34615` (`--naranja-osc`), no `#E95420`. Con texto blanco da 4,51:1 (AA). `#E95420` da 3,6:1 y lo descarta el propio manual de marca.
- `app.css`, reemplazando las reglas actuales de `.barra-app`:
  - `.barra-app { background: var(--naranja-osc); border-bottom: 3px solid var(--naranja-hover); }`
  - `.barra-app .navbar-brand, .barra-app .nav-link, .barra-app .navbar-text { color:#fff; }`
  - `.barra-app .nav-link:hover, .barra-app .nav-link:focus-visible { color:#fff; background: var(--naranja-hover); border-radius: var(--radio-btn); }`
  - Foco dentro de la barra: `outline: 2px solid #fff; outline-offset: 2px`. Quitar el `box-shadow` naranja de `.btn:focus` solo dentro de `.barra-app`, porque naranja sobre naranja no se ve.
  - `.isotipo` dentro de la barra: `background:#fff; color: var(--naranja-osc);`.
- `MainLayout.razor`: el botón «Salir» pasa a `btn btn-outline-light btn-sm`.
- No cambian los enlaces, las políticas, el formulario de salida ni el pie.

## B. Menú plegable en pantallas angostas (sin JS)
Bootstrap `collapse` necesita `bootstrap.js`, que no se carga. Se usa un checkbox con CSS, que la CSP permite y no requiere script.
- En `MainLayout.razor`, dentro de `<Authorized>` y antes del `<ul>`:
  ```razor
  <input type="checkbox" id="menu-app" class="menu-app-toggle" aria-label="Mostrar u ocultar el menú" />
  <label for="menu-app" class="menu-app-boton" aria-hidden="true">Menú ☰</label>
  ```
- El `<ul class="navbar-nav me-auto">` y el bloque de usuario y «Salir» quedan envueltos en `<div class="menu-app">…</div>`.
- `<header>`: `class="navbar barra-app px-3"` (sin `navbar-expand`; el CSS controla el diseño).
- `app.css`:
  - `.menu-app-toggle` visualmente oculto pero enfocable (patrón `position:absolute; opacity:0; width:1px; height:1px`).
  - `.menu-app-toggle:focus-visible + .menu-app-boton { outline:2px solid #fff; outline-offset:2px; }`
  - **≥ 1200 px:**
    - `.menu-app-boton { display:none }`;
    - `.menu-app { display:flex; flex:1; align-items:center; }`;
    - `.menu-app .navbar-nav { flex-direction:row; }`.
  - **< 1200 px:**
    - `.barra-app { flex-wrap:wrap; }`;
    - `.menu-app-boton { display:inline-block; margin-left:auto; color:#fff; cursor:pointer; padding:.25rem .5rem; border:1px solid rgba(255,255,255,.6); border-radius: var(--radio-btn); }`;
    - `.menu-app { display:none; width:100%; flex-direction:column; align-items:flex-start; padding:.5rem 0; }`;
    - `.menu-app-toggle:checked ~ .menu-app { display:flex; }`.
  - El corte es 1200 px, no 992, porque con todos los permisos la barra mide unos 1400 px.
- Criterio: a 390×844 no hay scroll horizontal en la página (`document.documentElement.scrollWidth <= innerWidth`), con el menú cerrado y con el menú abierto.

## C. Tamaño del texto en los gráficos
Problema: todos los SVG usan `viewBox` de 640×240 y `width:100%`. Un widget de 12 columnas amplía el texto unas 2 veces y uno de 4 lo reduce a la mitad.
- `WidgetVista.razor`: agregar el parámetro `[Parameter] public int Columnas { get; set; } = 6;` y pasarlo a `LineaVista`, `CascadaVista` y `ApiladasVista`. Dona, barras, tabla y KPI no cambian.
- `Tablero.razor`: `<WidgetVista … Columnas="@w.Ancho" />`.
- En cada una de las tres vistas: `[Parameter] public int Columnas { get; set; } = 6;` y `W` deja de ser constante: `var W = Math.Clamp(Columnas * 100, 480, 960);`. `H` y los márgenes no cambian. Todo lo que hoy usa `W` lo sigue usando; revisar que no quede ningún `640` fijo.
- Efecto en escritorio (1440 px): el texto queda entre ~0,9× y ~1,35× del tamaño nominal (hoy va de 0,5× a 2×).
- **Limitación aceptada (móvil):** en una sola columna (~360 px), un widget de 12 columnas queda a ~0,37× (hoy ~0,56×). Para leer las cifras en el celular está «Ver los datos». Arreglarlo exige medir el ancho con JS; queda fuera, ver D66.

## Pruebas
- `DashboardsWebTests`:
  - cambiar la afirmación `class="navbar navbar-expand barra-app` por `class="navbar barra-app`;
  - agregar que contiene `id="menu-app"` y `btn-outline-light`;
  - en el gerencial, el widget de línea de 8 columnas lleva `viewBox="0 0 800 240"`; en el P&G, la cascada de 6 columnas lleva `viewBox="0 0 600 240"`. Verificar el ancho real de cada widget en `ModuloFinanciero.cs` antes de afirmar.
- Las demás afirmaciones (`style="width:NN%"`, `role="img"`, `<details class="datos">`, `linearGradient` solo con una serie, `class="zona"`) siguen pasando.
- Capturas (Playwright, deviceScaleFactor 2) en `capturas/ui05/`:
  - gerencial y P&G a 1440×900;
  - gerencial a 390×844 con el menú cerrado y abierto (clic en la etiqueta);
  - comprobación de que no hay scroll horizontal a 390 px, con el resultado en el PR.

## Docs
- `06` §10 «Decisiones de diseño abiertas»: borrar la viñeta **Render**.
- `06` §12: agregar `| D66 | Render de dashboards: SSR + estado en URL + SVG, con JS propio solo como mejora progresiva (tooltips); sin InteractiveAuto, MudBlazor ni librerías de gráficos. Se reabre si un piloto pide filtros cruzados sin recargar (UI-03, UI-05) |`.
- `docs/planes/portal/00_INDICE.md`: agregar UI-05 a la tabla de orden y marcar D66 en «Corrección al análisis previo».
