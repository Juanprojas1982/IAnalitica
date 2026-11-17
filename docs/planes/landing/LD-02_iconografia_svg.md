# LD-02 — Iconografía SVG

Repo: `ianalitica-landing`. Rama: `ld-02-iconos` desde la rama de LD-01. Archivos: `src/pages/index.html`, `src/pages/funcionalidades.html` (si usa `.ic`), `dist/css/site.css`, `README.md`.

## Problema
Las ocho tarjetas de «Lo que puede usar hoy» usan caracteres Unicode como icono (`◎ ▲ ∑ ⟳ $ ✓ ▣ ⇩`). Cada uno se dibuja con la fuente del sistema del visitante: pesos y tamaños distintos, aspecto improvisado.

## Diseño
1. Un SVG en línea por tarjeta dentro de `.ic`: `viewBox="0 0 24 24"`, `fill="none"`, `stroke="currentColor"`, `stroke-width="1.75"`, `stroke-linecap="round"`, `stroke-linejoin="round"`, `aria-hidden="true"`, 22×22 px. Sin sprites ni peticiones extra.
2. Mismo lenguaje en los ocho: solo trazo, sin relleno, geometría simple.

| Tarjeta | Motivo del icono |
|---|---|
| Tablero gerencial | Cuadrícula de 4 paneles |
| Resultados y costos | Barras con línea de tendencia |
| Balance y razones | Balanza |
| Ciclo del ingreso | Documento con flecha circular |
| Cartera | Reloj con signo de moneda o calendario con moneda |
| Auditor de configuración | Escudo con visto bueno |
| Precios máximos de medicamentos | Cápsula con tope (línea horizontal encima) |
| Reportes | Documento con flecha hacia abajo |

3. `.card .ic`: `color:#fff` (hoy `#041022`), se conserva el degradado `--marca-600 → --acento`. Verificar contraste del trazo ≥ 3:1 sobre el punto más claro del degradado; si no llega, oscurecer el extremo cian del degradado solo en `.ic`.
4. Origen de los trazos: dibujados a mano o tomados de un juego con licencia permisiva. Si se toman de una librería, **verificar la licencia en su repositorio el día de la implementación** y anotar nombre, versión y licencia en el `README.md` (regla D32/D34: sin dependencias de pago ni licencias sin verificar). No se instala ningún paquete: se copian los trazos.

## No cambia
Las marcas `✓` de `.lista` y `.tres`, y el `+` de las preguntas frecuentes: son tipográficas y coherentes entre sí.

## Pruebas
- `node tools/revisar.mjs` → «Sin problemas».
- `grep -n 'class="ic">[^<]' src/pages/*.html` no devuelve nada (ningún `.ic` con texto directo).
- Captura de la cuadrícula en 1366 y 390 px adjunta al PR.
