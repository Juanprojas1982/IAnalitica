# IAnalitica · sitio web (landing)

Sitio **estático**, proyecto aparte de la plataforma (spec 13; decisiones D05 y D06 de la spec maestra): sin precios («Cotizar»), independiente del HIS, caso SaludIPS descrito sin cifras de mejora. Se publica en SmarterASP subiendo el contenido de `dist/`.

## Estructura
- `src/partials` (cabecera y pie) + `src/pages` (una página por archivo, con sus metadatos en el comentario inicial) → `python3 build.py --base https://dominio` genera `dist/` (HTML, `sitemap.xml`, `robots.txt`).
- `dist/css/tokens.css`: **la identidad visual completa** (colores, tipografía, radios). Cambiar la marca = editar este archivo.
- `dist/js/config.js`: endpoint del formulario, correo comercial y URL del portal.
- `tools/escena.html`: escena animada del dashboard con datos ficticios («Clínica Ejemplo»). `node tools/render.mjs` regenera videos (WebM y MP4), capturas WebP y poster. `node tools/revisar.mjs` revisa desbordes, alt, h1, enlaces y errores de consola en móvil y escritorio (requiere servir `dist/` en el puerto 8099).

## Estado y pendientes
- **Textos provisionales** (marcados con `data-provisional`): planes y cupos, tiempos de conexión, HIS compatibles, alojamiento y región, política de privacidad y términos (borrador: requiere revisión jurídica), caso SaludIPS, artículos de recursos («Próximamente»).
- **Marca**: paleta provisional tomada del sistema de diseño del portal (#0b2545 / #1f5fbf) más un acento cian. Falta aplicar la identidad corporativa de Procesos en Línea (logo, colores, tipografía) y el dominio (P8).
- **Formulario**: valida en el cliente, con honeypot y casilla obligatoria de autorización. Sin `endpointProspectos` abre el correo comercial con los datos. `POST /v1/prospectos` (→ `crm.Prospecto`, spec 24 §5.9) **aún no existe en la API**; la validación del lado del servidor y el captcha van ahí.
- **Analítica**: solo el banner de consentimiento y `window.medir(evento)`, que empuja a `dataLayer` únicamente con consentimiento. No hay proveedor configurado.
- **Fuente**: pila `Inter, Segoe UI, system-ui…`, sin cargar fuentes externas.
- Los medios son animaciones de una escena HTML con datos ficticios, no grabaciones del portal real.
- **Medios desalineados con los textos (pendiente):** la escena (`tools/escena.html`) y las capturas muestran «dinero detenido», «alertas de hoy», ocupación de agenda, glosas y un grupo de IPS consolidado, que el producto aún no ofrece. Los textos ya separan «lo que puede usar hoy» de «próximamente»; falta regenerar videos y capturas con solo lo disponible (o usar capturas reales del portal con empresa ficticia). `dash-grupo.webp` ya no se usa.
- **Regla de contenido:** una funcionalidad solo pasa de «Próximamente» a «hoy» cuando está en `main` de la plataforma y cuadrada con un piloto.
