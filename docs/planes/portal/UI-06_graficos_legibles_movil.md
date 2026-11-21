# UI-06 — Gráficos legibles en el celular (sin JS)

Rama: `portal/ui-06-graficos-movil` desde `portal/ui-05-barra-graficos`. Capa: Web.
Archivos: `Components/Dashboards/LineaVista.razor`, `CascadaVista.razor`, `ApiladasVista.razor`, `wwwroot/dashboards.css`, pruebas.
Símbolos para `impact`: `LineaVista`, `CascadaVista`, `ApiladasVista` (son Razor: confirmar con búsqueda de texto).

## Problema
Las capturas de UI-05 a 390 px muestran el texto del gráfico de líneas ilegible (~4 px). La causa es que el SVG se dibuja para el ancho de escritorio (`W` entre 480 y 960) y el navegador lo encoge entero al ancho del celular.

## Decisión de diseño
Se descarta ajustar el texto con JS. Cambiar el tamaño de letra no recoloca márgenes, ejes ni etiquetas: se cortarían y se encimarían.
**Cada componente dibuja dos lienzos en el servidor**: el amplio (el actual) y uno compacto de 360 de ancho, maquetado para el celular. El CSS muestra uno u otro. No usa JS, así que respeta D66 y la CSP. La leyenda, «Ver los datos» y las zonas de tooltip siguen funcionando igual.

## Cambios en cada vista (línea, cascada, apiladas)
1. Extraer el dibujo del `<svg>` a un método local que reciba `bool compacto` y devuelva el SVG. Calcula sus medidas así:
   - amplio: igual que hoy (`W = Math.Clamp(Columnas * 100, 480, 960)`, `H` y márgenes actuales);
   - compacto: `W = 360`, `H = 220`, `ML = 46`, `MR = 10`. `MT` y `MB` no cambian.
2. Envolver: `<div class="lienzo lienzo-amplio">…svg amplio…</div><div class="lienzo lienzo-compacto">…svg compacto…</div>`. La leyenda (`figcaption`) y `<details class="datos">` van **una sola vez**, fuera de los dos lienzos.
3. **Ids únicos:** en el compacto, todos los `id` del SVG llevan el sufijo `-m` (`t-…-m`, `d-…-m` y el `linearGradient` `g-…-m`), igual que sus `aria-labelledby` y `url(#…)`. El amplio no cambia (las pruebas afirman sus valores).
4. Reglas del compacto:
   - **Línea:** como máximo **4** etiquetas en el eje X (`paso = ceil(n / 4)`, misma regla de «la última se muestra»); ticks del eje Y con `Escala.Bonita(..., objetivo: 3)`; la etiqueta del último valor y las zonas de tooltip se mantienen.
   - **Cascada:** etiquetas de categoría recortadas a 10 caracteres con «…». El texto completo sigue en el `<title>` de cada barra. El valor debajo no cambia.
   - **Apiladas:** etiquetas del eje X con la misma regla de 4 como máximo; los segmentos y sus `<title>` no cambian.
5. `dashboards.css`:
   ```css
   .lienzo-compacto { display:none; }
   @media (max-width: 575.98px) { .lienzo-amplio { display:none; } .lienzo-compacto { display:block; } }
   ```
   El corte es 576 px y no 768: entre 576 y 768 el widget ocupa una columna de 540 a 736 px y el lienzo amplio se ve en escala ~0,7–0,9, que es legible.

## Criterios
- A 390×844, el texto de los ejes del gráfico de líneas del gerencial mide ≥ 10 px en pantalla. Medirlo con Playwright: `getBoundingClientRect().height` de un `.lienzo-compacto text.etiqueta` visible. Anotar el valor en el PR.
- A 390 px sigue sin haber scroll horizontal (repetir la comprobación de UI-05 en gerencial, P&G, cartera y FE/RIPS).
- A 1440 px el resultado es idéntico a UI-05 (comparar las capturas).
- En la página ningún `id` se repite: `document.querySelectorAll('[id]')` sin duplicados, comprobado con Playwright en gerencial y P&G.

## Pruebas
- Las afirmaciones existentes pasan sin cambios: `viewBox="0 0 800 240"`, `viewBox="0 0 600 240"`, `linearGradient` solo con una serie, `class="zona"`, `<details class="datos">`, `<th scope="row">2026-01</th>`.
  - Con dos lienzos, `linearGradient` aparece dos veces en el P&G; la afirmación de que **no** aparece en el gerencial sigue valiendo.
- Nuevas (`DashboardsWebTests`): el gerencial contiene `viewBox="0 0 360 220"` y `class="lienzo lienzo-compacto"`; `<details class="datos">` aparece **una sola vez** por widget de línea.
- Capturas en `capturas/ui06/`: gerencial y P&G a 390×844 y a 1440×900.

## Docs
- `03` §17 «Renderizador»: «cada gráfico SVG trae un lienzo compacto para pantallas < 576 px».
- `docs/planes/portal/00_INDICE.md`: agregar UI-06 a la tabla.
