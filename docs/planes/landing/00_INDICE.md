# Planes del landing (carpeta `landing/`) — acabado para mostrarlo a gerentes

Estado: **diseñados el 2026-11-17 y revisados el mismo día contra el código real de `landing/`** (después de aplicar la identidad de Procesos en Línea, reescribir los textos de la portada y de `funcionalidades.html`, y quitar las viñetas del hero). **NO implementados.** Implementación con Sonnet (D30): un plan = una rama = un PR. Si durante la implementación aparece una decisión de diseño que el plan no cierra, se detiene y vuelve a diseño (no se improvisa).
Contexto: `docs/contexto/07_usuarios_landing.md` §28 y `landing/README.md`. Las rutas de los planes (`src/pages`, `dist/css`, `tools/`, `build.py`) son relativas a `landing/`.

## Reglas que ningún plan rompe
- Sin precios (D05); sin cifras de ahorro ni ROI; capturas y escenas con empresa ficticia («Clínica Ejemplo»).
- **Ninguna afirmación nueva sobre el producto.** Solo se reordenan, acortan o reubican textos que ya están en el landing; los textos exactos están escritos en cada plan.
- Una funcionalidad solo figura como «hoy» si ya figura así en el landing.
- Sin dependencias nuevas ni paquetes de pago; no se cargan fuentes ni scripts externos. WCAG 2.2 AA.
- Los avisos `data-provisional` bloqueados por decisiones del dueño (planes y cupos, alojamiento y región, textos legales, referencias normativas, caso SaludIPS) **no se ocultan**: `build.py --publicar` (LD-01) falla mientras existan.
- Marca (manual de Procesos en Línea v1.0, ya aplicado en `dist/css/tokens.css`): iconos planos de un solo color (naranja o gris), sin degradados; botones con radio 4 px (`--radio-btn`); logo solo sobre blanco o gris muy claro; texto naranja pequeño sobre blanco con `--naranja-tx` (#C7410F) o `--naranja-osc` (#D34615), nunca con `#E95420`.

## Hechos del landing que el implementador debe conocer
1. **Requisito previo (cumplido el 2026-11-17):** `main` ya contiene la carpeta `landing/` (PR 14). Cada plan parte de `main` con el plan anterior ya fusionado.
2. `build.py` solo compone HTML (`src/partials` + `src/pages` → `dist/*.html`, `sitemap.xml`, `robots.txt`). **`dist/css`, `dist/js` y `dist/assets` son fuente**: se editan a mano y están versionados. Después de `python3 build.py`, los `dist/*.html` generados se versionan junto con los cambios de `src/`.
3. Marca ya aplicada: `dist/css/tokens.css` (naranja `#E95420`/`#D34615`, berenjena `#772953`, gris `#AEA79F`, texto `#333`, Ubuntu autoalojada). Hero claro, cabecera blanca, pie claro con logo. Cualquier color nuevo se toma de los tokens; no se crean colores.
4. Portada actual: titular «Analítica gerencial para *el sector salud.*», antetítulo «Para IPS, ESE y operadores farmacéuticos». Las viñetas `.hero .tres` ya no existen.
5. **Verificación local** (desde `landing/`):
   ```
   ln -sfn /opt/node-tools/node_modules tools/node_modules   # Playwright global de la sesión; tools/node_modules está en .gitignore. NO ejecutar «playwright install».
   python3 build.py && (python3 -m http.server 8099 -d dist &)
   node tools/revisar.mjs                                     # debe imprimir «Sin problemas»
   ```
   Comprobado el 2026-11-17: con ese enlace, `revisar.mjs` corre y hoy imprime «Sin problemas».
6. `ffmpeg` 6.1.1 está instalado en la sesión, con `libwebp`, `libvpx-vp9` y `libx264` (lo necesita LD-03).
7. Avisos `data-provisional`: 11 en 10 páginas (inventario y clasificación en LD-01). Marcadores sin resolver: `comercial@dominio-por-definir.co` (pie y `dist/js/config.js`) y `#portal-por-definir` (botón «Ingresar» en cabecera y pie, y `config.js`).
8. La versión intermedia de LD-01 y de este índice que entró por el PR 13 **queda reemplazada por esta revisión**.

## Hallazgos (medidos de nuevo el 2026-11-17 sobre el código actual)
| # | Hallazgo | Evidencia | Plan |
|---|---|---|---|
| 1 | Banner de cookies sobre los botones del hero | Resuelto en 1440×900, 1366×768 y 390×844 al quitar `.hero .tres`. **Sigue en 360×740**: los botones bajan a dos filas (477–596) y el banner empieza en 506. En móvil el banner mide 218 px. No hay regla automática que lo vigile | LD-01 |
| 2 | Notas internas visibles para el visitante | Aviso «Texto provisional…» en «Para quién» (portada) y en otras 9 páginas; correo `…dominio-por-definir.co` en el pie | LD-01 |
| 3 | «Ingresar» apunta a `#portal-por-definir` | `src/partials/head.html`, `foot.html`, `dist/js/config.js` | LD-01 |
| 4 | Iconos de las tarjetas son caracteres Unicode (◎ ▲ ∑ ⟳ $ ✓ ▣ ⇩) | `src/pages/index.html`, `.card .ic` (fondo `--naranja-tinte`, color `--naranja-osc`: ya planos y de un color) | LD-02 |
| 5 | Medios ilegibles | Hero en 390 px: video de 1280 px mostrado a 348 px (escala 0,27). Asistente en 1366 px: 570 px (0,45); en 390 px: 348 px | LD-03 |
| 6 | «Integración» y «Planes» son dos bandas seguidas con solo título y botón | `index.html`, `#casos` (426 px) y la sección siguiente (399 px) | LD-04 |
| 7 | «Lo que viene» (8 tarjetas) pesa casi lo mismo que «Lo que puede usar hoy» | 1366 px: cuadrículas de 523 y 449 px | LD-04 |
| 8 | Tarjetas desbalanceadas del auditor («Usuarios»: una línea) | `index.html`, bloque del auditor | LD-04 |
| 9 | Sin dominio, isotipo propio ni prueba social | P8; pilotos sin autorización | LD-05 |

## Planes y orden
| Orden | Plan | Rama | Depende de | Listo | Qué bloquea publicar (no implementar) |
|---|---|---|---|---|---|
| 1 | LD-01 Listo para mostrar: cookies, notas internas, marcadores | `landing/ld-01-listo-para-mostrar` | Requisito previo (hecho 1) | **Sí** | P8 (dominio, correo, portal), P5 (legal), P17/P11 (planes y cupos), P1 (alojamiento), referencias normativas, caso SaludIPS: `--publicar` falla hasta resolverlos |
| 2 | LD-02 Iconografía SVG | `landing/ld-02-iconos` | LD-01 fusionado | **Sí** | — |
| 3 | LD-04 Estructura de la portada | `landing/ld-04-estructura` | LD-02 fusionado (mismo `index.html`) | **Sí** | — |
| 4 | LD-03 Medios legibles | `landing/ld-03-medios` | LD-04 fusionado; usa `[hidden]` y `revisar.mjs` de LD-01 | **Sí** (`ffmpeg` verificado) | — |
| — | LD-05 Marca y prueba social | — | P8, pilotos con autorización escrita | A hecha; B bloqueada | No se toca en esta revisión |

Orden: **LD-01 → LD-02 → LD-04 → LD-03**. Cada PR adjunta las capturas y la salida de las pruebas que pide su plan.
Alcance: la portada (`src/pages/index.html`) salvo donde el plan diga otra cosa (LD-01 toca parciales y otras páginas; LD-03 toca también `asistente-ia.html`).
