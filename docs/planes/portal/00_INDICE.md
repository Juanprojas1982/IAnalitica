# Planes del portal — acabado visual de los dashboards (UI-01 a UI-04)

Estado: **diseñados el 2026-11-21 (D30: diseño Opus, código Sonnet). No implementados.** Un plan = una rama = un PR. Ramas apiladas: cada una parte de la anterior (aún no hay fusión en `main`).
Contexto: `docs/contexto/01_reglas_arquitectura_seguridad.md` (siempre), `03_modulos_dashboards_kpi.md` §17, `06_plan_pendientes_decisiones.md` §10 (render) y §13 (SSR+SVG). Marca: `landing/dist/css/tokens.css` (manual de Procesos en Línea v1.0).

## Por qué
El portal no se parece a lo que vende el landing. `landing/dist/assets/dash-gerencial.webp` muestra marca naranja/berenjena, Ubuntu, tarjetas con sombra y área bajo la línea. El portal real usa Bootstrap por defecto (azul `#1b6ec2`, navbar negra, Helvetica) y tarjetas con borde de 1 px. Quien compre por el landing verá otro producto.

## Hallazgos verificados en el código (2026-11-21)
| # | Hallazgo | Evidencia | Plan |
|---|---|---|---|
| 1 | Portal sin la marca del landing | `wwwroot/app.css`, `dashboards.css` con colores fijos; `MainLayout.razor` `bg-dark navbar-dark` | UI-01 |
| 2 | **Error de cifra:** la variación de un KPI en % se muestra relativa («−6,4 %» cuando el margen pasa de 18,7 % a 17,5 %); debe ir en puntos («−1,2 pts») | `KpiVista.razor` usa `SerieWidget.Variacion` = `(a−b)/|b|` para toda unidad; KPI `financiero.margen` en el tablero gerencial | UI-02 |
| 3 | KPI sin tendencia (solo número y variación) | `SerieWidget(Resultado, Anterior, Meta, Semaforo)` | UI-02 |
| 4 | Tooltips = `<title>` nativo (retardo, sin estilo); en líneas solo se puede apuntar a un punto de 4 px | `LineaVista.razor`, `CascadaVista.razor`, `DonaVista.razor`, `ApiladasVista.razor` | UI-03 |
| 5 | Cascada sin conectores y total del mismo tono que las series; dona muestra el total sin unidad (`Formato.Numero`) | `CascadaVista.razor`, `DonaVista.razor` | UI-04 |
| 6 | Paleta: `#b45309`/`#c2410c` se confunden con el naranja de marca; con la marca, el naranja (positivo de barras) queda junto al rojo negativo | `Paleta.cs` | UI-01, UI-04 |

## Corrección al análisis previo (no se hace)
- **Embudo para «dinero detenido»:** la métrica no existe; es PL-03. El tipo de widget se diseña con PL-03.
- **`MapaCalor`:** ningún dashboard lo usa (`grep TipoWidget.MapaCalor` en `Modulos.*` = 0).
- **Modo oscuro y skeletons:** no hacen falta para el cliente objetivo. El tablero es SSR sin `[StreamRendering]` y sin `blazor.web.js`, así que la página llega completa y «Cargando…» nunca se ve.
- **ECharts / `InteractiveAuto`:** se mantiene SSR+SVG. La interactividad entra como **mejora progresiva**: un JS propio externo que la CSP ya permite (`script-src 'self'`, mismo patrón que `js/copiar.js`). Sin JS el tablero funciona igual. Esto **propone cerrar** el pendiente de render de `06` §10 (decide el dueño).

## Reglas que ningún plan rompe
- Sin paquetes NuGet ni npm nuevos; sin CDN; sin scripts en línea (CSP); fuentes autoalojadas (Ubuntu, licencia UFL ya en `landing/dist/assets/fonts/LICENCE-ubuntu.txt`).
- Ningún color nuevo fuera de los tokens, salvo los de las series de `Paleta.cs` (contraste ≥ 3:1 sobre blanco, WCAG 1.4.11) y los de texto de estado ya existentes (`#15803d`, `#b91c1c`, `#b45309`: los de marca `--ok`/`--peligro` no llegan a 4,5:1 para texto).
- Se conserva todo el marcado que afirman las pruebas: `role="img"`, `<details class="datos">`, `<th scope="row">2026-01</th>`, `style="width:NN%"` de barras, textos «frente al periodo anterior», «+100 %» (KPI en pesos), `name="desde"`, `<h1>`.
- `TreatWarningsAsErrors`; `impact` de GitNexus antes de editar cada símbolo y `detect-changes` antes de cada commit (CLAUDE.md).
- El color nunca es la única señal (hay valor, signo y tabla de datos).

## Planes y orden
| Orden | Plan | Rama | Parte de | Capas |
|---|---|---|---|---|
| 1 | UI-01 Marca y sistema visual | `portal/ui-01-marca` | `docs/planes-portal` | Web |
| 2 | UI-02 KPI: puntos porcentuales y tendencia | `portal/ui-02-kpi` | UI-01 | Application + Web |
| 3 | UI-04 Acabado de gráficos | `portal/ui-04-graficos` | UI-02 | Web |
| 4 | UI-03 Tooltips y zonas de lectura | `portal/ui-03-tooltips` | UI-04 | Web (+ JS) |

UI-03 va al final porque agrega zonas a `LineaVista.razor`, que UI-04 reescribe en parte.

## Relación con otros planes
- **PL-07 (comparativos)** toca `SerieWidget` y `KpiVista.razor`. Si se implementa después de UI-02, debe conservar `Tendencia` y aplicar la regla de puntos para unidad `%` a «año anterior» y «acumulado» (nota agregada en PL-07).
- **PL-03** define el widget de etapas (embudo o barras) con estos tokens.

## Verificación común (cada PR)
1. `dotnet build IAnalitica.sln -c Release` sin advertencias.
2. `dotnet test` de Domain, Application e Integration (Docker: si `docker info` falla, arrancar `dockerd` en segundo plano).
3. Capturas a 1440×900 y 390×844 (deviceScaleFactor 2) de `/d/financiero.gerencial` y `/d/cartera.edades` (o los dashboards equivalentes con datos de `DashboardsWebTests`). Se pueden obtener guardando el HTML de la prueba y sirviéndolo con `wwwroot`. Rutas de las capturas en la descripción del PR.
4. `node .gitnexus/run.cjs detect-changes --scope all --repo .` limpio (sin `partial`/`truncated`) antes del commit.
