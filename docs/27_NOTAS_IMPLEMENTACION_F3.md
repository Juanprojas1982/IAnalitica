# Notas de implementación — F3 · Dashboards fase 1

Complementa a `24_SPEC_MAESTRA.md`, `14_spec_capa_semantica_dashboards.md` y `21_spec_reportes_en_segundo_plano.md` (mandan). Aquí: qué se implementó, qué quedó fuera y por qué, supuestos `[S]` y decisiones pendientes.

## 1. Qué incluye F3
| Entregable (§13) | Dónde | Cómo se verifica |
|---|---|---|
| Capa semántica: módulos, métricas, dashboards, widgets | `Domain/Modulos` (contratos, `CatalogoAnalitico`, dimensiones) | `CatalogoAnaliticoTests` (60): expresiones, filtros fijos, fuentes y dimensiones por lista blanca; falla al arrancar si algo no cumple |
| Motor de métricas | `Infrastructure/Semantica/MotorMetricas.cs` | SQL parametrizado solo desde `rpt.vw_*` registradas; con RLS **y** filtro por la empresa de la sesión; tope duro de 100.000 filas; `DashboardsTests` |
| Caché por empresa + versión de carga, metas y semáforo | `Application/Semantica/Consultas.cs`, `cfg.KpiMeta` | `DashboardsTests` (caché, semáforo), `SemanticaYReportesTests` |
| Vistas de dashboards | `database/scripts/0170_vistas_dashboards.sql` | Las 9 vistas se ejercitan con cifras calculadas a mano |
| Módulos FINANCIERO, CICLO_INGRESO, CARTERA (9 dashboards de fábrica) | `Modulos.Financiero`, `Modulos.Ingreso` | `DashboardsTests` (22), `DashboardsWebTests` (12) |
| Renderizador | `Web/Components/Dashboards`, `Pages/Dashboards` | HTML real por HTTP; capturas con Chromium revisadas a ojo |
| Consultas `/q/*` (24 §7.2) | `Web/Consultas/EndpointsQ.cs` | `DashboardsWebTests` |
| Reportes en segundo plano (PDF, Excel, CSV) | `Application/Reportes`, `Infrastructure/Reportes`, `Web/Consultas/EndpointsReportes.cs`, página «Mis reportes» | `ReportesTests` (14) + una prueba por el portal; el `.xlsx` se valida con el validador OpenXML |

## 2. Lo que NO se entregó (y por qué)
| Pendiente (24 §5.3) | Motivo |
|---|---|
| AGENDA, FARMACIA, ESE_PRESUPUESTO | La plataforma aún no ingiere sus hechos (citas, agenda, farmacia, presupuesto público). Sin datos no hay dashboard honesto. Dependen de ampliar la ingesta (contrato 05 §6b, P9) |
| AUDITOR_CONFIG | Es F4 (necesita `configuracion` y las 10 reglas) |
| Flujo de caja (incl. 13 semanas) y ejecución presupuestal | Necesitan tesorería (`recibos`) y presupuesto, que la ingesta todavía no acepta |
| «Dinero detenido por etapa» y «atendido no facturado» | Necesitan admisiones y prestaciones. Sí se entregan FE/RIPS, facturado sin radicar y re-trabajo |
| P&G **con** distribución | Es el motor de costos (F5). La pantalla dice expresamente «sin distribución de costos indirectos» |
| Editor de dashboards, `cfg.Dashboard` | Fase 2 (24 §5.2): en la fase 1 los de fábrica se versionan en código |
| Render `InteractiveAuto`, MudBlazor | Ver §5 (decisión) |
| Logo en el PDF | No se descarga una imagen remota desde el worker; se usa solo el color de marca |

**Criterio de aceptación de F3 («los 3 pilotos usan los dashboards»)**: no se puede cerrar aquí; requiere pilotos y datos reales.

## 3. Supuestos `[S]`
1. **Grupos del balance** (`rpt.vw_SaldoBalance`): se toman de la homologación (`dim.CuentaHomologada.GrupoBalance`); si falta, del PUC comercial: activo corriente = grupos 11–14; pasivo corriente = 21–25; el resto, no corriente. Las ESE usan el plan de la CGN: se validará con la ESE piloto.
2. **Ingresos** = toda la clase 4 (no solo la 41 operacional); **costos y gastos** = clases 5, 6 y 7. Se excluyen los comprobantes de clase `CIERRE`.
3. **Balance** = suma acumulada de los movimientos hasta el periodo final (el inicio del filtro se ignora). Es correcto solo si se cargó el histórico completo (o un comprobante de saldos iniciales).
4. **DSO por pagador** = saldo de cuentas por cobrar al corte ÷ facturado en los 90 días anteriores × 90. Solo cubre pagadores ligados a un tercero con cartera.
5. **Conciliación**: saldo de la cartera calculada frente al saldo contable de las cuentas con `DetalleCartera` (2 CxC, 1 CxP) hasta el corte.
6. **FE/RIPS**: por factura (no por línea); estados de FE `0 enviada, 1 cargada, 2 error, 3 sin enviar`; «problema» = error o sin enviar. Días a radicación = fecha de envío RIPS − fecha de emisión.
7. **Periodo por omisión** = últimos 12 meses terminando en el mes actual de Colombia (UTC−5). Si ningún widget usa periodo (cartera), no se aplica.
8. **Corte de cartera** por omisión = el más reciente calculado.
9. **Una métrica de dashboard se enlaza con una sola vista**: un filtro global que la vista no tiene (p. ej. sede en la contabilidad) **no se aplica** y el widget lo avisa («No aplica el filtro de sede…»); nunca se aplica en silencio.
10. **Tablas y barras** muestran 50/10 filas en pantalla; el reporte exporta todas (tope 100.000 por sección).
11. **Exportaciones**: un usuario puede tener 5 en cola o generándose y 1 a la vez (applock); se reutiliza un reporte idéntico (mismo **usuario**, formato, parámetros y versión de carga) de las últimas 24 h; caducan a los 7 días. Reutilizar entre usuarios queda descartado: complicaría la caducidad y los permisos.
12. **Descarga**: la sirve el portal con sesión (no hay URL firmada porque el almacén es disco local). Solo su dueño o quien tenga `admin.usuarios`; cada descarga se audita en `aud.Acceso`.
13. **CSV**: coma como separador, punto decimal, sin símbolos; texto que empiece por `= + - @` se neutraliza con `'` (inyección de fórmulas en Excel).
14. **Excel** escrito a mano sobre OpenXML (sin librerías): hoja «Resumen» + una por widget; números como números, formato por unidad.
15. **Notificaciones**: la campana cuenta las no leídas del usuario; abrir «Mis reportes» las marca leídas.
16. **Un reporte solo se genera con los permisos vigentes del solicitante**: si perdió el permiso entre el pedido y la generación, el reporte falla (no se entrega).

## 4. Licencias y costo (D32, D33)
- **QuestPDF 2026.8.0**: licencia comunitaria **v3.0 (6 jul 2026)**. Aplica a organizaciones con ingresos brutos **menores de USD 1.000.000 en su último ejercicio completado**, consolidados; las entidades del sector público y las empresas que cotizan en bolsa **no** califican, sea cual sea su ingreso. D33 (declaración del dueño, 26-oct-2026) lo cubre hoy. Dos puntos para el dueño: (a) la definición habla del *último ejercicio fiscal completado*, no del año en curso; (b) si se superan los USD 1 M hay 90 días para comprar la licencia profesional. Se usa solo para el PDF; **si se prefiere cero riesgo de licencia, el PDF puede reemplazarse** (el Excel y el CSV no dependen de ella).
- **Sin servicios de pago:** los archivos van a disco local. Pasar a Azure Blob (con URL firmada de 15 min, como pide la spec 21) requiere cotización y aprobación (D32).

## 5. Para decidir (diseño)
1. **Render `InteractiveAuto` y MudBlazor** (24 §3.3, §9; pendiente desde F0). F3 resolvió los dashboards con **SSR + estado en la URL + SVG**: sin circuitos SignalR, sin WebAssembly y sin JavaScript, accesibles y cacheables. Los filtros son un formulario `GET`. Si se quiere interactividad (arrastrar, tooltips ricos, filtros sin recargar), hay que decidir entre interactivo en servidor, WebAssembly o Auto, y un proyecto cliente. Recomendación: seguir con SSR para fase 1 y evaluar interactividad con los pilotos.
2. **Dashboards entre módulos**: `financiero.gerencial` muestra widgets de CICLO_INGRESO y CARTERA solo si esos módulos están activos (`ModulosAdicionales`). ¿Se prefiere un dashboard gerencial propio que no dependa de otros?
3. **Reportes con varios nodos del worker o de la API**: la carpeta de archivos debe ser compartida (o pasar a Blob). Hoy es `Reportes:Almacen:Ruta`.
4. **Superadmin** no tiene dashboards (no tiene empresa); el selector de empresa con motivo y registro (`aud.AccesoSuperadmin`, 24 §9) no está.
5. **Gráficos en el PDF**: hoy son tablas y KPI. Incluir gráficos exige renderizar SVG a imagen (QuestPDF admite SVG; no se hizo).

## 6. Hallazgos durante las pruebas (corregidos)
- Razor trata `<text>` dentro de bloques de código como etiqueta especial: los textos de los gráficos SVG se emiten con `RenderTreeBuilder` (`Svg.Texto`).
- `@(expr).ToString("0.#", …)` en un atributo: lo que va después de `@( … )` es texto literal; el ancho de las barras salía como `100.ToString(`. Se calcula antes (hay una prueba que lo vigila).
- Dapper no expande listas declaradas como `object`: se tipan (`int[]`/`string[]`).
- Las series de una línea pueden no coincidir en periodos (costos sin fila en febrero): el gráfico alinea por la unión de categorías y rellena con cero solo en dinero y cantidades.
- La etiqueta de una llave de texto (`grupo_puc`) se resuelve contra `dim.CuentaContable.Codigo`; las clases y estados tienen etiqueta fija.
- Los sintéticos «usuarios inexistentes» no sirven para probar el worker: este lee los permisos de la base, como debe.
