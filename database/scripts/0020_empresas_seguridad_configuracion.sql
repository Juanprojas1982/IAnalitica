/* 0020 · Empresas, seguridad propia y configuración. Requiere 0005 (seg.Usuario, seg.Rol de Identity). */
CREATE TABLE seg.Empresa (
    EmpresaId        int IDENTITY(1,1) CONSTRAINT PK_Empresa PRIMARY KEY,
    Codigo           varchar(20)   NOT NULL CONSTRAINT UQ_Empresa_Codigo UNIQUE,
    Nit              varchar(20)   NOT NULL CONSTRAINT UQ_Empresa_Nit UNIQUE,
    DigitoVerificacion char(1)     NULL,
    RazonSocial      nvarchar(200) NOT NULL,
    NombreComercial  nvarchar(150) NULL,
    TipoEntidad      varchar(30)   NOT NULL CHECK (TipoEntidad IN ('IPS','ESE','OPERADOR_FARMACEUTICO','LABORATORIO','CENTRO_DIAGNOSTICO')),
    Naturaleza       varchar(10)   NOT NULL DEFAULT 'PRIVADA' CHECK (Naturaleza IN ('PRIVADA','PUBLICA','MIXTA')),
    CodigoHabilitacion varchar(20) NULL,       -- REPS
    Direccion        nvarchar(200) NULL,
    Ciudad           nvarchar(100) NULL,
    Departamento     nvarchar(100) NULL,
    Telefono         varchar(30)   NULL,
    EmailContacto    nvarchar(256) NULL,
    SitioWeb         nvarchar(256) NULL,
    -- Marca (white-label del portal)
    LogoUrl          nvarchar(500) NULL,       -- Blob Storage; nunca binario en la BD
    LogoIconoUrl     nvarchar(500) NULL,
    ColorPrimario    char(7)       NOT NULL DEFAULT '#0B3D91' CHECK (ColorPrimario   LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'),
    ColorSecundario  char(7)       NOT NULL DEFAULT '#5B6B7F' CHECK (ColorSecundario LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'),
    -- Origen de datos
    SistemaOrigen    varchar(30)   NOT NULL DEFAULT 'SALUDIPS',
    VersionOrigen    varchar(30)   NULL,       -- dbo.cVersion de SaludIPS
    PaisIso          char(2)       NOT NULL DEFAULT 'CO',
    MonedaFuncional  char(3)       NOT NULL DEFAULT 'COP',
    ZonaHoraria      varchar(40)   NOT NULL DEFAULT 'SA Pacific Standard Time',
    HoraCarga        time(0)       NOT NULL DEFAULT '23:00',
    Activa           bit           NOT NULL DEFAULT 1,
    FechaVinculacion date          NOT NULL DEFAULT CAST(SYSUTCDATETIME() AS date),
    CreadaEn         datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE seg.EmpresaContacto (
    ContactoId  int IDENTITY(1,1) CONSTRAINT PK_EmpresaContacto PRIMARY KEY,
    EmpresaId   int NOT NULL CONSTRAINT FK_EC_Empresa REFERENCES seg.Empresa,
    Tipo        varchar(20) NOT NULL CHECK (Tipo IN ('REPRESENTANTE','GERENCIA','FINANCIERO','CONTADOR','TI','FACTURACION','COMERCIAL')),
    Nombre      nvarchar(150) NOT NULL,
    Cargo       nvarchar(100) NULL,
    Email       nvarchar(256) NOT NULL,
    Telefono    varchar(30) NULL,
    EsPrincipal bit NOT NULL DEFAULT 0,
    RecibeAlertas bit NOT NULL DEFAULT 0          -- alertas de carga fallida / KPI fuera de umbral
);

-- Módulos que la IPS usa realmente (evita dashboards vacíos que parecen error)
CREATE TABLE seg.EmpresaModulo (
    EmpresaId        int NOT NULL CONSTRAINT FK_EM_Empresa REFERENCES seg.Empresa,
    Modulo           varchar(20) NOT NULL CHECK (Modulo IN ('FINANCIERO','CICLO_INGRESO','CARTERA','TESORERIA','GLOSAS','AGENDA','DIAGNOSTICO','HOSPITALARIO',
                                                       'FARMACIA','INVENTARIO','NOMINA_COSTOS','ESE_PRESUPUESTO','AUDITOR_CONFIG','IA')),
    Activo           bit NOT NULL DEFAULT 1,
    FechaInicioDatos date NULL,                    -- desde cuándo hay datos confiables
    CONSTRAINT PK_EmpresaModulo PRIMARY KEY (EmpresaId, Modulo)
);

-- Usuarios y roles: ASP.NET Core Identity (seg.Usuario, seg.Rol, seg.UsuarioRol...) creados en el script 0005
-- Aquí solo las tablas propias que dependen de ellas.
CREATE TABLE seg.RolPermiso (
    RolId    int         NOT NULL CONSTRAINT FK_RolPermiso_Rol REFERENCES seg.Rol(Id),
    Permiso  varchar(60) NOT NULL,   -- dash.gerencial, dash.pyg, dash.cartera, reportes.exportar, admin.usuarios...
    CONSTRAINT PK_RolPermiso PRIMARY KEY (RolId, Permiso)
);

-- Restricción opcional por sede / centro de costo dentro de su empresa (aplicada en la capa de aplicación)
CREATE TABLE seg.UsuarioAlcance (
    UsuarioAlcanceId int IDENTITY(1,1) CONSTRAINT PK_UsuarioAlcance PRIMARY KEY,
    UsuarioId     int NOT NULL CONSTRAINT FK_UA_Usuario REFERENCES seg.Usuario(Id),
    EmpresaId     int NOT NULL CONSTRAINT FK_UA_Empresa REFERENCES seg.Empresa,
    SedeId        int NULL,
    CentroCostoId int NULL
);
-- Credenciales de la API: OpenIddict (0005) + seg.ApiClienteEmpresa (abajo).
GO

/* ============================ CONFIGURACIÓN ============================ */
-- Reglas que en SaludIPS están hardcodeadas por cliente (ej. copago como neto para ciertas EPS)
CREATE TABLE cfg.EmpresaParametro (
    EmpresaId  int NOT NULL CONSTRAINT FK_EP_Empresa REFERENCES seg.Empresa,
    Clave      varchar(60)   NOT NULL,   -- CARTERA.CODIGO_PARTICULAR, FACT.EPS_COPAGO_COMO_NETO, INV.COSTO_CON_IVA...
    Valor      nvarchar(500) NOT NULL,
    CONSTRAINT PK_EmpresaParametro PRIMARY KEY (EmpresaId, Clave)
);

CREATE TABLE cfg.KpiMeta (
    EmpresaId    int NOT NULL CONSTRAINT FK_KM_Empresa REFERENCES seg.Empresa,
    KpiCodigo    varchar(40) NOT NULL,   -- MARGEN_OPERACIONAL, DSO, PCT_GLOSA, RAZON_CORRIENTE, PCT_PENDIENTE_FACTURAR...
    PeriodoKey   int NOT NULL,
    Meta         decimal(19,4) NOT NULL,
    UmbralAlerta decimal(19,4) NULL,
    CONSTRAINT PK_KpiMeta PRIMARY KEY (EmpresaId, KpiCodigo, PeriodoKey)
);
GO
CREATE TABLE seg.HistorialClave (                         -- no repetir las últimas 5 (Identity no lo trae)
    UsuarioId int NOT NULL CONSTRAINT FK_HC_Usuario REFERENCES seg.Usuario(Id),
    HashClave nvarchar(max) NOT NULL,
    CreadoEn  datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_HC_Usuario ON seg.HistorialClave(UsuarioId, CreadoEn DESC);

-- Cliente OAuth (OpenIddict) ↔ empresa y límites de la API de ingesta
CREATE TABLE seg.ApiClienteEmpresa (
    ClientId        nvarchar(100) NOT NULL CONSTRAINT PK_ApiClienteEmpresa PRIMARY KEY,
    EmpresaId       int NOT NULL CONSTRAINT FK_ACE_Empresa REFERENCES seg.Empresa,
    Descripcion     nvarchar(150) NULL,
    LimitePorMinuto int NOT NULL DEFAULT 60,
    Activo          bit NOT NULL DEFAULT 1,
    UltimoUso       datetime2(0) NULL
);

/* ---------- 2. Módulos activos (reemplaza el CHECK de seg.EmpresaModulo del v2) ---------- */
-- Módulo = paquete de dashboards/funciones de salud. Lista cerrada:
--   FINANCIERO (P&G, balance, razones, flujo, presupuesto gerencial), CICLO_INGRESO, CARTERA, TESORERIA, GLOSAS,
--   AGENDA, DIAGNOSTICO, HOSPITALARIO, FARMACIA (dispensación, pendientes, precios máximos), INVENTARIO,
--   NOMINA_COSTOS, ESE_PRESUPUESTO, AUDITOR_CONFIG, IA
GO
CREATE TABLE seg.Notificacion (
    NotificacionId bigint IDENTITY(1,1) CONSTRAINT PK_Notificacion PRIMARY KEY,
    EmpresaId int NOT NULL CONSTRAINT FK_Notif_Empresa REFERENCES seg.Empresa,
    UsuarioId int NULL,                                  -- NULL = todos los administradores de la empresa
    Tipo varchar(30) NOT NULL,                           -- REPORTE_LISTO, CARGA_FALLIDA, HALLAZGO_CRITICO, CUPO_IA...
    Titulo nvarchar(200) NOT NULL, Enlace nvarchar(300) NULL,
    Leida bit NOT NULL DEFAULT 0, CreadaEn datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Notif_Usuario ON seg.Notificacion(EmpresaId, UsuarioId, Leida, CreadaEn DESC);
GO
CREATE TABLE cfg.Dashboard (
    DashboardId    int IDENTITY(1,1) CONSTRAINT PK_Dashboard PRIMARY KEY,
    Codigo         varchar(60) NOT NULL,
    Version        int NOT NULL DEFAULT 1,
    EmpresaId      int NULL CONSTRAINT FK_Dash_Empresa REFERENCES seg.Empresa,   -- NULL = de fábrica (global)
    Modulo         varchar(20) NOT NULL,
    Titulo         nvarchar(150) NOT NULL,
    Permiso        varchar(60) NOT NULL,
    DefinicionJson nvarchar(max) NOT NULL CONSTRAINT CK_Dash_Json CHECK (ISJSON(DefinicionJson) = 1),  -- forma de DefinicionDashboard
    Origen         varchar(12) NOT NULL CHECK (Origen IN ('FABRICA','PERSONALIZADO')),
    Activo         bit NOT NULL DEFAULT 1,
    ActualizadoEn  datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    ActualizadoPor int NULL
);
CREATE UNIQUE INDEX UX_Dashboard ON cfg.Dashboard(Codigo, Version, EmpresaId);

/* ---------- 4. Auditor de configuración (spec 16) ---------- */
CREATE TABLE cfg.ReglaConfiguracion (                    -- metadatos editables; la lógica vive en IReglaConfiguracion
    Codigo          varchar(10) NOT NULL CONSTRAINT PK_ReglaConfig PRIMARY KEY,   -- CON-01...
    Area            varchar(20) NOT NULL,
    SeveridadBase   varchar(8)  NOT NULL CHECK (SeveridadBase IN ('CRITICA','ALTA','MEDIA','BAJA')),
    Titulo          nvarchar(200) NOT NULL,
    PorQueImporta   nvarchar(800) NOT NULL,
    ComoCorregir    nvarchar(800) NOT NULL,
    RutaSaludIPS    nvarchar(200) NULL,                  -- "Facturación › Empresas › Cuentas contables"
    UmbralesJson    nvarchar(1000) NULL,
    VersionMinimaSaludIPS varchar(30) NULL,
    PorVerificar    bit NOT NULL DEFAULT 0,              -- reglas con supuestos [S]: salen como "por verificar"
    Activa          bit NOT NULL DEFAULT 1
);
CREATE TABLE cfg.ReglaConfiguracionEmpresa (             -- ajustes por empresa
    EmpresaId int NOT NULL CONSTRAINT FK_RCE_Empresa REFERENCES seg.Empresa,
    Codigo    varchar(10) NOT NULL CONSTRAINT FK_RCE_Regla REFERENCES cfg.ReglaConfiguracion,
    Activa    bit NOT NULL, SeveridadAjustada varchar(8) NULL, UmbralesJson nvarchar(1000) NULL,
    CONSTRAINT PK_ReglaConfigEmpresa PRIMARY KEY (EmpresaId, Codigo)
);
GO
