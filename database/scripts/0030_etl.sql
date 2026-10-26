/* 0030 · Control de cargas, staging y cola de trabajos (spec 05 §2–3, spec 20 §2). */
CREATE TABLE etl.Carga (
    CargaId         bigint IDENTITY(1,1) CONSTRAINT PK_Carga PRIMARY KEY,
    EmpresaId       int           NOT NULL CONSTRAINT FK_Carga_Empresa REFERENCES seg.Empresa,
    Entidad         varchar(40)   NOT NULL,
    Modo            varchar(12)   NOT NULL CHECK (Modo IN ('COMPLETA','VENTANA','INCREMENTAL','FOTO','INICIAL','REPROCESO')),  -- contrato 05 §3; REPROCESO = solo plataforma
    VentanaDesde    date          NULL,      -- VENTANA/INICIAL: se reemplaza todo lo del rango (captura anulaciones y ediciones)
    VentanaHasta    date          NULL,
    Corte           date          NULL,      -- FOTO
    Origen          varchar(60)   NULL,
    VersionAgente   varchar(20)   NULL,
    VersionFuente   varchar(30)   NULL,      -- versión de SaludIPS u otro HIS
    TotalLotes      int           NULL,      -- declarados al cerrar
    TotalFilas      int           NULL,
    SumaControl     decimal(23,4) NULL,
    HashPayload     varbinary(32) NULL,
    Estado          varchar(12)   NOT NULL DEFAULT 'ABIERTA'
                    CHECK (Estado IN ('ABIERTA','CERRADA','PROCESANDO','OK','ERROR','RECHAZADA')),
                    -- ABIERTA: recibe lotes · CERRADA: encolada · RECHAZADA: control (totales/sumaControl) no cuadra. Atómica: sin estado parcial
    FilasRecibidas  int NULL, FilasInsertadas int NULL, FilasActualizadas int NULL, FilasEliminadas int NULL, FilasRechazadas int NULL,
    IniciadaEn      datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    CerradaEn       datetime2(0)  NULL,
    FinalizadaEn    datetime2(0)  NULL,
    TraceParent     varchar(60)   NULL,
    Error           nvarchar(2000) NULL,
    CONSTRAINT CK_Carga_Ventana CHECK (Modo NOT IN ('VENTANA','INICIAL') OR (VentanaDesde IS NOT NULL AND VentanaHasta IS NOT NULL AND VentanaDesde <= VentanaHasta)),
    CONSTRAINT CK_Carga_Foto    CHECK (Modo <> 'FOTO' OR Corte IS NOT NULL)
);
CREATE UNIQUE INDEX UX_Carga_Hash ON etl.Carga(EmpresaId, Entidad, HashPayload) WHERE HashPayload IS NOT NULL AND Estado = 'OK';
CREATE INDEX IX_Carga_Empresa ON etl.Carga(EmpresaId, Entidad, IniciadaEn DESC);

CREATE TABLE etl.Rechazo (
    RechazoId  bigint IDENTITY(1,1) CONSTRAINT PK_Rechazo PRIMARY KEY,
    CargaId    bigint NOT NULL CONSTRAINT FK_Rechazo_Carga REFERENCES etl.Carga,
    EmpresaId  int NOT NULL,
    SourceKey  nvarchar(100) NULL,
    Motivo     nvarchar(400) NOT NULL,
    Payload    nvarchar(max) NULL
);

CREATE TABLE etl.PeriodoCierre (
    EmpresaId   int NOT NULL CONSTRAINT FK_PC_Empresa REFERENCES seg.Empresa,
    PeriodoKey  int NOT NULL,
    Estado      varchar(10) NOT NULL DEFAULT 'ABIERTO' CHECK (Estado IN ('ABIERTO','CERRADO')),
    CerradoEn   datetime2(0) NULL,
    CONSTRAINT PK_PeriodoCierre PRIMARY KEY (EmpresaId, PeriodoKey)
);

CREATE TABLE stg.Payload (                              -- un lote por fila; reintento idempotente por (CargaId, NumeroLote)
    PayloadId  bigint IDENTITY(1,1) CONSTRAINT PK_Payload PRIMARY KEY,
    CargaId    bigint NOT NULL CONSTRAINT FK_Payload_Carga REFERENCES etl.Carga,
    EmpresaId  int NOT NULL,
    Entidad    varchar(40) NOT NULL,
    NumeroLote int NOT NULL CHECK (NumeroLote >= 1),
    Filas      int NOT NULL,
    Json       nvarchar(max) NOT NULL CONSTRAINT CK_Payload_Json CHECK (ISJSON(Json) = 1),
    RecibidoEn datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_Payload_Lote UNIQUE (CargaId, NumeroLote)
);
GO
CREATE TABLE etl.Trabajo (
    TrabajoId     uniqueidentifier NOT NULL CONSTRAINT PK_Trabajo PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    Tipo          varchar(40) NOT NULL,        -- PROCESAR_CARGA, CALCULAR_CARTERA, AUDITAR_CONFIG, VALIDAR_PRECIOS, DISTRIBUIR_COSTOS,
                                               -- ALERTAS, GENERAR_REPORTE, ACTUALIZAR_CATALOGO_REGULATORIO, CERRAR_PERIODO_IA, ENVIAR_CORREO
    EmpresaId     int NULL,                    -- NULL = trabajo global (catálogo regulatorio, cierre de IA): solo lo ve rol_etl
    UsuarioId     int NULL,                    -- solicitante: GENERAR_REPORTE e IA se ejecutan con su contexto (RLS)
    ClaveExclusion varchar(100) NOT NULL,      -- p. ej. 'EMP:12|ENT:asientos' -> un solo trabajo activo por clave
    Prioridad     tinyint NOT NULL DEFAULT 5,
    Payload       nvarchar(2000) NULL,          -- solo identificadores (CargaId...), nunca datos
    Estado        varchar(12) NOT NULL DEFAULT 'PENDIENTE' CHECK (Estado IN ('PENDIENTE','EN_PROCESO','OK','ERROR','VENENO')),
    Intentos      tinyint NOT NULL DEFAULT 0,
    MaxIntentos   tinyint NOT NULL DEFAULT 5,
    DisponibleEn  datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),   -- backoff exponencial
    LeaseHasta    datetime2(0) NULL,
    TraceParent   varchar(60) NULL,                                 -- continuidad de la traza OpenTelemetry
    CreadoEn      datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    Error         nvarchar(2000) NULL
);
CREATE INDEX IX_Trabajo_Cola ON etl.Trabajo (Tipo, Estado, Prioridad, DisponibleEn) INCLUDE (ClaveExclusion, EmpresaId);
CREATE UNIQUE INDEX UX_Trabajo_Exclusion ON etl.Trabajo (ClaveExclusion) WHERE Estado IN ('PENDIENTE','EN_PROCESO');
GO
