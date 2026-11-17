# LD-05 — Marca y prueba social (bloqueado)

**No está listo.** Depende de decisiones del dueño y de los pilotos; se deja escrito para no perder el alcance.

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
