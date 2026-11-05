/* 0110 · Reportes en segundo plano (spec 21), auditoría de negocio (spec 19) y prospectos (crm). */
CREATE TABLE rpt.SolicitudReporte (
    SolicitudId    uniqueidentifier NOT NULL CONSTRAINT PK_SolicitudReporte PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
    EmpresaId      int NOT NULL CONSTRAINT FK_SolRep_Empresa REFERENCES seg.Empresa,
    UsuarioId      int NOT NULL,
    TipoReporte    varchar(60) NOT NULL,          -- DASHBOARD_PDF, INFORME_GERENCIAL, HALLAZGOS_XLSX, ESTADO_CUENTA_IA...
    Formato        varchar(5)  NOT NULL CHECK (Formato IN ('PDF','XLSX','CSV')),
    ParametrosJson nvarchar(2000) NOT NULL,       -- filtros (periodo, sede...), validados contra lista blanca
    HashParametros char(64) NOT NULL,             -- para reutilizar un resultado idéntico reciente
    Estado         varchar(12) NOT NULL DEFAULT 'PENDIENTE' CHECK (Estado IN ('PENDIENTE','EN_PROCESO','LISTO','ERROR','CANCELADO','EXPIRADO')),
    Progreso       tinyint NULL,                  -- 0-100, opcional
    BlobRuta       nvarchar(400) NULL,
    TamanoBytes    bigint NULL,
    VersionCarga   bigint NULL,                   -- datos con los que se generó
    SolicitadoEn   datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    IniciadoEn     datetime2(0) NULL,
    TerminadoEn    datetime2(0) NULL,
    ExpiraEn       datetime2(0) NULL,             -- p. ej. 7 días después de LISTO
    Error          nvarchar(500) NULL
);
CREATE INDEX IX_SolRep_Usuario ON rpt.SolicitudReporte (EmpresaId, UsuarioId, SolicitadoEn DESC);
GO
CREATE TABLE aud.Acceso (
    AccesoId   bigint IDENTITY(1,1) CONSTRAINT PK_Acceso PRIMARY KEY,
    UsuarioId  int NOT NULL,
    EmpresaId  int NULL,
    Accion     varchar(60) NOT NULL,
    Detalle    nvarchar(500) NULL,
    Ip         varchar(45) NULL,
    Fecha      datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE TABLE aud.CambioAdministrativo (
    CambioId     bigint IDENTITY(1,1) CONSTRAINT PK_CambioAdm PRIMARY KEY,
    EmpresaId    int NULL,                                -- NULL = cambio global (superadmin)
    UsuarioId    int NOT NULL,
    Entidad      varchar(60) NOT NULL,                    -- 'ia.Tarifa', 'seg.Usuario', 'fact.HallazgoConfiguracion'...
    ClaveEntidad nvarchar(200) NOT NULL,
    Accion       varchar(20) NOT NULL,                    -- CREAR, MODIFICAR, DESACTIVAR, RESTABLECER_CLAVE...
    ValorAnteriorJson nvarchar(max) NULL,
    ValorNuevoJson    nvarchar(max) NULL,
    Motivo       nvarchar(500) NULL,
    Ip           varchar(45) NULL,
    Fecha        datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE TABLE aud.AccesoSuperadmin (
    AccesoId  bigint IDENTITY(1,1) CONSTRAINT PK_AccesoSA PRIMARY KEY,
    UsuarioId int NOT NULL, EmpresaId int NOT NULL, Motivo nvarchar(300) NOT NULL,
    Inicio    datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(), Fin datetime2(0) NULL
);
CREATE TABLE aud.Seguridad (
    EventoId  bigint IDENTITY(1,1) CONSTRAINT PK_AudSeg PRIMARY KEY,
    EmpresaId int NULL, UsuarioId int NULL, NombreUsuario nvarchar(256) NULL,
    Evento    varchar(40) NOT NULL,                       -- LOGIN_FALLIDO, BLOQUEO, MFA_ACTIVADO, MFA_DESACTIVADO, API_IP_NUEVA...
    Detalle   nvarchar(400) NULL, Ip varchar(45) NULL,
    Fecha     datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
-- Permisos: el login de la app tiene solo INSERT y SELECT sobre aud.* (DENY UPDATE, DELETE).

/* ---------- 9. Prospectos de la landing (D06) ---------- */
-- esquema crm: se crea en el script 0010.
CREATE TABLE crm.Prospecto (
    ProspectoId bigint IDENTITY(1,1) CONSTRAINT PK_Prospecto PRIMARY KEY,
    Nombre nvarchar(150) NOT NULL, Cargo nvarchar(100) NULL, Institucion nvarchar(200) NOT NULL, Nit varchar(20) NULL,
    Ciudad nvarchar(100) NULL, Sedes smallint NULL, HisActual nvarchar(100) NULL,
    Correo nvarchar(256) NOT NULL, Telefono varchar(30) NULL,
    AutorizaDatos bit NOT NULL CHECK (AutorizaDatos = 1), AutorizaComercial bit NOT NULL,
    VersionPolitica varchar(30) NOT NULL, TextoAutorizacionHash char(64) NOT NULL, Ip varchar(45) NULL,
    Estado varchar(15) NOT NULL DEFAULT 'NUEVO', CreadoEn datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
