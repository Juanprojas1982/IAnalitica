# LD-04 — Estructura de la portada

Repo: `ianalitica-landing`. Rama: `ld-04-estructura` desde la rama de LD-02. Archivos: `src/pages/index.html`, `dist/css/site.css`.

Regla: **no se escribe ninguna afirmación nueva sobre el producto.** Los textos salen de la portada actual o de `src/pages/integracion.html`, `casos/saludips.html` y `funcionalidades.html`, que ya fueron ajustados a lo que el producto ofrece hoy.

## A. «Lo que viene» pesa menos que «Lo que puede usar hoy»
Hoy: 8 tarjetas con sombra, idénticas a las de arriba.
Diseño: reemplazar la cuadrícula `g4` de tarjetas por una lista compacta en dos columnas (una en móvil), sin sombra ni tarjeta: nombre en negrita + una línea de descripción, con una sola etiqueta «Próximamente» en el encabezado del bloque en lugar de ocho. Mismo orden y mismos textos. Clase nueva `.prox` en `site.css`. El bloque queda visualmente subordinado (tipografía `.95rem`, `--tx-suave`).
Criterio: la altura del bloque «Lo que viene» en 1366 px es ≤ 60 % de la de «Lo que puede usar hoy».

## B. Bandas «Integración» y «Planes»
Hoy: dos secciones seguidas con título, una frase y un botón.
Diseño:
1. **Integración** pasa a bloque `.dos`: a la izquierda título, `lead` y botón actuales; a la derecha, lista `.lista` con «Qué se conecta» tomado literalmente de `casos/saludips.html` (catálogos; contabilidad: asientos y cartera; facturación y estado de la factura electrónica; configuración del sistema) y la frase de otros HIS de `integracion.html`, resumida sin cambiar su sentido.
2. **Planes** deja de ser sección propia. Su frase («Según usuarios y módulos. Le preparamos una cotización con sus datos.») se integra en la sección de cierre, bajo el `lead`, con enlace de texto «Ver planes». El menú conserva «Planes». Motivo: los nombres y alcances de los planes son provisionales (P17); una banda propia promete más de lo que hay.

## C. Tarjetas desbalanceadas del auditor
Hoy: cuadrícula `g2` con «Contabilidad», «Facturación electrónica», «Tarifas» (tres frases cada una) y «Usuarios» (una).
Diseño: una sola tarjeta con cuatro filas (lista de definición `<dl>`: categoría a la izquierda, ejemplos a la derecha; apilado en móvil). Mismos textos. No se rellenan ejemplos para «Usuarios»: las demás reglas de usuarios no están implementadas.
Lo mismo si «Reportes» queda como tarjeta corta en la cuadrícula superior: se deja como está (8 tarjetas completan dos filas de 4).

## D. Ritmo
Tras A–C, verificar que no queden dos secciones consecutivas con el mismo fondo (`.s` / `.s.alt`) y reasignar `alt` si hace falta.

## Fuera de alcance
Prueba social y logos de clientes (LD-05). Cambios en otras páginas.

## Pruebas
- `node tools/revisar.mjs` → «Sin problemas» (un solo `h1`, sin desbordes, sin enlaces rotos). El ancla `#casos` sigue existiendo.
- Los datos estructurados (`SoftwareApplication`, `FAQPage`) no cambian.
- Diferencia de texto: adjuntar al PR la salida de `git diff --word-diff src/pages/index.html` para comprobar que no hay afirmaciones nuevas.
- Capturas completas en 1366 y 390 px, antes y después.
