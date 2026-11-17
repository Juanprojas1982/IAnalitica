# LD-01 — Listo para mostrar: cookies, notas internas y marcadores

Repo: `ianalitica-landing`. Rama: `ld-01-listo-para-mostrar` desde `propuesta/textos-ajustados`. Archivos: `dist/css/site.css`, `dist/js/site.js`, `dist/js/config.js`, `src/partials/head.html`, `src/partials/foot.html`, `src/pages/**/*.html`, `build.py`, `tools/revisar.mjs`, `README.md`.

## Objetivo
Que un gerente pueda abrir la portada sin ver nada tapado ni notas internas, y que sea imposible publicar con textos sin confirmar.

## A. Banner de cookies
Problema: `#cookies` es `position:fixed; left:16px; right:16px; bottom:16px; max-width:620px` y queda sobre `.hero .cta`.
Diseño:
1. Escritorio (> 640 px): anclar a la derecha (`left:auto; right:16px; max-width:400px`). Los botones del hero están a la izquierda.
2. Móvil (≤ 640 px): barra compacta. Texto corto: «Usamos cookies de medición solo si usted lo autoriza. [Más información]». Botones en una fila, `font-size:.85rem`, `padding` reducido. Altura objetivo ≤ 110 px.
3. Si en móvil aún se cruza con los botones: ocultar `.hero .tres` en ≤ 640 px (las tres viñetas repiten el `lead`) para que los botones suban. No quitar ni mover los botones.
4. Mantener `role="dialog"`, el foco visible y el comportamiento actual de consentimiento (`localStorage`, `window.medir`). No cargar analítica.

Criterio de aceptación (automatizado): en `tools/revisar.mjs`, con consentimiento sin decidir, en 1440×900, 1366×768 y 390×844, el rectángulo de `#cookies.on` **no intersecta** `.hero .cta`. Si intersecta, cuenta como problema.

## B. Avisos `data-provisional`
Son notas para el dueño, no para el visitante. Inventario: `grep -rn "data-provisional" src/pages`.
Clasificar cada uno (dejar la tabla resultante en el PR):
- **(a) Redactable para el cliente:** cuando el fondo es «se confirma en el diagnóstico» (alcance por tipo de institución, HIS compatibles, tiempos de conexión, módulos según el plan). Se reemplaza la caja `.aviso` por una frase normal en tono de cliente, sin la palabra «provisional», y se quita el atributo.
- **(b) Bloqueado por una decisión:** planes y cupos (P17, P11), alojamiento y región (P1), política de privacidad y términos (P5), referencias normativas de precios, caso SaludIPS. **No se redactan ni se ocultan**: conservan `data-provisional` y la caja visible.

`build.py`: nuevo indicador `--publicar`. Con él, el build **falla con código 1** y lista archivo y texto si en la salida queda algún `data-provisional` o la cadena `por-definir`. Sin el indicador, se comporta como hoy. Documentarlo en el `README.md` como el paso obligatorio antes de subir a SmarterASP.

## C. Marcadores `por-definir`
1. Correo del pie: hoy es texto fijo en `foot.html`. Cambiar a `<li data-correo></li>`; `site.js` lo llena desde `IANALITICA.correoComercial` como enlace `mailto:` y **elimina el `<li>`** si el valor está vacío o contiene `por-definir`.
2. «Ingresar» (`a[data-portal]`): `site.js` ya asigna `urlPortal`; añadir que **oculte** el enlace (y su `<li>` en el pie) si `urlPortal` está vacío o empieza por `#`.
3. Formulario de demo sin `endpointProspectos` y sin correo real: no abrir un `mailto:` a un buzón inexistente. Mostrar `msg er`: «El formulario aún no está habilitado.» (texto interno de preproducción; `--publicar` ya impide salir así).
4. `build.py` sigue usando `https://dominio-por-definir.co` como `--base` por defecto; `--publicar` exige `--base` explícito.

## Fuera de alcance
Definir dominio, correo, planes o región (P8, P17, P1). Crear `POST /v1/prospectos`.

## Pruebas
- `python3 build.py` → 11 páginas; `node tools/revisar.mjs` → «Sin problemas», incluida la regla nueva de cookies.
- `python3 build.py --publicar` → falla y lista los avisos (b) y los `por-definir`.
- Con `config.js` de fábrica: el pie no muestra correo y no hay «Ingresar». Con valores reales de prueba: aparecen ambos.
- Capturas de la portada en 1366×768 y 390×844 adjuntas al PR.
