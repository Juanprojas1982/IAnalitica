/* =====================================================================
   DDL complementario y correcciones para la SPEC MAESTRA (24_SPEC_MAESTRA.md, §6)
   Tablas que no estaban en 01_v2, 07, 10 ni 15 (las correcciones ya están aplicadas en esos archivos)
   y la RLS final. Sonnet reparte este contenido en los scripts DbUp de 24_SPEC_MAESTRA §6.2.
   NO EJECUTADO.
   ===================================================================== */

/* ---------- 1. Identidad (ASP.NET Core Identity + OpenIddict) ----------
   Las tablas de Identity y OpenIddict se generan con EF Core en el esquema seg
   (dotnet ef migrations script → script DbUp 0001). Se mapean con estos nombres:
     IdentityUser<int>  → seg.Usuario      IdentityRole<int> → seg.Rol      UserRoles → seg.UsuarioRol
     UserClaims → seg.UsuarioClaim  UserLogins → seg.UsuarioLogin  UserTokens → seg.UsuarioToken  RoleClaims → seg.RolClaim
     OpenIddict → seg.OidcAplicacion, seg.OidcAutorizacion, seg.OidcAlcance, seg.OidcToken
   Columnas propias de seg.Usuario (UsuarioAplicacion): EmpresaId int NULL, EsSuperAdmin bit, EsAdminPrincipal bit,
   NombreCompleto nvarchar(150), DebeCambiarClave bit, ClaveTemporalVenceEn datetime2 NULL, Activo bit,
   CreadoEn datetime2, CreadoPor int NULL.
   REEMPLAZA a seg.Usuario, seg.Rol, seg.UsuarioEmpresa y seg.ApiCliente del esquema v2.                   */

-- Restricciones de negocio sobre la tabla de Identity (script 0021, después de 0001)
ALTER TABLE seg.Usuario ADD CONSTRAINT CK_Usuario_Empresa
    CHECK ((EsSuperAdmin = 1 AND EmpresaId IS NULL) OR (EsSuperAdmin = 0 AND EmpresaId IS NOT NULL));
ALTER TABLE seg.Usuario ADD CONSTRAINT FK_Usuario_Empresa FOREIGN KEY (EmpresaId) REFERENCES seg.Empresa(EmpresaId);
CREATE UNIQUE INDEX UX_Usuario_AdminPrincipal ON seg.Usuario(EmpresaId) WHERE EsAdminPrincipal = 1;   -- D27

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
-- En 0020: CHECK (Modulo IN (... lista anterior ...))
-- Además: seg.Empresa.TipoEntidad IN ('IPS','ESE','OPERADOR_FARMACEUTICO','LABORATORIO','CENTRO_DIAGNOSTICO')

/* ---------- 3. Dashboards declarativos (capa semántica) ---------- */
-- Las MÉTRICAS se definen en código (módulos, DefinicionMetrica). Los DASHBOARDS se guardan como JSON.
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
CREATE TABLE fact.HallazgoConfiguracion (
    EmpresaId       int NOT NULL CONSTRAINT FK_HCfg_Empresa REFERENCES seg.Empresa,
    HallazgoId      bigint IDENTITY(1,1) NOT NULL,
    Codigo          varchar(10) NOT NULL CONSTRAINT FK_HCfg_Regla REFERENCES cfg.ReglaConfiguracion,
    ClaveEntidad    nvarchar(200) NOT NULL,              -- p. ej. 'mEmpresas:45' (dedupe por regla + entidad)
    NombreEntidad   nvarchar(300) NULL,
    Severidad       varchar(8) NOT NULL,
    ValorEncontrado nvarchar(400) NULL,
    ValorEsperado   nvarchar(400) NULL,
    ImpactoCantidad int NULL,
    ImpactoValor    decimal(19,4) NULL,
    ImpactoDetalle  nvarchar(400) NULL,
    Estado          varchar(14) NOT NULL DEFAULT 'NUEVO' CHECK (Estado IN ('NUEVO','ABIERTO','CORREGIDO','ACEPTADO','FALSO_POSITIVO')),
    Justificacion   nvarchar(500) NULL,
    PrimeraDeteccion datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    UltimaDeteccion  datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CerradoEn       datetime2(0) NULL,
    CerradoPor      int NULL,
    CONSTRAINT PK_HallazgoConfiguracion PRIMARY KEY (EmpresaId, HallazgoId)
);
CREATE UNIQUE INDEX UX_HCfg_Abierto ON fact.HallazgoConfiguracion(EmpresaId, Codigo, ClaveEntidad)
    WHERE Estado IN ('NUEVO','ABIERTO');
-- Copia de configuración de SaludIPS: una tabla saludips.<Tabla> por cada tabla de la lista blanca (spec 16 §2),
-- con las mismas columnas MENOS las excluidas, más EmpresaId int NOT NULL y CargaId bigint NOT NULL.
-- PK = (EmpresaId, PK original). Carga FOTO: DELETE por empresa + INSERT dentro de una transacción. Sonnet las genera
-- desde el diccionario (Resultados1.csv) y la lista blanca.

/* ---------- 5. ESE / presupuesto público (spec 17) ---------- */
CREATE TABLE dim.FuenteFinanciacion (
    FuenteId     int IDENTITY(1,1) CONSTRAINT PK_FuenteFin PRIMARY KEY,
    EmpresaId    int NOT NULL CONSTRAINT FK_FF_Empresa REFERENCES seg.Empresa,
    CodigoOrigen varchar(30) NOT NULL, Nombre nvarchar(200) NOT NULL,
    CONSTRAINT UQ_FuenteFin UNIQUE (EmpresaId, CodigoOrigen)
);
CREATE TABLE dim.RubroPresupuestal (
    RubroId      int IDENTITY(1,1) CONSTRAINT PK_Rubro PRIMARY KEY,
    EmpresaId    int NOT NULL CONSTRAINT FK_Rubro_Empresa REFERENCES seg.Empresa,
    Vigencia     smallint NOT NULL,
    Codigo       varchar(60) NOT NULL, Nombre nvarchar(300) NOT NULL,
    Tipo         char(1) NOT NULL CHECK (Tipo IN ('I','G')),          -- ingreso / gasto
    Naturaleza   tinyint NULL,
    CodigoCuipo  varchar(60) NULL,
    PermiteMovimientos bit NOT NULL,
    CodigoPadre  varchar(60) NULL,
    CONSTRAINT UQ_Rubro UNIQUE (EmpresaId, Vigencia, Codigo)
);
CREATE TABLE fact.PresupuestoMovimiento (                -- gasto e ingreso en una sola tabla por etapa
    EmpresaId    int NOT NULL CONSTRAINT FK_PM_Empresa REFERENCES seg.Empresa,
    SourceKey    nvarchar(100) NOT NULL,
    Vigencia     smallint NOT NULL,
    FechaKey     int NOT NULL CONSTRAINT FK_PM_Fecha REFERENCES dim.Fecha,
    RubroId      int NOT NULL CONSTRAINT FK_PM_Rubro REFERENCES dim.RubroPresupuestal,
    FuenteId     int NULL CONSTRAINT FK_PM_Fuente REFERENCES dim.FuenteFinanciacion,
    Etapa        varchar(20) NOT NULL CHECK (Etapa IN ('APROPIACION_INICIAL','ADICION','REDUCCION','CREDITO','CONTRACREDITO',
                    'CDP','RP','OBLIGACION','PAGO','AFORO_INICIAL','RECONOCIMIENTO','RECAUDO')),
    Valor        decimal(19,4) NOT NULL,                  -- reversiones/notas con signo negativo
    Documento    varchar(40) NULL,
    TerceroId    int NULL CONSTRAINT FK_PM_Tercero REFERENCES dim.Tercero,
    CargaId      bigint NOT NULL CONSTRAINT FK_PM_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_PresupuestoMovimiento PRIMARY KEY NONCLUSTERED (EmpresaId, SourceKey)
);
CREATE CLUSTERED COLUMNSTORE INDEX CCI_PresupuestoMovimiento ON fact.PresupuestoMovimiento;
CREATE TABLE fact.ContratoPublico (
    EmpresaId    int NOT NULL CONSTRAINT FK_CP_Empresa REFERENCES seg.Empresa,
    SourceKey    nvarchar(100) NOT NULL,
    Numero       varchar(40) NOT NULL, Objeto nvarchar(500) NULL,
    TerceroId    int NULL CONSTRAINT FK_CP_Tercero REFERENCES dim.Tercero,
    FechaInicio  date NULL, FechaFin date NULL,
    ValorInicial decimal(19,4) NOT NULL, ValorAdiciones decimal(19,4) NOT NULL DEFAULT 0,
    ValorComprometido decimal(19,4) NULL, PolizaVence date NULL, Estado varchar(20) NULL,
    CargaId      bigint NOT NULL CONSTRAINT FK_CP_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_ContratoPublico PRIMARY KEY (EmpresaId, SourceKey)
);

/* ---------- 6. Cola de trabajos (spec 20) — copiar de 20_spec, §2 ---------- */
/* ---------- 7. Reportes en segundo plano (spec 21) — copiar de 21_spec, §3 ---------- */

/* ---------- 8. Auditoría de negocio (spec 19) ---------- */
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

/* ---------- 10. Notificaciones del portal ---------- */
CREATE TABLE seg.Notificacion (
    NotificacionId bigint IDENTITY(1,1) CONSTRAINT PK_Notificacion PRIMARY KEY,
    EmpresaId int NOT NULL CONSTRAINT FK_Notif_Empresa REFERENCES seg.Empresa,
    UsuarioId int NULL,                                  -- NULL = todos los administradores de la empresa
    Tipo varchar(30) NOT NULL,                           -- REPORTE_LISTO, CARGA_FALLIDA, HALLAZGO_CRITICO, CUPO_IA...
    Titulo nvarchar(200) NOT NULL, Enlace nvarchar(300) NULL,
    Leida bit NOT NULL DEFAULT 0, CreadaEn datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_Notif_Usuario ON seg.Notificacion(EmpresaId, UsuarioId, Leida, CreadaEn DESC);

/* ---------- 12. RLS consolidada (script 0900, al final) ---------- */
GO
CREATE OR ALTER FUNCTION seg.fn_FiltroEmpresa (@EmpresaId int)
RETURNS TABLE WITH SCHEMABINDING
AS RETURN
    SELECT 1 AS Permitido
    WHERE IS_MEMBER(N'rol_etl') = 1
       OR EXISTS (SELECT 1 FROM seg.Usuario u
                  WHERE u.Id = CAST(SESSION_CONTEXT(N'UsuarioId') AS int) AND u.Activo = 1
                    AND (u.EsSuperAdmin = 1 OR u.EmpresaId = @EmpresaId));
GO
-- Variante para tablas con EmpresaId NULL = global (ia.Tarifa, cfg.Dashboard): lo global es visible para todos;
-- solo el superadmin escribe lo global (BLOCK predicate usa fn_FiltroEmpresa).
CREATE OR ALTER FUNCTION seg.fn_FiltroEmpresaOGlobal (@EmpresaId int)
RETURNS TABLE WITH SCHEMABINDING
AS RETURN
    SELECT 1 AS Permitido WHERE @EmpresaId IS NULL
    UNION ALL
    SELECT 1 FROM seg.fn_FiltroEmpresa(@EmpresaId);
GO
-- Variante para auditoría (EmpresaId NULL = evento global): NULL solo lo ve el superadmin.
CREATE OR ALTER FUNCTION seg.fn_FiltroAuditoria (@EmpresaId int)
RETURNS TABLE WITH SCHEMABINDING
AS RETURN
    SELECT 1 AS Permitido
    WHERE IS_MEMBER(N'rol_etl') = 1
       OR EXISTS (SELECT 1 FROM seg.Usuario u
                  WHERE u.Id = CAST(SESSION_CONTEXT(N'UsuarioId') AS int) AND u.Activo = 1
                    AND (u.EsSuperAdmin = 1 OR (@EmpresaId IS NOT NULL AND u.EmpresaId = @EmpresaId AND u.EsAdminPrincipal = 1)));
GO
CREATE ROLE rol_etl;     -- worker / procesos de carga (bypass de RLS)
CREATE ROLE rol_app;     -- web y API: SELECT/INSERT/UPDATE en negocio; solo SELECT/INSERT en aud.*
GO
/* Política única seg.PolTenant (dinámica: se genera con STRING_AGG sobre sys.tables, mismo patrón que tenía v2) sobre TODAS las tablas con columna EmpresaId en los esquemas
   seg, cfg, etl, stg, dim, fact, ia, rpt, reg, saludips; EXCLUYE seg.Usuario (lo lee el predicado) y crm.*.
   - fn_FiltroEmpresaOGlobal  para: ia.Tarifa, cfg.Dashboard (FILTER); BLOCK con fn_FiltroEmpresa.
   - fn_FiltroAuditoria        para: aud.CambioAdministrativo, aud.Seguridad (FILTER).
   - aud.AccesoSuperadmin: solo superadmin (sin acceso para empresas).
   - El resto: fn_FiltroEmpresa (FILTER + BLOCK AFTER INSERT + BLOCK AFTER UPDATE). */
