# PL-09 · Recaudo sin aplicar (anticipos y giros sin cruzar)

Estado: diseñado a nivel de hipótesis, **bloqueado por DP-8 y por datos reales**. Módulo `CARTERA`. Script `1110_recaudo_sin_aplicar.sql`.
Preparación (2026-11-16): **no listo.** Sigue bloqueado por DP-8 y el diagnóstico del paso 0; ninguna decisión del dueño lo destraba.
No implementar hasta ejecutar el diagnóstico del paso 0 contra una base real.

## Problema
`etl.usp_GenerarCorteCartera` toma como documento toda línea de una cuenta con `DetalleCartera IN (1, 2)` y calcula `ValorOriginal` por naturaleza (cuenta débito → `Debito`). Un pago recibido **sin cruzar contra una factura** es un crédito en una cuenta CxC sin `RelacionadoSourceKey`: su `ValorOriginal` es 0 y el `WHERE o.ValorOriginal <> 0` lo descarta.
Consecuencias hoy:
- La cartera por edades queda **sobrestimada** en ese valor (el saldo contable es menor).
- El DSO por pagador se infla.
- Solo se nota como `Diferencia` en `rpt.vw_ConciliacionCartera`, sin explicación.
Para una IPS con giro directo o pagos globales de EPS, este valor puede ser grande. Es `[S]`: depende de cómo SaludIPS contabilice el anticipo (¿misma cuenta CxC?, ¿cuenta 28 de anticipos recibidos?, ¿con o sin `dContabilidadRelacionadoId`?).

## Paso 0 · Diagnóstico (antes de cualquier código)
Consulta de solo lectura sobre la BD central de un piloto, por pagador:
1. Créditos en cuentas `DetalleCartera = 2` con `RelacionadoSourceKey IS NULL` y sin líneas que los referencien.
2. Su suma frente a `Diferencia` de `rpt.vw_ConciliacionCartera` (si explican la diferencia, la hipótesis es correcta).
3. Saldos de cuentas del pasivo que parezcan anticipos de pagadores (grupo 28 `[S]`).
Resultado esperado: decidir entre las variantes de abajo. Guardar la consulta en `docs/` como diagnóstico, igual que `30_DIAGNOSTICO_SIPS108_vs_SIPS550.md`.

## Diseño propuesto (variante «crédito sin cruzar en la misma cuenta»)
### Script `1110_recaudo_sin_aplicar.sql`
- **No cambiar** `usp_GenerarCorteCartera` ni `fact.CarteraDocumento` (la cartera por documento sigue igual y sigue cuadrando con `CARTERAESTADOCUENTAEDADES`, que es el criterio de aceptación de F2).
- Tabla nueva `fact.RecaudoSinAplicar (EmpresaId, CorteFechaKey, LineaSourceKey, CuentaId, TerceroId, PagadorId, SedeId, FechaMovimientoKey, Valor, Dias, CargaId)`, llenada por `etl.usp_GenerarRecaudoSinAplicar @EmpresaId, @Corte`, invocado desde `ManejadorCalcularCartera` después del corte de cartera (mismo trabajo, misma transacción lógica por corte: borra el corte y lo inserta).
- Regla: líneas de cuentas `DetalleCartera = 2`, `Estado = 1`, `FechaKey <= corte`, cuyo valor contrario a la naturaleza es > 0, sin `RelacionadoSourceKey` y que no sean reversiones; menos las líneas posteriores que las referencien (aplicaciones tardías).
- **Vista `rpt.vw_RecaudoSinAplicar`**: `EmpresaId, CorteFechaKey, PagadorId, SedeId, RangoDias, Movimientos, Valor`.
- **Vista `rpt.vw_CarteraNetaPagador`** (`UNION ALL` cartera CXC + sin aplicar en negativo): permite `cartera.saldo_neto_pagador` = `SUM(Valor)` y un DSO neto.

### Métricas (`ModuloCartera`)
`cartera.recaudo_sin_aplicar`, `cartera.saldo_cxc_neto`, `cartera.dso_neto`. En `cartera.conciliacion`: nueva columna «Explicado por recaudo sin aplicar» para que la diferencia residual sea la que importa.
Dashboard `cartera.sin_aplicar`: KPI valor y antigüedad · barras por pagador · tabla por rango de días.

## Archivos por capa (si se aprueba)
| Capa | Archivo |
|---|---|
| BD | `database/scripts/1110_recaudo_sin_aplicar.sql` |
| Infrastructure | `Ingesta/Procesamiento.cs` (`ManejadorCalcularCartera`) |
| Módulo | `Modulos.Ingreso/ModuloIngreso.cs` |
| Pruebas | `CarteraTests.cs` (ampliar), `DashboardsTests.cs`, aislamiento |

Símbolos para `impact`: `ManejadorCalcularCartera.EjecutarAsync`, `ModuloCartera.Registrar`.

## Pruebas
- Factura 1.000 + pago sin cruzar 400 del mismo tercero → cartera 1.000, sin aplicar 400, neto 600, diferencia de conciliación 0.
- El pago se cruza después (línea relacionada) → sale de «sin aplicar» en el corte siguiente y la cartera baja.
- Nota crédito sin cruzar: **también aparece** (es crédito sin aplicar); el tablero lo llama «créditos sin aplicar» si el paso 0 muestra que no son solo pagos.
- `CarteraTests` existentes siguen en verde sin cambios (regresión del cuadre de F2).

## Riesgos
- Si SaludIPS lleva los anticipos en una cuenta de pasivo sin `DetalleCartera`, esta regla no ve nada y hay que diseñar otra (parametrizar cuentas de anticipo por empresa).
- Confundir saldos iniciales acreedores con pagos: el diagnóstico debe cuantificarlos.
- Tocar el trabajo de cartera es tocar el criterio de aceptación de F2: por eso el diseño agrega y no modifica.
