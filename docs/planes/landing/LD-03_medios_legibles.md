# LD-03 — Medios legibles en móvil y en el bloque del asistente

Rama: `landing/ld-03-medios` desde `main` con LD-04 fusionado. Rutas relativas a `landing/`.
Archivos: `tools/escena.html`, `tools/render.mjs`, `tools/revisar.mjs`, `dist/assets/*`, `src/pages/index.html`, `src/pages/asistente-ia.html`, `dist/css/site.css`, `dist/js/site.js`, `README.md`, `dist/*.html` regenerados.
Usa `[hidden]{display:none!important}` de LD-01 (si no estuviera, agregarlo en `site.css`).

## Problema (medido el 2026-11-17)
La escena se graba a 1280×720 y se muestra reducida:
| Medio | Ancho mostrado | Escala | Texto más pequeño de la escena (11,5 px) se ve a |
|---|---|---|---|
| Hero en 390 px | 348 px | 0,27 | 3,1 px |
| Asistente (portada) en 1366 px | 570 px | 0,45 | 5,1 px |
| Asistente en 390 px | 348 px | 0,27 | 3,1 px |
Además, el móvil descarga el video del hero (WebM de 327 kB).

## Paso 0 (hecho en diseño)
- `tools/escena.html`: una sola página de 1280×720 con cuatro vistas (`.g.v[data-v]`: `gerencial`, `cartera`, `ciclo`, `asistente`). Parámetros de URL: `v` (vista), `t=end` (último cuadro, para capturas), `dur` (duración del bucle en ms). **No hay variante compacta**: se agrega el parámetro `c=1`. La escena no muestra ningún rótulo «en pruebas internas» (lo dicen la sección y la página del asistente); se mantiene así.
- `tools/render.mjs`: captura PNG de las 4 vistas a 1280×720 en `tools/.tmp`, graba 12 s de `gerencial` y `asistente` con `recordVideo` de Playwright y los codifica con `ffmpeg` a `hero-*.webm` (VP9, crf 38) y `hero-*.mp4` (H.264, crf 29). **La conversión PNG → WebP no está en el script** (se hacía a mano); este plan la agrega con `ffmpeg`, que ya es requisito del script.
- `ffmpeg -version` → 6.1.1, con `libwebp`, `libvpx-vp9` y `libx264`. Verificado.
- Uso actual de medios: `index.html` (hero: `hero-gerencial.*` + póster `dash-gerencial.webp`; asistente: `hero-asistente.*` + `dash-asistente.webp`), `asistente-ia.html` (`hero-asistente.*` + `dash-asistente.webp`), `funcionalidades.html` (`dash-gerencial.webp`, `dash-ciclo.webp`, `dash-cartera.webp`), `tools/og.html` (`dash-gerencial.webp`). Los cuatro `dash-*-640.webp` **no se usan en ninguna página**.
- Prototipo de la variante compacta renderizado el 2026-11-17 con el CSS de abajo: se ve completo y sin cortes; WebP 1x de 21 kB (gerencial) y 28 kB (asistente), 2x de 44 kB (gerencial); video compacto del asistente de 158 kB (WebM) y 103 kB (MP4).

## Diseño (cerrado)

### 1. Variante compacta de la escena: `?c=1`
Lienzo **720×540** (4:3). Texto mínimo **24 px** → en 348 px se ve a 11,6 px; en 572 px, a 19 px. Mismos datos ficticios, mismo encabezado «Clínica Ejemplo · Datos ficticios».
- **Gerencial compacta:** encabezado (logo, «Tablero gerencial», «Clínica Ejemplo · Datos ficticios»); 4 tarjetas en 2×2 (Ingresos, Costos y gastos, Utilidad, Margen) con la cifra y la variación sin «vs. periodo anterior» («▲ 6,4 %»); debajo, a todo el ancho, la gráfica «Ingresos, costos y utilidad por mes». Ocultos: chips de filtros, «Facturado en el periodo», «Cartera por cobrar» y el pie `.mk`.
- **Asistente compacta:** encabezado + solo la conversación (pregunta, respuesta que se escribe y «Fuente: …»). Ocultos: «Cartera mayor a 90 días por EPS», «Control de consumo», chips, el `h2` repetido «Asistente analista» de la tarjeta y `.mk`.

Cambios exactos en `tools/escena.html`:
1. CSS, inmediatamente después de `.hid{display:none!important}`:
```css
/* Variante compacta (?c=1): lienzo 720×540, texto mínimo 24 px */
.cmp body{width:720px;height:540px;font-size:24px}
.cmp body:before{background-size:72px 72px}
.cmp .top{left:24px;right:24px;top:16px;gap:16px}
.cmp .logo{width:48px;height:48px;font-size:22px;border-radius:8px}
.cmp .top h1{font-size:28px}.cmp .top small{font-size:24px}
.cmp .chips,.cmp .mk,.cmp .solo-amplia{display:none!important}
.cmp .g{left:24px;right:24px;top:92px;bottom:24px;gap:16px;grid-template-columns:repeat(2,1fr);grid-template-rows:auto auto 1fr}
.cmp .g>.c{grid-column:auto!important;grid-row:auto!important;padding:12px 16px;border-radius:10px}
.cmp .g>.c.ancha{grid-column:1/-1!important}
.cmp .c h2{font-size:24px;letter-spacing:.02em;margin-bottom:4px;text-transform:none}
.cmp .k{font-size:40px;display:inline-block}
.cmp .d{font-size:24px;display:inline-block;margin-left:10px}
.cmp .d .lg{display:none}
.cmp .chat{font-size:24px;gap:16px}.cmp .m{padding:14px 18px;border-radius:14px}
.cmp .fuente{font-size:24px}
.cmp [data-v=asistente]{grid-template-rows:1fr}.cmp [data-v=asistente] h2{display:none}
.cmp #l1,.cmp #l2{stroke-width:4px;vector-effect:non-scaling-stroke}.cmp #pt,.cmp .glow:after{display:none}
.cmp .ancha{display:flex;flex-direction:column}.cmp .ancha svg{flex:1;min-height:0}
```
2. Marcado (solo agregar clases o un `<span>`; ningún texto cambia):
   - Vista gerencial, en las 4 primeras tarjetas: `vs. periodo anterior</div>` → `<span class="lg"> vs. periodo anterior</span></div>` (Ingresos, Costos y gastos, Utilidad, Margen; **no** en la vista ciclo).
   - Tarjeta de la gráfica: `<div class="c" style="grid-column:span 8;grid-row:span 4"><h2>Ingresos, costos…` → `<div class="c ancha" …>`.
   - Tarjetas «Facturado en el periodo» y «Cartera por cobrar» (vista gerencial): agregar clase `solo-amplia`.
   - Vista asistente: tarjeta del chat → clase `ancha`; tarjetas «Cartera mayor a 90 días por EPS» y «Control de consumo» → clase `solo-amplia`.
3. Script: en la primera línea, después de `const q=new URLSearchParams(location.search),` agregar `CMP=q.get('c')==='1',`; y antes de `document.getElementById('tit').textContent=…` agregar `if(CMP)document.documentElement.classList.add('cmp');`.
Sin `c=1` la escena queda idéntica a hoy.

### 2. `tools/render.mjs` (reemplazo completo del cuerpo)
```js
// Genera los medios del sitio desde tools/escena.html: capturas WebP (amplias y compactas) y videos (WebM y MP4). Requiere ffmpeg. Uso: node tools/render.mjs
import { chromium } from 'playwright'; import { mkdirSync, renameSync } from 'node:fs'; import { execSync } from 'node:child_process'; import { pathToFileURL } from 'node:url'; import path from 'node:path';
const raiz = path.resolve(import.meta.dirname, '..'), url = q => pathToFileURL(path.join(raiz, 'tools/escena.html')).href + '?' + q;
const out = path.join(raiz, 'dist/assets'); mkdirSync(out, { recursive: true }); const tmp = path.join(raiz, 'tools/.tmp'); mkdirSync(tmp, { recursive: true });
const AMPLIA = { width: 1280, height: 720 }, COMPACTA = { width: 720, height: 540 };
const b = await chromium.launch();
// Capturas: [vista, compacta, escala, archivo]
for (const [v, c, dsf, nombre] of [['gerencial', false, 1, 'dash-gerencial'], ['cartera', false, 1, 'dash-cartera'], ['ciclo', false, 1, 'dash-ciclo'],
  ['gerencial', true, 1, 'dash-gerencial-c'], ['gerencial', true, 2, 'dash-gerencial-c-2x'], ['asistente', true, 1, 'dash-asistente-c'], ['asistente', true, 2, 'dash-asistente-c-2x']]) {
  const ctx = await b.newContext({ viewport: c ? COMPACTA : AMPLIA, deviceScaleFactor: dsf }); const p = await ctx.newPage();
  await p.goto(url(`v=${v}&t=end${c ? '&c=1' : ''}`)); await p.waitForTimeout(400);
  const png = path.join(tmp, `${nombre}.png`); await p.screenshot({ path: png }); await ctx.close();
  execSync(`ffmpeg -y -loglevel error -i ${png} -c:v libwebp -quality 80 -compression_level 6 ${out}/${nombre}.webp`);
}
// Videos (12 s en bucle): [vista, compacta, archivo]
for (const [v, c, nombre] of [['gerencial', false, 'hero-gerencial'], ['asistente', true, 'hero-asistente-c']]) {
  const size = c ? COMPACTA : AMPLIA, dur = 12000;
  const ctx = await b.newContext({ viewport: size, recordVideo: { dir: tmp, size } }); const p = await ctx.newPage();
  await p.goto(url(`v=${v}&dur=${dur}${c ? '&c=1' : ''}`)); await p.waitForTimeout(dur + 300);
  const f = await p.video().path(); await ctx.close(); const i = path.join(tmp, `video-${nombre}.webm`); renameSync(f, i);
  execSync(`ffmpeg -y -loglevel error -ss 0.4 -i ${i} -an -c:v libvpx-vp9 -b:v 0 -crf 38 -row-mt 1 ${out}/${nombre}.webm`);
  execSync(`ffmpeg -y -loglevel error -ss 0.4 -i ${i} -an -c:v libx264 -crf 29 -preset slow -pix_fmt yuv420p -movflags +faststart ${out}/${nombre}.mp4`);
}
await b.close(); console.log('Medios generados en dist/assets');
```
Después: `node tools/og.mjs` (sigue usando `dash-gerencial.webp`).

### 3. Archivos de `dist/assets`
- **Nuevos:** `dash-gerencial-c.webp` (720×540), `dash-gerencial-c-2x.webp` (1440×1080), `dash-asistente-c.webp`, `dash-asistente-c-2x.webp`, `hero-asistente-c.webm`, `hero-asistente-c.mp4` (720×540).
- **Se regeneran:** `dash-gerencial.webp`, `dash-cartera.webp`, `dash-ciclo.webp`, `hero-gerencial.webm`, `hero-gerencial.mp4`, `og.png`.
- **Se borran** (sin referencia después de este plan): `dash-asistente.webp`, `hero-asistente.webm`, `hero-asistente.mp4`, `dash-gerencial-640.webp`, `dash-cartera-640.webp`, `dash-ciclo-640.webp`, `dash-asistente-640.webp`.

### 4. Hero (`src/pages/index.html`): imagen fija siempre, video solo en escritorio
Reemplazar el `<video data-hero …>…</video>` y el `<span class="nota">…</span>` dentro de `<figure class="marco" …>` por:
```html
<picture><source media="(max-width:640px)" srcset="/assets/dash-gerencial-c.webp 720w, /assets/dash-gerencial-c-2x.webp 1440w" sizes="calc(100vw - 42px)" width="720" height="540"><img src="/assets/dash-gerencial.webp" width="1280" height="720" fetchpriority="high" alt="Tablero gerencial ilustrativo con ingresos, costos, utilidad y margen, con datos ficticios"></picture>
<video data-video data-webm="/assets/hero-gerencial.webm" data-mp4="/assets/hero-gerencial.mp4" hidden muted loop playsinline preload="none" width="1280" height="720" aria-label="Animación ilustrativa de un tablero gerencial con ingresos, costos, utilidad, margen, facturado y cartera, con datos ficticios"></video>
<span class="nota" data-nota-video="Datos ficticios · animación ilustrativa">Datos ficticios · imagen ilustrativa</span>
```
(El `<div class="bar">` del marco no cambia.)

### 5. Bloque del asistente: compacto en todos los anchos
En `index.html` (`#asistente`), reemplazar el `<figure class="shot rev" …>…</figure>` por:
```html
<figure class="shot rev" style="margin:0"><img src="/assets/dash-asistente-c.webp" srcset="/assets/dash-asistente-c.webp 720w, /assets/dash-asistente-c-2x.webp 1440w" sizes="(max-width:900px) calc(100vw - 42px), 572px" width="720" height="540" loading="lazy" alt="El asistente responde qué EPS explica el aumento de la cartera mayor a 90 días y cita su fuente, con datos ficticios"><video data-video data-webm="/assets/hero-asistente-c.webm" data-mp4="/assets/hero-asistente-c.mp4" hidden muted loop playsinline preload="none" width="720" height="540" aria-label="Animación ilustrativa del asistente respondiendo qué EPS explica el aumento de la cartera mayor a 90 días, con datos ficticios"></video><figcaption data-nota-video="Datos ficticios · animación ilustrativa · el análisis es automatizado y debe validarse antes de decisiones contables o tributarias">Datos ficticios · imagen ilustrativa · el análisis es automatizado y debe validarse antes de decisiones contables o tributarias</figcaption></figure>
```
En `asistente-ia.html`, reemplazar su `<figure class="shot" …>…</figure>` por el mismo bloque con estas diferencias: `class="shot"` (sin `rev`), **sin** `loading="lazy"`, `aria-label` del video = `El asistente responde qué EPS explica el aumento de la cartera mayor a 90 días y cita su fuente` (el actual), y `<figcaption>Datos ficticios</figcaption>` sin `data-nota-video`.

### 6. `dist/css/site.css`
- `.marco video,.marco img{width:100%;aspect-ratio:16/9;object-fit:cover}` → `.marco video{width:100%;aspect-ratio:16/9;object-fit:cover}.marco img{width:100%;height:auto}` (la imagen compacta es 4:3 y no debe recortarse).
- Agregar `.shot img,.shot video{width:100%}`.

### 7. `dist/js/site.js`
Borrar el bloque «video del hero» (las 3 líneas desde `// video del hero…` hasta `…v.controls = true; }`) y poner en su lugar:
```js
  // medios: la imagen fija va siempre; el video se monta solo en > 640 px, sin «reducir movimiento» ni ahorro de datos, al acercarse a la vista
  const conVideo = matchMedia('(min-width: 641px)').matches && !matchMedia('(prefers-reduced-motion: reduce)').matches && !navigator.connection?.saveData;
  const activar = fig => {
    const v = $('video[data-video]', fig), img = $('img', fig), nota = $('[data-nota-video]', fig);
    v.poster = img.currentSrc || img.src;
    v.innerHTML = `<source src="${v.dataset.webm}" type="video/webm"><source src="${v.dataset.mp4}" type="video/mp4">`;
    ($('picture', fig) || img).hidden = true; v.hidden = false; if (nota) nota.textContent = nota.dataset.notaVideo;
    v.load(); v.play().catch(() => {});
  };
  if (conVideo) {
    const figs = $$('video[data-video]').map(v => v.closest('figure'));
    const iov = 'IntersectionObserver' in window ? new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) { activar(e.target); iov.unobserve(e.target); } }), { rootMargin: '200px' }) : null;
    figs.forEach(f => iov ? iov.observe(f) : activar(f));
  }
```
Se observa la `figure` (el video oculto no tiene caja). La condición se evalúa una vez al cargar; girar o redimensionar después no cambia el medio. Con «reducir movimiento» o ahorro de datos, en cualquier ancho se ve la imagen fija (antes se mostraba el video con controles).

### 8. `tools/revisar.mjs`
Agregar antes de la comprobación de enlaces rotos:
```js
// LD-03: en móvil, imágenes compactas y ningún video; en escritorio, el video se activa
{ const ctx = await b.newContext({ viewport: { width: 390, height: 844 } }); const p = await ctx.newPage(); const videos = [];
  p.on('request', q => /\.(webm|mp4)(\?|$)/.test(q.url()) && videos.push(q.url()));
  for (const u of ['/', '/asistente-ia.html']) { await p.goto(B + u); await p.evaluate(() => window.scrollTo(0, document.body.scrollHeight)); await p.waitForTimeout(800); }
  if (videos.length) { console.log('✗ móvil descarga video:', videos); problemas++; }
  await p.goto(B + '/'); await p.locator('#asistente .shot img').scrollIntoViewIfNeeded(); await p.waitForTimeout(600);
  const m = await p.evaluate(() => ['.hero .marco img', '#asistente .shot img'].map(s => { const i = document.querySelector(s); return { s, w: Math.round(i.getBoundingClientRect().width), src: i.currentSrc }; }));
  for (const x of m) if (x.w < 330 || !/-c(-2x)?\.webp$/.test(x.src)) { console.log('✗ móvil, medio no compacto o angosto:', x); problemas++; }
  await ctx.close(); }
{ const ctx = await b.newContext({ viewport: { width: 1440, height: 900 } }); const p = await ctx.newPage();
  await p.goto(B + '/'); await p.locator('#asistente').scrollIntoViewIfNeeded(); await p.waitForTimeout(1500);
  const ok = await p.evaluate(() => [...document.querySelectorAll('video[data-video]')].every(v => !v.hidden && v.currentSrc));
  if (!ok) { console.log('✗ escritorio: algún video no se activó'); problemas++; }
  await ctx.close(); }
```

### 9. `README.md`
Actualizar la viñeta «Medios alineados con los textos»: `render.mjs` genera las WebP con `ffmpeg` (amplias 1280×720 para hero de escritorio, funcionalidades y `og.png`; compactas 720×540 y 1440×1080 con `?c=1` para el hero en móvil y el asistente), los videos `hero-gerencial` (1280×720, solo escritorio) y `hero-asistente-c` (720×540); el móvil nunca descarga video. Quitar la mención a las WebP de 640.

## Criterios de aceptación
1. Texto efectivo ≥ 11 px: en 390×844 el ancho mostrado de las imágenes compactas es ≥ 330 px (24 px × 330 ÷ 720 = 11 px); en 1366×768 el asistente se muestra a ≥ 500 px (≥ 16,6 px). Lo vigila la regla 8 en 390 px; el de escritorio se anota en el PR.
2. En 390 px no se descarga ningún `.webm` ni `.mp4` (regla 8).
3. Peso: cada WebP compacta 1x ≤ 60 kB y 2x ≤ 140 kB; `hero-asistente-c.webm` y `.mp4` ≤ 200 kB cada uno (`ls -l dist/assets`).
4. Ninguna referencia rota ni archivo huérfano: para cada archivo de `dist/assets/*.webp|webm|mp4`, `grep -rl <nombre> src tools/og.html` devuelve al menos un archivo; y `node tools/revisar.mjs` → «Sin problemas».
5. Sin salto de diseño: todas las imágenes y videos con `width`/`height` declarados (también en el `<source>` del `<picture>`).
6. `alt`, `aria-label` y pies conservan «datos ficticios»; con video activo, el pie dice «animación ilustrativa»; con imagen fija, «imagen ilustrativa».
7. Con «reducir movimiento» activado en escritorio (Playwright `reducedMotion: 'reduce'`): se ve la imagen fija y no se descarga video.
8. Capturas del hero y del asistente en 390 y 1366 px adjuntas al PR.

## Riesgos
- Si `ffmpeg` faltara en la sesión de implementación y no se pudiera instalar con `apt-get`, entregar solo las imágenes compactas (puntos 3 a 6 con imagen fija también en escritorio para el asistente) y decirlo en el PR; el video compacto queda pendiente.
- Safari anterior a 15 ignora `width`/`height` en `<source>`: la imagen se ve bien, solo puede haber un pequeño salto al cargar. Se acepta.

## Fuera de alcance
Cambiar datos, vistas o rótulos de la escena (salvo la variante `c=1`). Usar `srcset` en `funcionalidades.html`. Video de 90 s (pendiente de §28). `og.png` con otro diseño.
