# Análisis de los reportes de SaludIPS (rev. Opus)

Fuente: `reportes1..4.csv`, formato `código;nombre;SQL`. Son 286 filas, 154 códigos y 156 pares código-nombre: hay versiones distintas del mismo reporte entre archivos, probablemente de instalaciones distintas.
El SQL es T-SQL con `SET DATEFORMAT DMY` y marcadores que la app reemplaza como texto (`@Fecha Inicio`, `@Fecha Final`, `@Fecha Corte`, `@Sede`, `@Empresa`, `@Cuenta`, `@Tercero`, `@Bodega`, `@Vigencia`). Leí completos los financieros, contables, de inventario y de facturación clave. Los asistenciales solo los clasifiqué. No se ejecutó ninguno.

## 1. Para qué sirven
1. **Definen los KPI** con las reglas que la IPS ya acepta.
2. **Pruebas de cuadre:** cada cifra del dashboard debe coincidir con su reporte para el mismo periodo.
3. **Detectan reglas locales y errores** que no se deben copiar (§4).

## 2. Clasificación
| Familia | Reportes | Uso en la plataforma |
|---|---|---|
| Contabilidad | `ESTADORESULTADOSCENTROCOSTOHORIZONTAL`, `MOVIMIENTOSCONTABLESGENERAL`, `AUDITORIACONTABLE`, `LISTADOCUENTAS/CENTROSCOSTO/TERCEROS` | P&G por centro de costo; auditoría |
| Cartera CxC | `CARTERAESTADOCUENTA`, `…EDADES`, `…EDADESXTERCERO` | Antigüedad, concentración por EPS |
| CxP | `CUENTASXPAGARESTADOCUENTA`, `…EDADES`, `…EDADESXTERCERO` | Pasivos y calendario de pagos |
| Tesorería | `CONCILIACIONBANCARIADETALLADA`, `SIPS_19` (recibos), `SIPS_552` (informe fiscal de caja) | Caja, bancos, conciliación |
| Facturación | `SIPS_4/5/6`, `SIPS_35`, `SIPS_55*`, `SIPS_108`, `SIPS_551`, `FA_001`, `SIPS_12` (anuladas), `SIPS_39` (descuentos), `SIPS_059` (factura electrónica), `SIPS_95` (cirugías), `SIPS_102*`, `SIPS_133*` | Ingresos por servicio, EPS, usuario |
| Pendiente de facturar | `SIPS_37` | KPI: atendido no facturado |
| Productividad | `SIPS_3`, `SIPS_015`, `SIPS_25`, `SIPS_54`, `SIPS_98`, `SIPS_163` | Por médico y por usuario |
| Contratación | `SIPS_704` (capitación), `SIPS_FPGP` (PGP) | Ingreso por modalidad |
| Inventario | `almacenBalanceInventarioGeneralSinIva`, `…ExistenciasArticulosLotesBodega`, `…MovimientosInventario*`, `…ComprasAcumulado`, `SIPS_RTSF` | Valor, rotación, vencimientos |
| Presupuesto público | `CUIPO01–05`, `RelacionCompromisos2193` | **Central para ESE → spec 17** |
| Tributario | `DIAN1001…2276` | Fuera del dashboard |
| Asistencial | citas, morbilidad, triage, remisiones, Res. 256/1552/1604/1886/4505, PyP | Fase posterior |
| Nómina | `Nomina.Electronica`, `nominaIncapacidades` | Fase posterior (costo de personal) |

## 3. Reglas de negocio confirmadas por el SQL
- **Registro vigente:** `EstadoEntidad = 1`. `MOVIMIENTOSCONTABLESGENERAL` muestra `IN (1,2)` y rotula el 2 como «ANULADO».
- **Naturaleza:** `0 = débito`, `1 = crédito`; `MovimientoContrario = 1` invierte el saldo.
- **Plan vigente:** `NormatividadId = 1` y `Vigencia = YEAR(corte)`; las vigencias se enlazan por `Codigo`.
- **Comprobantes:**
  - `CDC0` es el cierre: lo excluye el P&G.
  - `CSI0` son los saldos iniciales: los reportes DIAN excluyen `CSI0` y `CDC0` de los movimientos y usan `CSI0` como saldo de apertura.
  - `TEB` es el traslado entre bodegas: se reconoce por el prefijo o por `TipoComprobanteBaseId`.
  - `CNOM` es la nómina.
- **P&G:** cuentas de las clases 4, 5, 6 y 7, acumulado del 1 de enero al corte.
- **Cartera (CxC = `DetalleCartera` 2; CxP = 1):**
  - **Origen:** línea confirmada en una cuenta de cartera, con valor del lado de la naturaleza de la cuenta.
  - **Abonos:** líneas que apuntan al origen por `dContabilidadRelacionadoId`, con la misma cuenta y el mismo tercero, del lado contrario. Se les restan sus reversiones, que son las líneas que apuntan a un abono.
  - **Exclusión:** las reversiones no cuentan como documento origen.
  - **Edad:** días desde `FechaCartera` (o la fecha del movimiento) hasta el corte. Tramos 0‑30, 31‑60, 61‑90, 91‑120, 121‑150, 151‑180, 181‑360 y >360.
  - **Cuenta de cobro:** `mFacturas.IdRipsEnvio` → `mRipsEnvios.NoCuentaCobro`.
- **Facturación válida:** `mFacturas.Anulado = 0` y `mServicios.Anulado = 0`.
- **Ingreso por tipo de servicio (`SIPS_4`):** se prorratea el total de la factura, `VrTotal × VrTotalServicio / VrServicios`. Los paquetes (`CodPaquete ≠ '0'`) toman `dFacturasPaq.VrTotal`.
- **Factura electrónica:** `0` enviada, `1` cargada, `2` con error, `3` sin enviar.
- **Particulares:** tercero genérico con identificación `200`; el paciente real se obtiene por `mPacientes.TerceroId` (`DETALLEXTERCEROEMPRESAPARTICULAR`). El ingreso de particulares va en las cuentas `41%`.
- **Inventario:** valor = cantidad × costo × (1 − descuento %), sin IVA. El saldo inicial es la suma de los movimientos anteriores a la fecha de inicio.
- **Referencias legadas (`rReferencias.CodClaseReferencia`):** 8 especialidad, 11 finalidad, 34 tipo de usuario/régimen, 43 clase de servicio.
- **Usuario de SIIFNet:** `AspNetUsers.Id` se enlaza convirtiendo los 8 primeros hexadecimales del guid `UsuarioID` a entero, o por igualdad directa.

**Por validar:** `AutorizacionesDIAN.Modalidad` vale 1 en factura electrónica y 3 en el reporte de capitación (`SIPS_704`). Que 3 signifique capitación es una inferencia por el nombre del reporte.

## 4. Errores y reglas no portables en los reportes
| # | Hallazgo | Reportes | Efecto | Tratamiento en la plataforma |
|---|---|---|---|---|
| 1 | `BETWEEN '@Fecha Inicio' AND '@Fecha Final'` sin hora final | `SIPS_4` (versión de reportes3), `SIPS_6`, `SIPS_210` | **Excluye el último día** salvo lo registrado a las 00:00 | Rango semiabierto `>= desde AND < hasta + 1 día` |
| 2 | Recibos unidos a formas de pago solo por `NoRecibo` | `SIPS_55`, `SIPS_55_1`, `SIPS_551`, `SIPS_552` | Duplica formas de pago si dos cajas repiten número | Unir por `CodCaja` + `NoRecibo` |
| 3 | División entre `VrServicios` sin protección | `SIPS_4`, `SIPS_6` (otras versiones sí filtran `> 0`) | Error de división por cero | `NULLIF(VrServicios, 0)` |
| 4 | Factura ↔ servicios por `IdAdmision` | 6 reportes de facturación | **Cantidades duplicadas** si la admisión tiene varias facturas; los valores no, por el prorrateo | Ver riesgo 1 de `02_mapeo` |
| 5 | Copago tratado como neto para EPS con códigos fijos (`'20','202304','96','13','35','med1'`) o por parámetro | `SIPS_551` (`SIPS_552` solo usa el parámetro) | Regla de un cliente | `cfg.EmpresaParametro` |
| 6 | Versiones por cliente (`SIPS_133_1 … PALERMO IMAGEN`, `_1`) | varios | Personalizaciones | Validar la versión de cada IPS |
| 7 | Nombres de pacientes en la salida | `FA_001`, `SIPS_12`, `SIPS_37`, `SIPS_39`, `SIPS_551`, `SIPS_552`, `SIPS_FPGP` | Datos personales | Solo agregados; paciente en hash |
| 8 | Cartera calculada en vivo con autouniones sobre 692 mil líneas | cartera y CxP | Lento a escala | Foto diaria precalculada |
| 9 | Fechas como texto `DMY` y parámetros concatenados | todos | Frágil e inyectable si el parámetro viene del usuario | Parámetros tipados en el agente |

## 5. Dashboards (prioridad) y su reporte de cuadre
| Dashboard | KPI principales | Cuadre contra |
|---|---|---|
| Gerencial | ingresos facturados vs recaudados, margen operacional, cartera total y vencida, caja, desviación presupuestal, atendido no facturado | combinación de los siguientes |
| P&G | por empresa, sede, centro de costo, línea | `ESTADORESULTADOSCENTROCOSTOHORIZONTAL` |
| Facturación | por servicio, EPS, modalidad, usuario; anuladas; descuentos | `SIPS_4`, `SIPS_5`, `SIPS_12`, `SIPS_39`, `SIPS_704` |
| Cartera y CxP | antigüedad, concentración, cuentas de cobro | `CARTERAESTADOCUENTAEDADES`, `CUENTASXPAGARESTADOCUENTAEDADES` |
| Tesorería | recaudo diario por forma de pago, conciliación | `SIPS_19`, `CONCILIACIONBANCARIADETALLADA` |
| Inventario | valor, rotación, vencimientos por lote y bodega | `almacenBalanceInventarioGeneralSinIva`, `almacenExistenciasArticulosLotesBodega` |
| Productividad | por médico, especialidad, usuario | `SIPS_3`, `SIPS_54` |
| Pendiente de facturar | valor y días por EPS y sede | `SIPS_37` |

En los cuadres, los reportes con errores de §4 se corrigen antes de comparar; la diferencia esperada debe quedar documentada.
