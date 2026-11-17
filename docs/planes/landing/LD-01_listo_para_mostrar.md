# LD-01 — Listo para mostrar: cookies, notas internas y marcadores

Rama: `landing/ld-01-listo-para-mostrar` desde `main` (con `landing/` ya fusionado, ver `00_INDICE.md` hecho 1). Rutas relativas a `landing/`.
Archivos: `src/partials/foot.html`, `src/partials/head.html`, `src/pages/index.html`, `src/pages/integracion.html`, `src/pages/funcionalidades.html`, `src/pages/asistente-ia.html`, `dist/css/site.css`, `dist/js/site.js`, `dist/js/config.js` (solo comentarios), `build.py`, `tools/revisar.mjs`, `README.md`, y los `dist/*.html` regenerados.

## Objetivo
Que un gerente abra la portada sin ver nada tapado ni notas internas, y que sea imposible publicar con textos sin confirmar.

## Estado verificado (2026-11-17)
- Hecho: se quitaron las viñetas `.hero .tres`. Con el consentimiento sin decidir, el banner ya no tapa los botones en 1440×900, 1366×768 ni 390×844.
- Pendiente: en **360×740** los dos botones bajan a dos filas (477–531 y 543–596) y el banner (506–724) **sí tapa** «Ver cómo funciona». En móvil el banner mide 218 px de alto. Ninguna regla automática lo vigila.
- La regla CSS `.tres{…}` y `.tres li:before{…}` sigue en `dist/css/site.css` sin uso.
- 11 avisos `data-provisional` en 10 páginas; `#portal-por-definir` en cabecera y pie; `comercial@dominio-por-definir.co` como texto fijo en el pie; el formulario de demo sin endpoint abre un `mailto:` a un buzón que no existe.

## A. Banner de cookies

### A.1 `src/partials/foot.html`
Reemplazar el `<div id="cookies" …>…</div>` completo por (mismo texto, la segunda mitad envuelta en `.ck-mas`; se quita el `style` en línea):
```html
<div id="cookies" role="dialog" aria-label="Cookies"><strong>Cookies</strong><p>Usamos cookies de medición solo si usted lo autoriza<span class="ck-mas">, para saber qué partes del sitio le sirven. Puede rechazarlas y el sitio funciona igual</span>. <a href="/legal/privacidad.html">Más información</a>.</p><div class="b"><button class="btn btn-c" id="ck-si" type="button">Aceptar</button><button class="btn btn-g" id="ck-no" type="button">Rechazar</button></div></div>
```

### A.2 `dist/css/site.css`
1. En la regla `#cookies{…}` cambiar `left:16px;right:16px;bottom:16px;max-width:620px` por `left:auto;right:16px;bottom:16px;max-width:420px` (anclado a la derecha; los botones del hero están a la izquierda). El resto de la regla no cambia.
2. Agregar después de las reglas de `#cookies`:
```css
#cookies p{margin:.4rem 0 0}
@media(max-width:640px){#cookies{left:12px;right:12px;bottom:12px;max-width:none;padding:12px 14px;font-size:.85rem}#cookies strong,#cookies .ck-mas{display:none}#cookies p{margin:0}#cookies .b{margin-top:8px;flex-wrap:nowrap}#cookies .btn{padding:.45rem .9rem;font-size:.85rem}}
html:has(#cookies.on){scroll-padding-bottom:230px}
[hidden]{display:none!important}
```
   - En móvil queda: «Usamos cookies de medición solo si usted lo autoriza. Más información.» y los dos botones en una fila.
   - `scroll-padding-bottom` evita que el foco del teclado quede debajo del banner mientras esté visible (WCAG 2.2, 2.4.11).
   - `[hidden]` con `!important` es necesario porque `img,video{display:block}` anula el `hidden` del navegador; lo usan C (abajo) y LD-03.
3. Borrar las reglas sin uso `.tres{…}` y `.tres li:before{…}` (están en la línea de `.cta{…}`/`.resp{…}`; no tocar esas dos).
4. No cambia: `role="dialog"`, foco visible (`#cookies :focus-visible` ya es blanco), `localStorage` con la clave `ianalitica-consent`, `window.medir`. No se carga analítica.

Medido con un prototipo de estas reglas (2026-11-17): escritorio 420×194 px abajo a la derecha, sin cruce con los botones en 1440×900, 1366×768, 1366×657, 1280×720, 1024×640 y 768×1024; móvil 116 px de alto, sin cruce en 390×844 y 360×740. En 320×568 aún se cruza (se acepta: el visitante puede cerrar el banner; queda fuera del criterio).

### A.3 Regla automática en `tools/revisar.mjs`
Insertar antes de la línea `const p = await (await b.newContext()).newPage();` (la de enlaces rotos):
```js
// LD-01: en la primera visita el banner de cookies aparece y no tapa los botones del hero
for (const [ancho, alto] of [[1440, 900], [1366, 768], [390, 844], [360, 740]]) {
  const ctx = await b.newContext({ viewport: { width: ancho, height: alto } }); const p = await ctx.newPage();
  await p.goto(B + '/'); await p.waitForTimeout(300);
  const r = await p.evaluate(() => {
    const ck = document.querySelector('#cookies.on'); if (!ck) return { visible: false };
    const c = ck.getBoundingClientRect();
    const tapa = [...document.querySelectorAll('.hero .cta .btn')].some(e => { const x = e.getBoundingClientRect(); return x.left < c.right && x.right > c.left && x.top < c.bottom && x.bottom > c.top; });
    return { visible: true, tapa, alto: Math.round(c.height) };
  });
  if (!r.visible) { console.log(`✗ ${ancho}×${alto}: el banner de cookies no aparece en la primera visita`); problemas++; }
  else if (r.tapa) { console.log(`✗ ${ancho}×${alto}: el banner de cookies tapa los botones del hero`); problemas++; }
  else if (ancho <= 640 && r.alto > 120) { console.log(`✗ ${ancho}×${alto}: el banner de cookies mide ${r.alto} px (máximo 120 en móvil)`); problemas++; }
  await ctx.close();
}
```
Se mide contra cada `.btn` y no contra `.hero .cta`, porque `.cta` ocupa todo el ancho del contenedor.

## B. Avisos `data-provisional`
Son notas para el dueño. Clasificación cerrada (inventario: `grep -rn "data-provisional" src/pages`):

| Página | Texto actual (sin «Texto provisional:») | Clase | Acción |
|---|---|---|---|
| `index.html` («Para quién») | el alcance por tipo de institución se confirma al cierre de los pilotos | (a) | Frase normal |
| `integracion.html` | los HIS compatibles y los tiempos de conexión se confirman en el diagnóstico | (a) | Frase normal |
| `funcionalidades.html` (arriba) | los módulos se activan según el plan y los datos disponibles… sin fecha comprometida | (a) | Frase normal |
| `asistente-ia.html` | el asistente no está habilitado para clientes… configuración de cada plan | (a) | Frase normal |
| `funcionalidades.html` (#precios) | referencias normativas de precios | (b) | Se queda |
| `planes.html` | nombres, alcances y cupos de los planes (P17, P11) | (b) | Se queda |
| `casos/saludips.html` | caso SaludIPS | (b) | Se queda |
| `legal/privacidad.html`, `legal/terminos.html` | borrador jurídico (P5) | (b) | Se queda |
| `seguridad.html` | alojamiento, región y encargados (P1) | (b) | Se queda |
| `recursos.html` | artículos y referencias normativas | (b) | Se queda |

**(a) Redactable:** se reemplaza la caja `.aviso` por un párrafo `.aclara`, **mismo texto sin el prefijo «Texto provisional:»** y con mayúscula inicial; sin atributo `data-provisional`. Textos exactos:
1. `index.html`: `<div class="aviso" style="margin-top:24px" data-provisional>Texto provisional: el alcance…</div>` → `<p class="aclara">El alcance por tipo de institución se confirma al cierre de los pilotos.</p>`
2. `integracion.html`: → `<p class="aclara">Los HIS compatibles y los tiempos de conexión se confirman en el diagnóstico.</p>`
3. `funcionalidades.html` (la caja `<div class="aviso w" …>` entre el encabezado y `#gerencial`): → `<div class="w"><p class="aclara">Los módulos se activan según el plan y los datos disponibles en su sistema. Lo marcado como «Próximamente» está en desarrollo y no tiene fecha comprometida.</p></div>`
4. `asistente-ia.html`: → `<p class="aclara">El asistente no está habilitado para clientes. Su disponibilidad depende de las pruebas internas en curso y de la configuración de cada plan.</p>`

CSS nuevo en `site.css` (junto a `.aviso`): `.aclara{margin:24px 0 0;color:var(--tx-suave);font-size:.95rem}` (6,1:1 sobre `#F5F5F5`, 6,7:1 sobre blanco).

**(b) Bloqueado:** no se redacta, no se oculta, conserva la caja y el atributo. Quedan **7**.

### B.1 `build.py --publicar`
Cambios exactos:
1. Docstring: `Uso: python3 build.py [--base https://dominio] [--publicar]` y una línea: `--publicar exige --base y falla (código 1) si en la salida queda algún data-provisional o «por-definir».`
2. Reemplazar la línea `base = next(…)` por:
```python
publicar = '--publicar' in sys.argv
base_arg = next((sys.argv[i + 1] for i, a in enumerate(sys.argv) if a == '--base' and i + 1 < len(sys.argv)), None)
if publicar and not base_arg: sys.exit('--publicar exige --base https://<dominio definitivo>')
base = (base_arg or 'https://dominio-por-definir.co').rstrip('/')
```
3. Al final, después del `print(f'{len(urls)} páginas → dist/')`:
```python
if publicar:
    pendientes = []
    for f in sorted(dist.rglob('*.html')) + [dist / 'sitemap.xml', dist / 'robots.txt', dist / 'js/config.js']:
        t = f.read_text(); rel = f.relative_to(dist)
        for m in re.finditer(r'<(\w+)[^>]*\bdata-provisional\b[^>]*>(.*?)</\1>', t, re.S):
            pendientes.append(f'{rel}: aviso provisional: {re.sub(r"<[^>]+>", "", m.group(2)).strip()[:110]}')
        for m in re.finditer(r'por-definir', t):
            pendientes.append(f'{rel}: marcador: …{t[max(0, m.start() - 40):m.end() + 20].strip()}…')
    if pendientes:
        print(f'No se puede publicar: {len(pendientes)} pendiente(s).', *pendientes, sep='\n'); sys.exit(1)
    print('Listo para publicar.')
```
Sin `--publicar` el comportamiento es el de hoy. `site.js` no se revisa (contiene la cadena `por-definir` en su lógica, ver C).

## C. Marcadores `por-definir`
1. `src/partials/head.html`: `<a class="ing" data-portal href="#portal-por-definir">Ingresar</a>` → `<a class="ing" data-portal hidden>Ingresar</a>`.
2. `src/partials/foot.html`, columna «Contacto»: `<li><a data-portal href="#portal-por-definir">Ingresar a la plataforma</a></li><li>comercial@dominio-por-definir.co</li>` → `<li hidden><a data-portal>Ingresar a la plataforma</a></li><li data-correo hidden></li>`.
3. `dist/js/site.js`: reemplazar la línea `$$('a[data-portal]').forEach(a => { if (C.urlPortal) a.href = C.urlPortal; });` por:
```js
  // «Ingresar» y correo del pie: ocultos en el HTML; aparecen solo con valores reales en config.js
  const valido = x => !!x && !x.includes('por-definir'), correoOk = valido(C.correoComercial);
  if (valido(C.urlPortal) && !C.urlPortal.startsWith('#')) $$('a[data-portal]').forEach(a => { a.href = C.urlPortal; (a.closest('li') || a).hidden = false; });
  if (correoOk) $$('[data-correo]').forEach(li => { const a = document.createElement('a'); a.href = 'mailto:' + C.correoComercial; a.textContent = C.correoComercial; li.append(a); li.hidden = false; });
```
4. Formulario (mismo archivo): dentro de `if (!C.endpointProspectos) {`, como primera línea:
   `if (!correoOk) { msg.className = 'msg er'; msg.textContent = 'El formulario aún no está habilitado.'; return; }`
   Y en el `catch`: `msg.textContent = correoOk ? 'No pudimos enviar la solicitud. Intente de nuevo o escríbanos a ' + C.correoComercial + '.' : 'No pudimos enviar la solicitud. Intente de nuevo.';`
   (Mensajes de preproducción: `--publicar` impide publicar mientras `config.js` tenga `por-definir`).
5. `dist/js/config.js`: **los valores no cambian**. Solo los comentarios: `endpointProspectos` «Vacío = el formulario abre el correo comercial; sin correo real, avisa que no está habilitado.»; `correoComercial` «Vacío o con por-definir = no se muestra en el pie.»; `urlPortal` «Vacío, con por-definir o empezando por # = no se muestra Ingresar.»
6. `README.md`: (i) sección nueva «Publicar»: `python3 build.py --publicar --base https://<dominio>` es obligatorio antes de subir `dist/` a SmarterASP, falla mientras existan avisos (b) o `por-definir`, y después hay que volver a correr `python3 build.py` sin `--base` si no se va a publicar (para no versionar un dominio de prueba); (ii) corregir la viñeta desactualizada «**Fuente**: pila `Inter, Segoe UI…`» por «**Fuente**: Ubuntu autoalojada (ver Marca); no se cargan fuentes externas.»; (iii) en «Formulario», decir que sin endpoint ni correo real el formulario avisa que no está habilitado.

## Criterios de aceptación
1. `node tools/revisar.mjs` → «Sin problemas», incluida la regla nueva de A.3 en 1440×900, 1366×768, 390×844 y 360×740.
2. `grep -rn "data-provisional" src | wc -l` → `7`; `grep -rn "por-definir" src` → nada.
3. `python3 build.py --publicar` → sale con código 1 y el mensaje «--publicar exige --base…».
4. `python3 build.py --publicar --base https://ejemplo.co; echo $?` → lista **9** pendientes (7 avisos + 2 marcadores en `js/config.js`) y `1`. Después, `python3 build.py` (sin `--base`) para dejar `dist/` como estaba.
5. Con `config.js` de fábrica (Playwright o navegador en 8099): en `/` no hay «Ingresar» visible en cabecera ni pie, ni correo en el pie; en `/demo.html`, con datos válidos y la casilla marcada, «Solicitar demo» muestra «El formulario aún no está habilitado.» y no abre `mailto:`.
6. Con valores de prueba inyectados sin tocar el archivo (en Playwright: `page.route('**/js/config.js', r => r.fulfill({ contentType: 'application/javascript', body: 'window.IANALITICA={endpointProspectos:"",correoComercial:"ventas@ejemplo.co",urlPortal:"https://app.ejemplo.co"}' }))`): aparecen «Ingresar» (cabecera y pie, `href` = `https://app.ejemplo.co`) y el enlace `mailto:ventas@ejemplo.co` en el pie.
7. Las 4 frases (a) se ven como texto normal gris, sin caja amarilla.
8. Capturas de la portada en 1366×768 y 390×844 (banner visible) adjuntas al PR, con la tabla de B.

## Fuera de alcance
Definir dominio, correo, portal, planes, región o textos legales (P8, P17, P11, P1, P5). Crear `POST /v1/prospectos`. Cambiar `config.js` a valores reales. Cualquier otro cambio visual de la portada (LD-02 a LD-04).
