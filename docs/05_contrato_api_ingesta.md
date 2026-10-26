# Contrato API de ingesta v1

Consumidores: agente SaludIPS (principal), otros sistemas (servicio nacional), conector de archivos.
Extracción de referencia para SaludIPS: `06_extraccion_saludips.sql`.

## 1. Autenticación y límites
- OAuth2 *client credentials*: `POST /connect/token` → JWT (60 min) con claim `empresa_id`. Credenciales: OpenIddict + `seg.ApiClienteEmpresa` (empresa y límites).
- **El `EmpresaId` sale del token; si el body trae otro, se ignora.**
- Límite por cliente (`LimitePorMinuto`), lote máximo de 5.000 filas o 10 MB con `Content-Encoding: gzip`.
- Versionado por URL (`/v1`). Cambios incompatibles solo en `/v2`.

## 2. Flujo de una carga
```
POST /v1/cargas                         { entidad, modo, ventanaDesde, ventanaHasta, versionAgente, versionFuente }
  → 201 { cargaId }
POST /v1/cargas/{cargaId}/lotes/{n}     { filas: [ ... ] }            (n = 1..N, reintento idempotente por n)
  → 202 { filasRecibidas, rechazadasValidacion: [...] }
POST /v1/cargas/{cargaId}/cerrar        { totalLotes, totalFilas, sumaControl }
  → 202  (se encola el procesamiento)
GET  /v1/cargas/{cargaId}               → { estado, filasInsertadas, actualizadas, eliminadas, rechazadas }
GET  /v1/cargas/{cargaId}/rechazos      → [{ sourceKey, motivo }]
GET  /v1/estado                         → última carga OK por entidad (el agente decide su ventana)
```
- `sumaControl` = Σ débito + Σ crédito (asientos) o Σ `valor*` de la entidad. Si no cuadra con lo recibido, la carga se rechaza completa.
- El procesamiento es atómico por carga: o se aplica todo o nada.

## 3. Modos
| Modo | Semántica | Uso |
|---|---|---|
| `COMPLETA` | Reemplaza el catálogo entero (filas ausentes → inactivas) | Catálogos pequeños, diario |
| `VENTANA` | Reemplaza **todos** los hechos con fecha en `[ventanaDesde, ventanaHasta]`: inserta, actualiza y **elimina los que no llegaron** | Hechos, diario 23:00 |
| `INCREMENTAL` | Upsert sin eliminar | Terceros; anulaciones de facturas fuera de la ventana |
| `FOTO` | Reemplaza la foto de la fecha `corte` | Admisiones pendientes |
| `INICIAL` | Igual que VENTANA, por bloques mensuales | Primera vinculación (histórico) |

**Ventana diaria recomendada:** desde el primer día del periodo contable más antiguo **no cerrado** (`SIIFNet.CierresContables`) hasta hoy, con un tope de 13 meses. Además, una recarga semanal del año en curso.
**Orden:** catálogos → asientos → facturas → recibos → inventario → conciliaciones → glosas → foto de pendientes → la plataforma recalcula la cartera (`etl.usp_GenerarCorteCartera`).

## 4. Reglas comunes
- Fechas `yyyy-MM-dd`; dinero `decimal` con 2 decimales (cantidades y costos unitarios con 4) **como número JSON**, nunca float. Redondeo en origen.
- Códigos son texto (`codigoOrigen`); las referencias a catálogos usan `<catalogo>Codigo`. Si el código no existe, la fila se rechaza con motivo `REFERENCIA_INEXISTENTE` (salvo los opcionales, que quedan en NULL).
- **Prohibido** enviar nombre, documento en claro, dirección, teléfono, e-mail, fecha de nacimiento o datos clínicos de pacientes. La API rechaza la carga si un tercero con `esPaciente = true` trae `nombre`.

## 5. Catálogos
| Entidad (valor de `entidad` en `POST /v1/cargas`) | Campos (`*` obligatorio) | Origen SaludIPS |
|---|---|---|
| `sedes` | codigoOrigen*, nombre*, nombreCorto, activa | `SIIFNet.Sedes` |
| `lineas-servicio` | codigoOrigen*, nombre* | `pTipoServicio` |
| `centros-costo` | codigoOrigen*, nombre*, codigoPadre, nivel, tipoOrigen, manejaMovimientos, unidadNegocio, activo | `SIIFNet.CentrosCostos` (última vigencia) |
| `cuentas` | codigo*, nombre*, nivel*, codigoPadre, naturaleza* (D/C), movimientoContrario*, detalleCartera* (0/1/2), esBanco, manejaCentroCosto, aceptaMovimiento | `SIIFNet.Cuentas` Normatividad 1 |
| `tipos-comprobante` | codigoOrigen*, prefijo*, nombre*, claseSugerida | `SIIFNet.TiposComprobantes` |
| `terceros` | codigoOrigen*, tipoPersona, identificacion* (hash si paciente), digitoVerificacion, nombre, esPaciente*, claseOrigen | `SIIFNet.Terceros` + `mPacientes.TerceroId` |
| `pagadores` | codigoOrigen*, nombre*, codigoAdministradora, terceroCodigo, esParticular, activo | `mEmpresas` + `mInstituciones` |
| `contratos` | codigoOrigen*, numeroContrato*, pagadorCodigo, modalidad* (EVENTO, CAPITACION, PGP, PAQUETE, OTRO), fechaInicio, fechaFin, valorContrato | `mContratos` |
| `servicios` | codigoOrigen* (`P:`/`S:`), nombre*, codigoEstandar (CUPS/CUM), tipo* | `pProcedimientos`, `pSuministros` |
| `articulos` | codigoOrigen*, nombre*, linea, grupo, subgrupo, generico, laboratorio, unidadMedida, codigoCum | `Almacen.ArticulosBase` (+ jerarquía) |
| `lotes` | codigoOrigen*, articuloCodigo*, numeroLote*, fechaVencimiento, registroSanitario | `Almacen.Lotes`, `RegistrosInvima` |
| `almacenes` | codigoOrigen*, nombre*, sedeCodigo, activo | `Almacen.Bodegas` (+ `pBodegas`) |
| `profesionales` | codigoOrigen*, nombre*, especialidad, activo | `mPersonal` |
| `usuarios-operativos` | codigoOrigen* (`C:`/`A:`), nombre*, activo | `cUsuarios`, `AspNetUsers` |
| `cuentas-bancarias` | codigoOrigen* (`B:`/`C:`), tipo* (BANCO/CAJA), entidad*, numeroEnmascarado, cuentaCodigo | `BancosCuentas`, `pCajas` |

## 6. Hechos
**`asientos`** (VENTANA por `fecha`)
sourceKey*, relacionadoSourceKey, movimientoSourceKey*, fecha*, tipoComprobanteCodigo*, numeroComprobante*, numDocumento, cuentaCodigo*, centroCostoCodigo, sedeCodigo, terceroCodigo, cuentaBancariaCodigo, articuloCodigo, usuarioCodigo, debito*, credito*, fechaCartera, fechaVencimiento, estado* (1/2), descripcion.
Validaciones: por `movimientoSourceKey` con estado 1, Σ débito = Σ crédito (tolerancia 0,01; si no cuadra, se rechaza el comprobante completo). `debito` y `credito` ≥ 0 y no ambos > 0.

**`facturas`** (VENTANA por `fechaEmision` + INCREMENTAL de anulaciones)
sourceKey*, numeroDocumento*, admisionSourceKey, fechaEmision*, fechaPrestacion, fechaEnvioRips, cuentaCobro, pagadorCodigo*, contratoCodigo, sedeCodigo*, almacenCodigo, centroCostoCodigo, lineaServicioCodigo, servicioCodigo, profesionalCodigo, usuarioCodigo, cantidad*, valorServicio*, descuento, cuotaModeradora, iva, valorNeto*, vrCostoOrigen, anulada*, estadoFE.
Validación: por factura no anulada, Σ valorNeto ≈ `mFacturas.VrTotal` (el agente envía `totalFactura` en un control aparte).

**`recibos`** (VENTANA por `fecha`)
sourceKey*, fecha*, cuentaBancariaCodigo*, tipoMovimiento*, conceptoCodigo, concepto, formaPago, pagadorCodigo, sedeCodigo, usuarioCodigo, numeroDocumento, numeroFactura, valor* (≥ 0), anulado*.

**`inventario`** (VENTANA por `fecha`)
sourceKey*, fecha*, tipoComprobanteCodigo*, numeroComprobante, almacenCodigo*, articuloCodigo*, loteCodigo, centroCostoCodigo, terceroCodigo, cantidadEntrada*, cantidadSalida*, costoUnitario*, porcentajeDescuento, porcentajeIva, valorEntrada*, valorSalida*, costoPromedio, estado*.

**`conciliaciones`** (VENTANA por `periodo`)
cuentaBancariaCodigo*, periodo* (yyyymm), saldoInicial*, saldoLibro*, saldoExtracto*, diferencia*, cerrada*.

**`admisiones-pendientes`** (FOTO por `corte`)
admisionSourceKey*, fechaIngreso*, fechaSalida, pagadorCodigo, sedeCodigo, tipoAtencion, profesionalCodigo, usuarioCodigo, valorServicios*, diasSinFacturar*.

**`glosas`** (VENTANA por `fechaGlosa`; solo con módulo GLOSAS activo)
sourceKey*, numeroFactura*, fechaGlosa*, fechaRespuesta, pagadorCodigo, sedeCodigo, motivoCodigo, valorGlosado*, valorAceptado, valorRechazado, estadoOrigen, anulada*.

**`presupuesto`** (no sale de SaludIPS: plantilla Excel en el portal o API; modo VENTANA por año y versión)
version*, periodo*, cuentaCodigo*, centroCostoCodigo, sedeCodigo, valor*.

## 6b. Hechos operativos (rev. 2, ver `10_hechos_operativos_y_costeo.sql` y `11_extraccion_operativa_saludips.sql`)
| Entidad | Modo | Campos clave |
|---|---|---|
| `especialidades` (catálogo) | COMPLETA | codigoOrigen*, nombre* |
| `agenda-capacidad` | VENTANA (incluye agenda futura) | fecha*, profesionalCodigo*, jornada*, especialidadCodigo, sedeCodigo, horaInicio, cuposOfertados*, minutosPorCupo* |
| `citas` | INCREMENTAL | sourceKey*, estado*, fechaCita*, horaCita, fechaSolicitud, fechaAsignacion, fechaCancelacion, motivoCancelacion, reprogramada, horaLlegada, inicioAtencion, finAtencion, confirmada, profesionalCodigo, especialidadCodigo, sedeCodigo, pagadorCodigo, tipoConsulta, tipoSolicitud, pacienteHash, usuarioCodigo, admisionSourceKey |
| `prestaciones` | INCREMENTAL | sourceKey*, admisionSourceKey*, fechas de orden, realización, salida, factura y radicación, numeroFactura, enlaceFactura, anulado*, fechaAnulacion, servicio, línea, CC, sede, pagador, profesional, profesional que ordena, especialidad, usuario, tieneAutorizacion, cantidad*, valorUnitario*, valorTotal*, descuento, tarifaPactada, costoInsumos |
| `estudios` | INCREMENTAL | sourceKey*, tipo*, prestacionSourceKey*, fechas de realización, toma, informe y entrega, entregado, imagenesRechazadas, imagenesRepetidas, motivos, dosis, profesionales, sede |
| `nomina` | VENTANA por periodo | sourceKey*, periodo*, contratoCodigo*, empleadoTerceroCodigo*, cargo, concepto*, cuentaCodigo, esCosto*, centroCostoCodigo, valor*, horas, dias, horasContratoMes |
| `envios` | INCREMENTAL | sourceKey*, canal* (FE/RIPS), numeroFactura*, fechaEnvio, estado*, codigoUnico, valor |
| `autorizaciones` | INCREMENTAL | sourceKey*, fechaSolicitud, fechaOrden, fechaRegistro, estado, numeroAutorizacion, servicioCodigo, cantidad, pagadorCodigo, sedeCodigo, prioritaria |

`profesionales` agrega `terceroCodigo` y `especialidadCodigo`. Nómina es dato sensible: la API exige que la empresa tenga activo el módulo NOMINA_COSTOS y la expone solo a roles de gerencia.

## 7. Errores
`400` contrato inválido · `401/403` credencial · `409` carga duplicada (mismo hash) o periodo cerrado en la plataforma · `413` lote grande · `422` validación de negocio (detalle por fila en `/rechazos`) · `429` límite (con `Retry-After`).

## 8. Supuestos por validar en SaludIPS
1. `SIIFNet.Sedes.Id` = `IdSede` de facturación (`cAdministracion`).
2. `mServicios.centroCostoID` apunta a `SIIFNet.CentrosCostos.Id` (y no `CodCentroCosto`).
3. `mServicios.VrCosto`: ¿unitario o total?
5. `TipoPersona`, `ClaseTercero`, `TipoCentroCosto`: significado de los valores (`CodTipoContrato` ya confirmado).
6. `pCajas.IdCuenta` contiene el código PUC.
7. Un mismo código en `pProcedimientos` y `pSuministros` (colisión `P:`/`S:`).
8. Reportes de recibos de SaludIPS (`SIPS_551`, `SIPS_552`) unen `dRecibosFormasPagos` solo por `NoRecibo`, sin `CodCaja`: duplican formas de pago si dos cajas repiten número. La extracción usa ambas columnas.
