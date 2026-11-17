# LD-03 — Medios legibles en móvil y en el bloque del asistente

Repo: `ianalitica-landing`. Rama: `ld-03-medios` desde la última rama integrada. Archivos: `tools/escena.html`, `tools/render.mjs`, `dist/assets/*`, `src/pages/index.html`, `dist/css/site.css`, `dist/js/site.js`, `README.md`.

## Problema (medido)
La escena se graba a 1280×720 y se muestra reducida:
| Medio | Ancho mostrado | Escala | Efecto |
|---|---|---|---|
| Hero en móvil (390 px) | 350 px | 0,27 | Las cifras del tablero no se leen |
| Asistente en escritorio (1366 px) | 570 px | 0,45 | La pregunta y la respuesta no se leen |
| Asistente en móvil | 348 px | 0,27 | Igual |

## Paso 0 (antes de diseñar el detalle)
Leer `tools/escena.html` y `tools/render.mjs` para ver cómo se elige la vista (gerencial / asistente) y cómo se generan WebM, MP4, WebP 1280/640 y `og.png`. El plan asume que la escena admite variantes por parámetro; si no, se agrega.

## Diseño
1. **Variante compacta de la escena** (parámetro nuevo, p. ej. `?c=1`): lienzo de 720×540, tipografía y tarjetas a mayor tamaño, menos contenido:
   - Gerencial compacta: las 4 cifras (ingresos, costos y gastos, utilidad, margen) y la gráfica. Sin filtros ni tarjetas laterales.
   - Asistente compacta: solo la conversación (pregunta, respuesta y fuente). Sin los paneles laterales.
   Mismos datos ficticios y mismos rótulos («Datos ficticios», asistente «en pruebas internas»). No se agrega ninguna funcionalidad que no esté hoy en la escena (regla del `README.md`).
2. **Hero en móvil (≤ 640 px): imagen fija, no video.** `<picture>` con la captura compacta gerencial (WebP 720 y 1440 de ancho, `width`/`height` declarados). El video solo se monta en > 640 px: sus `<source>` pasan a `data-src` y `site.js` los activa si `matchMedia('(min-width:641px)')` y no hay `prefers-reduced-motion` ni `saveData` (esa condición ya existe en `site.js`). Así el móvil no descarga el video.
3. **Bloque del asistente:** usar la variante compacta en todos los anchos (video compacto en escritorio; imagen fija compacta en ≤ 640 px con el mismo mecanismo).
4. `render.mjs`: generar también los recursos compactos. Nombres: `dash-gerencial-c.webp`, `dash-gerencial-c-2x.webp`, `dash-asistente-c.webp`, `dash-asistente-c-2x.webp`, `hero-asistente-c.webm`, `hero-asistente-c.mp4`. Borrar de `dist/assets` lo que quede sin referencia.

## Criterios de aceptación
- Tamaño efectivo del texto más pequeño de cada medio ≥ 11 px en 390 px (móvil) y en 1366 px (escritorio). Efectivo = tamaño en la escena × ancho mostrado ÷ ancho del lienzo.
- En 390 px la pestaña de red no descarga ningún `.webm` ni `.mp4`.
- Peso de cada WebP compacta ≤ 60 kB (1x) y ≤ 140 kB (2x). No se agregan librerías.
- `alt` / `aria-label` describen lo que se ve y conservan «datos ficticios».
- `node tools/revisar.mjs` → «Sin problemas». Sin salto de diseño al cargar (dimensiones declaradas).

## Riesgo
`ffmpeg` puede no estar instalado en la sesión: comprobar `ffmpeg -version` antes. Si falta y no se puede instalar, entregar solo las imágenes fijas compactas (puntos 2 y 3 en móvil) y dejar el video del asistente como pendiente, dicho en el PR.
