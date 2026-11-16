/* 1040 · Facturación del SaaS (08 §30): canon, cuenta de cobro, pagos, envíos y alertas. Requiere 0020, 0100, 0110, 0900.
   Decisiones del dueño (2026-11-05): una sola serie CC, sin DIAN/IVA/notas; mes vencido; PRORRATEO por días calendario del mes en altas,
   bajas y cambios de condición a mitad de mes; la suspensión por mora NO suspende el cobro del canon. */
IF SCHEMA_ID('fac') IS NULL EXEC (N'CREATE SCHEMA fac');
GO
CREATE TABLE fac.Parametro (                       -- global, sin EmpresaId; escritura solo vía fac.usp_GuardarParametro (superadmin)
    Clave       varchar(40)   NOT NULL CONSTRAINT PK_FacParametro PRIMARY KEY,
    Valor       nvarchar(500) NOT NULL,
    Descripcion nvarchar(200) NULL
);
INSERT fac.Parametro (Clave, Valor, Descripcion) VALUES
 ('DIAS_PAGO_DEFECTO',     '30',    N'[S1] Días calendario desde la emisión'),
 ('HORA_CIERRE_IA',        '01:00', N'Día 1, hora Bogotá: cierre del periodo de IA'),
 ('HORA_GENERACION',       '06:00', N'Día 1, hora Bogotá: generación, emisión y envío'),
 ('DIA_PREVALIDACION',     '25',    N'Día del mes M en que se simula la facturación y se alertan problemas'),
 ('REQUIERE_APROBACION',   '0',     N'Interruptor global: 1 = toda cuenta queda en BORRADOR hasta que el superadmin la emita'),
 ('PRORRATEO',            'DIAS_MES', N'[S] ROUND(canon × días/días del mes, 0), AwayFromZero; alta y baja inclusive'),
 ('INCLUIR_EXCEDENTE_IA',  '0',     N'1 = agrega la línea de excedente IA (requiere 1042 aplicado)'),
 ('UMBRAL_CUADRE_IA_COP',  '1000',  N'Diferencia máxima entre el corte y la suma de UsoLlamada sin retener'),
 ('VARIACION_MAX_PCT',     '50',    N'Variación del total frente al mes anterior que obliga a retener'),
 ('RECORDATORIO_DIAS_ANTES','3',    N'Recordatorio previo al vencimiento'),
 ('AVISOS_MORA_DIAS',      '1,15,30', N'Avisos de mora: días después del vencimiento'),
 ('CORREO_OPERACION',      N'[BUZÓN DEL PRESTADOR]', N'Destino del resumen diario de alertas (sin datos personales)'),
 ('EMISOR_RAZON_SOCIAL',   N'[RAZÓN SOCIAL TITULAR]', N'[S4]'),
 ('EMISOR_NIT',            N'[NIT]', N'[S4]'),
 ('INSTRUCCIONES_PAGO',    N'[CUENTA BANCARIA]', N'[S4] Cambiarla exige superadmin y queda en aud (riesgo de fraude, §34)');

CREATE TABLE fac.CanonPlan (                       -- sin EmpresaId: escritura solo vía fac.usp_GuardarCanonPlan
    CanonPlanId     int IDENTITY(1,1) CONSTRAINT PK_FacCanonPlan PRIMARY KEY,
    PlanId          smallint NOT NULL CONSTRAINT FK_FacCP_Plan REFERENCES ia.PlanComercial,
    VigenteDesde    date NOT NULL CONSTRAINT CK_FacCP_Dia1 CHECK (DAY(VigenteDesde) = 1),
    CanonMensualCop decimal(14,2) NOT NULL CONSTRAINT CK_FacCP_Valor CHECK (CanonMensualCop >= 0),
    CreadoPor       int NOT NULL,
    CreadoEn        datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_FacCanonPlan UNIQUE (PlanId, VigenteDesde)
);

-- Historial del plan y canon pactado por empresa. ia.EmpresaConfig.PlanId no tiene historial [H]: para cobrar manda esta tabla;
-- el comando que cambia el plan escribe ambas en una transacción.
CREATE TABLE fac.CondicionComercial (
    CondicionId        int IDENTITY(1,1) CONSTRAINT PK_FacCondicion PRIMARY KEY,
    EmpresaId          int NOT NULL CONSTRAINT FK_FacCond_Empresa REFERENCES seg.Empresa,
    VigenteDesde       date NOT NULL,                -- primer día de servicio (puede ser a mitad de mes: se prorratea)
    VigenteHasta       date NULL,                    -- último día de servicio (inclusive); NULL = abierta
    PlanId             smallint NOT NULL CONSTRAINT FK_FacCond_Plan REFERENCES ia.PlanComercial,
    CanonPactadoCop    decimal(14,2) NULL CONSTRAINT CK_FacCond_Canon CHECK (CanonPactadoCop >= 0),  -- NULL = canon del plan
    DiasPago           smallint NULL CONSTRAINT CK_FacCond_Dias CHECK (DiasPago BETWEEN 0 AND 180), -- NULL = DIAS_PAGO_DEFECTO
    RequiereAprobacion bit NULL,                     -- NULL = parámetro global; 1/0 = forzado para esta empresa
    Facturable         bit NOT NULL DEFAULT 1,       -- 0 = piloto sin cobro
    ReferenciaContrato nvarchar(60) NULL,
    CreadoPor          int NOT NULL,
    CreadoEn           datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_FacCond_Hasta CHECK (VigenteHasta IS NULL OR VigenteHasta >= VigenteDesde),
    CONSTRAINT UQ_FacCond UNIQUE (EmpresaId, VigenteDesde)
);
CREATE UNIQUE INDEX UX_FacCond_Abierta ON fac.CondicionComercial(EmpresaId) WHERE VigenteHasta IS NULL;
-- El no solapamiento lo garantiza fac.usp_GuardarCondicion (UPDLOCK sobre las filas de la empresa).

CREATE TABLE fac.Consecutivo (
    Serie   char(2) NOT NULL CONSTRAINT PK_FacConsecutivo PRIMARY KEY CONSTRAINT CK_FacCons_Serie CHECK (Serie = 'CC'),
    Ultimo  int NOT NULL DEFAULT 0 CONSTRAINT CK_FacCons_Ultimo CHECK (Ultimo >= 0)
);
INSERT fac.Consecutivo (Serie) VALUES ('CC');

CREATE TABLE fac.CuentaCobro (
    CuentaCobroId    int IDENTITY(1,1) CONSTRAINT PK_FacCuentaCobro PRIMARY KEY,
    EmpresaId        int NOT NULL CONSTRAINT FK_FacCC_Empresa REFERENCES seg.Empresa,
    PeriodoKey       int NOT NULL CONSTRAINT CK_FacCC_Periodo CHECK (PeriodoKey BETWEEN 202601 AND 209912 AND PeriodoKey % 100 BETWEEN 1 AND 12),
    CondicionId      int NOT NULL CONSTRAINT FK_FacCC_Cond REFERENCES fac.CondicionComercial,   -- vigente al cierre del periodo; el detalle por tramo está en las líneas
    Numero           varchar(20) NULL,               -- CC-000123, asignado al emitir
    Estado           varchar(10) NOT NULL DEFAULT 'BORRADOR'
                     CONSTRAINT CK_FacCC_Estado CHECK (Estado IN ('BORRADOR','DESCARTADA','EMITIDA','PAGADA','ANULADA')),
    TotalCop         decimal(16,2) NOT NULL DEFAULT 0 CONSTRAINT CK_FacCC_Total CHECK (TotalCop >= 0),
    PagadoCop        decimal(16,2) NOT NULL DEFAULT 0,
    SaldoCop         AS (TotalCop - PagadoCop) PERSISTED,
    DiasPago         smallint NOT NULL,              -- foto al generar
    FechaEmision     date NULL,                      -- fecha de negocio Bogotá al emitir
    FechaVencimiento date NULL,                      -- FechaEmision + DiasPago: la mora corre desde el día siguiente
    ClienteRazonSocial nvarchar(200) NULL, ClienteNit varchar(20) NULL, ClienteDv char(1) NULL, ClienteDireccion nvarchar(300) NULL,  -- foto al emitir
    MotivosRetencion varchar(400) NULL,              -- códigos (SIN_CANON, SIN_DESTINATARIO, VARIACION_ANOMALA, CUADRE_IA, APROBACION_REQUERIDA...)
    Observaciones    nvarchar(500) NULL,             -- VISIBLE AL CLIENTE; en fac.* no se guarda texto interno (§30.4)
    PdfRuta          nvarchar(400) NULL,
    PdfSha256        binary(32) NULL,
    ReemplazaA       int NULL CONSTRAINT FK_FacCC_Reemplaza REFERENCES fac.CuentaCobro,
    CreadaEn         datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    EmitidaPor       int NULL, EmitidaEn datetime2(0) NULL,   -- EmitidaPor NULL = emisión automática
    AnuladaPor       int NULL, AnuladaEn datetime2(0) NULL, MotivoAnulacion nvarchar(500) NULL,
    VersionFila      rowversion NOT NULL,
    CONSTRAINT UQ_FacCC_Empresa UNIQUE (CuentaCobroId, EmpresaId),       -- destino de FK compuestas
    CONSTRAINT CK_FacCC_Numero CHECK ((Estado IN ('BORRADOR','DESCARTADA') AND Numero IS NULL)
                                   OR (Estado IN ('EMITIDA','PAGADA','ANULADA') AND Numero IS NOT NULL AND FechaEmision IS NOT NULL
                                       AND FechaVencimiento IS NOT NULL AND ClienteNit IS NOT NULL)),
    CONSTRAINT CK_FacCC_Pagado  CHECK (PagadoCop >= 0 AND PagadoCop <= TotalCop),
    CONSTRAINT CK_FacCC_Pagada  CHECK (Estado <> 'PAGADA' OR PagadoCop = TotalCop),
    CONSTRAINT CK_FacCC_Anulada CHECK (Estado <> 'ANULADA' OR (MotivoAnulacion IS NOT NULL AND AnuladaEn IS NOT NULL AND PagadoCop = 0))
);
CREATE UNIQUE INDEX UX_FacCC_Periodo ON fac.CuentaCobro(EmpresaId, PeriodoKey) WHERE Estado IN ('BORRADOR','EMITIDA','PAGADA');  -- idempotencia
CREATE UNIQUE INDEX UX_FacCC_Numero  ON fac.CuentaCobro(Numero) WHERE Numero IS NOT NULL;
CREATE INDEX IX_FacCC_Cartera ON fac.CuentaCobro(Estado, FechaVencimiento) INCLUDE (EmpresaId, SaldoCop, Numero);
CREATE INDEX IX_FacCC_Empresa ON fac.CuentaCobro(EmpresaId, PeriodoKey DESC);

CREATE TABLE fac.CuentaCobroLinea (
    LineaId            int IDENTITY(1,1) CONSTRAINT PK_FacLinea PRIMARY KEY,
    CuentaCobroId      int NOT NULL,
    EmpresaId          int NOT NULL,
    Orden              tinyint NOT NULL,
    Tipo               varchar(12) NOT NULL CONSTRAINT CK_FacLinea_Tipo CHECK (Tipo IN ('CANON','EXCEDENTE_IA')),
    Concepto           nvarchar(200) NOT NULL,       -- «Canon mensual plan X · 01-oct a 14-oct-2026 (14/31 días)»
    PeriodoServicioKey int NOT NULL,
    -- Prorrateo (D48): un tramo por condición comercial vigente dentro del mes. Mes completo: DiasFacturados = DiasPeriodo.
    CondicionId        int NULL CONSTRAINT FK_FacLinea_Cond REFERENCES fac.CondicionComercial,   -- NULL en EXCEDENTE_IA
    FechaDesde         date NOT NULL,
    FechaHasta         date NOT NULL,
    DiasFacturados     smallint NOT NULL,
    DiasPeriodo        smallint NOT NULL,            -- días calendario del mes de servicio
    CanonMensualCop    decimal(14,2) NULL,           -- canon de referencia del tramo (antes de prorratear); NULL en EXCEDENTE_IA
    ValorCop           decimal(16,2) NOT NULL CONSTRAINT CK_FacLinea_Valor CHECK (ValorCop >= 0),   -- CANON: ROUND(CanonMensualCop * DiasFacturados / DiasPeriodo, 0)
    CanonPlanId        int NULL CONSTRAINT FK_FacLinea_CanonPlan REFERENCES fac.CanonPlan,   -- NULL si el canon es pactado o la línea es IA
    CONSTRAINT FK_FacLinea_CC FOREIGN KEY (CuentaCobroId, EmpresaId) REFERENCES fac.CuentaCobro (CuentaCobroId, EmpresaId),
    CONSTRAINT UQ_FacLinea_Tramo UNIQUE (CuentaCobroId, Tipo, FechaDesde),
    CONSTRAINT UQ_FacLinea_Empresa UNIQUE (LineaId, EmpresaId),
    CONSTRAINT CK_FacLinea_Fechas CHECK (FechaHasta >= FechaDesde AND DiasFacturados BETWEEN 1 AND DiasPeriodo
                                         AND DiasFacturados = DATEDIFF(DAY, FechaDesde, FechaHasta) + 1),
    CONSTRAINT CK_FacLinea_Canon  CHECK ((Tipo = 'CANON' AND CondicionId IS NOT NULL AND CanonMensualCop IS NOT NULL)
                                      OR (Tipo = 'EXCEDENTE_IA' AND CondicionId IS NULL AND CanonMensualCop IS NULL AND DiasFacturados = DiasPeriodo))
);

-- Solo con INCLUIR_EXCEDENTE_IA = 1: base auditable del excedente (§31) y detalle agregado.
CREATE TABLE fac.LiquidacionIa (
    LineaId                int NOT NULL CONSTRAINT PK_FacLiqIa PRIMARY KEY,
    EmpresaId              int NOT NULL,
    PeriodoKey             int NOT NULL,
    CupoIncluidoCop        decimal(14,2) NOT NULL,
    ConsumoCop             decimal(16,2) NOT NULL,
    ExcedenteBrutoCop      decimal(16,2) NOT NULL,
    TopeAplicadoCop        decimal(14,2) NULL,
    ExcedenteFacturableCop decimal(16,2) NOT NULL,
    ExcedenteAbsorbidoCop  decimal(16,2) NOT NULL,
    HuboConsumoEnExcedente bit NOT NULL,
    CambioConfigEnPeriodo  bit NOT NULL,
    DiferenciaCuadreCop    decimal(16,2) NOT NULL,
    CONSTRAINT FK_FacLiqIa_Linea FOREIGN KEY (LineaId, EmpresaId) REFERENCES fac.CuentaCobroLinea (LineaId, EmpresaId),
    CONSTRAINT FK_FacLiqIa_Corte FOREIGN KEY (EmpresaId, PeriodoKey) REFERENCES ia.CorteMensual (EmpresaId, PeriodoKey),
    CONSTRAINT CK_FacLiqIa CHECK (ExcedenteFacturableCop >= 0 AND ExcedenteAbsorbidoCop >= 0
                                  AND ExcedenteBrutoCop = ExcedenteFacturableCop + ExcedenteAbsorbidoCop)
);
CREATE TABLE fac.DetalleIa (                       -- SIN UsuarioId, conversación, mensaje, texto ni herramientas
    LineaId int NOT NULL, EmpresaId int NOT NULL,
    ModeloId smallint NOT NULL CONSTRAINT FK_FacDetIa_Modelo REFERENCES ia.Modelo,
    TarifaId int NOT NULL CONSTRAINT FK_FacDetIa_Tarifa REFERENCES ia.Tarifa,
    Llamadas int NOT NULL, TokensEntrada bigint NOT NULL, TokensSalida bigint NOT NULL, TokensCacheLectura bigint NOT NULL,
    ValorVentaCop decimal(16,2) NOT NULL,          -- consumo del mes a esa tarifa (incluye lo cubierto por el cupo)
    CONSTRAINT PK_FacDetalleIa PRIMARY KEY (LineaId, ModeloId, TarifaId),
    CONSTRAINT FK_FacDetIa_Linea FOREIGN KEY (LineaId, EmpresaId) REFERENCES fac.CuentaCobroLinea (LineaId, EmpresaId)
);

CREATE TABLE fac.Pago (
    PagoId           int IDENTITY(1,1) CONSTRAINT PK_FacPago PRIMARY KEY,
    EmpresaId        int NOT NULL CONSTRAINT FK_FacPago_Empresa REFERENCES seg.Empresa,
    Clase            varchar(10) NOT NULL CONSTRAINT CK_FacPago_Clase CHECK (Clase IN ('RECAUDO','RETENCION','OTRO')),  -- RETENCION: la que practique el cliente [S]
    Medio            varchar(14) NULL CONSTRAINT CK_FacPago_Medio CHECK (Medio IN ('TRANSFERENCIA','CONSIGNACION','PSE','CHEQUE','OTRO')),
    FechaPago        date NOT NULL,
    ValorCop         decimal(16,2) NOT NULL CONSTRAINT CK_FacPago_Valor CHECK (ValorCop > 0),
    Referencia       nvarchar(60) NULL,              -- comprobante; NUNCA números de cuenta ni de tarjeta
    ValorAplicadoCop decimal(16,2) NOT NULL DEFAULT 0,
    SaldoSinAplicarCop AS (ValorCop - ValorAplicadoCop) PERSISTED,   -- saldo a favor
    Estado           varchar(8) NOT NULL DEFAULT 'VIGENTE' CONSTRAINT CK_FacPago_Estado CHECK (Estado IN ('VIGENTE','ANULADO')),
    SoporteRuta      nvarchar(400) NULL,             -- solo lo descarga el superadmin
    Observacion      nvarchar(300) NULL,             -- visible al cliente
    RegistradoPor    int NOT NULL, RegistradoEn datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    AnuladoPor       int NULL, AnuladoEn datetime2(0) NULL, MotivoAnulacion nvarchar(300) NULL,
    VersionFila      rowversion NOT NULL,
    CONSTRAINT UQ_FacPago_Empresa UNIQUE (PagoId, EmpresaId),
    CONSTRAINT CK_FacPago_Aplicado CHECK (ValorAplicadoCop BETWEEN 0 AND ValorCop),
    CONSTRAINT CK_FacPago_Medio2 CHECK (Clase <> 'RECAUDO' OR Medio IS NOT NULL),
    CONSTRAINT CK_FacPago_Anulado CHECK (Estado <> 'ANULADO' OR (ValorAplicadoCop = 0 AND MotivoAnulacion IS NOT NULL))
);
CREATE UNIQUE INDEX UX_FacPago_Ref ON fac.Pago(EmpresaId, Clase, Referencia, FechaPago) WHERE Referencia IS NOT NULL AND Estado = 'VIGENTE';
CREATE INDEX IX_FacPago_Empresa ON fac.Pago(EmpresaId, FechaPago DESC) INCLUDE (ValorCop, ValorAplicadoCop, Estado);

CREATE TABLE fac.AplicacionPago (
    AplicacionId  int IDENTITY(1,1) CONSTRAINT PK_FacAplicacion PRIMARY KEY,
    EmpresaId     int NOT NULL,
    PagoId        int NOT NULL,
    CuentaCobroId int NOT NULL,
    ValorCop      decimal(16,2) NOT NULL CONSTRAINT CK_FacApl_Valor CHECK (ValorCop > 0),
    AplicadaPor   int NULL,                          -- NULL = automática (FIFO por vencimiento)
    AplicadaEn    datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    ReversadaPor  int NULL, ReversadaEn datetime2(0) NULL, MotivoReverso nvarchar(300) NULL,
    CONSTRAINT FK_FacApl_Pago FOREIGN KEY (PagoId, EmpresaId) REFERENCES fac.Pago (PagoId, EmpresaId),                -- un pago de A nunca va a una cuenta de B
    CONSTRAINT FK_FacApl_CC   FOREIGN KEY (CuentaCobroId, EmpresaId) REFERENCES fac.CuentaCobro (CuentaCobroId, EmpresaId),
    CONSTRAINT CK_FacApl_Reverso CHECK (ReversadaEn IS NULL OR MotivoReverso IS NOT NULL)
);
CREATE INDEX IX_FacApl_Cuenta ON fac.AplicacionPago(CuentaCobroId) INCLUDE (ValorCop) WHERE ReversadaEn IS NULL;
CREATE INDEX IX_FacApl_Pago   ON fac.AplicacionPago(PagoId) INCLUDE (ValorCop) WHERE ReversadaEn IS NULL;

CREATE TABLE fac.Envio (                           -- bitácora + outbox (etl.Trabajo ENVIAR_CORREO en la misma transacción)
    EnvioId        uniqueidentifier NOT NULL CONSTRAINT PK_FacEnvio PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    EmpresaId      int NOT NULL CONSTRAINT FK_FacEnvio_Empresa REFERENCES seg.Empresa,
    CuentaCobroId  int NOT NULL,
    Tipo           varchar(14) NOT NULL CONSTRAINT CK_FacEnvio_Tipo CHECK (Tipo IN ('CUENTA_COBRO','RECORDATORIO','AVISO_MORA','ANULACION')),
    Hito           varchar(12) NULL,                 -- PREVIO_3, MORA_15… (idempotencia de los recordatorios)
    EsReenvio      bit NOT NULL DEFAULT 0,
    ConAdjunto     bit NOT NULL,                     -- 1 solo para CUENTA_COBRO (D42)
    AdjuntoSha256  binary(32) NULL,
    MessageId      varchar(200) NOT NULL,            -- Message-ID propio, igual en todos los reintentos
    Estado         varchar(10) NOT NULL DEFAULT 'PENDIENTE' CONSTRAINT CK_FacEnvio_Estado CHECK (Estado IN ('PENDIENTE','ENVIANDO','ENVIADO','FALLIDO','REBOTADO','CANCELADO')),
    Intentos       tinyint NOT NULL DEFAULT 0,
    CodigoSmtp     varchar(10) NULL,                 -- código numérico; NUNCA el texto del servidor
    CodigoError    varchar(40) NULL,                 -- SIN_DESTINATARIO, AUTENTICACION, RECHAZO_DESTINATARIO, TIMEOUT, SMTP_NO_CONFIGURADO
    SolicitadoPor  int NULL,
    CreadoEn       datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    UltimoIntentoEn datetime2(0) NULL, EnviadoEn datetime2(0) NULL,
    CONSTRAINT UQ_FacEnvio_Empresa UNIQUE (EnvioId, EmpresaId),
    CONSTRAINT FK_FacEnvio_CC FOREIGN KEY (CuentaCobroId, EmpresaId) REFERENCES fac.CuentaCobro (CuentaCobroId, EmpresaId),
    CONSTRAINT CK_FacEnvio_Adjunto CHECK (ConAdjunto = 0 OR Tipo = 'CUENTA_COBRO')
);
CREATE UNIQUE INDEX UX_FacEnvio_Hito ON fac.Envio(CuentaCobroId, Tipo, Hito) WHERE Hito IS NOT NULL AND Estado <> 'CANCELADO';
CREATE INDEX IX_FacEnvio_Estado ON fac.Envio(Estado, CreadoEn) INCLUDE (EmpresaId, CuentaCobroId);

CREATE TABLE fac.EnvioDestinatario (               -- foto de los destinatarios al encolar (evidencia); retención limitada (§34)
    EnvioId uniqueidentifier NOT NULL, EmpresaId int NOT NULL, Orden tinyint NOT NULL,
    Clase char(2) NOT NULL CONSTRAINT CK_FacDest_Clase CHECK (Clase IN ('TO','CC')),
    ContactoId int NULL,                           -- seg.EmpresaContacto; sin FK porque el contacto puede borrarse
    Email nvarchar(256) NOT NULL, Nombre nvarchar(150) NULL,
    CONSTRAINT PK_FacEnvioDest PRIMARY KEY (EnvioId, Orden),
    CONSTRAINT FK_FacDest_Envio FOREIGN KEY (EnvioId, EmpresaId) REFERENCES fac.Envio (EnvioId, EmpresaId)
);

-- Alertas para el superadmin. seg.Notificacion no sirve: EmpresaId es NOT NULL [H]. RLS: solo superadmin (§30.4).
CREATE TABLE fac.Alerta (
    AlertaId      int IDENTITY(1,1) CONSTRAINT PK_FacAlerta PRIMARY KEY,
    EmpresaId     int NULL CONSTRAINT FK_FacAlerta_Empresa REFERENCES seg.Empresa,   -- NULL = global (p. ej. SMTP caído)
    PeriodoKey    int NULL,
    CuentaCobroId int NULL CONSTRAINT FK_FacAlerta_CC REFERENCES fac.CuentaCobro,
    Codigo        varchar(30) NOT NULL CONSTRAINT CK_FacAlerta_Codigo CHECK (Codigo IN ('SIN_CONDICION','SIN_CANON','SIN_DESTINATARIO',
                    'VARIACION_ANOMALA','CUADRE_IA','CAMBIO_CONFIG_IA','APROBACION_REQUERIDA','ENVIO_FALLIDO','SMTP_NO_CONFIGURADO','TRABAJO_VENENO','TOTAL_CERO')),
    Detalle       nvarchar(300) NULL,              -- sin datos personales
    CreadaEn      datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    AtendidaPor   int NULL, AtendidaEn datetime2(0) NULL
);
CREATE UNIQUE INDEX UX_FacAlerta_Abierta ON fac.Alerta(EmpresaId, PeriodoKey, Codigo) WHERE AtendidaEn IS NULL;   -- no repetir la misma alerta

CREATE TABLE fac.TareaProgramada (                 -- idempotencia de los trabajos periódicos (§32.4)
    Clave        varchar(100) NOT NULL CONSTRAINT PK_FacTarea PRIMARY KEY,   -- 'IA:CIERRE|PER:202610', 'FAC:GEN|PER:202610', 'FAC:MORA|DIA:20261105'
    CompletadaEn datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
/* ---------- Permisos de BD ---------- */
GRANT SELECT ON SCHEMA::fac TO rol_app;
GRANT INSERT, UPDATE ON fac.CondicionComercial TO rol_app;   -- el BLOCK de RLS deja escribir solo al superadmin
GRANT INSERT, UPDATE ON fac.CuentaCobro TO rol_app;
GRANT INSERT, UPDATE, DELETE ON fac.CuentaCobroLinea TO rol_app;   -- DELETE solo en BORRADOR (regenerar), lo valida el handler
GRANT INSERT, UPDATE ON fac.Pago TO rol_app;
GRANT INSERT, UPDATE ON fac.AplicacionPago TO rol_app;
GRANT INSERT, UPDATE ON fac.Envio TO rol_app;
GRANT INSERT ON fac.EnvioDestinatario TO rol_app;
GRANT UPDATE ON fac.Alerta TO rol_app;                        -- marcar atendida
GRANT EXECUTE ON SCHEMA::fac TO rol_app;
DENY INSERT, UPDATE, DELETE ON fac.Parametro TO rol_app;      -- sin EmpresaId (sin RLS): solo vía procedimientos que exigen EsSuperAdmin
DENY INSERT, UPDATE, DELETE ON fac.CanonPlan TO rol_app;
DENY INSERT, UPDATE, DELETE ON fac.Consecutivo TO rol_app;
DENY INSERT, UPDATE, DELETE ON fac.TareaProgramada TO rol_app;
DENY DELETE ON fac.CuentaCobro TO rol_app;
DENY DELETE ON fac.Pago TO rol_app;
DENY DELETE ON fac.AplicacionPago TO rol_app;
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::fac TO rol_etl;
GO
INSERT seg.RolPermiso (RolId, Permiso)                        -- patrón de 0130 [H]
SELECT r.Id, v.Permiso
FROM (VALUES ('SUPERADMIN','fac.administrar'),('SUPERADMIN','fac.consultar'),('ADMIN_EMPRESA','fac.consultar')) v (Rol, Permiso)
JOIN seg.Rol r ON r.NormalizedName = v.Rol
WHERE NOT EXISTS (SELECT 1 FROM seg.RolPermiso x WHERE x.RolId = r.Id AND x.Permiso = v.Permiso);
GO
