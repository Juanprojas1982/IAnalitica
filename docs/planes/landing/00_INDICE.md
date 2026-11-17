# Planes del landing (`ianalitica-landing`) — acabado para mostrarlo a gerentes

Estado: **diseñados el 2026-11-17 a partir de una revisión visual de la portada (escritorio 1366×768 y móvil 390×844). NO implementados.** Implementación con Sonnet (D30): un plan = una rama = un PR en el repo `ianalitica-landing`. Un cambio de diseño durante la implementación vuelve a diseño.
Contexto: `docs/contexto/07_usuarios_landing.md` §28 y el `README.md` del landing. Reglas que no se tocan: sin precios (D05), sin cifras de ahorro ni ROI, capturas con empresa ficticia, una funcionalidad solo pasa a «hoy» cuando está en `main` y cuadrada con un piloto.

## Hechos del repo del landing que el implementador debe conocer
1. **No tiene remoto** (solo ramas locales `master` y `propuesta/textos-ajustados`). Si el contenedor se pierde, se pierde el landing. Pendiente P22 (`06` §10). No se puede abrir PR hasta que exista el remoto; mientras tanto, una rama local por plan.
2. **Desde el 2026-11-17 la rama más avanzada es `ld-05-marca`** (identidad corporativa aplicada, sale de `propuesta/textos-ajustados`): LD-01 a LD-04 parten de ella. Cambió la paleta (naranja/berenjena, fondo claro) y la tipografía (Ubuntu): donde un plan cite colores o clases (`--acento`, `--marca-*`, `.btn-g` en el hero), manda `dist/css/tokens.css` actual. Antes, la rama de trabajo era `propuesta/textos-ajustados` (3 commits delante de `master`, «sin publicar: requiere revisión del dueño»). Los planes parten de esa rama.
3. `build.py` solo compone HTML (`src/partials` + `src/pages` → `dist/*.html`, `sitemap.xml`, `robots.txt`). **`dist/css`, `dist/js` y `dist/assets` son fuente**: se editan a mano y están versionados. No hay minificación ni dependencias.
4. Verificación: `python3 build.py && python3 -m http.server 8099 -d dist` y, en otra terminal, `node tools/revisar.mjs` (debe imprimir «Sin problemas»). `revisar.mjs` y `render.mjs` importan `playwright`; `tools/node_modules` está en `.gitignore`. En la sesión en la nube Playwright es global: si el `import` falla, resolverlo con `createRequire` o `NODE_PATH`, **sin** ejecutar `playwright install`.
5. Hay 11 avisos `data-provisional` repartidos en 10 páginas, y dos marcadores sin resolver: `comercial@dominio-por-definir.co` (pie y `dist/js/config.js`) y `#portal-por-definir` (botón «Ingresar», cabecera y pie).

## Hallazgos de la revisión (medidos)
| # | Hallazgo | Evidencia | Plan |
|---|---|---|---|
| 1 | El banner de cookies tapa los botones del hero en la primera visita | 1366×768: `#cookies` y 580–752, `.hero .cta` y 593–649. 390×844: 609–828 frente a 683–806 | LD-01 |
| 2 | Notas internas visibles para el visitante | Aviso «Texto provisional…» en «Para quién»; correo `…dominio-por-definir.co` en el pie | LD-01 |
| 3 | «Ingresar» apunta a `#portal-por-definir` | `src/partials/head.html`, `foot.html` | LD-01 |
| 4 | Iconos de las tarjetas son caracteres Unicode (◎ ▲ ∑ ⟳ $ ✓ ▣ ⇩) | `src/pages/index.html`, clase `.ic` | LD-02 |
| 5 | Medios ilegibles: la escena de 1280 px se muestra a 350 px en móvil (escala 0,27) y la del asistente a 570 px en escritorio (0,45) | Rectángulos de `.hero .marco` y `.shot video` | LD-03 |
| 6 | «Integración» y «Planes» son dos bandas seguidas con solo título y botón | `index.html`, secciones `#casos` y la siguiente | LD-04 |
| 7 | «Lo que viene» (8 tarjetas) pesa igual que «Lo que puede usar hoy» (8 tarjetas) | `index.html`, sección `#funcionalidades` | LD-04 |
| 8 | Tarjetas desbalanceadas (p. ej. «Usuarios» del auditor: una línea) | `index.html`, bloque del auditor | LD-04 |
| 9 | Sin identidad de marca ni prueba social | `tokens.css` provisional; P8; sin pilotos | LD-05 |

## Planes y orden
| # | Plan | Depende de | Listo | Qué falta |
|---|---|---|---|---|
| LD-01 | Listo para mostrar: cookies, notas internas, marcadores | — | **Sí** | Publicar de verdad exige P8, P5, P17, P1 (el plan lo hace cumplir con `--publicar`) |
| LD-02 | Iconografía SVG | — | **Sí** | — |
| LD-03 | Medios legibles en móvil y en el bloque del asistente | — | **Sí** | Requiere `ffmpeg` solo si se regeneran videos |
| LD-04 | Estructura de la portada | LD-02 (comparten `index.html`; hacer después para no chocar) | **Sí** | — |
| LD-05 | Marca y prueba social | P8 (dominio), pilotos con autorización escrita | **A hecha (2026-11-17, rama local `ld-05-marca`); B no** | Dominio, tonos derivados e isotipo propio; prueba social tras los pilotos |

Orden sugerido: LD-01 → LD-02 → LD-04 → LD-03. LD-05 espera.
Alcance: solo la portada salvo donde el plan diga otra cosa. Ningún plan agrega afirmaciones nuevas sobre el producto: los textos se reordenan o se acortan, no se inventan.
