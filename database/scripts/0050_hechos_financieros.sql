/* 0050 · Hechos financieros. */
/* ============================ HECHOS ============================ */
-- SourceKey = Id del registro en la fuente. Carga por VENTANA: el SP reemplaza todo lo del rango
-- (las anulaciones en SaludIPS cambian EstadoEntidad/Anulado y no siempre FechaDigitacion).

/* --- Contabilidad: SIIFNet.mMovimientosDetalles ⨝ mMovimientos --- */
CREATE TABLE fact.AsientoLinea (
    AsientoLineaId  bigint IDENTITY(1,1) NOT NULL,
    EmpresaId       int NOT NULL CONSTRAINT FK_AL_Empresa REFERENCES seg.Empresa,
    SourceKey       nvarchar(100) NOT NULL,             -- mMovimientosDetalles.Id
    RelacionadoSourceKey nvarchar(100) NULL,            -- dContabilidadRelacionadoId (abonos/cruces de cartera)
    MovimientoSourceKey  nvarchar(100) NOT NULL,        -- mMovimientos.Id
    FechaKey        int NOT NULL CONSTRAINT FK_AL_Fecha REFERENCES dim.Fecha,
    PeriodoKey      int NOT NULL,
    TipoComprobanteId int NOT NULL CONSTRAINT FK_AL_TC REFERENCES dim.TipoComprobante,
    NumeroComprobante varchar(30) NOT NULL,
    NumDocumento    varchar(40) NULL,                   -- factura / documento cruce
    CuentaId        int NOT NULL CONSTRAINT FK_AL_Cuenta REFERENCES dim.CuentaContable,
    CentroCostoId   int NULL CONSTRAINT FK_AL_CC REFERENCES dim.CentroCosto,
    SedeId          int NULL CONSTRAINT FK_AL_Sede REFERENCES dim.Sede,
    TerceroId       int NULL CONSTRAINT FK_AL_Tercero REFERENCES dim.Tercero,
    CuentaBancariaId int NULL CONSTRAINT FK_AL_CB REFERENCES dim.CuentaBancaria,
    ArticuloId      int NULL CONSTRAINT FK_AL_Art REFERENCES dim.Articulo,
    UsuarioOpId     int NULL CONSTRAINT FK_AL_UOp REFERENCES dim.UsuarioOperativo,
    Debito          decimal(19,4) NOT NULL DEFAULT 0,
    Credito         decimal(19,4) NOT NULL DEFAULT 0,
    FechaCartera    date NULL,
    FechaVencimiento date NULL,
    Estado          tinyint NOT NULL DEFAULT 1 CHECK (Estado IN (1,2)),   -- 1 confirmado, 2 anulado (SaludIPS EstadoEntidad)
    Descripcion     nvarchar(300) NULL,
    CargaId         bigint NOT NULL CONSTRAINT FK_AL_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_AsientoLinea PRIMARY KEY NONCLUSTERED (AsientoLineaId),
    CONSTRAINT UQ_AsientoLinea UNIQUE (EmpresaId, SourceKey)
);
CREATE CLUSTERED COLUMNSTORE INDEX CCI_AsientoLinea ON fact.AsientoLinea;
CREATE INDEX IX_AL_Relacionado ON fact.AsientoLinea(EmpresaId, RelacionadoSourceKey) WHERE RelacionadoSourceKey IS NOT NULL;

/* --- Facturación: grano = servicio facturado (mFacturas ⨝ dFacturasServicios ⨝ mServicios) --- */
CREATE TABLE fact.FacturaLinea (
    FacturaLineaId  bigint IDENTITY(1,1) NOT NULL,
    EmpresaId       int NOT NULL CONSTRAINT FK_FL_Empresa REFERENCES seg.Empresa,
    SourceKey       nvarchar(100) NOT NULL,             -- NoFactura|IdSede|IdServicio
    TipoDocumento   varchar(12) NOT NULL DEFAULT 'FACTURA' CHECK (TipoDocumento IN ('FACTURA','NOTA_CREDITO','NOTA_DEBITO')),
    NumeroDocumento varchar(30) NOT NULL,
    AdmisionSourceKey varchar(30) NULL,
    FechaEmisionKey int NOT NULL CONSTRAINT FK_FL_Fecha REFERENCES dim.Fecha,
    FechaPrestacionKey int NULL CONSTRAINT FK_FL_FechaP REFERENCES dim.Fecha,
    FechaRadicacionKey int NULL CONSTRAINT FK_FL_FechaR REFERENCES dim.Fecha,  -- contrato: fechaEnvioRips
    CuentaCobro     varchar(30) NULL,                   -- mRipsEnvios.NoCuentaCobro
    PagadorId       int NULL CONSTRAINT FK_FL_Pagador REFERENCES dim.Pagador,
    ContratoId      int NULL CONSTRAINT FK_FL_Contrato REFERENCES dim.Contrato,
    SedeId          int NULL CONSTRAINT FK_FL_Sede REFERENCES dim.Sede,
    AlmacenId       int NULL CONSTRAINT FK_FL_Alm REFERENCES dim.Almacen,
    CentroCostoId   int NULL CONSTRAINT FK_FL_CC REFERENCES dim.CentroCosto,
    LineaServicioId int NULL CONSTRAINT FK_FL_LS REFERENCES dim.LineaServicio,
    ServicioId      int NULL CONSTRAINT FK_FL_Serv REFERENCES dim.Servicio,
    ArticuloId      int NULL CONSTRAINT FK_FL_Art REFERENCES dim.Articulo,
    LoteId          int NULL CONSTRAINT FK_FL_Lote REFERENCES dim.Lote,
    ProfesionalId   int NULL CONSTRAINT FK_FL_Prof REFERENCES dim.Profesional,
    UsuarioOpId     int NULL CONSTRAINT FK_FL_UOp REFERENCES dim.UsuarioOperativo,   -- facturador
    Cantidad        decimal(18,4) NOT NULL DEFAULT 1,
    ValorServicio   decimal(19,4) NOT NULL,             -- mServicios.VrTotal
    Descuento       decimal(19,4) NOT NULL DEFAULT 0,
    CuotaModeradora decimal(19,4) NOT NULL DEFAULT 0,
    Iva             decimal(19,4) NOT NULL DEFAULT 0,
    ValorNeto       decimal(19,4) NOT NULL,             -- prorrateo: VrTotalFactura * VrServicio / NULLIF(VrServicios,0) (regla SIPS_4)
    CostoDirecto    decimal(19,4) NULL,                 -- contrato: vrCostoOrigen (mServicios.VrCosto) [S unitario/total]
    Anulada         bit NOT NULL DEFAULT 0,
    EstadoFE        tinyint NULL,                       -- 0 enviada, 1 cargada, 2 error, 3 sin enviar
    CargaId         bigint NOT NULL CONSTRAINT FK_FL_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_FacturaLinea PRIMARY KEY NONCLUSTERED (FacturaLineaId),
    CONSTRAINT UQ_FacturaLinea UNIQUE (EmpresaId, SourceKey)
);
CREATE CLUSTERED COLUMNSTORE INDEX CCI_FacturaLinea ON fact.FacturaLinea;

/* --- Atendido no facturado (regla SIPS_37), foto diaria --- */
CREATE TABLE fact.AdmisionPendienteFactura (
    EmpresaId       int NOT NULL CONSTRAINT FK_APF_Empresa REFERENCES seg.Empresa,
    CorteFechaKey   int NOT NULL CONSTRAINT FK_APF_Corte REFERENCES dim.Fecha,
    AdmisionSourceKey varchar(30) NOT NULL,
    FechaIngresoKey int NOT NULL CONSTRAINT FK_APF_Ingreso REFERENCES dim.Fecha,
    FechaSalidaKey  int NULL CONSTRAINT FK_APF_Salida REFERENCES dim.Fecha,
    PagadorId       int NULL CONSTRAINT FK_APF_Pagador REFERENCES dim.Pagador,
    SedeId          int NULL CONSTRAINT FK_APF_Sede REFERENCES dim.Sede,
    TipoAtencion    nvarchar(60) NULL,
    ProfesionalId   int NULL CONSTRAINT FK_APF_Prof REFERENCES dim.Profesional,
    UsuarioOpId     int NULL CONSTRAINT FK_APF_UOp REFERENCES dim.UsuarioOperativo,
    ValorServicios  decimal(19,4) NOT NULL,
    DiasSinFacturar int NOT NULL,
    CargaId         bigint NOT NULL CONSTRAINT FK_APF_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_AdmisionPendiente PRIMARY KEY (EmpresaId, CorteFechaKey, AdmisionSourceKey)
);

/* --- Glosas --- */
CREATE TABLE fact.Glosa (
    GlosaId         bigint IDENTITY(1,1) CONSTRAINT PK_Glosa PRIMARY KEY,
    EmpresaId       int NOT NULL CONSTRAINT FK_Glosa_Empresa REFERENCES seg.Empresa,
    SourceKey       nvarchar(100) NOT NULL,
    NumeroFactura   varchar(30) NOT NULL,
    FechaGlosaKey   int NOT NULL CONSTRAINT FK_Glosa_Fecha REFERENCES dim.Fecha,
    FechaRespuestaKey int NULL CONSTRAINT FK_Glosa_FechaR REFERENCES dim.Fecha,
    PagadorId       int NULL CONSTRAINT FK_Glosa_Pagador REFERENCES dim.Pagador,
    MotivoGlosaId   smallint NULL CONSTRAINT FK_Glosa_Motivo REFERENCES dim.MotivoGlosa,
    SedeId          int NULL CONSTRAINT FK_Glosa_Sede REFERENCES dim.Sede,
    CentroCostoId   int NULL CONSTRAINT FK_Glosa_CC REFERENCES dim.CentroCosto,
    ValorGlosado    decimal(19,4) NOT NULL,
    ValorAceptado   decimal(19,4) NOT NULL DEFAULT 0,
    ValorRechazado  decimal(19,4) NOT NULL DEFAULT 0,
    Estado          varchar(15) NOT NULL DEFAULT 'ABIERTA',   -- contrato: estadoOrigen
    Anulada         bit NOT NULL DEFAULT 0,
    CargaId         bigint NOT NULL CONSTRAINT FK_Glosa_Carga REFERENCES etl.Carga,
    CONSTRAINT UQ_Glosa UNIQUE (EmpresaId, SourceKey)
);

/* --- Cartera CxC / CxP: foto por corte, CALCULADA aquí con etl.usp_GenerarCorteCartera --- */
CREATE TABLE fact.CarteraDocumento (
    CarteraId       bigint IDENTITY(1,1) NOT NULL,
    EmpresaId       int NOT NULL CONSTRAINT FK_Cart_Empresa REFERENCES seg.Empresa,
    CorteFechaKey   int NOT NULL CONSTRAINT FK_Cart_Corte REFERENCES dim.Fecha,
    TipoCartera     char(3) NOT NULL CHECK (TipoCartera IN ('CXC','CXP')),
    LineaOrigenSourceKey nvarchar(100) NOT NULL,
    NumeroComprobante varchar(30) NULL,
    NumeroDocumento varchar(40) NULL,
    CuentaCobro     varchar(30) NULL,
    TerceroId       int NULL CONSTRAINT FK_Cart_Tercero REFERENCES dim.Tercero,
    PagadorId       int NULL CONSTRAINT FK_Cart_Pagador REFERENCES dim.Pagador,
    CuentaId        int NOT NULL CONSTRAINT FK_Cart_Cuenta REFERENCES dim.CuentaContable,
    SedeId          int NULL CONSTRAINT FK_Cart_Sede REFERENCES dim.Sede,
    CentroCostoId   int NULL CONSTRAINT FK_Cart_CC REFERENCES dim.CentroCosto,
    FechaMovimientoKey int NOT NULL CONSTRAINT FK_Cart_FM REFERENCES dim.Fecha,
    FechaCartera    date NULL,
    FechaVencimiento date NULL,
    ValorOriginal   decimal(19,4) NOT NULL,
    Abonos          decimal(19,4) NOT NULL,
    Saldo           decimal(19,4) NOT NULL,
    Dias            int NOT NULL,                       -- desde FechaCartera (o FechaMovimiento) al corte
    RangoEdad       varchar(10) NOT NULL,               -- 0-30, 31-60, 61-90, 91-120, 121-150, 151-180, 181-360, >360
    CargaId         bigint NULL,
    CONSTRAINT PK_Cartera PRIMARY KEY NONCLUSTERED (CarteraId)
);
CREATE CLUSTERED COLUMNSTORE INDEX CCI_Cartera ON fact.CarteraDocumento;

/* --- Tesorería: recibos de caja (mRecibos + dRecibosFormasPagos) y movimientos bancarios --- */
CREATE TABLE fact.TesoreriaMovimiento (
    MovimientoId    bigint IDENTITY(1,1) NOT NULL,
    EmpresaId       int NOT NULL CONSTRAINT FK_Tes_Empresa REFERENCES seg.Empresa,
    SourceKey       nvarchar(100) NOT NULL,
    FechaKey        int NOT NULL CONSTRAINT FK_Tes_Fecha REFERENCES dim.Fecha,
    CuentaBancariaId int NOT NULL CONSTRAINT FK_Tes_CB REFERENCES dim.CuentaBancaria,
    TipoMovimiento  varchar(10) NOT NULL CHECK (TipoMovimiento IN ('INGRESO','EGRESO','TRASLADO')),
    ConceptoCodigo  varchar(20) NULL,           -- código del concepto en la fuente (05 §6 recibos)
    Concepto        varchar(40) NOT NULL,       -- RECAUDO_CARTERA, RECAUDO_PARTICULAR, COPAGO, CUOTA_MODERADORA, PAGO_PROVEEDOR, NOMINA, IMPUESTOS...
    FormaPago       varchar(40) NULL,           -- SIIFNet.FormasPagos.Nombre
    TerceroId       int NULL CONSTRAINT FK_Tes_Tercero REFERENCES dim.Tercero,
    PagadorId       int NULL CONSTRAINT FK_Tes_Pagador REFERENCES dim.Pagador,
    CentroCostoId   int NULL CONSTRAINT FK_Tes_CC REFERENCES dim.CentroCosto,
    SedeId          int NULL CONSTRAINT FK_Tes_Sede REFERENCES dim.Sede,
    UsuarioOpId     int NULL CONSTRAINT FK_Tes_UOp REFERENCES dim.UsuarioOperativo,
    NumeroDocumento varchar(30) NULL,
    NumeroFactura   varchar(30) NULL,
    Valor           decimal(19,4) NOT NULL CHECK (Valor >= 0),
    Anulado         bit NOT NULL DEFAULT 0,
    CargaId         bigint NOT NULL CONSTRAINT FK_Tes_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_Tesoreria PRIMARY KEY NONCLUSTERED (MovimientoId),
    CONSTRAINT UQ_Tesoreria UNIQUE (EmpresaId, SourceKey)
);
CREATE CLUSTERED COLUMNSTORE INDEX CCI_Tesoreria ON fact.TesoreriaMovimiento;

CREATE TABLE fact.ConciliacionBancaria (                -- Tesoreria.ConciliacionesBancarias
    EmpresaId        int NOT NULL CONSTRAINT FK_CBan_Empresa REFERENCES seg.Empresa,
    CuentaBancariaId int NOT NULL CONSTRAINT FK_CBan_CB REFERENCES dim.CuentaBancaria,
    PeriodoKey       int NOT NULL,
    SaldoInicial     decimal(19,4) NOT NULL,
    SaldoLibro       decimal(19,4) NOT NULL,
    SaldoExtracto    decimal(19,4) NOT NULL,
    Diferencia       decimal(19,4) NOT NULL,
    Cerrada          bit NOT NULL,
    CargaId          bigint NOT NULL CONSTRAINT FK_CBan_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_ConciliacionBancaria PRIMARY KEY (EmpresaId, CuentaBancariaId, PeriodoKey)
);

/* --- Inventario: Almacen.MovimientosAlmacen(Detalles) --- */
CREATE TABLE fact.MovimientoInventario (
    MovInvId        bigint IDENTITY(1,1) NOT NULL,
    EmpresaId       int NOT NULL CONSTRAINT FK_MI_Empresa REFERENCES seg.Empresa,
    SourceKey       nvarchar(100) NOT NULL,             -- MovimientosAlmacenDetalles.Id
    FechaKey        int NOT NULL CONSTRAINT FK_MI_Fecha REFERENCES dim.Fecha,
    TipoComprobanteId int NOT NULL CONSTRAINT FK_MI_TC REFERENCES dim.TipoComprobante,
    NumeroComprobante varchar(30) NULL,
    AlmacenId       int NOT NULL CONSTRAINT FK_MI_Alm REFERENCES dim.Almacen,
    ArticuloId      int NOT NULL CONSTRAINT FK_MI_Art REFERENCES dim.Articulo,
    LoteId          int NULL CONSTRAINT FK_MI_Lote REFERENCES dim.Lote,
    CentroCostoId   int NULL CONSTRAINT FK_MI_CC REFERENCES dim.CentroCosto,
    TerceroId       int NULL CONSTRAINT FK_MI_Tercero REFERENCES dim.Tercero,
    CantidadEntrada decimal(18,4) NOT NULL DEFAULT 0,
    CantidadSalida  decimal(18,4) NOT NULL DEFAULT 0,
    CostoUnitario   decimal(19,4) NOT NULL,
    PorcentajeDescuento decimal(9,4) NOT NULL DEFAULT 0,
    PorcentajeIva   decimal(9,4) NOT NULL DEFAULT 0,
    ValorEntrada    decimal(19,4) NOT NULL,             -- CantEntrada*Costo*(1-Desc/100)  (regla almacen*SinIva)
    ValorSalida     decimal(19,4) NOT NULL,
    CostoPromedio   decimal(19,4) NULL,
    Estado          tinyint NOT NULL DEFAULT 1 CHECK (Estado IN (1,2)),
    CargaId         bigint NOT NULL CONSTRAINT FK_MI_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_MovimientoInventario PRIMARY KEY NONCLUSTERED (MovInvId),
    CONSTRAINT UQ_MovimientoInventario UNIQUE (EmpresaId, SourceKey)
);
CREATE CLUSTERED COLUMNSTORE INDEX CCI_MovimientoInventario ON fact.MovimientoInventario;

/* --- Presupuesto gerencial (fuente: Excel/API; SaludIPS solo tiene presupuesto público) --- */
CREATE TABLE fact.Presupuesto (
    PresupuestoId   bigint IDENTITY(1,1) CONSTRAINT PK_Presupuesto PRIMARY KEY,
    EmpresaId       int NOT NULL CONSTRAINT FK_Pres_Empresa REFERENCES seg.Empresa,
    Version         varchar(20) NOT NULL DEFAULT 'APROBADO',
    PeriodoKey      int NOT NULL,
    CuentaId        int NOT NULL CONSTRAINT FK_Pres_Cuenta REFERENCES dim.CuentaContable,
    CentroCostoId   int NULL CONSTRAINT FK_Pres_CC REFERENCES dim.CentroCosto,
    SedeId          int NULL CONSTRAINT FK_Pres_Sede REFERENCES dim.Sede,
    ValorPresupuestado decimal(19,4) NOT NULL,          -- misma convención que rpt.vw_EstadoResultados.Saldo (por naturaleza)
    CargaId         bigint NOT NULL CONSTRAINT FK_Pres_Carga REFERENCES etl.Carga,
    CONSTRAINT UQ_Presupuesto UNIQUE (EmpresaId, Version, PeriodoKey, CuentaId, CentroCostoId, SedeId)
);
GO
