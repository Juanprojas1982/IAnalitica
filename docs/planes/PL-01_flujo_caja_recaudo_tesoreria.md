# PL-01 · Flujo de caja, recaudo de cartera, caja y conciliación bancaria

Estado: diseñado, no implementado. Módulo `TESORERIA` (permiso `dash.tesoreria`, ya sembrado). Script `1050_tesoreria.sql`.
Preparación (2026-11-16): **listo para implementar.** DP-1 aprobada (D54). Partes B y C tras la verificación de módulo de PL-02.

## Objetivo
Responder: ¿cuánto entró y salió de caja y bancos?, ¿cuánto pagó cada EPS y a cuántos días?, ¿qué se recaudó en caja y por quién?, ¿qué cuentas bancarias están sin conciliar?

## Partes (cada una entregable por separado)
### A · Desde `asientos` (sin conector, datos ya ingeridos)
**Vista `rpt.vw_MovimientoDisponible`** — entradas y salidas de las cuentas de disponible.
- Fuente: `fact.AsientoLinea` ⨝ `dim.CuentaContable`, `Estado = 1`, cuentas con `EsBanco = 1` o `LEFT(Codigo,2) = '11'` (DP-1 aprobada, D54), comprobantes con `tc.Clase <> 'CIERRE'`.
- **Traslados:** agrupar por `MovimientoSourceKey`; neto = Σ débito − Σ crédito sobre cuentas de disponible. Si el neto del comprobante es 0 es traslado entre cuentas propias y se excluye. Entradas = neto > 0; salidas = −neto cuando < 0.
- **Saldos iniciales:** los comprobantes con `tc.Clase = 'SALDO_INICIAL'` (valor del `CHECK` de `dim.TipoComprobante`, `0040`) no son flujo: van a la columna `SaldoInicial` y quedan fuera de `Entradas`/`Salidas`/`FlujoNeto` (DP-1). Que SaludIPS marque así la apertura es `[S]`: si llegan como `NORMAL`, el primer periodo mostrará una entrada inflada y el tablero debe rotularlo.
- Columnas: `EmpresaId, PeriodoKey, FechaKey, SedeId, CuentaId, Entradas, Salidas, FlujoNeto, SaldoInicial`. La sede y la cuenta son las de la línea de disponible de mayor valor del comprobante.
- Limitación a documentar: clasifica por comprobante, no por concepto (no distingue nómina de proveedores).

**Vista `rpt.vw_RecaudoCartera`** — abonos aplicados a cuentas por cobrar.
- Fuente: líneas `p` de `fact.AsientoLinea` con `RelacionadoSourceKey = o.SourceKey`, misma `CuentaId` y `TerceroId`, donde la cuenta tiene `DetalleCartera = 2` (misma regla de `#abono` en `etl.usp_GenerarCorteCartera`).
- `EsEfectivo` = 1 si el mismo `MovimientoSourceKey` del abono toca una cuenta de disponible; si no, es cruce sin caja (nota crédito, glosa aceptada, castigo) `[S]`.
- Columnas: `EmpresaId, PeriodoKey` (del abono)`, SedeId, PagadorId` (vía `dim.Pagador.TerceroId`, igual que la cartera)`, EsEfectivo, Abonos` (conteo)`, ValorAbonado, ValorEfectivo, ValorSinCaja, DiasPonderados` (= Σ valor × días entre `COALESCE(o.FechaCartera, fecha de o)` y la fecha del abono).
- Restar reversiones (líneas relacionadas con el abono), como hace `#rev`.

**Métricas** (`ModuloTesoreria`, nuevo en `IAnalitica.Modulos.Ingreso`):
`tesoreria.entradas`, `tesoreria.salidas`, `tesoreria.flujo_neto` (dims periodo, sede, cuenta) · `tesoreria.recaudo_cartera` = `SUM(ValorEfectivo)`, `tesoreria.cruces_sin_caja` = `SUM(ValorSinCaja)`, `tesoreria.dias_de_pago` = `SUM(DiasPonderados)/NULLIF(SUM(ValorAbonado),0)` con `KpiMeta: "DIAS_PAGO"` (dims periodo, sede, pagador).
**Dashboards:** `tesoreria.flujo_caja` (3 KPI + línea entradas/salidas por periodo + tabla por cuenta) y `tesoreria.recaudo` (KPI recaudo, días de pago, cruces sin caja + barras por pagador + tabla por pagador).

### B · Ingesta de `recibos` (caja)
Requiere la verificación de `ModuloRequerido` de PL-02 §1 (si PL-02 no está mezclado, se crea aquí con esa misma definición).
- `CatalogoEntidades`: `new EntidadIngesta("recibos", new[] { Modos.Ventana, Modos.Inicial }, new[] { "valor" }, ModuloRequerido: "TESORERIA")`.
- `etl.usp_ProcesarRecibos` → `fact.TesoreriaMovimiento`, ventana por `fecha`. Campos del contrato `02` §15.
  - Obligatorios: `sourceKey, fecha, cuentaBancariaCodigo, tipoMovimiento, valor, anulado` → `CAMPO_REQUERIDO`.
  - `tipoMovimiento` ∉ (`INGRESO`,`EGRESO`,`TRASLADO`) → `VALOR_INVALIDO:tipoMovimiento`; `valor < 0` → `VALORES_INVALIDOS`.
  - `cuentaBancariaCodigo` inexistente → `REFERENCIA_INEXISTENTE:cuentaBancariaCodigo`. Pagador, sede y usuario son opcionales (NULL si no existen).
  - `Concepto` es `NOT NULL`: `ISNULL(LEFT(concepto, 40), 'SIN_CONCEPTO')`. **No normalizar** a categorías (la extracción envía el texto de `pTipoConcepto`); la normalización es un pendiente `[S]`.
- `ProcesadorCargas.AplicarAsync`: `case "recibos"`.
- **Vista `rpt.vw_RecaudoCaja`**: `EmpresaId, PeriodoKey, FechaKey, SedeId, PagadorId, UsuarioOpId, Concepto, FormaPago, Recibos, Valor, RecibosAnulados, ValorAnulado` (solo `TipoMovimiento = 'INGRESO'`). No tocar `rpt.vw_FlujoCaja` (queda para cuando existan egresos).
- Métricas: `tesoreria.recaudo_caja`, `tesoreria.recibos`, `tesoreria.pct_recibos_anulados`. Dimensiones nuevas en `Dimensiones.Todas`: `concepto_caja`, `forma_pago` (llave legible, sin tabla).
- Dashboard `tesoreria.caja`: KPI + barras por concepto + dona por forma de pago + tabla por usuario (anulaciones por cajero).

### C · Ingesta de `conciliaciones`
- `new EntidadIngesta("conciliaciones", new[] { Modos.Ventana }, Array.Empty<string>(), ModuloRequerido: "TESORERIA")` (sin suma de control: no hay una columna de valor única).
- `etl.usp_ProcesarConciliaciones` → `fact.ConciliacionBancaria`, llave `(EmpresaId, CuentaBancariaId, PeriodoKey)`. Ventana por `periodo` (yyyymm) derivado de `VentanaDesde/Hasta`; borra las ausentes del rango. `sourceKey` para rechazos = `cuentaBancariaCodigo|periodo`.
- **Vista `rpt.vw_ConciliacionBancaria`**: `EmpresaId, PeriodoKey, CuentaBancariaId, SaldoLibro, SaldoExtracto, Diferencia, DiferenciaAbs, SinCerrar` (1/0).
- Métricas `tesoreria.diferencia_conciliacion` = `SUM(DiferenciaAbs)`, `tesoreria.cuentas_sin_cerrar`. Dimensión nueva `cuenta_bancaria` (`dim.CuentaBancaria`, `CuentaBancariaId`, `Entidad`).

## Archivos por capa
| Capa | Archivo | Cambio |
|---|---|---|
| BD | `database/scripts/1050_tesoreria.sql` | 2 SP + 4 vistas |
| Domain | `src/IAnalitica.Domain/Modulos/Dimensiones.cs` | 3 dimensiones |
| Application | `src/IAnalitica.Application/Ingesta/Entidades.cs` | 2 entidades |
| Infrastructure | `src/IAnalitica.Infrastructure/Ingesta/Procesamiento.cs` | 2 `case` en `AplicarAsync` |
| Módulo | `src/IAnalitica.Modulos.Ingreso/ModuloIngreso.cs` (o `ModuloTesoreria.cs` nuevo) | `ModuloTesoreria` y agregarlo a `Modulos` |
| Pruebas | `tests/IAnalitica.Integration.Tests/Ingesta/TesoreriaTests.cs` (nuevo), `DashboardsTests.cs`, `AislamientoEntreEmpresasTests.cs` | ver abajo |

Símbolos para `impact` antes de editar: `CatalogoEntidades`, `ProcesadorCargas.AplicarAsync`, `ModuloIngreso.Modulos`, `Dimensiones.Todas`.

## Pruebas
- A: comprobante banco↔CxC cuenta como entrada y como recaudo efectivo; traslado banco↔banco no aparece; nota crédito contra CxC va a `ValorSinCaja`; abono reversado no suma; días de pago ponderados = caso calculado a mano.
- **Cuadre:** Σ `FlujoNeto` + Σ `SaldoInicial` de todos los periodos = saldo de las cuentas de disponible en `rpt.vw_SaldoBalance` (misma base, ≤ 0,01). Comprobante `SALDO_INICIAL` no aparece como entrada.
- B y C: insertar/actualizar/eliminar por ventana, cada rechazo, anulados, `ENTIDAD_NO_SOPORTADA` ya no aplica, módulo inactivo (ver PL-02).
- Aislamiento: empresa A no ve movimientos ni recaudo de B por las 4 vistas.

## Riesgos y supuestos `[S]`
- Disponible (D54) vale para el PUC comercial; una ESE con plan CGN no usa el grupo 11 igual: mantener el tablero apagado para ESE hasta PL-14 fase 4.
- `EsEfectivo` asume que el pago y el cruce van en el mismo comprobante.
- `PagadorId` por tercero: un tercero con varios pagadores toma el menor (igual que la cartera actual).
- `recibos` de la extracción solo trae ingresos de caja: el tablero se llama «Recaudo en caja», no «Flujo de caja».
- Rendimiento de `vw_MovimientoDisponible` (agrupa por comprobante): medir con 13 meses; si pasa de 2 s, materializar en tabla calculada dentro de la cadena.

## Fase siguiente (no incluida)
Flujo proyectado a 13 semanas = cartera por edades × tasa histórica de recaudo por pagador (de `vw_RecaudoCartera`) − CxP por vencimiento − nómina. Requiere `IMetricaCalculada` y datos reales para calibrar.
