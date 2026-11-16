# PL-13 · Farmacia, inventario y validador de precios completo

Estado: diseñado, no implementado. Módulos `FARMACIA` e `INVENTARIO`. Script `1150_farmacia.sql`.
Depende de PL-10 (`inventario`). Es el paquete del **operador farmacéutico piloto**. Cierra el pendiente técnico 5 y parte del 8 (`06` §10).

## Objetivo
1. Tablero de pendientes de dispensación (prioridad 1 por riesgo de sanción, `03` §20).
2. Inventario: valor, rotación, cobertura y vencimientos.
3. Validador de precios: reglas R3, R4, R7–R9; descarga del catálogo oficial; bajada a SaludIPS.

## Bloqueo previo (no es código)
**Las normas de `03` §20 salieron de búsquedas web (oct-2026) y deben confirmarse con el área regulatoria antes de implementar reglas** (Circulares 021 y 022 de 2026 de la CNPMDM, Res. 1604 de 2013, plazos MIPRES). Sin esa confirmación solo se implementan las partes A y B.

## Parte A · Inventario (sin dependencia regulatoria)
- Vistas: `rpt.vw_InventarioMensual` existe. Nuevas en 1150: `rpt.vw_InventarioSaldo` (saldo en cantidad y valor por almacén, artículo y lote, acumulado hasta el periodo; `Acumulado: true` en las métricas) y `rpt.vw_VencimientoLotes` (saldo por lote con `DiasParaVencer` y rango `VENCIDO`, `0-30`, `31-90`, `91-180`, `>180`; usa `dim.Lote.FechaVencimiento`; la fecha de referencia es el último corte cargado, **no** `GETDATE()` en la vista, para que la caché sea coherente).
- `ModuloInventario` (en `Modulos.Farmacia`): `inventario.valor`, `inventario.salidas_valor`, `inventario.dias_inventario` = `30.0*SUM(SaldoValor)/NULLIF(SUM(ValorSalidaMes),0)`, `inventario.valor_vencido`, `inventario.valor_por_vencer_90`. Dimensiones nuevas `almacen`, `articulo`, `rango_vencimiento`.
- Dashboards `inventario.resumen` y `inventario.vencimientos`.

## Parte B · Pendientes de dispensación
- **Falta el contrato de datos**: `ordenes-externas` está en la lista «a añadir» (P9) sin campos definidos y sin tabla destino. Paso 0: definir el contrato con el equipo funcional a partir de `SIPS_119`, `SIPS_700`, `SIPS_701` (reportes de dispensación ya existentes) y escribir la extracción. **Sin ese paso no hay parte B.**
- Diseño propuesto (a validar): `fact.FormulaDispensacion` (EmpresaId, SourceKey, FechaFormula, PagadorId, AlmacenId, ArticuloId, PacienteHash, CantidadFormulada, CantidadEntregada, FechaEntrega, FechaEntregaPendiente, ValorPendiente, Estado). Entidad `dispensaciones` `INCREMENTAL`.
- Vista `rpt.vw_PendientesDispensacion`: pendientes abiertos por rango de antigüedad (`<24h`, `24-48h`, `>48h`), pagador, punto y artículo; `% resueltos en ≤ 48 h`; **«pendiente con existencia disponible»** = pendiente abierto cuyo artículo tiene saldo > 0 en otro almacén de la empresa (cruce con `vw_InventarioSaldo`).
- `ModuloFarmacia`: `farmacia.pendientes`, `farmacia.valor_pendiente`, `farmacia.pct_resuelto_48h` (`KpiMeta`), `farmacia.pendientes_con_existencia`, `farmacia.pct_completas_primera_visita`. Dashboard `farmacia.pendientes`.
- El plazo de 48 h es un parámetro (`cfg.EmpresaParametro`), no una constante: la norma se confirma primero.

## Parte C · Validador completo (tras confirmación regulatoria)
- Contrato: agregar `tarifas-suministros` y `catalogo-medicamentos` si la foto `configuracion` no los cubre ya (verificar `saludips.pTarifasSuministros` y `saludips.pSuministros` en `1000_saludips_configuracion.sql`: probablemente ya llegan por FOTO y no hace falta entidad nueva).
- Reglas nuevas (una clase por regla, mismo motor): **R3** precio facturado > máximo (necesita valor unitario facturado por CUM: `fact.FacturaLinea` con `ServicioId` → CUM; el enlace servicio↔CUM es `[S]`), **R4** precio de compra > máximo (necesita compras: `fact.MovimientoInventario` de entrada), **R7** marca de regulado incoherente con la lista, **R8** IVA (solo tras confirmar con el contador; por defecto desactivada), **R9** tarifa desactualizada (umbral parametrizable).
- Se mantiene la regla vigente: **ningún hallazgo crítico sin factor de presentación validado**.
- Importador: tarea `ACTUALIZAR_CATALOGO_REGULATORIO` (ya nombrada en `etl.Trabajo`) que descarga el dataset `nauz-qkjw` de datos.gov.co. Requiere: confirmar campos y unidades (`[S]`), y **agregar el host a la política de red del despliegue** (D32 no aplica: es gratuito, pero es una salida nueva). Hasta entonces, el importador JSON manual sigue siendo el camino.
- Bajada a SaludIPS: implementar `GET /v1/regulacion/precios-maximos?desde=` (declarado y no implementado). Devuelve CUM, máximo por presentación y por unidad, vigencias, circular. Autorizado con el token del conector; datos globales (sin `EmpresaId`). La validación en tiempo real dentro de SaludIPS es trabajo del otro repositorio.

## Archivos por capa
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1150_farmacia.sql` (vistas; tabla y SP de dispensación si se aprueba B) |
| Contracts | `Ingesta.Contracts/Dtos.cs` (respuesta de precios máximos) |
| Application | `Ingesta/Entidades.cs`, `Regulacion/*` |
| Infrastructure | `Ingesta/Procesamiento.cs`, `Regulacion/*` (reglas, importador) |
| Api | `Endpoints.cs` (bajada) |
| Módulo | `Modulos.Farmacia/ModuloFarmacia.cs` |
| Pruebas | `InventarioTests.cs`, `FarmaciaTests.cs`, `ValidadorPreciosTests.cs`, `ApiIngestaTests.cs`, aislamiento |

Símbolos para `impact`: `ModuloFarmacia`, `CatalogoEntidades`, reglas del validador (clase base), `Endpoints` de la API, `Dimensiones.Todas`.

## Pruebas
- Inventario: saldo acumulado = entradas − salidas; lote vencido y por vencer en su rango; días de inventario contra caso a mano.
- Pendiente con existencia en otra bodega aparece; sin existencia no.
- R3 y R4 sin factor validado quedan «por verificar», no críticas.
- Bajada: solo con token de conector; no filtra por empresa; `desde` devuelve solo lo cambiado.
- Aislamiento: inventario y pendientes de A invisibles para B; el catálogo `reg.*` sí es común.

## Riesgos y supuestos `[S]`
- Unidades (presentación vs. unidad de consumo): sigue siendo el problema central; `pSuministros.Costo` y `pTarifasSuministros.Valor` por unidad es `[S]` (P2).
- Responsabilidad: el validador advierte; debe constar en contrato e interfaz.
- `SIPS_706` tiene el NIT del operador escrito en el SQL: no reutilizarlo tal cual.
- MIPRES y SISMED quedan **fuera**: sus plazos y emisores están «por verificar» en el propio contexto.
