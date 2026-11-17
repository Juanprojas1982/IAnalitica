# IAnalitica · sitio web (landing)

Vive en la carpeta `landing/` del repo `ianalitica` desde el 2026-11-17 (antes era un repo local aparte); los comandos de este archivo se ejecutan desde `landing/`. No forma parte de `IAnalitica.sln` ni del CI.

Sitio **estático**, proyecto aparte de la plataforma (spec 13; decisiones D05 y D06 de la spec maestra): sin precios («Cotizar»), independiente del HIS, caso SaludIPS descrito sin cifras de mejora. Se publica en SmarterASP subiendo el contenido de `dist/`.

## Estructura
- `src/partials` (cabecera y pie) + `src/pages` (una página por archivo, con sus metadatos en el comentario inicial) → `python3 build.py --base https://dominio` genera `dist/` (HTML, `sitemap.xml`, `robots.txt`).
- `dist/css/tokens.css`: **la identidad visual completa** (colores, tipografía, radios). Cambiar la marca = editar este archivo.
- `dist/js/config.js`: endpoint del formulario, correo comercial y URL del portal.
- `tools/escena.html`: escena animada del dashboard con datos ficticios («Clínica Ejemplo»). `node tools/render.mjs` regenera videos (WebM y MP4), capturas WebP y poster. `node tools/revisar.mjs` revisa desbordes, alt, h1, enlaces y errores de consola en móvil y escritorio (requiere servir `dist/` en el puerto 8099).

## Publicar
`python3 build.py --publicar --base https://<dominio>` es obligatorio antes de subir `dist/` a SmarterASP. Falla mientras existan avisos `data-provisional` (clase b) o marcadores `por-definir`. Después, si no se va a publicar, volver a correr `python3 build.py` sin `--base` para no versionar un dominio de prueba.

## Estado y pendientes
- **Textos provisionales** (marcados con `data-provisional`): planes y cupos, tiempos de conexión, HIS compatibles, alojamiento y región, política de privacidad y términos (borrador: requiere revisión jurídica), caso SaludIPS, artículos de recursos («Próximamente»).
- **Marca**: identidad de Procesos en Línea aplicada (manual «Identidad Corporativa – Procesos en Línea» v1.0): naranja `#E95420` / `#D34615`, berenjena `#772953`, gris `#AEA79F`, texto `#333333`, tipografía Ubuntu autoalojada (`dist/assets/fonts`, Ubuntu Font Licence 1.0, tomada de `@fontsource/ubuntu` 5.3.0; licencia incluida). Logo oficial (`Logo.png` del sitio corporativo) en `dist/assets/logo-procesos-en-linea.png`, solo con el fondo blanco vuelto transparente; va sobre blanco o gris claro, nunca sobre berenjena (no existe versión en blanco). Los tonos marcados «derivado» en `tokens.css` no están en el manual y hay que confirmarlos. Botones en `#D34615` (blanco encima 4,51:1) porque `#E95420` da 3,65:1. `node tools/og.mjs` regenera `og.png`. Sigue pendiente el dominio (P8) y un isotipo propio de IAnalitica: el símbolo del logo corporativo es un mazo judicial. Iconos: SVG en línea de trazo propio (24×24, trazo 2, `currentColor`), sin librería externa.
- **Formulario**: valida en el cliente, con honeypot y casilla obligatoria de autorización. Sin `endpointProspectos` abre el correo comercial con los datos; sin endpoint ni correo real, el formulario avisa que no está habilitado. `POST /v1/prospectos` (→ `crm.Prospecto`, spec 24 §5.9) **aún no existe en la API**; la validación del lado del servidor y el captcha van ahí.
- **Analítica**: solo el banner de consentimiento y `window.medir(evento)`, que empuja a `dataLayer` únicamente con consentimiento. No hay proveedor configurado.
- **Fuente**: Ubuntu autoalojada (ver Marca); no se cargan fuentes externas.
- Los medios son animaciones de una escena HTML con datos ficticios, no grabaciones del portal real.
- **Medios alineados con los textos:** la escena (`tools/escena.html`) solo muestra lo que el producto ofrece hoy (gerencial, cartera, ciclo del ingreso) y el asistente, rotulado como en pruebas internas. Si una funcionalidad pasa a «hoy», se agrega a la escena y se regenera. `node tools/render.mjs` (requiere `ffmpeg`) genera las WebP con `ffmpeg` (amplias 1280×720 para el hero de escritorio, `funcionalidades.html` y `og.png`; compactas 720×540 y 1440×1080 con `?c=1` para el hero en móvil y el bloque del asistente) y los videos `hero-gerencial` (1280×720, solo escritorio) y `hero-asistente-c` (720×540); el móvil nunca descarga video. `og.png` (1200×630) se regenera con `node tools/og.mjs`.
- **Regla de contenido:** una funcionalidad solo pasa de «Próximamente» a «hoy» cuando está en `main` de la plataforma y cuadrada con un piloto.
