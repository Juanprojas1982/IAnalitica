# PL-03 · Atendido no facturado y dinero detenido por etapa

Estado: diseñado, no implementado. Módulo `CICLO_INGRESO` (existente). Script `1070_ciclo_ingreso.sql`.
Preparación (2026-11-16): **listo para implementar.** DP-3 aprobada (D55): diarias 35 días + última de cada mes.
Prioridad 1 del MVP gerencial (`03` §18).

## Objetivo
Una sola vista del dinero que aún no es caja: atendido sin facturar → facturado sin radicar → radicado sin pagar, con valor, documentos y antigüedad por pagador y sede.

## Decisión de diseño
`rpt.vw_CicloIngreso` usa `fact.Prestacion`, que no se ingiere. Se construye una vista nueva con lo que sí hay; `vw_CicloIngreso` no se toca.

| Etapa | Fuente | Valor |
|---|---|---|
| `SIN_FACTURAR` | `fact.AdmisionPendienteFactura` en su último corte | `ValorServicios` |
| `SIN_RADICAR` | `fact.CarteraDocumento` CXC (último corte) cuya factura no tiene `FechaRadicacionKey` | `Saldo` |
| `RADICADO_SIN_PAGAR` | `fact.CarteraDocumento` CXC cuya factura sí está radicada | `Saldo` |
| `CARTERA_SIN_FACTURA` | CXC sin factura enlazada (saldos iniciales, otros deudores) | `Saldo` |

- Enlace cartera ↔ factura: `CarteraDocumento.NumeroDocumento = FacturaLinea.NumeroDocumento` y `Anulada = 0` (mismo enlace que ya usa `usp_GenerarCorteCartera` para `CuentaCobro`; `[S]`). Se resuelve con `OUTER APPLY ... MAX(FechaRadicacionKey)`.
- Las tres etapas de cartera suman exactamente `cartera.saldo_cxc`: es la prueba de cuadre.
- **Glosado no es una etapa** (es parte de la cartera radicada): se muestra aparte con `glosas.valor_pendiente` si el módulo GLOSAS está activo (`ModulosAdicionales`).
- **Sin historia en v1:** cada fuente usa su propio último corte; la vista no expone `corte` (los cortes de cartera y de la foto pueden diferir un día y el filtro `MAX(corte)` del motor ocultaría una etapa).

## Cambios
### 1. Entidad `admisiones-pendientes`
- `new EntidadIngesta("admisiones-pendientes", new[] { Modos.Foto }, new[] { "valorServicios" })`.
- `etl.usp_ProcesarAdmisionesPendientes` (FOTO): lee `etl.Carga.Corte`; valida obligatorios (`admisionSourceKey, fechaIngreso, valorServicios, diasSinFacturar`), `valorServicios < 0` → `VALORES_INVALIDOS`, duplicado por `admisionSourceKey`; en una transacción **borra el corte y lo inserta** (`@Del` = filas borradas, `@Upd = 0`). Referencias opcionales a NULL.
- Retención (DP-3) al final del mismo SP: borrar cortes de la empresa con más de 35 días que no sean el último de su mes.
- `ProcesadorCargas.AplicarAsync`: `case "admisiones-pendientes"`. `FilaProceso` no necesita el corte (lo lee el SP).

### 2. Vistas (`1070_ciclo_ingreso.sql`)
- `rpt.vw_PendienteFacturar`: `EmpresaId, CorteFechaKey, SedeId, PagadorId, UsuarioOpId, TipoAtencion, RangoDias` (`0-7`, `8-15`, `16-30`, `31-60`, `>60`)`, Admisiones, Valor, DiasPonderados`. Con dimensión `corte` (el motor toma el último).
- `rpt.vw_DineroDetenido`: `EmpresaId, Etapa, SedeId, PagadorId, Documentos, Valor, DiasPonderados` según la tabla de arriba.

### 3. Métricas y dashboards (`ModuloCicloIngreso`)
- `ciclo_ingreso.pendiente_facturar`, `ciclo_ingreso.admisiones_pendientes`, `ciclo_ingreso.dias_sin_facturar` = `SUM(DiasPonderados)/NULLIF(SUM(Valor),0)` (`KpiMeta: "DIAS_SIN_FACTURAR"`).
- `ciclo_ingreso.dinero_detenido` = `SUM(Valor)` con dims `etapa` (ya existe en `Dimensiones`), `pagador`, `sede`; `ciclo_ingreso.dias_detenido`.
- Dimensiones nuevas: `rango_dias`, `tipo_atencion` (llave legible). Agregar etiquetas de etapa en `Dimensiones.EtiquetaFija` solo si `Humanizar` no basta.
- Dashboard `ciclo_ingreso.dinero_detenido`: KPI total detenido, sin facturar, sin radicar, radicado sin pagar · `Embudo` por etapa · barras apiladas pagador × etapa · tabla por pagador · KPI de glosa pendiente (`ModulosAdicionales: GLOSAS`).
- Dashboard `ciclo_ingreso.pendiente_facturar`: KPI valor, admisiones, días · barras por rango de días · tabla por usuario y por pagador.
- Enlazar desde `financiero.gerencial` con un KPI «Dinero detenido» (`ModulosAdicionales: CICLO_INGRESO`).

### 4. Ayuda de pruebas
`tests/.../Ingesta/AyudaIngesta.cs`: `CrearCargaAsync`/`ProcesarAsync` no aceptan `corte`; agregar parámetro opcional `DateOnly? corte = null`.

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1070_ciclo_ingreso.sql` |
| Domain | `Dimensiones.cs` |
| Application | `Ingesta/Entidades.cs` |
| Infrastructure | `Ingesta/Procesamiento.cs` |
| Módulos | `Modulos.Ingreso/ModuloIngreso.cs`, `Modulos.Financiero/ModuloFinanciero.cs` (un KPI) |
| Pruebas | `CicloIngresoTests.cs` (nuevo), `AyudaIngesta.cs`, `DashboardsTests.cs`, aislamiento |

Símbolos para `impact`: `CatalogoEntidades`, `ProcesadorCargas.AplicarAsync`, `ModuloCicloIngreso.Registrar`, `ModuloFinancieroAnalitico.Registrar`, `Procesamiento.CrearCargaAsync` (pruebas).

## Pruebas
- FOTO: segunda foto del mismo corte reemplaza; otro corte convive; retención borra lo que debe y conserva fin de mes.
- Cuadre: Σ etapas de cartera = `cartera.saldo_cxc` del último corte.
- Factura radicada después: el documento pasa de `SIN_RADICAR` a `RADICADO_SIN_PAGAR` al reprocesar.
- Admisión facturada desaparece de la foto siguiente (lo garantiza la extracción; probar que la plataforma no la retiene).
- Aislamiento por las dos vistas.

## Riesgos y supuestos `[S]`
- Enlace por `NumeroDocumento` sin sede: dos sedes con la misma numeración se cruzarían. Confirmar con datos reales; si pasa, agregar sede al enlace.
- `SIN_RADICAR` solo aparece si la factura ya está contabilizada (entra por cartera). Facturas sin `MovimientoContableId` (regla CON-01 del auditor) quedan fuera: anotarlo en el pie del tablero.
- `fechaEnvioRips` como radicación es `[S]` heredado.
- Foto diaria de 12 meses de admisiones: medir volumen en piloto (DP-3).
