# PL-10 · Ingesta operativa base y costos confiables

Estado: diseñado, no implementado. Script `1120_ingesta_operativa.sql`. Cierra los pendientes técnicos 2 y 3 (`06` §10).
Preparación (2026-11-16): **listo para implementar.** `inventario` sin `ModuloRequerido` aprobado (D65).
Depende de PL-02 solo por la verificación de `ModuloRequerido` (nómina); si PL-02 no está mezclado, se crea aquí con la definición de PL-02 §1. P2 no bloquea el plan: solo impide usar `CostoInsumos` (ver riesgos).
**Es la base de PL-11, PL-12 y PL-13**: sin `prestaciones`, `nomina` e `inventario` no hay agendas con costo, productividad, margen por servicio ni farmacia.

## Objetivo
1. Aceptar y procesar `prestaciones`, `nomina` e `inventario` (hoy `ENTIDAD_NO_SOPORTADA`).
2. Que el motor de costos deje de tratar todo centro como `PRODUCTIVO` y tenga bases reales (`COSTO_PERSONAL`, `ACTIVIDADES`, `ATENCIONES`, `CONSUMO_INSUMOS`, y las manuales).
3. Habilitar margen por servicio y por pagador (`rpt.vw_MargenServicio` ya existe y depende de `fact.Prestacion`).

## Situación actual
- Tablas `fact.Prestacion`, `fact.NominaCosto`, `fact.MovimientoInventario` existen (`0050`, `0060`). Extracción escrita y **no ejecutada** en `docs/06` (inventario) y `docs/11` (prestaciones, nómina).
- `dim.CentroCosto.TipoCC` por defecto `PRODUCTIVO`; `TipoOrigen` guarda el valor crudo; la homologación es `[S]`.
- `etl.usp_CalcularBasesCosto` (`0150`) **ya calcula las cinco bases calculadas** (`COSTO_PERSONAL`, `INGRESO`, `ACTIVIDADES`, `ATENCIONES`, `CONSUMO_INSUMOS`) y solo borra las de `Origen = 'CALCULADA'` (verificado). Hoy solo `INGRESO` tiene datos porque las otras tablas están vacías. Las bases manuales no tienen pantalla de carga.
- Permisos de BD: `rol_app` tiene solo `SELECT` sobre `dim` y `SELECT, UPDATE` sobre `fact`. El portal **no puede** hoy actualizar `dim.CentroCosto` ni insertar en `fact.CostoBaseValor`.

## Cambios
### 1. Entidades (`CatalogoEntidades`)
| Entidad | Modos | Suma de control | Módulo requerido |
|---|---|---|---|
| `prestaciones` | `INCREMENTAL`, `INICIAL` | `valorTotal` | — |
| `nomina` | `VENTANA` | `valor` | `NOMINA_COSTOS` |
| `inventario` | `VENTANA`, `INICIAL` | `valorEntrada`, `valorSalida` | — |

`inventario` va **sin** `ModuloRequerido` a propósito: alimenta la base `CONSUMO_INSUMOS` del costeo (módulo `FINANCIERO`) aunque la empresa no tenga `INVENTARIO`; los tableros de inventario sí exigen el módulo (PL-13). Aprobado (D65).
El pendiente técnico 2 de `06` §10 la llama `movimientos-inventario`; el nombre del contrato (`02` §15) es `inventario` y es el que manda: corregir `06` al cerrar el plan.

`INCREMENTAL` hoy solo lo usan terceros y anulaciones: verificar que `IniciarCargaHandler` y `RepositorioCargas` no asumen ventana para hechos incrementales (marca de agua en `GET /v1/estado`).

### 2. Procedimientos (`1120_ingesta_operativa.sql`), patrón de `usp_ProcesarFacturas`
- `etl.usp_ProcesarPrestaciones`: upsert por `SourceKey`, sin borrado. Obligatorios `sourceKey, admisionSourceKey, anulado, cantidad, valorUnitario, valorTotal`. Calcula `FechaRealizacionKey` (fecha fuera de `dim.Fecha`, 2015–2035 → `FECHA_INVALIDA`). Referencias opcionales a NULL. `valorTotal < 0` → `VALORES_INVALIDOS`.
- `etl.usp_ProcesarNomina`: ventana por `periodo` (yyyymm). Obligatorios `sourceKey, periodo, contratoCodigo, empleadoTerceroCodigo, conceptoCodigo, concepto, esCosto, valor`.
  - **`conceptoCodigo` falta en la lista del contrato (`02` §15) pero `fact.NominaCosto.ConceptoCodigo` es `NOT NULL` y la extracción de `docs/11` ya lo envía** (`k.Codigo AS conceptoCodigo`): es obligatorio. Corregir `02` §15 al cerrar el plan.
  - **Privacidad:** si una fila trae `nombre`, `documento` o `identificacion`, se rechaza toda la carga; el empleado viaja solo como código de tercero. Esto **no** va en el SP: va en `ReglasPrivacidad.Revisar` (Application), que hoy solo revisa `terceros`, y lo aplica `RecibirLoteHandler` al recibir el lote. El mensaje de ese handler está escrito para terceros («un tercero paciente trae nombre»): que `Revisar` devuelva también el motivo para que cada entidad dé el suyo.
- `etl.usp_ProcesarInventario`: ventana por `fecha`. Obligatorios del contrato (`02` §15). `cantidadEntrada` y `cantidadSalida` ≥ 0 y no ambas > 0.
- Tras `prestaciones`, `nomina` o `inventario`: encolar `CALCULAR_COSTOS` de los meses afectados (ampliar la condición que hoy solo mira `asientos`/`facturas`). Una carga `INCREMENTAL` no tiene ventana en `etl.Carga`: `usp_ProcesarPrestaciones` agrega a la firma del patrón dos salidas, `@FechaMin date OUTPUT` y `@FechaMax date OUTPUT` (mínimo y máximo de la fecha de realización de las filas aceptadas; NULL si no hubo). `AplicarAsync` hoy devuelve `(Ins, Upd, Del, Rech)`: ampliar la tupla con `(DateTime? Desde, DateTime? Hasta)` y que `ProcesarAsync` use ese rango cuando la carga no trae ventana. Es la única desviación del patrón `@Ins/@Upd/@Del/@Rech` y solo para SP incrementales de hechos.

### 3. Bases de costo
**No hay que reescribir `etl.usp_CalcularBasesCosto`:** la versión de `0150` ya calcula las cuatro bases con estas mismas definiciones (verificado línea por línea):
- `COSTO_PERSONAL` = Σ `fact.NominaCosto.Valor` con `EsCosto = 1` por centro de costo y periodo.
- `ACTIVIDADES` = Σ `Cantidad` de `fact.Prestacion` no anuladas por centro y periodo de realización.
- `ATENCIONES` = admisiones distintas (`AdmisionSourceKey`) por centro y periodo.
- `CONSUMO_INSUMOS` = Σ `ValorSalida` de `fact.MovimientoInventario` con `Estado = 1` por centro y periodo.
- Las bases `MANUAL` no se tocan (el `DELETE` filtra `Origen = 'CALCULADA'`).

El trabajo de este punto es **probarlas con datos** (nunca corrieron con tablas llenas) y, solo si una prueba falla, corregir el SP con `CREATE OR ALTER` en 1120.
- Prueba de regresión obligatoria: `CostosTests` existentes en verde (conservación del costo, `THROW 50010`).

### 4. Clasificación de centros y bases manuales (portal `/costos`)
- **Permisos (script 1120), lo mínimo:** `GRANT UPDATE (TipoCC) ON dim.CentroCosto TO rol_app;` y `GRANT INSERT, DELETE ON fact.CostoBaseValor TO rol_app;` (`UPDATE` sobre `fact` ya lo tiene). La RLS de ambas tablas (`BLOCK AFTER INSERT/UPDATE` por `EmpresaId`) impide escribir en otra empresa. Sin estos permisos las dos pestañas fallan con error de permisos en tiempo de ejecución. Prueba: con `rol_app` no se puede cambiar otra columna de `dim.CentroCosto`.
- Nueva pestaña «Centros de costo»: lista con `TipoOrigen` y selector `TipoCC` (PRODUCTIVO / APOYO / ADMINISTRATIVO). `ClasificarCentrosCostoCommand` (lote), auditado, permiso `admin.usuarios` (igual que el resto de `Costos/Comandos.cs`). La carga `COMPLETA` de `centros-costo` **no pisa `TipoCC`** (verificado: la definición en `DefinicionesCatalogo` no incluye esa columna); dejar una prueba que lo fije.
- Sugerencia automática por `TipoOrigen` solo como valor propuesto, nunca aplicado solo (`[S]`).
- Nueva pestaña «Bases manuales»: periodo × centro × valor para `M2`, `KILOS_ROPA`, `PORCENTAJE`; «copiar del mes anterior». `GuardarBasesManualesCommand` (solo bases con `cfg.CostoBase.Origen = 'MANUAL'`; `Valor ≥ 0`, lo exige el `CHECK`). `PORCENTAJE`: `fact.CostoBaseValor` no guarda el centro que reparte (llave `EmpresaId, PeriodoKey, BaseCodigo, CentroCostoId`), así que hay **un** juego de porcentajes por periodo, común a todos los centros que usen esa base; la suma del periodo debe dar 100. Ojo: `etl.usp_DistribuirCostos` reparte en proporción a la base **entre los destinos elegibles** (excluye al propio origen y a los centros ya distribuidos) y renormaliza, así que el porcentaje es un peso relativo: si un centro con porcentaje queda excluido, el resto se reparte su parte. Decirlo en la pantalla. Porcentajes distintos por centro origen exigirían otra tabla: fuera de este plan.
- Recalcular al guardar (mismo mecanismo y tope de 24 meses que hoy).

### 5. Métricas nuevas (`ModuloFinancieroAnalitico`)
Sobre `rpt.vw_MargenServicio`: `financiero.ingreso_servicio`, `financiero.costo_asignado_servicio`, `financiero.margen_servicio`, `financiero.margen_servicio_pct`; dimensiones `periodo`, `centro_costo`, `pagador`, y nueva `servicio` (`dim.Servicio`, `ServicioId`, `Nombre`). Dashboard `financiero.margen_servicio` con aviso fijo: «costo asignado por precio relativo; aproximación hasta tener ficha de costo por procedimiento» (así lo dice la vista).

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1120_ingesta_operativa.sql` |
| Domain | `Modulos/Dimensiones.cs` |
| Application | `Ingesta/Entidades.cs`, `Ingesta/Comandos.cs` (`ReglasPrivacidad`), `Costos/*` (comandos nuevos) |
| Infrastructure | `Ingesta/Procesamiento.cs`, `Auditoria/RepositorioCostos.cs` (clasificación y bases manuales) |
| Módulo | `Modulos.Financiero/ModuloFinanciero.cs` |
| Web | `Components/Pages/Costos.razor` |
| Pruebas | `HechosOperativosTests.cs` (nuevo), `CostosTests.cs`, `CostosWebTests.cs`, `IngestaTests.cs`, aislamiento |

Símbolos para `impact`: `CatalogoEntidades`, `ReglasPrivacidad.Revisar`, `ProcesadorCargas.ProcesarAsync`, `ProcesadorCargas.EncolarCostosAsync`, `ManejadorCalcularCostos`, `DefinicionesCatalogo`.

## Pruebas
- Cada procesador: alta, actualización, rechazo por causa; nómina con nombre → carga rechazada; nómina sin `conceptoCodigo` → `CAMPO_REQUERIDO:conceptoCodigo`; nómina sin módulo activo → 422.
- Portal con `rol_app`: clasificar centros y guardar bases manuales funciona; no puede cambiar `Nombre` de un centro ni tocar centros o bases de otra empresa.
- Bases: caso con 2 centros de apoyo y 3 productivos calculado a mano para las 4 bases; la suma distribuida = costo original.
- `TipoCC` sobrevive a una recarga del catálogo.
- Aislamiento: nómina de A invisible para B; `dash.nomina_costos` solo gerencia.

## Riesgos y supuestos `[S]`
- `mServicios.VrCosto` ¿unitario o total? (P2) → `CostoInsumos` no se usa en ninguna métrica hasta resolverlo.
- `mServicios.centroCostoID` → `SIIFNet.CentrosCostos.Id` (`[S]` de `02` §15).
- Volumen: la carga inicial de prestaciones puede ser la más grande (127 mil servicios en la instalación revisada); medir la transacción única (decisión abierta en `06` §10).
- El conector debe enviar estas entidades: la extracción de `docs/11` no se ha ejecutado contra una base real.
