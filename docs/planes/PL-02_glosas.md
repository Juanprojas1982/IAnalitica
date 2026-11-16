# PL-02 · Glosas

Estado: diseñado, no implementado. Módulo `GLOSAS` (permiso `dash.glosas`, ya sembrado para FINANCIERO y CARTERA). Script `1060_glosas.sql`.
Preparación (2026-11-16): **listo para implementar.** DP-2 sigue abierta y solo afecta la clasificación de motivos (se guarda el código de origen).

## Objetivo
Responder: ¿cuánto me glosan sobre lo facturado?, ¿qué EPS y qué causa?, ¿cuánto acepto y cuánto defiendo?, ¿cuánto sigue sin respuesta y hace cuántos días?

## Cambios
### 1. Entidad y verificación de módulo
- `CatalogoEntidades`: `new EntidadIngesta("glosas", new[] { Modos.Ventana, Modos.Inicial }, new[] { "valorGlosado" }, ModuloRequerido: "GLOSAS")`.
- **Hacer cumplir `ModuloRequerido`** (hoy no se usa): en `IniciarCargaHandler`, si la entidad lo declara y la empresa no tiene el módulo activo en `seg.EmpresaModulo` → `422 VALIDACION_NEGOCIO` («la empresa no tiene activo el módulo GLOSAS»). Reutilizar `IModulosEmpresa`; verificar que funciona con el token del conector (sesión sin usuario, con `EmpresaId`).

### 2. Script `1060_glosas.sql`
- `ALTER TABLE fact.Glosa ADD MotivoCodigoOrigen varchar(10) NULL;` — `dim.MotivoGlosa` es global y está vacía; el código de origen se conserva siempre y `MotivoGlosaId` se llena solo si el código existe en el catálogo.
- `etl.usp_ProcesarGlosas`, ventana por `fechaGlosa`:
  - Obligatorios: `sourceKey, numeroFactura, fechaGlosa, valorGlosado, anulada`.
  - `valorGlosado < 0`, `valorAceptado < 0` o `valorRechazado < 0` → `VALORES_INVALIDOS`.
  - **No rechazar** si aceptado + rechazado > glosado (dato real sucio probable); queda para una regla del auditor.
  - Pagador, sede y motivo opcionales. `Estado = ISNULL(LEFT(estadoOrigen, 15), 'ABIERTA')` (los valores de `dGlosas.Estado` son `[S]`).
  - `CentroCostoId`: NULL (el contrato no lo trae).
- **Vista `rpt.vw_Glosas`** (grano periodo de la glosa, sede, pagador, motivo, estado):
  `EmpresaId, PeriodoKey, SedeId, PagadorId, MotivoCodigo` (= `ISNULL(MotivoCodigoOrigen, 'SIN_MOTIVO')`)`, Estado, Glosas, ValorGlosado, ValorAceptado, ValorDefendido` (= `ValorRechazado`: lo que la IPS no aceptó)`, ValorPendiente` (= glosado − aceptado − defendido, piso 0)`, GlosasSinRespuesta, GlosasConRespuesta, DiasRespuestaSuma` (Σ días entre glosa y respuesta, solo con respuesta). Excluye `Anulada = 1`. La vista **no** expone un promedio.
- **Vista `rpt.vw_GlosaSobreFacturado`** (`UNION ALL` al grano periodo, sede, pagador):
  filas de `fact.Glosa` (`ValorGlosado, ValorAceptado`, `ValorFacturado = 0`) + filas de `fact.FacturaLinea` no anuladas (`ValorFacturado = ValorNeto`, glosas en 0). Permite `100.0*SUM(ValorGlosado)/NULLIF(SUM(ValorFacturado),0)`.

### 3. Módulo `ModuloGlosas` (en `IAnalitica.Modulos.Ingreso`)
Métricas: `glosas.valor_glosado`, `glosas.valor_aceptado`, `glosas.valor_defendido`, `glosas.valor_pendiente`, `glosas.cantidad`, `glosas.sin_respuesta`, `glosas.dias_respuesta` = `SUM(DiasRespuestaSuma)/NULLIF(SUM(GlosasConRespuesta),0)` (no `AVG`: sería promedio de promedios), `glosas.pct_aceptado` = `100.0*SUM(ValorAceptado)/NULLIF(SUM(ValorGlosado),0)`, `glosas.pct_sobre_facturado` (vista 2, `KpiMeta: "PCT_GLOSA"`), `glosas.pct_aceptado_sobre_facturado` (vista 2).
Dimensiones nuevas: `motivo_glosa` y `estado_glosa` (llave legible).
Dashboard `glosas.resumen`: KPI % glosado sobre facturado, % aceptado, valor pendiente, días de respuesta · línea % glosa por periodo · barras por pagador · barras por motivo · tabla por pagador (glosado, aceptado, defendido, pendiente).

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1060_glosas.sql` |
| Domain | `src/IAnalitica.Domain/Modulos/Dimensiones.cs` |
| Application | `src/IAnalitica.Application/Ingesta/Entidades.cs`, `Comandos.cs` (`IniciarCargaHandler`) |
| Infrastructure | `src/IAnalitica.Infrastructure/Ingesta/Procesamiento.cs` |
| Módulo | `src/IAnalitica.Modulos.Ingreso/ModuloGlosas.cs` (nuevo) + registro en `ModuloIngreso.Modulos` |
| Pruebas | `tests/.../Ingesta/GlosasTests.cs` (nuevo), `ApiIngestaTests.cs`, `IngestaTests.cs`, aislamiento |

Símbolos para `impact`: `CatalogoEntidades`, `IniciarCargaHandler.Handle`, `ProcesadorCargas.AplicarAsync`, `ModuloIngreso.Modulos`, `Dimensiones.Todas`.

## Pruebas
- Ventana: inserta, actualiza, elimina ausentes, respeta lo de fuera. Rechazos por campo y por valores. Anulada no cuenta en vistas.
- Módulo inactivo → 422; activo → 201. Las entidades sin `ModuloRequerido` no cambian (regresión de `ApiIngestaTests`).
- Caso conocido: facturado 1.000, glosado 100, aceptado 30, defendido 50 → % glosa 10, % aceptado 30, pendiente 20.
- Días de respuesta: grupo A con 1 glosa a 10 días y grupo B con 3 glosas a 2 días → 4 días (16/4), no 6.
- Aislamiento por ambas vistas.

## Riesgos y supuestos `[S]`
- **Glosado y facturado del mismo periodo, no por cohorte**: una glosa de marzo sobre una factura de enero suma en marzo. Rotular el KPI «glosado en el periodo / facturado en el periodo». La versión por cohorte necesita enlazar `NumeroFactura` + sede con `fact.FacturaLinea` (el enlace por número es `[S]`).
- DP-2: sin catálogo de motivos, el tablero muestra códigos crudos. Los plazos normativos de respuesta (alertas) quedan fuera hasta verificar la norma vigente.
- `ValorRechazado` se interpreta como «rechazado por la IPS» (defendido). Confirmar con `dGlosasItems.Rechazada`.
- La extracción solo aplica «si la IPS usa el módulo»: la instalación revisada tiene glosas marcadas ⚠️ sin datos (`03` §18 B).
