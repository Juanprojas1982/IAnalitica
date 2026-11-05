# Spec: Auditor de configuración de SaludIPS (IAnalitica)

Estado: diseño (Opus). Implementación: Sonnet.
Objetivo: **detectar la configuración errada de SaludIPS antes de que produzca errores** (facturas que no contabilizan, servicios sin tarifa, RIPS rechazados, tarifas ilegales, agendas inconsistentes) y decirle a cada empresa **qué corregir, dónde y por qué**. IAnalitica **no modifica** SaludIPS: diagnostica, sugiere y hace seguimiento hasta que la empresa corrige.

## 1. Flujo
```
SaludIPS (cada noche)                         IAnalitica
  Conector ── foto de tablas de configuración ──►  esquema saludips.* (copia por empresa)
             (lista blanca, sin secretos)            │
                                                     ▼
                                          Motor de reglas (IReglaConfiguracion)
                                                     │  + evidencia de impacto en los datos
                                                     ▼
                                          Hallazgos ── ciclo de vida ── dashboard, correo, API
                                                     │
                         la empresa corrige en SaludIPS ◄┘
                         la foto siguiente lo verifica → se cierra solo
```
- **Solo lectura.** La corrección la hace la empresa en SaludIPS. Así la responsabilidad no se traslada y no hay riesgo de que IAnalitica dañe la configuración.
- **Foto completa diaria:** las tablas de configuración son pequeñas (cientos a miles de filas).
- **Verificación automática:** si en la foto siguiente la regla ya no falla, el hallazgo pasa a `CORREGIDO` sin intervención.

## 2. Qué se extrae (lista blanca)
Tablas de configuración de SaludIPS, **sin columnas sensibles**: se excluyen claves y firmas (`cUsuarios.ClaveC`, `ClavePermisoC`, `FirmaUsuario`, `AspNetUsers.PasswordHash`, `SecurityStamp`), parámetros secretos (`*.Secreto`) e imágenes (`cAdministracion.Logo`).

| Área | Tablas |
|---|---|
| Pagadores y contratos | `mEmpresas`, `mEmpresasCuentasContables`, `mContratos`, `SedesXContrato`, `TipoServiciosXEmpresa`, `pServiciosEmpresa`, `mInstituciones`, `pEstratosCopago` |
| Tarifas y portafolio | `pTarifasProcedimientos`, `pTarifasSuministros`, `pPortafolio`, `pProcedimientos`, `pSuministros`, `pTipoServicio`, `pTiposConceptosCuentasContables` |
| Contabilidad (por vigencia) | `SIIFNet.Cuentas`, `CentrosCostos`, `TiposComprobantes`, `TiposComprobantesCuentasContables`, `TiposComprobantesCentrosCostos`, `FormasPagosCuentasContables`, `BancosCuentas`, `CierresContables`, `Retenciones` |
| Almacén | `Almacen.ArticulosBase`, `Lotes`, `RegistrosInvima`, `Bodegas`, `ConfiguracionesContablesArticulos`, `ConfiguracionesContablesGrupos`, `ConfiguracionesContablesGruposCentrosCostos` |
| Nómina | `Nomina.CuentasCentrosCostos`, `ConfiguracionesAnuales`, `Contratos` (fechas y estado, sin salario), `Conceptos` |
| Factura electrónica | `AutorizacionesDIAN`, `AutorizacionesDIANSedesComprobantes`, `SIIFNet.Terceros` (solo pagadores y proveedores; NIT, DV, correo FE) |
| Caja | `pCajas`, `SIIFNet.FormasPagos` |
| Agenda | `dCitasHorarios`, `mPersonal` (sin firma), `pConsultorios`, `mCitasFestivo` |
| Parámetros | `SIIFNet.Parametros` (sin secretos), `cConfiguracionAnual`, `cAdministracion` (sin logo) |
| Usuarios y permisos | `cUsuarios` (sin claves), `AspNetUsers` (sin hash), `SIIFNet.PermisosUsuarios`, `SIIFNet.Permisos` |

Además, **la evidencia de impacto** sale de los hechos que ya se cargan: facturas sin contabilizar, envíos FE con error, servicios en $0, movimientos sin centro de costo, etc.

## 3. Catálogo inicial de reglas
Cada regla define el código, el área, la severidad, la condición, la **evidencia de impacto** (lo que ya está fallando por esa causa), el texto de corrección y la **ruta en SaludIPS** para corregirla.

### A. Pagadores, contratos y tarifas
| Código | Regla | Severidad | Evidencia de impacto |
|---|---|---|---|
| TAR‑01 | Pagador activo **sin manual tarifario** (`mEmpresas.CodManual`) | Crítica | Servicios facturados en $0 o con tarifa manual |
| TAR‑02 | Servicio del portafolio que un pagador puede usar (`TipoServiciosXEmpresa`) **sin tarifa** en su manual y plan. Replica el reporte `SIPS_198` | Alta | Servicios de los últimos 30 días con `VrUnitario = 0` |
| TAR‑03 | Tarifa en $0, negativa, o con **cambio > X %** frente a `ValorAnterior` (posible error de digitación) | Alta | Facturado con esa tarifa |
| TAR‑04 | Tarifa de medicamento **> precio máximo regulado** (módulo 15, reglas R1 y R2) | Crítica | Monto en riesgo |
| TAR‑05 | Contrato **vencido** (`FechaFinaliza`) con facturación activa, contrato sin sedes (`SedesXContrato`), o tope consumido > 90 % (`VrPacientesFacturados` / `VrContrato`) | Alta | Facturas emitidas después del vencimiento |
| TAR‑06 | Procedimiento **sin CUPS** (`pProcedimientos.CodCups`) o suministro **sin CUM** (`pSuministros.CodCum`) | Alta | Rechazos RIPS y FE asociados |
| TAR‑07 | Procedimiento del portafolio **sin centro de costo** (`pPortafolio.CodCentroCosto`) | Media | Ingresos sin CC en la contabilidad |
| TAR‑08 | Pagador sin enlace a tercero (`mEmpresas.CodInstitucionAdm` → `mInstituciones.TerceroId`) | Alta | Cartera sin tercero |

### B. Contabilidad (la causa más común de que SaludIPS «funcione mal»)
| Código | Regla | Severidad | Evidencia |
|---|---|---|---|
| CON‑01 | Pagador activo **sin cuentas contables en la vigencia actual** (`mEmpresasCuentasContables`) | Crítica | Facturas sin `MovimientoContableId` y errores en `EnviosFacturaContabilidad` |
| CON‑02 | **Pre-vigencia:** desde el 1 de noviembre, configuración del **año siguiente** incompleta (cuentas de pagadores, comprobantes, formas de pago, almacén, nómina, `cConfiguracionAnual`, parámetros) | Crítica en diciembre | Lista de lo que dejará de contabilizar el 1 de enero |
| CON‑03 | Cuenta configurada que **no existe en la vigencia**, **no acepta movimiento** (`ManejaMovimientos = 0`) o es de **clase incoherente** (por ejemplo, la CxC apunta a una cuenta de ingreso) | Crítica | Contabilización fallida o mal clasificada |
| CON‑04 | Cuenta de CxC de pagadores **sin `DetalleCartera = 2`** (o de CxP sin `1`) | Alta | **La cartera no aparece** en `CARTERAESTADOCUENTAEDADES` ni en IAnalitica |
| CON‑05 | Tipo de comprobante sin cuentas en la vigencia (`TiposComprobantesCuentasContables`), forma de pago sin cuenta (`FormasPagosCuentasContables`), caja sin cuenta (`pCajas.IdCuenta`) o banco sin cuenta | Alta | Recibos y comprobantes sin contabilizar |
| CON‑06 | Cuenta con `ManejaCentroCosto = 1` y movimientos **sin CC** en el mes | Media | $ sin CC, lo que distorsiona el P&G por CC |
| CON‑07 | Meses anteriores **sin cierre contable** (`CierresContables`) pasados N días | Media | — |
| CON‑08 | Artículos o grupos **sin configuración contable de la vigencia** (`ConfiguracionesContablesArticulos` o `Grupos`) | Alta | Movimientos de inventario sin contabilizar |
| CON‑09 | Nómina: CC × clase de concepto **sin cuenta** de la vigencia (`Nomina.CuentasCentrosCostos`) o `ConfiguracionesAnuales` del año faltante (SMLV, UVT) | Alta | Nómina sin contabilizar o liquidada con valores del año anterior |

### C. Factura electrónica y DIAN
| Código | Regla | Severidad |
|---|---|---|
| FE‑01 | Resolución DIAN **vencida o por vencer** en menos de 30 días (`AutorizacionesDIAN.FechaFinVigencia`) | Crítica / alta |
| FE‑02 | Numeración **por agotarse**: `NumeracionActual` mayor al 90 % del rango `NumeracionDesde`–`NumeracionHasta` | Alta |
| FE‑03 | Sede o tipo de comprobante que factura **sin resolución asignada** (`AutorizacionesDIANSedesComprobantes`) | Crítica |
| FE‑04 | Tercero pagador con **DV del NIT inválido** (cálculo módulo 11 de la DIAN) o **sin correo de FE** (`EmailFacturaElectronica`) | Alta (evidencia: envíos FE con error) |
| FE‑05 | Terceros **duplicados** (mismo NIT en varios Id) | Media |

### D. Agenda
| Código | Regla | Severidad |
|---|---|---|
| AGE‑01 | Horarios **solapados** del mismo médico (misma fecha y franja horaria, en una o varias sedes) | Alta |
| AGE‑02 | Médico **inactivo** con agenda vigente, o especialidad del horario distinta de la del médico | Media |
| AGE‑03 | `MinutosPaciente` atípico (< 5 o > 120) o `NoPacientes` = 0 con horario activo | Media |
| AGE‑04 | Festivos del año **no cargados** (`mCitasFestivo`) | Alta en diciembre |
| AGE‑05 | Médico sin enlace a tercero o sin registro profesional (`mPersonal.TerceroId`, `NoRegistro`) | Media (sin costo por hora; riesgo RIPS) |

### E. Almacén y medicamentos
| Código | Regla | Severidad |
|---|---|---|
| ALM‑01 | Medicamento sin registro INVIMA o con registro **vencido** (`RegistrosInvima.FechaVigencia`) | Alta |
| ALM‑02 | Artículo con `ManejaLotes = 1` y movimientos sin lote | Media |
| ALM‑03 | `StockMinimo` mayor que `StockMaximo`, o mínimo en 0 en medicamentos de alta rotación | Baja |
| ALM‑04 | Marca de regulado (`MedicamentoRegulado`) que no coincide con la lista oficial | Media |

### F. Parámetros, usuarios y seguridad
| Código | Regla | Severidad |
|---|---|---|
| PAR‑01 | Parámetros presentes en la vigencia anterior y **ausentes o con valor por defecto en la actual** (`SIIFNet.Parametros` es por vigencia) | Alta |
| PAR‑02 | `cConfiguracionAnual` sin registros del año en curso (`AnoParametro`) | Alta |
| USU‑01 | **Usuario activo de un empleado retirado** (`AspNetUsers.TerceroId` frente a `Nomina.Contratos.FechaSalida`) | Crítica (seguridad) |
| USU‑02 | **Segregación de funciones:** un mismo usuario puede facturar y anular facturas, o crear tarifas y facturar | Alta |
| USU‑03 | Usuarios genéricos o compartidos (sin tercero, nombres como «admin» o «facturacion») | Media |

*Las reglas que dependen de significados por confirmar llevan la marca [S] en la implementación y salen como «por verificar» hasta validarlas con el usuario funcional.*

## 4. Diseño técnico
```csharp
// IAnalitica.Domain/Auditoria/IReglaConfiguracion.cs
public interface IReglaConfiguracion
{
    string Codigo { get; }                         // "CON-01"
    string Area { get; }                           // Contabilidad, Tarifas, FE, Agenda, Almacen, Seguridad
    Severidad SeveridadBase { get; }
    string VersionMinimaSaludIPS { get; }          // la regla aplica desde cierta versión del esquema
    Task<IReadOnlyList<HallazgoConfiguracion>> EvaluarAsync(ContextoAuditoria ctx, CancellationToken ct);
}
```
- **Una regla nueva es una clase nueva**, registrada en el módulo. El núcleo no cambia. La mayoría de las reglas son una consulta SQL sobre `saludips.*`; las que necesitan cálculo (DV del NIT, solapes de agenda) se escriben en C#.
- **Metadatos editables sin código** (`cfg.ReglaConfiguracion`): activa por empresa, severidad ajustada, umbrales (el X % de TAR‑03, los N días de CON‑07), texto de corrección y **ruta en SaludIPS** («Facturación › Empresas › Cuentas contables»).
- **Hallazgo** (`fact.HallazgoConfiguracion`): empresa, regla, entidad afectada (código y nombre), valor encontrado y esperado, **evidencia de impacto** (cantidad y $ en 30 días), estados `NUEVO`, `ABIERTO`, `CORREGIDO`, `ACEPTADO` (con justificación y usuario) y `FALSO_POSITIVO` (retroalimenta la regla), fechas y responsable.
- Los hallazgos se deduplican por regla y entidad: el mismo problema no se crea cada noche, se actualiza.

## 5. Lo que ve cada empresa
- **Índice de salud de la configuración** (0–100) por área y total, con tendencia.
- **Lista priorizada:** primero las críticas con impacto en $, después las que impiden contabilizar o facturar.
- Cada hallazgo muestra **qué está mal, por qué importa, qué ya está fallando por esto, cómo corregirlo y dónde**.
- **Resumen semanal por correo** al administrador, con lo nuevo, lo corregido y lo pendiente por severidad.
- **Campaña de cierre de año** (noviembre y diciembre): checklist de la configuración del año siguiente (CON‑02, PAR‑01, PAR‑02, AGE‑04, FE‑01).
- Exportación a Excel para el equipo que corrige.

## 6. Valor adicional para ti como fabricante de SaludIPS
La vista consolidada del superadmin muestra **qué errores de configuración se repiten entre clientes**. Eso orienta el producto: mejores valores por defecto, validaciones en las pantallas de SaludIPS, copia automática de la configuración entre vigencias y documentación. También reduce los casos de soporte: muchos tiquetes de «SaludIPS no contabiliza» o «la factura sale en cero» son configuración.

## 7. Riesgos
1. **Falsos positivos** que hacen ignorar el módulo. Mitigación: empezar con 10–15 reglas de alta certeza (CON‑01, CON‑02, CON‑04, FE‑01, FE‑02, FE‑03, TAR‑01, TAR‑02, TAR‑06, USU‑01), con evidencia de impacto, y el estado `FALSO_POSITIVO` para calibrar.
2. **Versiones de SaludIPS distintas** entre clientes: cada regla declara su versión mínima y el conector envía la versión.
3. **Datos de usuarios y permisos:** se envían sin credenciales, solo para la auditoría y visibles únicamente al administrador de la empresa.

## 8. Implementación (orden)
1. Conector: entidad `configuracion` (FOTO), con la lista blanca de §2 y la exclusión de columnas sensibles.
2. Esquema `saludips.*` en IAnalitica y su carga.
3. Motor de reglas, primeras 10 reglas y estados de los hallazgos.
4. Dashboard «Salud de la configuración» y correo semanal.
5. Resto del catálogo, campaña de cierre de año y vista consolidada del fabricante.
