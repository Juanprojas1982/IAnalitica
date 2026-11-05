/* REEMPLAZADO (24_SPEC_MAESTRA §6.1, rev. 1.1): el DDL vigente y verificado está en database/scripts. Este archivo se conserva solo como registro del diseño. */
/* =====================================================================
   Plataforma analítica contable multiempresa - Sector salud   (v2)
   Motor: SQL Server 2022 / Azure SQL Database
   Modelo: estrella multi-tenant (1 BD, EmpresaId en toda tabla de negocio)
   Aislamiento: Row-Level Security por SESSION_CONTEXT('UsuarioId')
   Fuente principal: SaludIPS (SIIFNet + facturación + almacén)

   Script vigente según 24_SPEC_MAESTRA.md (§6). Contiene: esquemas (DbUp 0010), seg/cfg (0020),
   etl/stg (0030), dimensiones (0040), hechos financieros (0050), dim.Fecha (0130), cartera (0140) y vistas (0150).
   Identity/OpenIddict (0001) se crean ANTES; la RLS (0900) se crea al FINAL, en 23_ddl_complementario.sql.
   ===================================================================== */
-- CREATE DATABASE SaludAnalytics COLLATE Modern_Spanish_CI_AS;
-- GO
-- USE SaludAnalytics;
-- GO
CREATE SCHEMA seg;
GO
CREATE SCHEMA cfg;
GO
CREATE SCHEMA etl;
GO
CREATE SCHEMA stg;
GO
CREATE SCHEMA dim;
GO
CREATE SCHEMA fact;
GO
CREATE SCHEMA rpt;
GO
CREATE SCHEMA aud;
GO
CREATE SCHEMA ia;
GO
CREATE SCHEMA reg;
GO
CREATE SCHEMA saludips;
GO
CREATE SCHEMA crm;
GO

/* ============================ SEGURIDAD / EMPRESAS ============================ */
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

-- Usuarios y roles: ASP.NET Core Identity (seg.Usuario, seg.Rol, seg.UsuarioRol...) creados en el script 0001
-- (ver 23_ddl_complementario.sql §1). Aquí solo las tablas propias que dependen de ellas.
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
-- Credenciales de la API: OpenIddict (0001) + seg.ApiClienteEmpresa (23_ddl_complementario.sql §1).
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

/* ============================ ETL / CONTROL ============================ */
CREATE TABLE etl.Carga (
    CargaId         bigint IDENTITY(1,1) CONSTRAINT PK_Carga PRIMARY KEY,
    EmpresaId       int           NOT NULL CONSTRAINT FK_Carga_Empresa REFERENCES seg.Empresa,
    Entidad         varchar(40)   NOT NULL,
    Modo            varchar(12)   NOT NULL DEFAULT 'VENTANA' CHECK (Modo IN ('INICIAL','VENTANA','INCREMENTAL','REPROCESO')),
    VentanaDesde    date          NULL,      -- VENTANA: se reemplaza todo lo del rango (captura anulaciones y ediciones)
    VentanaHasta    date          NULL,
    Origen          varchar(60)   NULL,
    VersionAgente   varchar(20)   NULL,
    HashPayload     varbinary(32) NULL,
    Estado          varchar(15)   NOT NULL DEFAULT 'RECIBIDA' CHECK (Estado IN ('RECIBIDA','PROCESANDO','OK','ERROR','PARCIAL')),
    FilasRecibidas  int NULL, FilasInsertadas int NULL, FilasActualizadas int NULL, FilasEliminadas int NULL, FilasRechazadas int NULL,
    IniciadaEn      datetime2(0)  NOT NULL DEFAULT SYSUTCDATETIME(),
    FinalizadaEn    datetime2(0)  NULL,
    Error           nvarchar(2000) NULL
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

CREATE TABLE stg.Payload (
    PayloadId  bigint IDENTITY(1,1) CONSTRAINT PK_Payload PRIMARY KEY,
    CargaId    bigint NOT NULL CONSTRAINT FK_Payload_Carga REFERENCES etl.Carga,
    EmpresaId  int NOT NULL,
    Entidad    varchar(40) NOT NULL,
    Json       nvarchar(max) NOT NULL CONSTRAINT CK_Payload_Json CHECK (ISJSON(Json) = 1)
);
GO

/* ============================ DIMENSIONES GLOBALES ============================ */
CREATE TABLE dim.Fecha (
    FechaKey    int  NOT NULL CONSTRAINT PK_Fecha PRIMARY KEY,   -- yyyymmdd
    Fecha       date NOT NULL CONSTRAINT UQ_Fecha UNIQUE,
    Anio        smallint NOT NULL, Trimestre tinyint NOT NULL, Mes tinyint NOT NULL,
    PeriodoKey  int NOT NULL,
    NombreMes   nvarchar(12) NOT NULL,
    SemanaIso   tinyint NOT NULL,
    DiaSemana   tinyint NOT NULL,                                -- 1 = lunes
    EsFinDeMes  bit NOT NULL,
    EsHabil     bit NOT NULL                                     -- L-V; festivos se marcan aparte
);

CREATE TABLE dim.CuentaHomologada (
    CuentaEstandarId int IDENTITY(1,1) CONSTRAINT PK_CuentaHomologada PRIMARY KEY,
    Codigo        varchar(20)   NOT NULL CONSTRAINT UQ_CuentaHom_Codigo UNIQUE,
    Nombre        nvarchar(200) NOT NULL,
    Clasificacion varchar(12)   NOT NULL CHECK (Clasificacion IN ('ACTIVO','PASIVO','PATRIMONIO','INGRESO','GASTO','COSTO_VENTA','COSTO_PRODUCCION','ORDEN')),
    GrupoEstadoResultados varchar(40) NULL,   -- INGRESOS_OPERACIONALES, COSTO_PRESTACION, GASTOS_ADMON, GASTOS_VENTAS, OTROS_INGRESOS, OTROS_GASTOS, IMPUESTOS
    GrupoBalance  varchar(40) NULL,           -- ACTIVO_CORRIENTE, PASIVO_CORRIENTE... (razones financieras)
    Nivel         tinyint NOT NULL,
    PadreId       int NULL CONSTRAINT FK_CuentaHom_Padre REFERENCES dim.CuentaHomologada
);

CREATE TABLE dim.MotivoGlosa (
    MotivoGlosaId smallint IDENTITY(1,1) CONSTRAINT PK_MotivoGlosa PRIMARY KEY,
    Codigo    varchar(10)   NOT NULL CONSTRAINT UQ_MotivoGlosa UNIQUE,
    Categoria varchar(40)   NOT NULL,
    Nombre    nvarchar(200) NOT NULL
);
GO

/* ============================ DIMENSIONES POR EMPRESA ============================ */
-- Convención: CodigoOrigen = llave natural en la fuente. Únicas por (EmpresaId, CodigoOrigen).
CREATE TABLE dim.Sede (
    SedeId        int IDENTITY(1,1) CONSTRAINT PK_Sede PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_Sede_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(30) NOT NULL,                 -- SIIFNet.Sedes.Id
    Nombre        nvarchar(150) NOT NULL,
    Ciudad        nvarchar(100) NULL,
    Activa        bit NOT NULL DEFAULT 1,
    CONSTRAINT UQ_Sede UNIQUE (EmpresaId, CodigoOrigen)
);

CREATE TABLE dim.Almacen (
    AlmacenId     int IDENTITY(1,1) CONSTRAINT PK_Almacen PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_Almacen_Empresa REFERENCES seg.Empresa,
    SedeId        int NULL CONSTRAINT FK_Almacen_Sede REFERENCES dim.Sede,
    CodigoOrigen  varchar(30) NOT NULL,                 -- Almacen.Bodegas.Codigo
    Nombre        nvarchar(150) NOT NULL,
    Activo        bit NOT NULL DEFAULT 1,
    CONSTRAINT UQ_Almacen UNIQUE (EmpresaId, CodigoOrigen)
);

CREATE TABLE dim.LineaServicio (                        -- pTipoServicio / unidad funcional
    LineaServicioId int IDENTITY(1,1) CONSTRAINT PK_LineaServicio PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_LS_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(30) NOT NULL,
    Nombre        nvarchar(120) NOT NULL,
    CONSTRAINT UQ_LS UNIQUE (EmpresaId, CodigoOrigen)
);

CREATE TABLE dim.CentroCosto (                          -- SIIFNet.CentrosCostos (vigencia más reciente por código)
    CentroCostoId int IDENTITY(1,1) CONSTRAINT PK_CentroCosto PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_CC_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(30) NOT NULL,                 -- Codigo
    Nombre        nvarchar(150) NOT NULL,
    CodigoPadre   varchar(30) NULL,
    Nivel         tinyint NOT NULL DEFAULT 1,
    TipoCC        varchar(15) NOT NULL DEFAULT 'PRODUCTIVO' CHECK (TipoCC IN ('PRODUCTIVO','APOYO','ADMINISTRATIVO')),
    ManejaMovimientos bit NOT NULL DEFAULT 1,
    LineaServicioId int NULL CONSTRAINT FK_CC_LS REFERENCES dim.LineaServicio,
    SedeId        int NULL CONSTRAINT FK_CC_Sede REFERENCES dim.Sede,
    Activo        bit NOT NULL DEFAULT 1,
    CONSTRAINT UQ_CC UNIQUE (EmpresaId, CodigoOrigen)
);

CREATE TABLE dim.CuentaContable (                       -- SIIFNet.Cuentas NormatividadId = 1, una fila por Codigo
    CuentaId      int IDENTITY(1,1) CONSTRAINT PK_CuentaContable PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_Cuenta_Empresa REFERENCES seg.Empresa,
    Codigo        varchar(25) NOT NULL,
    Nombre        nvarchar(200) NOT NULL,
    ClasePuc      AS CAST(LEFT(Codigo, 1) AS char(1)) PERSISTED,
    Nivel         tinyint NOT NULL,
    CodigoPadre   varchar(25) NULL,
    Naturaleza    char(1) NOT NULL CHECK (Naturaleza IN ('D','C')),   -- SaludIPS: 0 = D, 1 = C
    MovimientoContrario bit NOT NULL DEFAULT 0,                       -- invierte el signo del saldo
    DetalleCartera tinyint NOT NULL DEFAULT 0 CHECK (DetalleCartera IN (0,1,2)),  -- 0 no, 1 CxP, 2 CxC
    EsBanco       bit NOT NULL DEFAULT 0,                             -- SaludIPS UsoCuenta = 2
    ManejaCentroCosto bit NOT NULL DEFAULT 0,
    AceptaMovimiento  bit NOT NULL DEFAULT 1,
    CuentaEstandarId int NULL CONSTRAINT FK_Cuenta_Hom REFERENCES dim.CuentaHomologada,
    CONSTRAINT UQ_Cuenta UNIQUE (EmpresaId, Codigo)
);

CREATE TABLE dim.TipoComprobante (                      -- SIIFNet.TiposComprobantes
    TipoComprobanteId int IDENTITY(1,1) CONSTRAINT PK_TipoComprobante PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_TC_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(20) NOT NULL,                 -- TiposComprobantes.Id
    Prefijo       varchar(20) NOT NULL,                 -- CSI0, CDC0, TEB... (puede variar por IPS)
    Nombre        nvarchar(150) NOT NULL,
    Clase         varchar(15) NOT NULL DEFAULT 'NORMAL' CHECK (Clase IN ('NORMAL','SALDO_INICIAL','CIERRE','CIERRE_COSTOS','TRASLADO','NOMINA','FACTURA','OTRO')),
    CONSTRAINT UQ_TC UNIQUE (EmpresaId, CodigoOrigen)
);

CREATE TABLE dim.Tercero (                              -- SIIFNet.Terceros; un tercero puede tener varios roles
    TerceroId     int IDENTITY(1,1) CONSTRAINT PK_Tercero PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_Tercero_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(30) NOT NULL,                 -- SIIFNet.Terceros.Id
    TipoPersona   char(1) NOT NULL DEFAULT 'J' CHECK (TipoPersona IN ('N','J')),
    Identificacion varchar(64) NOT NULL,                -- persona natural paciente: hash SHA-256 salado
    Nombre        nvarchar(250) NULL,                   -- paciente: NULL
    EsPagador     bit NOT NULL DEFAULT 0,
    EsProveedor   bit NOT NULL DEFAULT 0,
    EsPaciente    bit NOT NULL DEFAULT 0,
    EsEmpleado    bit NOT NULL DEFAULT 0,
    CONSTRAINT UQ_Tercero UNIQUE (EmpresaId, CodigoOrigen),
    CONSTRAINT CK_Tercero_Paciente CHECK (EsPaciente = 0 OR Nombre IS NULL)
);

CREATE TABLE dim.Pagador (                              -- dbo.mEmpresas (EPS, aseguradoras, particulares)
    PagadorId     int IDENTITY(1,1) CONSTRAINT PK_Pagador PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_Pagador_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(30) NOT NULL,                 -- mEmpresas.IdEmpresa
    Nombre        nvarchar(200) NOT NULL,
    TerceroId     int NULL CONSTRAINT FK_Pagador_Tercero REFERENCES dim.Tercero,
    TipoPagador   varchar(25) NOT NULL DEFAULT 'OTRO',  -- EPS, ARL, SOAT_ADRES, PREPAGADA, PARTICULAR, ENTE_TERRITORIAL, OTRO
    Regimen       varchar(20) NULL,
    CONSTRAINT UQ_Pagador UNIQUE (EmpresaId, CodigoOrigen)
);

CREATE TABLE dim.Contrato (                             -- dbo.mContratos
    ContratoId    int IDENTITY(1,1) CONSTRAINT PK_Contrato PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_Contrato_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(30) NOT NULL,
    NumeroContrato varchar(40) NOT NULL,
    PagadorId     int NULL CONSTRAINT FK_Contrato_Pagador REFERENCES dim.Pagador,
    Modalidad     varchar(12) NOT NULL DEFAULT 'EVENTO' CHECK (Modalidad IN ('EVENTO','CAPITACION','PGP','PAQUETE','OTRO')),
    FechaInicio   date NULL,
    FechaFin      date NULL,
    ValorContrato decimal(19,4) NULL,
    CONSTRAINT UQ_Contrato UNIQUE (EmpresaId, CodigoOrigen)
);

CREATE TABLE dim.Servicio (                             -- CUPS / procedimientos / estancias (rCups, pProcedimientos)
    ServicioId    int IDENTITY(1,1) CONSTRAINT PK_Servicio PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_Servicio_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(40) NOT NULL,
    Nombre        nvarchar(250) NOT NULL,
    Tipo          varchar(20) NOT NULL,                 -- CONSULTA, PROCEDIMIENTO, ESTANCIA, MEDICAMENTO, INSUMO, PAQUETE
    LineaServicioId int NULL CONSTRAINT FK_Servicio_LS REFERENCES dim.LineaServicio,
    CONSTRAINT UQ_Servicio UNIQUE (EmpresaId, CodigoOrigen)
);

CREATE TABLE dim.Articulo (                             -- Almacen.ArticulosBase (+ línea/grupo/subgrupo)
    ArticuloId    int IDENTITY(1,1) CONSTRAINT PK_Articulo PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_Articulo_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(40) NOT NULL,
    Nombre        nvarchar(250) NOT NULL,
    Linea         nvarchar(100) NULL,
    Grupo         nvarchar(100) NULL,
    SubGrupo      nvarchar(100) NULL,
    Generico      nvarchar(150) NULL,
    Laboratorio   nvarchar(150) NULL,
    UnidadMedida  nvarchar(40) NULL,
    CONSTRAINT UQ_Articulo UNIQUE (EmpresaId, CodigoOrigen)
);

CREATE TABLE dim.Lote (                                 -- Almacen.Lotes (+ RegistrosInvima)
    LoteId        int IDENTITY(1,1) CONSTRAINT PK_Lote PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_Lote_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(30) NOT NULL,
    ArticuloId    int NOT NULL CONSTRAINT FK_Lote_Articulo REFERENCES dim.Articulo,
    NumeroLote    varchar(40) NOT NULL,
    FechaVencimiento date NULL,
    RegistroSanitario varchar(60) NULL,
    CONSTRAINT UQ_Lote UNIQUE (EmpresaId, CodigoOrigen)
);

CREATE TABLE dim.Especialidad (                          -- rReferencias clase 8
    EspecialidadId int IDENTITY(1,1) CONSTRAINT PK_Especialidad PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_Esp_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(20) NOT NULL,
    Nombre        nvarchar(120) NOT NULL,
    CONSTRAINT UQ_Especialidad UNIQUE (EmpresaId, CodigoOrigen)
);

CREATE TABLE dim.Profesional (                          -- dbo.mPersonal (médicos)
    ProfesionalId int IDENTITY(1,1) CONSTRAINT PK_Profesional PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_Prof_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(30) NOT NULL,
    Nombre        nvarchar(150) NOT NULL,
    Especialidad  nvarchar(120) NULL,
    EspecialidadId int NULL CONSTRAINT FK_Prof_Esp REFERENCES dim.Especialidad,
    TerceroCodigo varchar(30) NULL,                     -- mPersonal.TerceroId: enlace con nómina (costo por hora)
    Activo        bit NOT NULL DEFAULT 1,
    CONSTRAINT UQ_Profesional UNIQUE (EmpresaId, CodigoOrigen)
);

CREATE TABLE dim.UsuarioOperativo (                     -- dbo.cUsuarios (smallint) y AspNetUsers (guid)
    UsuarioOpId   int IDENTITY(1,1) CONSTRAINT PK_UsuarioOp PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_UOp_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(40) NOT NULL,                 -- IdcUsuario
    CodigoOrigenAlterno varchar(40) NULL,               -- UsuarioID guid de SIIFNet
    Nombre        nvarchar(150) NOT NULL,
    Activo        bit NOT NULL DEFAULT 1,
    CONSTRAINT UQ_UOp UNIQUE (EmpresaId, CodigoOrigen)
);
CREATE UNIQUE INDEX UX_UOp_Alterno ON dim.UsuarioOperativo(EmpresaId, CodigoOrigenAlterno) WHERE CodigoOrigenAlterno IS NOT NULL;

CREATE TABLE dim.CuentaBancaria (                       -- SIIFNet.BancosCuentas y dbo.pCajas
    CuentaBancariaId int IDENTITY(1,1) CONSTRAINT PK_CuentaBancaria PRIMARY KEY,
    EmpresaId     int NOT NULL CONSTRAINT FK_CB_Empresa REFERENCES seg.Empresa,
    CodigoOrigen  varchar(30) NOT NULL,                 -- 'B:' + Id banco | 'C:' + CodCaja
    Tipo          varchar(10) NOT NULL CHECK (Tipo IN ('BANCO','CAJA')),
    Entidad       nvarchar(100) NOT NULL,               -- banco o nombre de caja
    NumeroEnmascarado varchar(20) NULL,
    CuentaId      int NULL CONSTRAINT FK_CB_Cuenta REFERENCES dim.CuentaContable,
    CONSTRAINT UQ_CB UNIQUE (EmpresaId, CodigoOrigen)
);
GO

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
    FechaRadicacionKey int NULL CONSTRAINT FK_FL_FechaR REFERENCES dim.Fecha,
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
    CostoDirecto    decimal(19,4) NULL,                 -- mServicios.VrCosto
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
    Estado          varchar(15) NOT NULL DEFAULT 'ABIERTA',
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

/* ============================ AUDITORÍA ============================ */
CREATE TABLE aud.Acceso (
    AccesoId   bigint IDENTITY(1,1) CONSTRAINT PK_Acceso PRIMARY KEY,
    UsuarioId  int NOT NULL,
    EmpresaId  int NULL,
    Accion     varchar(60) NOT NULL,
    Detalle    nvarchar(500) NULL,
    Ip         varchar(45) NULL,
    Fecha      datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

/* ============================ dim.Fecha 2015-2035 ============================ */
WITH n AS (
    SELECT TOP (DATEDIFF(DAY, '2015-01-01', '2035-12-31') + 1)
           ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT dim.Fecha (FechaKey, Fecha, Anio, Trimestre, Mes, PeriodoKey, NombreMes, SemanaIso, DiaSemana, EsFinDeMes, EsHabil)
SELECT YEAR(x.d) * 10000 + MONTH(x.d) * 100 + DAY(x.d), x.d, YEAR(x.d), DATEPART(QUARTER, x.d), MONTH(x.d),
       YEAR(x.d) * 100 + MONTH(x.d),
       CHOOSE(MONTH(x.d), N'Enero', N'Febrero', N'Marzo', N'Abril', N'Mayo', N'Junio', N'Julio', N'Agosto', N'Septiembre', N'Octubre', N'Noviembre', N'Diciembre'),
       DATEPART(ISO_WEEK, x.d), x.dow,
       CASE WHEN x.d = EOMONTH(x.d) THEN 1 ELSE 0 END,
       CASE WHEN x.dow <= 5 THEN 1 ELSE 0 END
FROM n
CROSS APPLY (SELECT DATEADD(DAY, n.i, CAST('2015-01-01' AS date)) AS d) d0
CROSS APPLY (SELECT d0.d AS d, (DATEPART(WEEKDAY, d0.d) + @@DATEFIRST - 2) % 7 + 1 AS dow) x;
GO

/* RLS: roles de BD, funciones de filtro y política única → 23_ddl_complementario.sql §12 (script 0900, al final). */

/* ============================ CÁLCULO DE CARTERA ============================ */
-- Réplica de CARTERAESTADOCUENTAEDADES / CUENTASXPAGARESTADOCUENTAEDADES de SaludIPS:
--  origen  = línea confirmada en cuenta con DetalleCartera (2 CxC, 1 CxP) con valor del lado de su naturaleza
--  abonos  = líneas que la referencian (RelacionadoSourceKey), misma cuenta y tercero, lado contrario
--            menos sus reversiones (líneas que referencian a esos abonos), mismo lado de la naturaleza
--  se excluyen como origen las líneas que referencian a un abono (reversiones)
--  edad    = días desde FechaCartera (o fecha del movimiento) al corte
CREATE OR ALTER PROCEDURE etl.usp_GenerarCorteCartera
    @EmpresaId int,
    @Corte     date,
    @CargaId   bigint = NULL
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;
    DECLARE @CorteKey int = YEAR(@Corte) * 10000 + MONTH(@Corte) * 100 + DAY(@Corte);

    SELECT a.SourceKey, a.RelacionadoSourceKey, a.FechaKey, a.NumeroComprobante, a.NumDocumento,
           a.CuentaId, a.TerceroId, a.SedeId, a.CentroCostoId, a.Debito, a.Credito,
           a.FechaCartera, a.FechaVencimiento, c.Naturaleza, c.DetalleCartera
    INTO #lin
    FROM fact.AsientoLinea a
    JOIN dim.CuentaContable c ON c.CuentaId = a.CuentaId
    WHERE a.EmpresaId = @EmpresaId AND a.Estado = 1 AND a.FechaKey <= @CorteKey;

    CREATE CLUSTERED INDEX CX ON #lin (SourceKey);
    CREATE INDEX IX_Rel ON #lin (RelacionadoSourceKey) INCLUDE (CuentaId, TerceroId, Debito, Credito);

    -- abonos directos (TP)
    SELECT o.SourceKey AS OrigenKey, p.SourceKey AS AbonoKey, p.Debito, p.Credito
    INTO #abono
    FROM #lin o
    JOIN #lin p ON p.RelacionadoSourceKey = o.SourceKey AND p.CuentaId = o.CuentaId AND p.TerceroId = o.TerceroId
    WHERE o.DetalleCartera IN (1, 2);

    -- reversiones de abonos (TDev)
    SELECT ab.OrigenKey, SUM(r.Debito) AS Debito, SUM(r.Credito) AS Credito
    INTO #rev
    FROM #abono ab
    JOIN #lin r ON r.RelacionadoSourceKey = ab.AbonoKey
    GROUP BY ab.OrigenKey;

    BEGIN TRAN;
    DELETE fact.CarteraDocumento WHERE EmpresaId = @EmpresaId AND CorteFechaKey = @CorteKey;

    WITH o AS (
        SELECT l.*,
               CASE l.Naturaleza WHEN 'D' THEN l.Debito ELSE l.Credito END AS ValorOriginal,
               CASE l.Naturaleza WHEN 'D' THEN ISNULL(ab.Credito, 0) - ISNULL(rv.Debito, 0)
                                          ELSE ISNULL(ab.Debito, 0)  - ISNULL(rv.Credito, 0) END AS Abonos,
               DATEDIFF(DAY, COALESCE(l.FechaCartera, f.Fecha), @Corte) AS Dias
        FROM #lin l
        JOIN dim.Fecha f ON f.FechaKey = l.FechaKey
        OUTER APPLY (SELECT SUM(x.Debito) AS Debito, SUM(x.Credito) AS Credito FROM #abono x WHERE x.OrigenKey = l.SourceKey) ab
        LEFT JOIN #rev rv ON rv.OrigenKey = l.SourceKey
        WHERE l.DetalleCartera IN (1, 2)
          AND NOT EXISTS (SELECT 1 FROM #lin p                       -- excluir reversiones de abonos
                          JOIN #lin q ON q.SourceKey = p.RelacionadoSourceKey
                          WHERE p.SourceKey = l.RelacionadoSourceKey AND q.SourceKey IS NOT NULL)
    )
    INSERT fact.CarteraDocumento (EmpresaId, CorteFechaKey, TipoCartera, LineaOrigenSourceKey, NumeroComprobante, NumeroDocumento,
           CuentaCobro, TerceroId, PagadorId, CuentaId, SedeId, CentroCostoId, FechaMovimientoKey, FechaCartera, FechaVencimiento,
           ValorOriginal, Abonos, Saldo, Dias, RangoEdad, CargaId)
    SELECT @EmpresaId, @CorteKey, CASE o.DetalleCartera WHEN 2 THEN 'CXC' ELSE 'CXP' END, o.SourceKey, o.NumeroComprobante, o.NumDocumento,
           (SELECT MAX(fl.CuentaCobro) FROM fact.FacturaLinea fl WHERE fl.EmpresaId = @EmpresaId AND fl.NumeroDocumento = o.NumDocumento),
           o.TerceroId,
           (SELECT MIN(pg.PagadorId) FROM dim.Pagador pg WHERE pg.EmpresaId = @EmpresaId AND pg.TerceroId = o.TerceroId),
           o.CuentaId, o.SedeId, o.CentroCostoId, o.FechaKey, o.FechaCartera, o.FechaVencimiento,
           o.ValorOriginal, o.Abonos, o.ValorOriginal - o.Abonos, o.Dias,
           CASE WHEN o.Dias <= 30 THEN '0-30' WHEN o.Dias <= 60 THEN '31-60' WHEN o.Dias <= 90 THEN '61-90'
                WHEN o.Dias <= 120 THEN '91-120' WHEN o.Dias <= 150 THEN '121-150' WHEN o.Dias <= 180 THEN '151-180'
                WHEN o.Dias <= 360 THEN '181-360' ELSE '>360' END,
           @CargaId
    FROM o
    WHERE o.ValorOriginal <> 0 AND o.ValorOriginal - o.Abonos <> 0;
    COMMIT;
END;
GO

/* ============================ VISTAS PARA DASHBOARDS ============================ */
-- Heredan RLS. Saldo = por naturaleza de la cuenta (regla SaludIPS); Utilidad = Crédito - Débito (ingresos +, costos/gastos -).
CREATE OR ALTER VIEW rpt.vw_EstadoResultados AS
SELECT a.EmpresaId, a.PeriodoKey, a.SedeId, a.CentroCostoId, c.CuentaId, c.Codigo, c.ClasePuc,
       ISNULL(h.GrupoEstadoResultados, 'SIN_HOMOLOGAR') AS GrupoEstadoResultados,
       SUM(CASE WHEN (c.Naturaleza = 'D' AND c.MovimientoContrario = 0) OR (c.Naturaleza = 'C' AND c.MovimientoContrario = 1)
                THEN a.Debito - a.Credito ELSE a.Credito - a.Debito END) AS Saldo,
       SUM(a.Credito - a.Debito) AS Utilidad
FROM fact.AsientoLinea a
JOIN dim.CuentaContable c      ON c.CuentaId = a.CuentaId
JOIN dim.TipoComprobante tc    ON tc.TipoComprobanteId = a.TipoComprobanteId
LEFT JOIN dim.CuentaHomologada h ON h.CuentaEstandarId = c.CuentaEstandarId
WHERE a.Estado = 1
  AND c.ClasePuc IN ('4', '5', '6', '7')
  AND tc.Clase <> 'CIERRE'
GROUP BY a.EmpresaId, a.PeriodoKey, a.SedeId, a.CentroCostoId, c.CuentaId, c.Codigo, c.ClasePuc, h.GrupoEstadoResultados;
GO
CREATE OR ALTER VIEW rpt.vw_EjecucionPresupuestal AS
SELECT COALESCE(r.EmpresaId, p.EmpresaId) AS EmpresaId, COALESCE(r.PeriodoKey, p.PeriodoKey) AS PeriodoKey,
       COALESCE(r.CuentaId, p.CuentaId) AS CuentaId, COALESCE(r.CentroCostoId, p.CentroCostoId) AS CentroCostoId,
       COALESCE(r.SedeId, p.SedeId) AS SedeId,
       ISNULL(r.Saldo, 0) AS Real, ISNULL(p.Valor, 0) AS Presupuesto,
       ISNULL(r.Saldo, 0) - ISNULL(p.Valor, 0) AS Desviacion,
       CASE WHEN ISNULL(p.Valor, 0) = 0 THEN NULL ELSE ISNULL(r.Saldo, 0) / p.Valor END AS PctEjecucion
FROM (SELECT EmpresaId, PeriodoKey, CuentaId, CentroCostoId, SedeId, SUM(Saldo) AS Saldo
      FROM rpt.vw_EstadoResultados GROUP BY EmpresaId, PeriodoKey, CuentaId, CentroCostoId, SedeId) r
FULL JOIN (SELECT EmpresaId, PeriodoKey, CuentaId, CentroCostoId, SedeId, SUM(ValorPresupuestado) AS Valor
           FROM fact.Presupuesto WHERE Version = 'APROBADO'
           GROUP BY EmpresaId, PeriodoKey, CuentaId, CentroCostoId, SedeId) p
  ON  r.EmpresaId = p.EmpresaId AND r.PeriodoKey = p.PeriodoKey AND r.CuentaId = p.CuentaId
  AND ISNULL(r.CentroCostoId, 0) = ISNULL(p.CentroCostoId, 0) AND ISNULL(r.SedeId, 0) = ISNULL(p.SedeId, 0);
GO
CREATE OR ALTER VIEW rpt.vw_AntiguedadCartera AS
SELECT c.EmpresaId, c.CorteFechaKey, c.TipoCartera, c.PagadorId, c.TerceroId, c.SedeId, c.RangoEdad,
       COUNT(*) AS Documentos, SUM(c.ValorOriginal) AS ValorOriginal, SUM(c.Saldo) AS Saldo
FROM fact.CarteraDocumento c
GROUP BY c.EmpresaId, c.CorteFechaKey, c.TipoCartera, c.PagadorId, c.TerceroId, c.SedeId, c.RangoEdad;
GO
CREATE OR ALTER VIEW rpt.vw_Facturacion AS
SELECT f.EmpresaId, f.FechaEmisionKey / 100 AS PeriodoKey, f.SedeId, f.PagadorId, f.ContratoId, f.LineaServicioId,
       f.CentroCostoId, f.ProfesionalId, f.UsuarioOpId,
       COUNT(DISTINCT f.NumeroDocumento) AS Facturas,
       SUM(f.Cantidad) AS Cantidad,
       SUM(f.ValorNeto) AS ValorFacturado,
       SUM(f.Descuento) AS Descuentos,
       SUM(f.CuotaModeradora) AS CuotasModeradoras,
       SUM(f.CostoDirecto) AS CostoDirecto
FROM fact.FacturaLinea f
WHERE f.Anulada = 0
GROUP BY f.EmpresaId, f.FechaEmisionKey / 100, f.SedeId, f.PagadorId, f.ContratoId, f.LineaServicioId,
         f.CentroCostoId, f.ProfesionalId, f.UsuarioOpId;
GO
CREATE OR ALTER VIEW rpt.vw_FacturasAnuladas AS
SELECT f.EmpresaId, f.FechaEmisionKey / 100 AS PeriodoKey, f.SedeId, f.UsuarioOpId,
       COUNT(DISTINCT f.NumeroDocumento) AS FacturasAnuladas, SUM(f.ValorNeto) AS ValorAnulado
FROM fact.FacturaLinea f
WHERE f.Anulada = 1
GROUP BY f.EmpresaId, f.FechaEmisionKey / 100, f.SedeId, f.UsuarioOpId;
GO
CREATE OR ALTER VIEW rpt.vw_FlujoCaja AS
SELECT t.EmpresaId, t.FechaKey, t.FechaKey / 100 AS PeriodoKey, t.CuentaBancariaId, t.SedeId, t.Concepto, t.FormaPago,
       SUM(CASE WHEN t.TipoMovimiento = 'INGRESO' THEN t.Valor ELSE 0 END) AS Ingresos,
       SUM(CASE WHEN t.TipoMovimiento = 'EGRESO'  THEN t.Valor ELSE 0 END) AS Egresos
FROM fact.TesoreriaMovimiento t
WHERE t.TipoMovimiento <> 'TRASLADO' AND t.Anulado = 0
GROUP BY t.EmpresaId, t.FechaKey, t.CuentaBancariaId, t.SedeId, t.Concepto, t.FormaPago;
GO
CREATE OR ALTER VIEW rpt.vw_InventarioMensual AS
SELECT m.EmpresaId, m.FechaKey / 100 AS PeriodoKey, m.AlmacenId, m.ArticuloId, m.LoteId,
       SUM(m.CantidadEntrada) AS CantidadEntrada, SUM(m.CantidadSalida) AS CantidadSalida,
       SUM(m.ValorEntrada) AS ValorEntrada, SUM(m.ValorSalida) AS ValorSalida
FROM fact.MovimientoInventario m
WHERE m.Estado = 1
GROUP BY m.EmpresaId, m.FechaKey / 100, m.AlmacenId, m.ArticuloId, m.LoteId;
GO
