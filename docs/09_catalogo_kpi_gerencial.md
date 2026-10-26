# Catálogo de KPI gerenciales desde SaludIPS (rev. Opus)

Fuentes: repo `Juanprojas1982/SaludIPS2` (código), diccionario y conteos de una instalación, 156 reportes.
Leyenda de disponibilidad:
- ✅ dato capturado y con volumen en la instalación revisada.
- ⚠️ el módulo existe en SaludIPS, pero en esta instalación no tiene datos (otra IPS puede usarlo).
- ❌ SaludIPS no lo captura.
- **[S]** significado de campo inferido, por validar.

## 0. Qué es SaludIPS (hechos del repo)
- **Stack:** .NET Framework 4.8 y ASP.NET MVC con WebForms heredado (`.aspx`), EF6 (`.edmx`), Crystal Reports, servicios de Windows para factura electrónica, un worker de tipificación de soportes y otro (`Conexia`) que genera adjuntos de autorizaciones cada 30 s.
- **17 módulos (Areas):**
  - Asistenciales y de facturación: SIFacturación (88 controladores), SIHClínica (97), SICitas, SILaboratorio, Cirugías, SIPyP, SIHC3280 (cursos de vida), SITipificación.
  - Administrativos y financieros: Almacén (32), Nómina (33), Presupuesto (22), Contabilidad (19), Cartera (radicación y estados de cuenta), Tesorería, SIIFNet, Reportes.
  - Apis: pacientes, citas, resultados de imagen, RDA, Aquila.
- **Dashboards existentes:** solo dos y operativos. `DashboardEnvios` muestra el estado de FE y RIPS por mes, priorizando ERROR, SIN ENVIAR y MODIFICADO; `WorkerDashboard` muestra la tipificación. **No hay tablero gerencial.** Ese es el espacio del producto.
- **Perfil de la instalación revisada:** IPS **ambulatoria y de diagnóstico**. Tiene 212 mil citas, 110 mil admisiones, 127 mil servicios, 97 mil estudios de imagen y 22 mil líneas de nómina. Triage, camas, cirugía, salidas, laboratorio clínico, quejas y remisiones están **vacíos**.
  - **Consecuencia:** el producto necesita **paquetes de dashboards por perfil de IPS** (ambulatoria, diagnóstica, hospitalaria/quirúrgica), que se activan según `seg.EmpresaModulo`.

## 1. Para qué decisiones
Cada KPI debe responder a una decisión. Si no cambia una decisión, no va al tablero gerencial.

| Decisión de dirección | Preguntas |
|---|---|
| **Dirección estratégica** | ¿Qué servicios crecer, sostener o cerrar? ¿Con qué EPS crecer o renegociar? ¿Cuál es la mezcla de pagadores? |
| **Precios y tarifas** | ¿Qué servicios se venden por debajo del costo? ¿Cuánto se otorga en descuentos? ¿El precio facturado coincide con la tarifa pactada? |
| **Procesos y tiempos** | ¿Dónde se detiene el paciente? ¿Dónde se detiene el dinero (atención → factura → radicación → pago)? |
| **Talento y capacidad** | ¿Cuánta capacidad pagada no se usa? ¿Qué productividad tiene cada profesional y cada facturador? |
| **Errores y re-trabajo** | ¿Cuánto cuestan las anulaciones, los rechazos de FE, las glosas, los estudios repetidos y la inasistencia? |
| **Caja y riesgo** | ¿Cuánta caja habrá en 30, 60 y 90 días? ¿Qué EPS pone en riesgo la liquidez? |

## 2. Catálogo de KPI

### A. Estrategia, ingresos y rentabilidad
| KPI | Cálculo | Fuente SaludIPS | Disp. | Decisión |
|---|---|---|---|---|
| Ingreso por línea, servicio, EPS, contrato, sede y médico | Σ valor prorrateado (regla `SIPS_4`) | `mFacturas`, `mServicios`, `pTipoServicio`, `mEmpresas`, `mContratos` | ✅ | Crecer, sostener o cerrar |
| Concentración de pagadores | % del ingreso de las 3 y las 5 mayores EPS; índice HHI | ídem | ✅ | Riesgo de dependencia |
| **Precio realizado vs tarifa pactada** | `mServicios.VrUnitario` frente a `pTarifasProcedimientos.Valor` (manual y plan del contrato) | `mServicios`, `pTarifasProcedimientos`, `mEmpresas.CodManual` | ✅ [S: llave de manual y plan] | Detectar subfacturación y errores de tarifa |
| Descuentos otorgados | Σ `VrDescuento` por usuario, EPS y servicio | `mServicios`, `mFacturas` (`SIPS_39`) | ✅ | Controlar descuentos |
| **Margen de contribución por servicio** | Ingreso − costo directo (insumos con `VrCosto` o costo promedio + mano de obra por tiempo) | `mServicios.VrCosto`, `dServicios`, `Almacen`, nómina | ⚠️ parcial | **Bajar o subir precios, renegociar** |
| **Margen por centro de costo con distribución** | P&G del CC + costos de apoyo distribuidos | contabilidad + nómina (`CentrosCostosContrato.Porcentaje`) | ✅ requiere motor de distribución | Qué unidad es realmente rentable |
| Rentabilidad por contrato de capitación o PGP | Ingreso fijo − costo de los servicios prestados a esa población | `AutorizacionesDIAN.Modalidad`, `mContratos`, servicios | ⚠️ [S] | Renegociar la prima |
| Punto de equilibrio por servicio y sede | Costos fijos / margen de contribución unitario | derivado | derivado | Abrir o cerrar sedes y agendas |

### B. Ciclo del ingreso: dónde se detiene el dinero
| KPI | Cálculo | Fuente | Disp. | Decisión |
|---|---|---|---|---|
| **Atendido no facturado** ($ y días) | Admisiones con servicios sin factura vigente (`SIPS_37`) | `mAdmisiones`, `mServicios`, `mFacturas` | ✅ | Acelerar facturación |
| **Días atención → factura** | `FechaFactura` − `FechaAtencion` (o `FechaSalidaHC`) | `mAdmisiones`, `mFacturas` | ✅ [S] | Cuello en facturación |
| Estado FE: % rechazada o con error, días a aceptación | `EnviosFacturaElectronica.Estado`, `FechaEnviado` | ídem (ya lo muestra `DashboardEnvios`) | ✅ | Calidad de la facturación |
| **Días factura → radicación** | `mRipsEnvios.FechaEnvio` − `FechaFactura`; facturas sin cuenta de cobro | `mFacturas.IdRipsEnvio`, `mRipsEnvios` | ✅ | El dinero no radicado no se cobra |
| Facturas sin validación RIPS (CUV) | Estado del envío RIPS | `EnviosRips` | ⚠️ (vacía aquí) | Facturas que no se pueden radicar |
| Días radicación → pago y DSO por EPS | Cartera desde contabilidad (regla `CARTERAESTADOCUENTAEDADES`) | `SIIFNet` | ✅ | Con qué EPS crecer o endurecer |
| Glosa: % sobre lo radicado, causa, aceptada vs recuperada, días de respuesta | `dGlosas`, `dGlosasItems`, `dGlosasTramite` | ⚠️ (vacías aquí) | Causas raíz, por área y por facturador |
| **Re-trabajo de facturación** | Facturas anuladas y renumeradas, notas débito y crédito, por usuario y motivo | `mFacturas.Anulado`, `FechaAnulacion`, `ObservacionAnular`, renumeración, NC por glosa | ✅ | Capacitar o reasignar facturadores |
| Servicios anulados después de ordenados | `mServicios.Anulado`, `FechaAnulacion` | ✅ | Errores de orden y de cargo |
| Autorizaciones: % de servicios sin autorización y días de trámite | `mAutorizaciones`, `dAutorizaciones`, `mServicios.NoAutorizacion` | ⚠️ (2 filas aquí) | Origen de las glosas por autorización |

### C. Acceso y oportunidad (citas)
| KPI | Cálculo | Fuente | Disp. | Decisión |
|---|---|---|---|---|
| **Oportunidad (días)** por especialidad | `FechaCita` − `FechaUsuario` (fecha solicitada por el usuario, confirmado en la interfaz). El reporte `SIPS_014` usa `FechaDigitacion`: **no es la definición normativa** | `dCitas` | ✅ | Abrir agenda donde la espera es alta |
| **Inasistencia %** y **costo** | `Asistio = 0` / citas; costo = citas perdidas × ingreso promedio por cita | `dCitas`, facturación | ✅ | Recordatorios (ya existe `SaludIPSWhatsApp`), sobre-agenda |
| Cancelaciones: %, motivo, anticipación y reprogramación | `dCitasCanceladas.FechaCancela` vs `FechaCita`, `CodCitasMotivoCancela`, `MoverCita` | ✅ (15,5 mil) | Política de cancelación |
| Confirmación previa | `EstaConfirmado` | ✅ | Eficacia del recordatorio |
| **Ocupación de agenda** | Citas asignadas / capacidad (`dCitasHorarios.NoPacientes` o jornada / `MinutosPaciente`) | `dCitas`, `dCitasHorarios` | ✅ | Capacidad ociosa |
| Demanda insatisfecha | Solicitudes sin cita (`SIPS_702`) | `dCitasNoAsignadas` | ⚠️ (vacía aquí) | Contratar o reasignar especialistas |
| **Espera en sala** | Inicio de atención − hora de llegada (`HoraAtencion`, que se registra al marcar asistencia) | `dCitas`, `mAdmisiones.FechaAtencion` | ✅ [S] | Flujo de pacientes |
| Puntualidad de agenda | Hora real de atención − `HoraCita` | ídem | ✅ [S] | Disciplina de agenda |

### D. Talento, capacidad y productividad
| KPI | Cálculo | Fuente | Disp. | Decisión |
|---|---|---|---|---|
| **Costo de personal por centro de costo** | Σ devengos y aportes distribuidos por `% CentrosCostosContrato` | `Nomina.Historicos`, `CentrosCostosContrato` | ✅ | Base del costeo |
| **Costo por hora contratada** | Costo mensual / `HorasContratoMensuales` | `Nomina.Historicos`, `Contratos` | ✅ | Costo de cada consulta |
| **Costo de capacidad ociosa** | (horas de agenda no asignadas + inasistidas) × costo por hora | agenda + nómina | ✅ (cuando el médico está en nómina) | **Reducir horas, reasignar, abrir agenda** |
| Producción por profesional | Cantidad y $ por médico, especialidad y finalidad (`SIPS_3`) | `mServicios.IdMedico` | ✅ | Productividad y contratación por evento |
| Pacientes por hora de agenda | Atendidos / horas programadas | agenda | ✅ | Estándares de rendimiento |
| Productividad administrativa | Admisiones, facturas, recibos y cargos por usuario y hora; tasa de error por usuario | `IdcUsuario` en cada tabla | ✅ | Dimensionar el personal administrativo |
| Horas extra, recargos e incapacidades | Conceptos de nómina por CC | `Nomina.Novedades`, `Historicos` | ✅ | Costo de sobrecarga |
| Médicos por honorarios (no nómina) | Pago por evento desde CxP | contabilidad | ✅ [S] | Comparar nómina vs evento |

### E. Apoyo diagnóstico (imágenes y laboratorio)
| KPI | Cálculo | Fuente | Disp. | Decisión |
|---|---|---|---|---|
| Volumen y $ por modalidad, equipo y médico solicitante | Estudios por tipo | `dLabRadiologia`, `mServicios`, `EstudiosDICOM` | ✅ (97 mil) | Uso de equipos y quién refiere |
| **Oportunidad de entrega del informe** | `FechaEntregado` − fecha de realización | `dLabRadiologia.FechaEntregado`, `mServicios.FechaRealizado` | ✅ | Cuello de lectura |
| **Calidad técnica: imágenes rechazadas o repetidas** y motivo | `NoImagenesRechazadasId`, `NoImagenesRepetidasId`, `MotivoRechazoEstudio` | `dLabRadiologia` | ✅ | **Costo del error** (insumo, tiempo de equipo, radiación) |
| Dosis de radiación por estudio | `DosisRadiacionId` | ídem | ✅ | Calidad y riesgo |
| Laboratorio: tiempo toma → resultado | `dLabResultados` | ⚠️ (vacía aquí) | Oportunidad de resultados |

### F. Farmacia, insumos e inventario
| KPI | Cálculo | Fuente | Disp. | Decisión |
|---|---|---|---|---|
| Valor de inventario, rotación y días de inventario por bodega y línea | Saldo / consumo | `Almacen.MovimientosAlmacen*` | ✅ | Capital inmovilizado |
| Vencidos y próximos a vencer ($) | Lote y saldo | `Almacen.Lotes` | ✅ | Pérdidas evitables |
| **Diferencias de inventario** (conteo vs sistema) | `ConteosInventario*` | ⚠️ [S] | Pérdidas y control |
| **Consumo no facturado (fuga)** | Salidas de bodega a paciente sin cargo facturado | `dServicios`, `MovimientosAlmacenDetalles.dServicioId`, `dHojaGastoQx` | ✅ parcial | Dinero que se va sin cobrar |
| Precio de compra vs última compra y por proveedor | `OrdenesComprasDetalles`, `ValorUltimaCompra` | ✅ | Negociar con proveedores |
| Margen por medicamento o insumo | Precio facturado − costo promedio | `dServicios`, `CostoPromedio` | ✅ | Política de precios |

### G. Hospitalario y quirúrgico (paquete para IPS con estos módulos)
| KPI | Fuente | Disp. |
|---|---|---|
| Ocupación de camas, giro cama, estancia promedio | `mAdmisiones`, `mSalidas`, `pCamas`, `mCenso` | ⚠️ |
| Triage: espera por clasificación | `mTriage.Fecha` → `FechaAtencion` | ⚠️ |
| Quirófano: utilización, tiempos (inicio, fin, recuperación), cancelaciones y causa | `mActoQx`, `mHCCirugiasProgramacion`, `dCirugiasRechazadas` | ⚠️ |
| Rentabilidad por cirugía (hoja de gasto vs paquete) | `dHojaGastoQx`, `dFacturasPaq`, `dCirugias` (honorarios, sala, material) | ⚠️ |

### H. Financiero
P&G con y sin distribución, EBITDA, **balance y razones financieras** (liquidez, endeudamiento, capital de trabajo, DSO y DPO), **flujo de caja proyectado** (cartera por edades × recaudo histórico por EPS + CxP + nómina), ejecución presupuestal y conciliación bancaria. Fuente: `SIIFNet`, `Tesoreria`. ✅

### I. Riesgo contractual, calidad y cumplimiento
| KPI | Fuente | Disp. | Por qué importa en dinero |
|---|---|---|---|
| Cumplimiento de metas de PyP y cursos de vida por población | `SIPyP`, `SIHC3280`, Res. 202 y 4505 | ✅/⚠️ | En capitación y PGP, el incumplimiento suele acarrear descuentos según el contrato |
| Completitud de reportes obligatorios | reportes 202/4505/1552/256 | ✅ | Sanciones y descuentos |
| Quejas: volumen, causa, tiempo de respuesta | `mQuejas`, `mQuejasTramites` | ⚠️ | Reputación y contratos |
| Remisiones y contrarreferencia | `dRemisiones` | ⚠️ | Capacidad resolutiva |

## 3. Cruces que generan decisiones (el valor del producto)
1. **Capacidad ociosa vs demanda insatisfecha** por especialidad: mover horas de donde sobran a donde falta oportunidad.
2. **Costo de la inasistencia** (ingreso perdido + costo de hora pagada) frente al costo de los recordatorios por WhatsApp. Justifica invertir en recordatorios.
3. **Dinero detenido por etapa:** $ atendido sin facturar, facturado sin radicar, radicado sin pagar y glosado. Cada etapa tiene un responsable y un tiempo.
4. **Precio vs costo por servicio:** lista de servicios con margen negativo para decidir precio, contrato o mezcla.
5. **Costo de los errores:** anulaciones + rechazos de FE + glosas aceptadas + estudios repetidos + fugas de insumos, en $ y en horas, con responsable.
6. **EPS que conviene:** margen × días de cartera × tasa de glosa × volumen. Decide con quién crecer.
7. **Benchmark entre las IPS de la plataforma (anónimo):** oportunidad, inasistencia, días de cartera por EPS, costo por consulta. Ninguna IPS sola puede tenerlo.

## 4. Lo que SaludIPS no captura y conviene agregar (backlog del HIS)
| Brecha | Impacto | Propuesta en SaludIPS |
|---|---|---|
| **Ficha de costo estándar por procedimiento** (insumos y minutos por rol) | Sin ella el margen por servicio es aproximado | Tabla de estándar por CUPS |
| Marcas de tiempo completas del flujo (llegada, llamado, inicio y fin de consulta) | Espera y duración confiables | `HoraInicioE` y `HoraFinalE` existen pero están en desuso (código comentado): reactivarlas |
| Motivo de inasistencia | Acción dirigida | Catálogo al marcar inasistencia |
| Horas efectivamente laboradas (`mTurnosN` vacío) | Productividad real vs contratada | Registro de turnos o integración con el reloj |
| Causa estructurada de anulación de factura | Hoy es texto libre (`ObservacionAnular`) | Catálogo de motivos |
| Fecha de pago por factura en la contabilidad | DSO por factura exacto | Ya se infiere por cruces; validar |

## 5. Hechos nuevos que requiere la BD central (extracción)
| Hecho | Grano | Origen |
|---|---|---|
| `fact.Cita` | cita | `dCitas` (+ cancelaciones) |
| `fact.AgendaCapacidad` | profesional × fecha × jornada | `dCitasHorarios` |
| `fact.Prestacion` | servicio prestado (ordenado, realizado, facturado, anulado, con sus tiempos) | `mServicios`, `mAdmisiones` |
| `fact.EstudioDiagnostico` | estudio | `dLabRadiologia`, `dLabResultados` |
| `fact.NominaCosto` | contrato × concepto × CC × periodo | `Nomina.Historicos`, `CentrosCostosContrato` |
| `fact.EnvioDocumento` | factura × envío (FE, RIPS) | `EnviosFacturaElectronica`, `EnviosRips` |
| `fact.Autorizacion` | autorización | `mAutorizaciones`, `dAutorizaciones` |
| Motor `etl.usp_DistribuirCostos` | CC × periodo | P&G + nómina + bases de distribución |

## 6. Prioridad del MVP gerencial (ambulatoria y diagnóstica)
1. Dinero detenido por etapa: atendido no facturado, días a factura, a radicación y a pago, y FE con error.
2. Cartera por edades y por EPS, con DSO.
3. P&G por centro de costo **con distribución** de nómina y apoyo.
4. Oportunidad, inasistencia (con su costo) y ocupación de agenda por especialidad.
5. Costo de capacidad ociosa por especialidad.
6. Imágenes: volumen, entrega del informe, repetidos y rechazados.
7. Re-trabajo: anulaciones, renumeraciones y servicios anulados por usuario.
8. Precio realizado vs tarifa y descuentos.
9. Balance, razones financieras y flujo de caja proyectado.

Todo lo marcado **[S]** se valida con el código o con el usuario funcional antes de publicar un KPI.
