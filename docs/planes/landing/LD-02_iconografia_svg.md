# LD-02 — Iconografía SVG

Rama: `landing/ld-02-iconos` desde `main` con LD-01 fusionado. Rutas relativas a `landing/`.
Archivos: `src/pages/index.html`, `dist/css/site.css`, `tools/revisar.mjs`, `README.md`, `dist/index.html` regenerado.

## Estado verificado (2026-11-17)
- Las ocho tarjetas de «Lo que puede usar hoy» (`#funcionalidades`, cuadrícula `.grid.g4`) llevan un carácter Unicode dentro de `<div class="ic">`: `◎` Tablero gerencial, `▲` Resultados y costos, `∑` Balance y razones, `⟳` Ciclo del ingreso, `$` Cartera, `✓` Auditor de configuración, `▣` Precios máximos de medicamentos, `⇩` Reportes. Cada uno se dibuja con la fuente del sistema del visitante (pesos y tamaños distintos).
- `.ic` solo existe en `src/pages/index.html` (8 usos). `funcionalidades.html` no lo usa.
- Estilo actual, ya coherente con la marca (plano, un solo color, sin degradado): `.card .ic{width:44px;height:44px;border-radius:var(--radio);background:var(--naranja-tinte);display:grid;place-items:center;margin-bottom:14px;color:var(--naranja-osc);font-size:1.3rem}`. **Se conserva**: el diseño original (trazo blanco sobre degradado) queda descartado porque el manual pide iconos planos de un solo color, sin degradados.
- Contraste del trazo `#D34615` sobre `#FDEDE7`: 3,96:1 (≥ 3:1 de WCAG 1.4.11; los iconos son decorativos, pero se cumple igual).

## Diseño (cerrado)
1. Un SVG en línea por tarjeta, dentro del `<div class="ic">` actual, en lugar del carácter. Atributos comunes, siempre en este orden:
   `viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"`
   Solo trazo, sin relleno, sin degradado; el color sale de `currentColor` (= `--naranja-osc`). Sin sprites ni archivos aparte.
2. Trazos **propios** (dibujados para este plan, no tomados de ninguna librería): no hay licencia que verificar ni dependencia que anotar.
3. CSS (`dist/css/site.css`), regla `.card .ic`: quitar `font-size:1.3rem`. Agregar `.card .ic svg{width:24px;height:24px;display:block}`. Nada más cambia.

### Los 8 iconos (contenido exacto de cada `<svg>`)
Prototipo renderizado y revisado a 24 px y a 96 px el 2026-11-17.

| Tarjeta (`href`) | Motivo | Contenido del `<svg>` |
|---|---|---|
| Tablero gerencial (`#gerencial`) | Cuadrícula de 4 paneles | `<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>` |
| Resultados y costos (`#resultados`) | Barras con línea de tendencia y flecha | `<path d="M3 21h18"/><path d="M6 21v-5M12 21v-8M18 21v-11"/><path d="M4 11l5-4 4 2 7-5"/><path d="M16 4h4v4"/>` |
| Balance y razones (`#balance`) | Balanza | `<path d="M12 4v17M8 21h8M5 7h14"/><path d="M5 7 2 14a3 3 0 0 0 6 0L5 7z"/><path d="M19 7l-3 7a3 3 0 0 0 6 0l-3-7z"/>` |
| Ciclo del ingreso (`#ciclo`) | Documento con flecha circular | `<path d="M10 21H5a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h7l5 5v1"/><path d="M12 3v5h5"/><path d="M21 17.5a3.5 3.5 0 1 1-1.03-2.47"/><path d="M18.2 15.03h1.77v-1.77"/>` |
| Cartera (`#cartera`) | Moneda con signo de pesos | `<circle cx="12" cy="12" r="9"/><path d="M15 9h-4a1.5 1.5 0 0 0 0 3h2a1.5 1.5 0 0 1 0 3H9M12 7v2M12 15v2"/>` |
| Auditor de configuración (`#auditor`) | Escudo con visto bueno | `<path d="M12 3l8 3v5c0 5-3.4 8.6-8 10-4.6-1.4-8-5-8-10V6z"/><path d="M8.5 12l2.5 2.5 4.5-5"/>` |
| Precios máximos de medicamentos (`#precios`) | Cápsula bajo una línea de tope | `<path d="M4 4h16"/><rect x="8" y="8" width="8" height="13" rx="4"/><path d="M8 14.5h8"/>` |
| Reportes (`#reportes`) | Documento con flecha hacia abajo | `<path d="M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8z"/><path d="M14 3v5h5"/><path d="M12 11v6M9 14l3 3 3-3"/>` |

Ejemplo completo de la primera tarjeta (las otras siete siguen el mismo patrón; título y texto no cambian):
```html
<a class="card rev" href="/funcionalidades.html#gerencial"><div class="ic"><svg viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false"><rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/></svg></div><h3>Tablero gerencial</h3><p>Ingresos, costos, utilidad y margen del periodo, frente al periodo anterior.</p></a>
```

### Regla en `tools/revisar.mjs`
En el `p.evaluate` por página, agregar al objeto devuelto `icTexto: [...document.querySelectorAll('.ic')].filter(e => e.textContent.trim() || !e.querySelector('svg')).length` y, junto a las demás comprobaciones: `if (r.icTexto) { console.log(\`✗ ${u}: ${r.icTexto} icono(s) .ic sin SVG o con texto\`); problemas++; }`.

### `README.md`
Agregar en «Marca»: «Iconos: SVG en línea de trazo propio (24×24, trazo 2, `currentColor`), sin librería externa.»

## No cambia
Las marcas `✓` de `.lista`, el `+`/`–` de las preguntas frecuentes y los números de `.pasos`: son tipográficos y coherentes entre sí. Textos de las tarjetas. Tamaño y fondo de `.ic`.

## Criterios de aceptación y pruebas
1. `node tools/revisar.mjs` → «Sin problemas» (incluida la regla nueva).
2. `grep -n 'class="ic">[^<]' src/pages/*.html` → nada; `grep -c '<svg viewBox="0 0 24 24"' src/pages/index.html` → `8`.
3. Los 8 iconos se ven del mismo grosor y tamaño en Chrome y Firefox; ninguno toca el borde del cuadro de 44 px.
4. Captura de la cuadrícula en 1366 y 390 px adjunta al PR.

## Fuera de alcance
Iconos en otras páginas o secciones; isotipo de IAnalitica (LD-05); cualquier cambio de texto o de orden de las tarjetas (LD-04).
