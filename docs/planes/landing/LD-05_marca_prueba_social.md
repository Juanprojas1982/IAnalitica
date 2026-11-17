# LD-05 — Marca y prueba social

**Parte A (identidad visual): implementada el 2026-11-17** en la rama local `ld-05-marca` del landing (commit `e06e589`), con el manual «Identidad Corporativa – Procesos en Línea» v1.0 y el logo oficial del sitio corporativo. Falta de A: dominio (P8), confirmar los tonos «derivado» de `tokens.css` y decidir si IAnalitica tendrá isotipo propio (el símbolo del logo corporativo es un mazo judicial). **Parte B (prueba social): bloqueada** por los pilotos.

Lo que sigue es el diseño original, que se conserva como referencia.

## A. Identidad visual — bloqueado por P8
Entradas que debe entregar el dueño: logo (SVG), colores, tipografía y dominio de Procesos en Línea / IAnalitica.
Trabajo cuando existan:
1. `dist/css/tokens.css` (único lugar de la marca): reemplazar `--marca-*`, `--acento`, `--fuente`. Revisar contraste AA de texto y botones con los colores nuevos.
2. `dist/assets/favicon.svg`, `og.png` (1200×630) y `theme-color` en `head.html`.
3. Si la tipografía es una fuente web: autoalojarla (WOFF2, `font-display:swap`, subconjunto latino) y verificar su licencia; hoy no se carga ninguna fuente externa.
4. Regenerar la escena y los medios (`tools/render.mjs`) con la paleta nueva.
5. `python3 build.py --publicar --base https://<dominio>` (indicador de LD-01).

## B. Prueba social — bloqueado por los pilotos
No hay clientes en operación. Reglas vigentes (`07` §28, D05, acuerdo de piloto §9): sin cifras de mejora hasta medirlas; nombre o logo de una institución solo con autorización escrita del texto final.
Trabajo cuando exista al menos un piloto con autorización:
1. Franja bajo el hero con el nombre o logo autorizado, o el caso anónimo si así lo autorizó.
2. Página `casos/` con los resultados medidos en el informe conjunto del piloto.

## Qué no hacer mientras tanto
No usar logos, testimonios ni cifras ilustrativas. No inventar una paleta «definitiva».
