# LD-04 — Estructura de la portada

Rama: `landing/ld-04-estructura` desde `main` con LD-02 fusionado. Rutas relativas a `landing/`.
Archivos: `src/pages/index.html`, `dist/css/site.css`, `dist/index.html` regenerado.

Regla: **no se escribe ninguna afirmación nueva sobre el producto.** Todos los textos de este plan ya existen en `src/pages/index.html` o en `src/pages/casos/saludips.html`; se reubican, se acortan o se unen dos frases existentes. Los textos exactos están abajo; no se cambia ninguna palabra fuera de ellos.

## Estado verificado (2026-11-17, después de los textos nuevos)
Secciones de la portada, en orden: hero · `.s` El problema · `.s.alt#funcionalidades` (hoy + lo que viene) · `.s` Auditor · `.s.osc#asistente` · `.s.alt#como-funciona` · `.s` Seguridad · `.s.alt` Para quién · `.s#casos` Integración · `.s.alt` Planes · `.s` Preguntas frecuentes · `.s.cierre`.
- En 1366 px, «Lo que puede usar hoy» (encabezado + cuadrícula) mide 642 px y «Lo que viene» 538 px (8 tarjetas con sombra, cada una con su etiqueta «Próximamente»).
- `#casos` (426 px) y Planes (399 px): dos bandas seguidas con título, una frase y un botón.
- Auditor: `.grid.g2` con «Contabilidad», «Facturación electrónica», «Tarifas» (tres ejemplos cada una) y «Usuarios» (uno).

## A. «Lo que viene» subordinado a «Lo que puede usar hoy»
En `#funcionalidades`, reemplazar desde `<div class="head rev" style="margin-top:48px"><h2>Lo que viene</h2>…` hasta el cierre de la segunda `<div class="grid g4">…</div>` (la de las 8 tarjetas «Próximamente») por:
```html
<div class="head prox-head rev"><span class="etiq">Próximamente</span><h2>Lo que viene</h2><p class="lead">En desarrollo. Se activan a medida que se validan con los pilotos.</p></div>
<ul class="prox rev">
<li><strong>Dinero detenido por etapa.</strong> Atendido sin facturar, facturado sin radicar y radicado sin pagar, por EPS.</li>
<li><strong>Glosas.</strong> Cuánto le glosan sobre lo facturado, por EPS y por causa.</li>
<li><strong>Recaudo y flujo de caja.</strong> Cuánto paga cada EPS y a cuántos días; entradas y salidas de caja y bancos.</li>
<li><strong>Presupuesto.</strong> Lo presupuestado frente a lo ejecutado, por cuenta y centro de costo.</li>
<li><strong>Agendas y productividad.</strong> Ocupación, inasistencia y su costo, y producción por profesional.</li>
<li><strong>Farmacia e inventario.</strong> Pendientes de dispensación, rotación y vencimientos por lote.</li>
<li><strong>Presupuesto público (ESE).</strong> Ejecución de ingresos y gastos y simulación de la categorización de riesgo.</li>
<li><strong>Grupos de instituciones.</strong> Vista consolidada para quien dirige varias empresas.</li>
</ul>
```
Mismo orden y mismos textos (título + punto + descripción). Una sola etiqueta «Próximamente» en el encabezado en lugar de ocho.

CSS nuevo (`site.css`, después de `.etiq`):
```css
.prox-head{margin:56px 0 20px}.prox-head h2{font-size:1.5rem;margin-top:10px}
.prox{list-style:none;padding:0;margin:0;display:grid;grid-template-columns:1fr 1fr;gap:12px 40px;font-size:.95rem;color:var(--tx-suave)}
.prox li{padding-left:14px;border-left:2px solid var(--linea)}.prox strong{color:var(--tx);font-weight:500}
@media(max-width:640px){.prox{grid-template-columns:1fr}}
```
Sin sombra ni tarjeta. Medido con un prototipo: en 1366 px «Lo que viene» queda en 356 px frente a 663 px de «hoy» (0,54); en 390 px, 0,38.

## B. Integración en dos columnas y Planes dentro del cierre
1. Reemplazar la sección `<section class="s" id="casos">…</section>` completa por:
```html
<section class="s" id="casos"><div class="w dos"><div class="rev"><p class="kicker">Integración</p><h2>Integrado de forma nativa con SaludIPS</h2><p class="lead">SaludIPS es el primer HIS con un conector propio hacia IAnalitica.</p><a class="btn btn-o" href="/casos/saludips.html">Ver la integración con SaludIPS</a></div>
<div class="card rev"><h3>Qué se conecta</h3><ul class="lista"><li>Catálogos: sedes, centros de costo, cuentas, terceros y pagadores.</li><li>Contabilidad: asientos y cartera.</li><li>Facturación y estado de la factura electrónica.</li><li>Configuración del sistema, para el auditor de configuración.</li></ul></div></div></section>
```
   - Se quita del `lead` la segunda frase («Le contamos qué se conecta y qué tableros quedan activos.»): la columna derecha ya lo muestra.
   - La lista es literal de `casos/saludips.html` («Qué se conecta»). **No** se agrega la frase de otros HIS: ya está en la pregunta frecuente «¿Funciona con un HIS distinto de SaludIPS?».
   - El ancla `#casos` se conserva.
2. **Borrar** la sección de Planes completa (`<section class="s alt"><div class="w"><div class="head c rev"><p class="kicker">Planes</p>…</section>`). Motivo: nombres y alcances de los planes son provisionales (P17); una banda propia promete más de lo que hay. El menú y el pie conservan «Planes».
3. En la sección de cierre, después de `<p><a class="btn btn-c" href="/demo.html" data-medir="clic_demo">Solicitar demo</a></p>`, agregar:
```html
<p class="planes">Un plan a la medida de su institución, según usuarios y módulos. <a href="/planes.html">Ver planes</a></p>
```
   (Une el título «Un plan a la medida de su institución» y la frase «Según usuarios y módulos» de la banda borrada; se deja de lado «Le preparamos una cotización con sus datos».)
   CSS: `.cierre .planes{margin:20px 0 0;color:var(--berenjena-tinte);font-size:.95rem}.cierre .planes a{color:#fff}` (7,8:1 y 9,4:1 sobre berenjena; el enlace conserva el subrayado del navegador; el foco ya es blanco en `.cierre`).

## C. Auditor: una tarjeta con lista de definición
En la sección del auditor, reemplazar `<div class="grid g2">…</div>` (las 4 tarjetas, columna derecha) por:
```html
<div class="card rev"><dl class="reglas">
<div><dt>Contabilidad</dt><dd>Pagadores sin cuentas contables; cuentas de cartera mal marcadas; configuración del año siguiente incompleta.</dd></div>
<div><dt>Facturación electrónica</dt><dd>Resoluciones vencidas o por vencer; numeración a punto de agotarse; sedes que facturan sin resolución.</dd></div>
<div><dt>Tarifas</dt><dd>Pagadores sin manual tarifario; servicios sin tarifa; procedimientos sin CUPS o medicamentos sin CUM.</dd></div>
<div><dt>Usuarios</dt><dd>Usuarios activos de empleados retirados.</dd></div>
</dl></div>
```
Mismos textos. No se rellenan ejemplos para «Usuarios».
CSS:
```css
dl.reglas{margin:0}dl.reglas>div{display:grid;grid-template-columns:12rem 1fr;gap:16px;padding:14px 0;border-top:1px solid var(--linea)}
dl.reglas>div:first-child{border-top:0;padding-top:0}dl.reglas>div:last-child{padding-bottom:0}dl.reglas dt{font-weight:700}dl.reglas dd{margin:0;color:var(--tx-suave)}
@media(max-width:640px){dl.reglas>div{grid-template-columns:1fr;gap:4px}}
```
La cuadrícula superior de 8 tarjetas no cambia (dos filas de 4 completas).

## D. Ritmo de fondos
Tras B, Integración (`.s`) quedaría seguida de Preguntas frecuentes (`.s`). Cambiar la sección de preguntas frecuentes de `class="s"` a `class="s alt"`. Orden final: hero · s · s.alt · s · s.osc · s.alt · s · s.alt · s (#casos) · s.alt (preguntas) · cierre. Ninguna pareja consecutiva con el mismo fondo.

## Criterios de aceptación y pruebas
1. `node tools/revisar.mjs` → «Sin problemas» (un solo `h1`, sin desbordes, sin enlaces rotos; `/planes.html` sigue enlazado desde menú, pie y cierre).
2. En 1366×768, alto de «Lo que viene» (de `.prox-head` al final de `.prox`) ≤ 60 % del de «Lo que puede usar hoy» (del primer `.head` al final de la primera `.grid.g4`). Medir con Playwright y anotar los dos valores en el PR.
3. `grep -c 'id="casos"' src/pages/index.html` → `1`; `grep -c 'class="kicker">Planes' src/pages/index.html` → `0`.
4. Los dos bloques `application/ld+json` (`SoftwareApplication`, `FAQPage`) no cambian.
5. `git diff --word-diff main -- src/pages/index.html` adjunto al PR: las únicas palabras agregadas son las de este plan (títulos de «Lo que viene» con punto, la lista «Qué se conecta» y la frase de planes del cierre).
6. Capturas de página completa en 1366 y 390 px, antes y después.

## Fuera de alcance
Prueba social y logos de clientes (LD-05). Cambios en otras páginas (incluida la sección «Lo que viene» de `funcionalidades.html`). Iconos (LD-02) y medios (LD-03). Textos del hero.
