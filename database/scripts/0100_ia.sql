/* 0100 · Asistente IA: parámetros, proveedores, modelos, tarifas, planes, configuración y consumo (07). */
/* ============================ PARÁMETROS (área de parámetros, superadmin) ============================ */
CREATE TABLE ia.Parametro (
    Clave   varchar(40)   NOT NULL CONSTRAINT PK_IaParametro PRIMARY KEY,
    Valor   nvarchar(200) NOT NULL,
    Descripcion nvarchar(200) NULL
);
INSERT ia.Parametro (Clave, Valor, Descripcion) VALUES
 ('TRM_USD_COP',            '4000', N'TRM para mostrar el costo de proveedor en COP (actualizable)'),
 ('ALERTA_1_PCT',           '70',   N'Primer aviso sobre el cupo incluido'),
 ('ALERTA_2_PCT',           '90',   N'Segundo aviso sobre el cupo incluido'),
 ('RESERVA_MINUTOS',        '10',   N'Vida de una reserva de cuota'),
 ('MODELO_DEFECTO',         'claude-haiku-5-5', N'Modelo por defecto para empresas nuevas');

/* ============================ PROVEEDORES Y MODELOS (costo) ============================ */
CREATE TABLE ia.Proveedor (
    ProveedorId  smallint IDENTITY(1,1) CONSTRAINT PK_IaProveedor PRIMARY KEY,
    Codigo       varchar(20)  NOT NULL CONSTRAINT UQ_IaProveedor UNIQUE,
    Nombre       nvarchar(60) NOT NULL,
    BaseUrl      varchar(200) NULL,
    SecretoKeyVault varchar(100) NOT NULL,          -- nombre del secreto en Azure Key Vault
    PaisProcesamiento char(2) NULL,
    RequiereConsentimientoEspecifico bit NOT NULL DEFAULT 0,   -- 1: exige ia.Consentimiento vigente por empresa
    Activo       bit NOT NULL DEFAULT 1
);

CREATE TABLE ia.Modelo (
    ModeloId     smallint IDENTITY(1,1) CONSTRAINT PK_IaModelo PRIMARY KEY,
    ProveedorId  smallint NOT NULL CONSTRAINT FK_IaModelo_Prov REFERENCES ia.Proveedor,
    Codigo       varchar(60)  NOT NULL,
    Nombre       nvarchar(80) NOT NULL,
    PrecioEntradaMTok        decimal(12,4) NOT NULL,   -- USD por 1M tokens
    PrecioSalidaMTok         decimal(12,4) NOT NULL,
    PrecioCacheLecturaMTok   decimal(12,4) NULL,
    PrecioCacheEscrituraMTok decimal(12,4) NULL,
    UmbralPrecioLargoTokens  int NULL,                 -- prompts mayores a esto usan precio largo
    PrecioEntradaLargoMTok   decimal(12,4) NULL,
    PrecioSalidaLargoMTok    decimal(12,4) NULL,
    ContextoMaxTokens  int NOT NULL,
    SalidaMaxTokens    int NOT NULL,
    SoportaHerramientas bit NOT NULL DEFAULT 1,
    SoportaCache       bit NOT NULL DEFAULT 0,
    SoportaConteoTokens bit NOT NULL DEFAULT 0,
    EvaluacionAprobada bit NOT NULL DEFAULT 0,
    Activo       bit NOT NULL DEFAULT 1,
    PreciosVigentesDesde date NOT NULL,
    CONSTRAINT UQ_IaModelo UNIQUE (ProveedorId, Codigo)
);

INSERT ia.Proveedor (Codigo, Nombre, BaseUrl, SecretoKeyVault, PaisProcesamiento, RequiereConsentimientoEspecifico, Activo) VALUES
 ('ANTHROPIC', N'Anthropic', NULL,                        'ia-anthropic-key', 'US', 0, 1),
 ('DEEPSEEK',  N'DeepSeek',  'https://api.deepseek.com',  'ia-deepseek-key',  'CN', 1, 0),   -- deshabilitado por decisión
 ('MOONSHOT',  N'Kimi (Moonshot)', 'https://api.moonshot.ai', 'ia-moonshot-key', 'CN', 1, 0); -- deshabilitado por decisión

-- Precios Anthropic según tabla vigente a 2026-10-06; VERIFICAR antes de producción.
-- Cache de Haiku 5.5 no confirmado: se deja NULL (se cobra como entrada normal hasta confirmarlo).
INSERT ia.Modelo (ProveedorId, Codigo, Nombre, PrecioEntradaMTok, PrecioSalidaMTok, PrecioCacheLecturaMTok,
                  UmbralPrecioLargoTokens, PrecioEntradaLargoMTok, PrecioSalidaLargoMTok,
                  ContextoMaxTokens, SalidaMaxTokens, SoportaCache, SoportaConteoTokens, PreciosVigentesDesde)
SELECT p.ProveedorId, v.Codigo, v.Nombre, v.PIn, v.POut, v.PCache, v.Umbral, v.PInL, v.POutL, 1000000, 128000, 1, 1, '2026-10-06'
FROM ia.Proveedor p
CROSS JOIN (VALUES
  ('claude-haiku-5-5',  N'Claude Haiku 5.5',  0.10,  0.50, CAST(NULL AS decimal(12,4)), 100000, 0.50, 2.50),
  ('claude-sonnet-5-5', N'Claude Sonnet 5.5', 2.00, 10.00, 0.20, NULL, NULL, NULL),
  ('claude-opus-5-5',   N'Claude Opus 5.5',   4.00, 20.00, 0.20, NULL, NULL, NULL)
) v (Codigo, Nombre, PIn, POut, PCache, Umbral, PInL, POutL)
WHERE p.Codigo = 'ANTHROPIC';

CREATE TABLE ia.PromptVersion (
    PromptVersionId int IDENTITY(1,1) CONSTRAINT PK_IaPrompt PRIMARY KEY,
    Version      varchar(20) NOT NULL CONSTRAINT UQ_IaPrompt UNIQUE,
    TextoSistema nvarchar(max) NOT NULL,
    Activa       bit NOT NULL DEFAULT 0,
    CreadaEn     datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE UNIQUE INDEX UX_IaPrompt_Activa ON ia.PromptVersion(Activa) WHERE Activa = 1;
GO

/* ============================ TARIFA DE VENTA Y PLANES (COP) ============================ */
-- "Valor del token" que paga el cliente. Global (EmpresaId NULL) con excepción por empresa.
-- Se valora cada llamada con la tarifa vigente en su fecha (queda guardada en ia.UsoLlamada).
CREATE TABLE ia.Tarifa (
    TarifaId     int IDENTITY(1,1) CONSTRAINT PK_IaTarifa PRIMARY KEY,
    EmpresaId    int NULL CONSTRAINT FK_IaTarifa_Empresa REFERENCES seg.Empresa,
    VigenteDesde date NOT NULL,
    ValorMTokEntradaCop      decimal(14,2) NOT NULL,   -- COP por 1M tokens de entrada
    ValorMTokSalidaCop       decimal(14,2) NOT NULL,   -- COP por 1M tokens de salida
    ValorMTokCacheLecturaCop decimal(14,2) NOT NULL,   -- COP por 1M tokens leídos de caché
    CreadaPor    int NULL,
    CreadaEn     datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE UNIQUE INDEX UX_IaTarifa ON ia.Tarifa(EmpresaId, VigenteDesde);

-- Plan comercial: define el cupo de IA incluido en el canon mensual del contrato
CREATE TABLE ia.PlanComercial (
    PlanId          smallint IDENTITY(1,1) CONSTRAINT PK_IaPlan PRIMARY KEY,
    Codigo          varchar(20)   NOT NULL CONSTRAINT UQ_IaPlan UNIQUE,
    Nombre          nvarchar(80)  NOT NULL,
    CupoIncluidoCop decimal(14,2) NOT NULL,       -- consumo valorado a tarifa incluido en el canon
    Activo          bit NOT NULL DEFAULT 1
);
GO

/* ============================ CONFIGURACIÓN POR EMPRESA ============================ */
CREATE TABLE ia.EmpresaConfig (
    EmpresaId          int NOT NULL CONSTRAINT PK_IaEmpresaConfig PRIMARY KEY CONSTRAINT FK_IaEC_Empresa REFERENCES seg.Empresa,
    Habilitado         bit NOT NULL DEFAULT 0,
    PlanId             smallint NOT NULL CONSTRAINT FK_IaEC_Plan REFERENCES ia.PlanComercial,
    CupoIncluidoCop    decimal(14,2) NULL,         -- NULL = el del plan; valor = pactado en el contrato
    -- Nivel 2: consumo adicional facturable sobre el cupo incluido
    ExcedenteAutorizado bit NOT NULL DEFAULT 0,
    TopeExcedenteCop   decimal(14,2) NULL,         -- máximo adicional del mes
    ExcedenteAutorizadoPor int NULL,
    ExcedenteAutorizadoEn  datetime2(0) NULL,
    -- Modelos
    ModeloPrincipalId  smallint NOT NULL CONSTRAINT FK_IaEC_ModP REFERENCES ia.Modelo,
    ModeloRespaldoId   smallint NULL CONSTRAINT FK_IaEC_ModR REFERENCES ia.Modelo,
    ModeloAhorroId     smallint NULL CONSTRAINT FK_IaEC_ModA REFERENCES ia.Modelo,
    AhorroDesdePct     tinyint NULL,               -- % del cupo a partir del cual se usa ModeloAhorroId
    -- Límites operativos
    TopeUsuarioDiarioCop decimal(14,2) NULL,
    MaxTokensSalidaPorLlamada int NOT NULL DEFAULT 2000,
    MaxIteracionesHerramientas tinyint NOT NULL DEFAULT 5,
    MaxTokensHistorial int NOT NULL DEFAULT 15000,
    TopeCostoProveedorUsd decimal(12,4) NOT NULL DEFAULT 100,   -- protección de plataforma (no visible al cliente)
    -- Visibilidad y retención
    VisibilidadCosto   varchar(6) NOT NULL DEFAULT 'ADMIN' CHECK (VisibilidadCosto IN ('ADMIN','TODOS')),
    GuardarTextoConversacion bit NOT NULL DEFAULT 0,
    RetencionDias      smallint NOT NULL DEFAULT 90,
    ActualizadoEn      datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_IaEC_Excedente CHECK (ExcedenteAutorizado = 0 OR (TopeExcedenteCop IS NOT NULL AND TopeExcedenteCop > 0)),
    CONSTRAINT CK_IaEC_Ahorro    CHECK ((ModeloAhorroId IS NULL AND AhorroDesdePct IS NULL) OR (ModeloAhorroId IS NOT NULL AND AhorroDesdePct BETWEEN 1 AND 100))
);

CREATE TABLE ia.RolPermitido (
    EmpresaId int NOT NULL CONSTRAINT FK_IaRP_Empresa REFERENCES seg.Empresa,
    RolId     int NOT NULL CONSTRAINT FK_IaRP_Rol REFERENCES seg.Rol(Id),
    CONSTRAINT PK_IaRolPermitido PRIMARY KEY (EmpresaId, RolId)
);

-- Consentimiento firmado por la empresa para un proveedor (obligatorio si RequiereConsentimientoEspecifico = 1)
CREATE TABLE ia.Consentimiento (
    ConsentimientoId int IDENTITY(1,1) CONSTRAINT PK_IaConsentimiento PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_IaCons_Empresa REFERENCES seg.Empresa,
    ProveedorId   smallint NOT NULL CONSTRAINT FK_IaCons_Prov REFERENCES ia.Proveedor,
    DocumentoVersion varchar(20) NOT NULL,         -- versión de 08_consentimiento_proveedor_ia.md
    DocumentoHash varbinary(32) NOT NULL,          -- SHA-256 del documento firmado
    DocumentoUrl  nvarchar(500) NOT NULL,          -- PDF firmado en Blob Storage
    FirmadoPor    nvarchar(150) NOT NULL,          -- representante legal
    FirmadoPorIdentificacion varchar(30) NOT NULL,
    FirmadoEn     datetime2(0) NOT NULL,
    RevocadoEn    datetime2(0) NULL
);
CREATE UNIQUE INDEX UX_IaCons_Vigente ON ia.Consentimiento(EmpresaId, ProveedorId) WHERE RevocadoEn IS NULL;
GO

/* ============================ CONSUMO ============================ */
CREATE TABLE ia.ConsumoPeriodo (
    EmpresaId        int NOT NULL CONSTRAINT FK_IaCP_Empresa REFERENCES seg.Empresa,
    PeriodoKey       int NOT NULL,
    CupoIncluidoCop  decimal(14,2) NOT NULL,       -- foto al abrir el periodo
    TokensEntrada    bigint NOT NULL DEFAULT 0,
    TokensSalida     bigint NOT NULL DEFAULT 0,
    TokensCache      bigint NOT NULL DEFAULT 0,
    ValorConsumoCop  decimal(16,2) NOT NULL DEFAULT 0,   -- a tarifa de venta
    CostoProveedorUsd decimal(14,6) NOT NULL DEFAULT 0,
    ReservadoCop     decimal(16,2) NOT NULL DEFAULT 0,
    ReservadoUsd     decimal(14,6) NOT NULL DEFAULT 0,
    EnExcedenteDesde datetime2(0) NULL,
    AlertaNivel      tinyint NOT NULL DEFAULT 0,
    CONSTRAINT PK_IaConsumoPeriodo PRIMARY KEY (EmpresaId, PeriodoKey)
);

CREATE TABLE ia.Reserva (
    ReservaId    uniqueidentifier NOT NULL CONSTRAINT PK_IaReserva PRIMARY KEY,
    EmpresaId    int NOT NULL,
    PeriodoKey   int NOT NULL,
    UsuarioId    int NOT NULL,
    MontoCop     decimal(16,2) NOT NULL,
    MontoUsd     decimal(14,6) NOT NULL,
    ExpiraEn     datetime2(0) NOT NULL
);
CREATE INDEX IX_IaReserva_Empresa ON ia.Reserva(EmpresaId, ExpiraEn);

CREATE TABLE ia.Conversacion (
    ConversacionId uniqueidentifier NOT NULL CONSTRAINT PK_IaConversacion PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    EmpresaId    int NOT NULL CONSTRAINT FK_IaConv_Empresa REFERENCES seg.Empresa,
    UsuarioId    int NOT NULL CONSTRAINT FK_IaConv_Usuario REFERENCES seg.Usuario(Id),
    Titulo       nvarchar(150) NULL,
    Dashboard    varchar(40) NULL,
    CreadaEn     datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    UltimoMensajeEn datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_IaConv_Usuario ON ia.Conversacion(EmpresaId, UsuarioId, UltimoMensajeEn DESC);

CREATE TABLE ia.Mensaje (
    MensajeId     bigint IDENTITY(1,1) CONSTRAINT PK_IaMensaje PRIMARY KEY,
    ConversacionId uniqueidentifier NOT NULL CONSTRAINT FK_IaMsg_Conv REFERENCES ia.Conversacion,
    EmpresaId     int NOT NULL,
    Rol           varchar(10) NOT NULL CHECK (Rol IN ('USUARIO','ASISTENTE')),
    Texto         nvarchar(max) NULL,
    ContextoJson  nvarchar(max) NULL,
    FuentesJson   nvarchar(max) NULL,
    ValorCop      decimal(14,2) NULL,             -- valor de la respuesta (se muestra al usuario si aplica)
    Valoracion    smallint NULL CHECK (Valoracion IN (-1, 1)),
    ComentarioValoracion nvarchar(500) NULL,
    CreadoEn      datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE ia.UsoLlamada (
    UsoId          bigint IDENTITY(1,1) CONSTRAINT PK_IaUso PRIMARY KEY,
    EmpresaId      int NOT NULL,
    UsuarioId      int NOT NULL,
    ConversacionId uniqueidentifier NULL,
    MensajeId      bigint NULL,
    ModeloId       smallint NOT NULL CONSTRAINT FK_IaUso_Modelo REFERENCES ia.Modelo,
    TarifaId       int NOT NULL CONSTRAINT FK_IaUso_Tarifa REFERENCES ia.Tarifa,
    Iteracion      tinyint NOT NULL,
    TokensEntrada  int NOT NULL,
    TokensSalida   int NOT NULL,
    TokensCacheLectura  int NOT NULL DEFAULT 0,
    TokensCacheEscritura int NOT NULL DEFAULT 0,
    CostoProveedorUsd decimal(14,6) NOT NULL,
    ValorVentaCop  decimal(14,2) NOT NULL,
    EnExcedente    bit NOT NULL DEFAULT 0,
    LatenciaMs     int NOT NULL,
    Herramientas   varchar(400) NULL,
    Resultado      varchar(15) NOT NULL CHECK (Resultado IN ('OK','ERROR','RECHAZO','CORTADO_TOPE','RESPALDO')),
    Fecha          datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_IaUso_EmpresaFecha ON ia.UsoLlamada(EmpresaId, Fecha) INCLUDE (UsuarioId, ValorVentaCop, CostoProveedorUsd);

-- Corte mensual: base del cobro del excedente
CREATE TABLE ia.CorteMensual (
    EmpresaId        int NOT NULL CONSTRAINT FK_IaCorte_Empresa REFERENCES seg.Empresa,
    PeriodoKey       int NOT NULL,
    CupoIncluidoCop  decimal(14,2) NOT NULL,
    ValorConsumoCop  decimal(16,2) NOT NULL,
    ExcedenteCop     decimal(16,2) NOT NULL,       -- a facturar
    CostoProveedorUsd decimal(14,6) NOT NULL,
    TrmUsdCop        decimal(12,2) NOT NULL,
    Estado           varchar(10) NOT NULL DEFAULT 'CERRADO' CHECK (Estado IN ('CERRADO','FACTURADO')),
    CerradoEn        datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_IaCorteMensual PRIMARY KEY (EmpresaId, PeriodoKey)
);
GO
