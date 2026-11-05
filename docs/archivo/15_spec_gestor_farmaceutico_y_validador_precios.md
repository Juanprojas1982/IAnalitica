# Spec: Módulo Gestor Farmacéutico y Validador de Precios Máximos (IAnalitica)

Estado: diseño (Opus). Implementación: Sonnet.
Cliente objetivo: operador y dispensador farmacéutico para afiliados de varias EPS.
**Advertencia regulatoria:** las normas citadas salen de búsquedas web (oct‑2026) y **deben confirmarse con el área regulatoria** antes de implementar reglas. El sistema guarda la norma y la vigencia de cada regla para poder cambiarlas sin código.

## 1. Marco regulatorio que alimenta el módulo (a validar)
| Tema | Norma / fuente | Qué implica para el sistema |
|---|---|---|
| **Precios máximos de venta (régimen de control directo)** | Circular 022 de 2026 de la Comisión Nacional de Precios de Medicamentos y Dispositivos Médicos (CNPMDM), con la metodología de la Circular 18 de 2024; precios **vigentes desde el 19‑ago‑2026** según MinSalud. Según análisis del sector, el máximo aplica a todas las transacciones | Validar tarifas de venta, facturas **y compras** contra el precio máximo de cada presentación (CUM) |
| Lista oficial de precios máximos | Dataset de datos.gov.co «Precio máximo de venta de los medicamentos por presentación comercial» (`nauz-qkjw`) | Sincronización automática. **Por validar:** campos, unidades y frecuencia de actualización |
| **SISMED** (reporte de precios) | Circular 021 de 2026 de la CNPMDM, que deroga las Circulares 06 de 2018 y 17 de 2023; unidades en **Unidad Mínima de Dispensación (UMD)** | Control de completitud y oportunidad del reporte; normalización a UMD. Periodicidad y rol del gestor **por confirmar** en el texto |
| **Pendientes de entrega** | Resolución 1604 de 2013, art. 1: entregar el pendiente en máximo 48 horas (en las órdenes judiciales citadas, a domicilio) | Indicador de pendientes y alertas a las 24 y 48 h |
| **MIPRES (tecnologías no financiadas con la UPC)** | Anexo técnico del módulo dispensador v3.1 (mar‑2026), Resolución 740 de 2024. Una norma de 2026 encontrada indica el reporte de entrega en tiempo real o máximo en 8 días calendario y la causa de no entrega. **El número y el emisor de esa norma deben verificarse** | Indicador de entregas no reportadas o reportadas fuera de plazo (sin reporte no se cobra) |
| Control de la Supersalud | En 2025‑2026 hubo sanciones y medidas cautelares a gestores farmacéuticos por pendientes, entre ellas **pendientes con existencia disponible** | Alerta «pendiente con stock en otra bodega»: es exactamente lo que la Supersalud encontró |
| Registro sanitario / CUM | INVIMA: CUM por presentación comercial; el estado se consulta por vigencia | Validar que el CUM exista y que el registro esté vigente |

## 2. Validador de configuración tarifaria (precios máximos)

### 2.1 Qué valida
| # | Regla | Datos | Severidad |
|---|---|---|---|
| R1 | **Tarifa configurada para la EPS > precio máximo** de ese CUM, en la vigencia actual | `pTarifasSuministros` (manual/plan → EPS vía `mEmpresas.CodManual`) + `pSuministros.CodCum` | Crítica |
| R2 | **Tarifa que quedará por encima** del precio máximo de una circular **futura** (antes de su entrada en vigencia) | ídem + circular con `VigenteDesde` futura | Alta (alerta anticipada con días restantes) |
| R3 | **Precio facturado > precio máximo** (dispensaciones ya facturadas) | `dServicios.VrUnitario`, `mFacturas` | Crítica (monto en riesgo = exceso × unidades) |
| R4 | **Precio de compra > precio máximo** | `Almacen.OrdenesComprasDetalles`, `MovimientosAlmacenDetalles` (entradas) | Alta (reclamo al proveedor) |
| R5 | **Margen negativo:** tarifa a la EPS < costo de compra o costo promedio | tarifas + `CostoPromedio` | Alta (pérdida por CUM y EPS) |
| R6 | **CUM inválido:** vacío, inexistente, o registro sanitario vencido | `pSuministros.CodCum`, `Almacen.ArticulosBase.CodigoCUM`, `Almacen.RegistrosInvima.FechaVigencia` | Alta |
| R7 | **Marca de regulado incoherente:** `ArticulosBase.MedicamentoRegulado` no coincide con la lista oficial | ídem | Media |
| R8 | **IVA mal configurado** en medicamentos (`ManejaIva`, `IncluyeIva`) | tarifas, artículos | Media. La regla tributaria por producto se confirma con el contador |
| R9 | **Tarifa desactualizada:** sin cambios desde hace más de N meses o con diferencia frente a la última compra mayor que X % | tarifas, compras | Baja |

### 2.2 El problema técnico central: las unidades
- La lista oficial está **por presentación comercial** (y el SISMED pide UMD). La tarifa de SaludIPS puede estar por unidad de consumo. Comparar sin normalizar produce **falsos positivos y falsos negativos**.
- **Solución:** una tabla de conversión por CUM (`reg.FactorPresentacion`: UMD por presentación). Se toma del dataset oficial si trae el dato y, si no, se completa y valida por el químico farmacéutico. Una regla R1, R3 o R4 **no se marca como crítica si el factor no está validado**: queda como «por verificar».
- Las comparaciones se hacen **en la misma base de IVA**. Por confirmar si el precio máximo se expresa con o sin IVA para cada producto.

### 2.3 Dónde corre (decisión)
| Momento | Dónde | Por qué |
|---|---|---|
| **Al guardar o importar tarifas y al facturar** (bloqueo o advertencia en tiempo real) | **SaludIPS**, contra una copia local del catálogo regulatorio | No depende de internet al facturar; el error se evita, no se detecta después |
| Mantenimiento del catálogo regulatorio (circulares, precios máximos, vigencias, factores) | **IAnalitica**, centralizado para todos los clientes | Se mantiene una vez y se distribuye a 30 o más operadores. Es un servicio vendible |
| Distribución del catálogo | El **conector** descarga cada noche `GET /v1/regulacion/precios-maximos?desde=…` y lo guarda en `analitica.PrecioMaximoRegulado` en la BD de SaludIPS | Reutiliza el conector que ya existe (canal de bajada) |
| Monitoreo, anticipación (R2), montos en riesgo, histórico de hallazgos | **IAnalitica** (dashboard «Cumplimiento de precios») | Vista gerencial y de auditoría |

### 2.4 Modelo de datos (IAnalitica, esquema `reg`)
```sql
-- esquema reg: se crea en el script 0010. Este bloque = script 0080.
CREATE TABLE reg.Circular (
    CircularId int IDENTITY PRIMARY KEY, Entidad varchar(20) NOT NULL,      -- CNPMDM, MINSALUD, INVIMA
    Numero varchar(20) NOT NULL, Anio smallint NOT NULL, Tema varchar(40) NOT NULL,  -- PRECIO_MAXIMO, SISMED...
    FechaExpedicion date NOT NULL, VigenteDesde date NOT NULL, VigenteHasta date NULL,
    UrlFuente nvarchar(400) NOT NULL, Notas nvarchar(1000) NULL,
    CONSTRAINT UQ_Circular UNIQUE (Entidad, Numero, Anio));

CREATE TABLE reg.PrecioMaximo (
    PrecioMaximoId bigint IDENTITY PRIMARY KEY,
    CircularId int NOT NULL REFERENCES reg.Circular,
    Cum varchar(30) NOT NULL,                       -- expediente-consecutivo
    Principio nvarchar(300) NULL, Presentacion nvarchar(400) NULL,
    PrecioMaximoPresentacion decimal(19,4) NOT NULL,
    PrecioMaximoUnidad decimal(19,4) NULL,          -- si la fuente lo trae
    VigenteDesde date NOT NULL, VigenteHasta date NULL,
    CONSTRAINT UQ_PrecioMaximo UNIQUE (CircularId, Cum));
CREATE INDEX IX_PrecioMaximo_Cum ON reg.PrecioMaximo (Cum, VigenteDesde DESC);

CREATE TABLE reg.FactorPresentacion (               -- UMD por presentación; validado por QF
    Cum varchar(30) NOT NULL PRIMARY KEY, UnidadesPorPresentacion decimal(12,4) NOT NULL,
    Fuente varchar(20) NOT NULL CHECK (Fuente IN ('OFICIAL','MANUAL')),
    ValidadoPor nvarchar(150) NULL, ValidadoEn datetime2(0) NULL);

-- Hallazgos por empresa (con RLS)
CREATE TABLE fact.HallazgoTarifario (
    EmpresaId int NOT NULL REFERENCES seg.Empresa, HallazgoId bigint IDENTITY,
    Regla varchar(5) NOT NULL, Severidad varchar(10) NOT NULL, Estado varchar(12) NOT NULL DEFAULT 'ABIERTO', -- ABIERTO, CORREGIDO, ACEPTADO, FALSO_POSITIVO
    Cum varchar(30) NULL, ProductoId int NULL, PagadorId int NULL, ContratoId int NULL,
    ValorObservado decimal(19,4) NULL, ValorLimite decimal(19,4) NULL,
    ExcesoUnitario decimal(19,4) NULL, UnidadesAfectadas decimal(18,4) NULL, MontoEnRiesgo decimal(19,4) NULL,
    CircularId int NULL REFERENCES reg.Circular, FactorValidado bit NOT NULL DEFAULT 0,
    DetectadoEn datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(), CerradoEn datetime2(0) NULL, CerradoPor int NULL,
    CONSTRAINT PK_HallazgoTarifario PRIMARY KEY (EmpresaId, HallazgoId));
```
**Datos que agrega la extracción** (`11_`): tarifas de suministros (`pTarifasSuministros` + manual/plan → pagador), catálogo de medicamentos con CUM, ATC, marca de regulado, IVA y factor (`pSuministros`, `Almacen.ArticulosBase`, `RegistrosInvima`), compras (`OrdenesComprasDetalles` y entradas de almacén) y órdenes externas (fórmulas) con su estado de entrega (`dHCOrdenesExternas`, `mServicios`, `dServicios`).

## 3. Dashboards del gestor farmacéutico

### 3.1 Oportunidad y pendientes (prioridad 1: riesgo de sanción)
- Fórmulas recibidas, dispensadas completas en la primera visita (%), parciales y pendientes.
- **Pendientes abiertos:** cantidad, valor, antigüedad (< 24 h, 24–48 h, > 48 h) por EPS, punto, CUM y causa.
- **% de pendientes resueltos en 48 h o menos** (Res. 1604 de 2013), con tendencia.
- **🔴 Pendientes con existencia disponible** en otra bodega o punto: lo que sancionó la Supersalud.
- Pendientes por **desabastecimiento** (cruce con la lista de desabastecidos del INVIMA, *por confirmar la fuente*) frente a falta de gestión interna.
- Entregas a domicilio: volumen y oportunidad.

### 3.2 Inventario y abastecimiento
- Cobertura en días por CUM y punto; quiebres de stock (frecuencia y duración); quiebres que generaron pendientes.
- Vencimientos y averías en $ por lote; rotación; inventario inmovilizado.
- Sugerencia de reposición por consumo histórico y estacionalidad (fase 2).

### 3.3 Rentabilidad por EPS, contrato y CUM
- Ingreso por tarifa − costo de compra = margen por CUM, EPS y modalidad (evento, capitación o PGP). **Confirmado en el reporte `SIPS_706`:** `mContratos.CodTipoContrato` 1 = Evento, 2 = Capitación, 3 = PGP, 4 = Paquete.
- **Capitación y PGP:** costo dispensado por afiliado frente al valor de la cápita o techo del PGP; consumo acumulado del mes contra el techo.
- **Top de CUM por costo** (alto costo) y concentración (unos pocos pacientes anonimizados generan el X % del costo).
- Cartera, glosas y DSO por EPS (del paquete financiero), clave con EPS intervenidas.

### 3.4 Cumplimiento regulatorio
- **Precios:** hallazgos R1–R9 por severidad, monto en riesgo, anticipación de la próxima circular y estado de corrección.
- **MIPRES:** dispensaciones no UPC sin reporte o reportadas fuera de plazo, y su valor (dinero que no se puede cobrar).
- **SISMED:** completitud del reporte del periodo (CUM vendidos frente a reportados, en UMD) y fecha de envío.
- **Facturación electrónica y RIPS** de la dispensación (ya existe en el ciclo del ingreso).

### 3.5 Compras y proveedores
- Precio de compra por CUM y proveedor frente al precio máximo, al histórico y entre proveedores.
- Concentración de proveedores; variación de precios; oportunidad de entrega del proveedor (orden → recepción).

### 3.6 Operación por punto de dispensación
- Fórmulas por día y hora (picos), tiempo de espera *(si se registra)* y productividad por auxiliar.
- Errores: devoluciones (`dServicios.Devuelve`), anulaciones y ajustes de inventario.

## 4. Hallazgos en SaludIPS relevantes
1. SaludIPS **ya tiene** el CUM (`pSuministros.CodCum`, `Almacen.ArticulosBase.CodigoCUM`), la marca de regulado y de reporte al SISMED (`MedicamentoRegulado`, `ReportaSISMED`), el registro INVIMA con vigencia y las tarifas por manual y plan (`pTarifasSuministros`). **La base del validador existe; falta la lista oficial y las reglas.**
2. Hay reportes de dispensación: `SIPS_119` (Res. 1604), `SIPS_700` (órdenes pendientes de entrega), `SIPS_701` y `SIPS_706` (estadísticas de dispensación). Sus definiciones sirven para el cuadre.
3. `SIPS_706` tiene **el nombre y el NIT del operador escritos en el SQL** («PROINSALUD S.A.»). Es otra regla de un cliente fija en el código, que hay que llevar a parámetros.
4. `Almacen.PreciosListas` y `PreciosArticulos` están vacías en la instalación revisada: las tarifas reales viven en `pTarifasSuministros`. Hay que confirmar cuál usa el operador.

## 5. Riesgos
1. **Falsos positivos por unidades** (§2.2): sin el factor validado, el validador pierde credibilidad en una semana. Es lo primero que se resuelve.
2. **Normas cambiantes:** circulares nuevas varias veces al año. Por eso el catálogo es versionado por circular y vigencia, y las reglas se parametrizan.
3. **Responsabilidad:** el validador **advierte**; la decisión de tarifa es del operador. Debe quedar en el contrato y en la interfaz.
4. **Datos clínicos:** los dashboards de pacientes de alto costo usan solo hashes y agregados.

## 6. Prioridad de implementación (dentro del plan general)
1. Catálogo regulatorio en IAnalitica: carga de la Circular 022 de 2026 desde datos.gov.co y factores por CUM.
2. Validador R1, R2, R6 y R5 sobre las tarifas (`pTarifasSuministros`) y hallazgos con monto en riesgo.
3. Dashboard de pendientes (3.1), con alerta de «pendiente con stock».
4. Bajada del catálogo a SaludIPS y validación en tiempo real al guardar tarifas y al facturar.
5. R3, R4 y MIPRES/SISMED, más los dashboards 3.2 a 3.6.
