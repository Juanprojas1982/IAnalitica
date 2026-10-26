# Mapeo SaludIPS → modelo analítico (rev. Opus)

Fuentes: `Resultados1.csv` (diccionario: esquema;tabla;filas;orden;columna;tipo;longitud;nullable;es_pk), `Resultados2.csv` (FK) y los 156 reportes (`03_analisis_reportes_SaludIPS.md`).
Alcance: metadatos y conteos de **una** instalación. Nada se ejecutó contra la BD.
Leyenda: **[C]** confirmado por el SQL de los reportes · **[S]** supuesto por validar.

## 1. Hallazgos

| Hecho | Evidencia | Implicación |
|---|---|---|
| 767 tablas; 547 vacías en esta instalación | conteo | Cada IPS necesita un **perfil de módulos activos** (`seg.EmpresaModulo`) |
| Contabilidad completa en `SIIFNet` | `mMovimientos` 110.726 · `mMovimientosDetalles` 691.986 · `Cuentas` 7.189 · `CentrosCostos` 205 | Fuente de P&G, cartera, CxP y bancos |
| Plan de cuentas y centros de costo **por vigencia** | `Cuentas.Vigencia`, `CentrosCostos.Vigencia`; reportes enlazan vigencias por `Codigo` **[C]** | En la BD central: una fila por **código** |
| Dos normatividades | reportes filtran `NormatividadId = 1` (108 veces) **[C]** | Se extrae solo la 1; la NIIF (`CuentaNIIFId`) queda fuera de v1 |
| Estados | `EstadoEntidad` 1 confirmado, 2 anulado **[C]**; 0 **[S]** eliminado/borrador | Se envían 1 y 2; el 0 no se envía |
| Naturaleza | 0 débito, 1 crédito; `MovimientoContrario` invierte el signo **[C]** | Regla del saldo |
| Cartera **sin tabla propia** | Reportes de cartera solo usan contabilidad: cuentas con `DetalleCartera` 2 (CxC) o 1 (CxP) y cruces por `dContabilidadRelacionadoId` **[C]**. `mFacturas.Saldo` **no** lo usa ningún reporte | Se recalcula en la BD central (`etl.usp_GenerarCorteCartera`) |
| Comprobantes especiales | `CSI0` saldo inicial, `CDC0` cierre, `TEB` traslado entre bodegas, `CNOM` nómina **[C]**; pueden variar por IPS **[S]** | `dim.TipoComprobante.Clase` configurable |
| Bancos | `Cuentas.UsoCuenta = 2` **[C]**; `SIIFNet.BancosCuentas` (18) | `dim.CuentaBancaria` |
| Facturación | `mFacturas` 108.393; servicios `mServicios` 127.455; `dFacturasServicios` 123.924 | Ver riesgo 1 |
| Recaudo en caja | `mRecibos` 48.318 + `dRecibosFormasPagos`; `dRecaudoFacturas` vacío | Tesorería; el recaudo de EPS está en comprobantes contables |
| Glosas | módulo vacío (`dGlosas*` = 0) | Esta IPS no lo usa; la glosa se ve solo por cuentas (`mEmpresasCuentasContables.CuentaGlosa*`) **[S]** |
| Presupuesto | `Presupuesto.*` vacío y es presupuesto público (CDP, RP, CUIPO) | Privadas: el presupuesto gerencial entra por Excel/API. **ESE: el presupuesto público es central → spec 17** |
| Inventario | `Almacen.MovimientosAlmacenDetalles` 9.171, `Lotes` 680, `ArticulosBase` 12.088; valor = cant × costo × (1 − desc %) sin IVA **[C]** | `fact.MovimientoInventario` |
| Usuarios con dos identidades | `cUsuarios.IdcUsuario` (smallint) en facturación/caja; `UsuarioID` guid ↔ `AspNetUsers.Id` con conversión especial **[C]** | Códigos `C:` y `A:` |
| Datos sensibles | `mPacientes` 196.793; `SIIFNet.Terceros` 197.764 (incluye pacientes con contacto); `cUsuarios.ClaveC`, firmas | Paciente = tercero referenciado por `mPacientes.TerceroId` → hash; nunca se extraen claves ni firmas |
| Montos en `float` | todos los valores | `ROUND(…, 2)` y `decimal` en la extracción |
| Escala | 3 sedes, 4 bodegas, 75 pagadores, 92 contratos | IPS mediana |

## 2. Mapeo a la BD central

| Destino | Origen | Notas |
|---|---|---|
| `dim.Sede` | `SIIFNet.Sedes` | **[S]** mismo Id que `IdSede` de facturación (`cAdministracion`) |
| `dim.CentroCosto` | `SIIFNet.CentrosCostos` (última vigencia por código) | `TipoCentroCosto` sin significado conocido **[S]** |
| `dim.LineaServicio` | `pTipoServicio` | Es el agrupador de los reportes de facturación (`SIPS_4`) **[C]** |
| `dim.CuentaContable` | `SIIFNet.Cuentas` Normatividad 1 | + `DetalleCartera`, `MovimientoContrario`, `UsoCuenta` |
| `dim.TipoComprobante` | `SIIFNet.TiposComprobantes` | Clase por prefijo, heredada vía `TipoComprobanteBaseId` **[C]** (regla `TEB`) |
| `dim.Tercero` | `SIIFNet.Terceros` | Paciente si `mPacientes.TerceroId` lo referencia |
| `dim.Pagador` | `mEmpresas` | Particular: `cAdministracion.IdEmpresaParticulares` **[C]**; enlace con tercero vía `CodInstitucionAdm` **[S]** |
| `dim.Contrato` | `mContratos` | Modalidad desde `CodTipoContrato` **[S]** |
| `dim.Servicio` | `pProcedimientos` (`P:`) + `pSuministros` (`S:`) | Colisión de códigos posible **[S]** |
| `dim.Articulo`, `dim.Lote` | `Almacen.ArticulosBase` (+ jerarquía), `Almacen.Lotes`, `RegistrosInvima` | |
| `dim.Almacen` | `Almacen.Bodegas` | Sede vía `pBodegas.CodBodegaSIIFNet` |
| `dim.Profesional` | `mPersonal` (+ `rReferencias` clase 8 = especialidad **[C]**) | |
| `dim.UsuarioOperativo` | `cUsuarios` + `AspNetUsers` | |
| `dim.CuentaBancaria` | `SIIFNet.BancosCuentas` + `pCajas` | |
| `fact.AsientoLinea` | `mMovimientosDetalles` ⨝ `mMovimientos` | Base de P&G, cartera, CxP y bancos |
| `fact.FacturaLinea` | `mFacturas` ⨝ servicios | Ver riesgo 1 |
| `fact.TesoreriaMovimiento` | `mRecibos` ⨝ `dRecibosFormasPagos` (por `CodCaja` + `NoRecibo`) | |
| `fact.ConciliacionBancaria` | `Tesoreria.ConciliacionesBancarias` | `EstadoEntidad` 0 abierta, 1 cerrada **[C]** |
| `fact.MovimientoInventario` | `Almacen.MovimientosAlmacen(Detalles)` | |
| `fact.AdmisionPendienteFactura` | `mAdmisiones` sin factura vigente (regla `SIPS_37`) | |
| `fact.Glosa` | `dGlosas` + `dGlosasItems` | Solo con módulo activo |
| `fact.CarteraDocumento` | **calculada** desde `fact.AsientoLinea` | |
| `fact.Presupuesto` | Excel/API | |

## 3. Riesgos

1. **Enlace factura ↔ servicios.** Ningún reporte usa `dFacturasServicios`: los 6 reportes de facturación enlazan por `IdAdmision`. Mi extracción usa `dFacturasServicios` para no duplicar cantidades cuando una admisión tiene varias facturas, pero **no sé si esa tabla está completa** en todos los flujos (paquetes, copagos, capitación). Antes de decidir, ejecutar:
   ```sql
   SELECT COUNT(*) AS FacturasSinDetalle FROM dbo.mFacturas f
   WHERE f.Anulado = 0 AND NOT EXISTS (SELECT 1 FROM dbo.dFacturasServicios d WHERE d.NoFactura = f.NoFactura AND d.IdSede = f.IdSede);
   SELECT COUNT(*) AS AdmisionesConVariasFacturas FROM (SELECT IdAdmision FROM dbo.mFacturas WHERE Anulado = 0 GROUP BY IdAdmision HAVING COUNT(*) > 1) x;
   ```
   Si hay facturas sin detalle, la extracción vuelve a la regla de los reportes (prorrateo por `IdAdmision`).
2. **Facturación vs contabilidad.** La contabilización va por envío (`mFacturas.MovimientoContableId`, `EnviosFacturaContabilidad`). Lo facturado y no contabilizado no aparece en el P&G: el dashboard muestra ambos y la diferencia.
3. **Anulaciones.** Cambian `Anulado`/`FechaAnulacion` o `EstadoEntidad`, no `FechaDigitacion`: una carga incremental por `FechaDigitacion` las pierde. Por eso la carga diaria es por **ventana**.
4. **Módulos distintos por IPS.** Sin perfil de módulos, un dashboard vacío parece un fallo.
5. **Versiones.** `cVersion` está vacía en esta BD: la versión del esquema no se puede leer de ahí. Los reportes recibidos tienen variantes por archivo (posibles instalaciones distintas).

## 4. Preguntas abiertas
1. Significado de `EstadoEntidad = 0`, `TipoPersona`, `ClaseTercero`, `TipoCentroCosto`, `CodTipoContrato`, `AutorizacionesDIAN.Modalidad` (1 en factura electrónica, 3 en `SIPS_704` capitación) y `Discriminator`.
2. Resultado de las dos consultas del riesgo 1.
3. Supuestos de la tabla §2 marcados **[S]**.
4. Dónde vive el presupuesto gerencial.
5. ¿Las 30 IPS tienen la misma versión? ¿Dónde se registra la versión instalada?
