# UI-01 — Marca y sistema visual del portal

Rama: `portal/ui-01-marca` desde `docs/planes-portal`. Capa: Web.
Archivos: `src/IAnalitica.Web/wwwroot/css/tokens.css` (nuevo), `wwwroot/fonts/ubuntu-{400,500,700}.woff2` + `LICENCE-ubuntu.txt` (copiados de `landing/dist/assets/fonts/`), `wwwroot/app.css`, `wwwroot/dashboards.css`, `Components/App.razor`, `Components/Layout/MainLayout.razor`, `Components/Dashboards/Paleta.cs`.

## Objetivo
Que el portal tenga la misma identidad que el landing, sin cambiar el marcado de los widgets.

## 1. `wwwroot/css/tokens.css` (nuevo)
- Copiar el bloque `:root{…}` de `landing/dist/css/tokens.css` **tal cual**, con el comentario de procedencia (los «derivado» siguen por confirmar, P8). Quitar `--ancho`.
- Agregar `@font-face` para Ubuntu 400/500/700 con `font-display: swap` y `src: url('../fonts/ubuntu-NNN.woff2') format('woff2')`. La CSP ya lo permite (`default-src 'self'`).
- Tokens propios del portal (derivados, mismo comentario):
  `--estado-ok-tx:#15803d; --estado-alerta-tx:#b45309; --estado-peligro-tx:#b91c1c;` (texto de estado AA) · `--eje:#E6E6E6;` · `--tx-eje:#5C5C5C;`.

## 2. `App.razor`
Cargar `css/tokens.css` **antes** de `app.css` (después de Bootstrap).

## 3. `app.css`
- `html, body { font-family: var(--fuente); color: var(--tx); }` · `body { background: var(--fondo-alt); }`.
- Enlaces y `.btn-link`: `var(--naranja-tx)`; hover `var(--naranja-hover)`.
- `.btn-primary`: fondo y borde `var(--naranja-osc)`, hover/active `var(--naranja-hover)`, `border-radius: var(--radio-btn)`. `.btn-outline-primary`: texto y borde `var(--naranja-tx)`, hover fondo `var(--naranja-osc)` y texto blanco.
- Anillo de foco: reemplazar `#258cfb` por `var(--naranja)`.
- No tocar las reglas de validación ni las de `#blazor-error-ui`.

## 4. `MainLayout.razor`
- `<header class="navbar navbar-expand bg-dark navbar-dark px-3">` → `<header class="navbar navbar-expand barra-app px-3">`.
- Marca: `<a class="navbar-brand" href="/"><span class="isotipo" aria-hidden="true">IA</span>IAnalitica</a>`.
- En `app.css`: `.barra-app { background:#fff; border-bottom:3px solid var(--naranja); }`, `.barra-app .nav-link, .barra-app .navbar-text { color: var(--tx); }`, `.barra-app .nav-link:hover { color: var(--naranja-tx); }`, `.isotipo` cuadro de 1.75rem, fondo `var(--naranja-osc)`, texto blanco 700, `border-radius: var(--radio-btn)`, `margin-right:.5rem` (como el mockup del landing; **no** es un isotipo oficial, P8).
- No cambiar los enlaces, las políticas ni el formulario de salida.

## 5. `dashboards.css`
Reemplazar los colores fijos por tokens. No cambiar selectores ni la grilla.
- `.widget`: sin borde, `background: var(--fondo)`, `border-radius: var(--radio)`, `box-shadow: var(--sombra)`, `padding: 1rem 1.15rem`.
- `.widget h2`: `font-size:.75rem; font-weight:500; text-transform:uppercase; letter-spacing:.06em; color: var(--tx-suave)`.
- `.kpi-valor`: `font-size:2rem; color: var(--tx)`. `.tablero-encabezado .modulo`: `var(--naranja-tx)`.
- `.barra-filtros`: `background: var(--fondo); border: 1px solid var(--linea); border-radius: var(--radio)`. Inputs: borde `var(--linea)`, foco con `var(--naranja)`.
- `.grafico .eje` → `var(--eje)`; `.grafico .etiqueta` → `fill: var(--tx-eje)`; `.dona-total` → `fill: var(--tx)`.
- `.barra` → `var(--naranja-osc)`; `.barra.neg` → `var(--estado-peligro-tx)`; `.barra-pista` → `var(--fondo-alt)`.
- `.kpi-variacion.mejor`/`.semaforo-verde`/`.ok` → `var(--estado-ok-tx)`; `.peor`/`.semaforo-rojo`/`.mal` → `var(--estado-peligro-tx)`; amarillo/regular → `var(--estado-alerta-tx)`.
- `.datos summary` → `var(--naranja-tx)`.
- Auditor (`.hallazgo`, `.indice-total`): mismas sustituciones; `.hallazgo` y `.indice-total` con `box-shadow: var(--sombra)` y sin borde salvo el izquierdo de severidad.

## 6. `Paleta.cs`
```csharp
public static readonly string[] Series = { "#D34615", "#772953", "#1F5FBF", "#0F766E", "#7C3AED", "#475569", "#15803D", "#A16207" };
public const string Positivo = "#15803D";
public const string Negativo = "#B91C1C";
/// <summary>Barra de total o resultado (cascada): berenjena, distinto de positivo y negativo.</summary>
public const string Total = "#772953";
```
Actualizar el `<summary>` de la clase: serie 0 y 1 = marca; todas ≥ 3:1 sobre blanco. Verificar el contraste con un cálculo (anotar los valores en el PR). `Total` se usa en UI-04.

## Pruebas
- Las existentes pasan sin cambios (el marcado de los widgets no cambia).
- Integración (`DashboardsWebTests`, un caso nuevo o extensión de uno existente): la página del tablero contiene `css/tokens.css` y `class="navbar navbar-expand barra-app`.

## Fuera de alcance
Modo oscuro; páginas fuera del layout (`/ingresar`, 2FA) salvo lo que herede `app.css`; isotipo oficial (P8).

## Docs
`03` §17 «Renderizador»: agregar «estilo con los tokens de marca del landing (`wwwroot/css/tokens.css`)». `07` §28: el portal ya usa la identidad visual.
