# PL-10 · Ingesta operativa base y costos confiables

Estado: diseñado, no implementado. Script `1120_ingesta_operativa.sql`. Cierra los pendientes técnicos 2 y 3 (`06` §10).
**Es la base de PL-11, PL-12 y PL-13**: sin `prestaciones`, `nomina` e `inventario` no hay agendas con costo, productividad, margen por servicio ni farmacia.

## Objetivo
1. Aceptar y procesar `prestaciones`, `nomina` e `inventario` (hoy `ENTIDAD_NO_SOPORTADA`).
2. Que el motor de costos deje de tratar todo centro como `PRODUCTIVO` y tenga bases reales (`COSTO_PERSONAL`, `ACTIVIDADES`, `ATENCIONES`, `CONSUMO_INSUMOS`, y las manuales).
3. Habilitar margen por servicio y por pagador (`rpt.vw_MargenServicio` ya existe y depende de `fact.Prestacion`).

## Situación actual
- Tablas `fact.Prestacion`, `fact.NominaCosto`, `fact.MovimientoInventario` existen (`0050`, `0060`). Extracción escrita y **no ejecutada** en `docs/06` (inventario) y `docs/11` (prestaciones, nómina).
- `dim.CentroCosto.TipoCC` por defecto `PRODUCTIVO`; `TipoOrigen` guarda el valor crudo; la homologación es `[S]`.
- `etl.usp_CalcularBasesCosto` solo llena `INGRESO`. `fact.CostoBaseValor` admite las demás; las manuales no tienen pantalla de carga.

## Cambios
### 1. Entidades (`CatalogoEntidades`)
| Entidad | Modos | Suma de control | Módulo requerido |
|---|---|---|---|
| `prestaciones` | `INCREMENTAL`, `INICIAL` | `valorTotal` | — |
| `nomina` | `VENTANA` | `valor` | `NOMINA_COSTOS` |
| `inventario` | `VENTANA`, `INICIAL` | `valorEntrada`, `valorSalida` | — |

`INCREMENTAL` hoy solo lo usan terceros y anulaciones: verificar que `IniciarCargaHandler` y `RepositorioCargas` no asumen ventana para hechos incrementales (marca de agua en `GET /v1/estado`).

### 2. Procedimientos (`1120_ingesta_operativa.sql`), patrón de `usp_ProcesarFacturas`
- `etl.usp_ProcesarPrestaciones`: upsert por `SourceKey`, sin borrado. Obligatorios `sourceKey, admisionSourceKey, anulado, cantidad, valorUnitario, valorTotal`. Calcula `FechaRealizacionKey`. Referencias opcionales a NULL. `valorTotal < 0` → `VALORES_INVALIDOS`.
- `etl.usp_ProcesarNomina`: ventana por `periodo` (yyyymm). Obligatorios `sourceKey, periodo, contratoCodigo, empleadoTerceroCodigo, concepto, esCosto, valor`. **Privacidad:** rechazar toda la carga si una fila trae un campo `nombre` o `documento` (ampliar `ReglasPrivacidad.Revisar` a `nomina`); el empleado viaja solo como código de tercero.
- `etl.usp_ProcesarInventario`: ventana por `fecha`. Obligatorios del contrato (`02` §15). `cantidadEntrada` y `cantidadSalida` ≥ 0 y no ambas > 0.
- Tras `prestaciones`, `nomina` o `inventario`: encolar `CALCULAR_COSTOS` de los meses afectados (ampliar la condición que hoy solo mira `asientos`/`facturas`; para `INCREMENTAL` usar el rango de fechas de las filas aceptadas, que el SP devuelve).

### 3. Bases de costo
`etl.usp_CalcularBasesCosto` (versión nueva con `CREATE OR ALTER` en el script 1120):
- `COSTO_PERSONAL` = Σ `fact.NominaCosto.Valor` con `EsCosto = 1` por centro de costo y periodo.
- `ACTIVIDADES` = Σ `Cantidad` de `fact.Prestacion` no anuladas por centro y periodo de realización.
- `ATENCIONES` = admisiones distintas (`AdmisionSourceKey`) por centro y periodo.
- `CONSUMO_INSUMOS` = Σ `ValorSalida` de `fact.MovimientoInventario` con `Estado = 1` por centro y periodo.
- `INGRESO`: sin cambio. Las bases `MANUAL` no se tocan.
- Prueba de regresión obligatoria: `CostosTests` existentes en verde (conservación del costo, `THROW 50010`).

### 4. Clasificación de centros y bases manuales (portal `/costos`)
- Nueva pestaña «Centros de costo»: lista con `TipoOrigen` y selector `TipoCC` (PRODUCTIVO / APOYO / ADMINISTRATIVO). `ClasificarCentrosCostoCommand` (lote), auditado. **La carga `COMPLETA` de `centros-costo` no debe pisar `TipoCC`** (verificar `DefinicionesCatalogo`; si lo pisa, excluir la columna del upsert).
- Sugerencia automática por `TipoOrigen` solo como valor propuesto, nunca aplicado solo (`[S]`).
- Nueva pestaña «Bases manuales»: periodo × centro × valor para `M2`, `KILOS_ROPA`, `PORCENTAJE`; «copiar del mes anterior». `GuardarBasesManualesCommand`. `PORCENTAJE`: la suma por regla debe dar 100.
- Recalcular al guardar (mismo mecanismo y tope de 24 meses que hoy).

### 5. Métricas nuevas (`ModuloFinancieroAnalitico`)
Sobre `rpt.vw_MargenServicio`: `financiero.ingreso_servicio`, `financiero.costo_asignado_servicio`, `financiero.margen_servicio`, `financiero.margen_servicio_pct`; dimensiones `periodo`, `centro_costo`, `pagador`, y nueva `servicio` (`dim.Servicio`, `ServicioId`, `Nombre`). Dashboard `financiero.margen_servicio` con aviso fijo: «costo asignado por precio relativo; aproximación hasta tener ficha de costo por procedimiento» (así lo dice la vista).

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1120_ingesta_operativa.sql` |
| Domain | `Modulos/Dimensiones.cs` |
| Application | `Ingesta/Entidades.cs`, `Ingesta/Comandos.cs` (`ReglasPrivacidad`), `Costos/*` (comandos nuevos) |
| Infrastructure | `Ingesta/Procesamiento.cs`, `Ingesta/DefinicionesCatalogo.cs` (solo si pisa `TipoCC`) |
| Módulo | `Modulos.Financiero/ModuloFinanciero.cs` |
| Web | `Components/Pages/Costos.razor` |
| Pruebas | `HechosOperativosTests.cs` (nuevo), `CostosTests.cs`, `CostosWebTests.cs`, `IngestaTests.cs`, aislamiento |

Símbolos para `impact`: `CatalogoEntidades`, `ReglasPrivacidad.Revisar`, `ProcesadorCargas.ProcesarAsync`, `ProcesadorCargas.EncolarCostosAsync`, `ManejadorCalcularCostos`, `DefinicionesCatalogo`.

## Pruebas
- Cada procesador: alta, actualización, rechazo por causa; nómina con nombre → carga rechazada; nómina sin módulo activo → 422.
- Bases: caso con 2 centros de apoyo y 3 productivos calculado a mano para las 4 bases; la suma distribuida = costo original.
- `TipoCC` sobrevive a una recarga del catálogo.
- Aislamiento: nómina de A invisible para B; `dash.nomina_costos` solo gerencia.

## Riesgos y supuestos `[S]`
- `mServicios.VrCosto` ¿unitario o total? (P2) → `CostoInsumos` no se usa en ninguna métrica hasta resolverlo.
- `mServicios.centroCostoID` → `SIIFNet.CentrosCostos.Id` (`[S]` de `02` §15).
- Volumen: la carga inicial de prestaciones puede ser la más grande (127 mil servicios en la instalación revisada); medir la transacción única (decisión abierta en `06` §10).
- El conector debe enviar estas entidades: la extracción de `docs/11` no se ha ejecutado contra una base real.
