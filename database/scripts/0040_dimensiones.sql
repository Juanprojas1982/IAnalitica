/* 0040 · Dimensiones globales y por empresa (incluye ESE: fuente de financiación y rubro presupuestal). */
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
    NombreCorto   nvarchar(60) NULL,
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
    TipoOrigen    varchar(20) NULL,                     -- valor crudo de SaludIPS; su homologación a TipoCC es [S] (05 §8.5)
    UnidadNegocio nvarchar(100) NULL,
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
    DigitoVerificacion char(1) NULL,
    ClaseOrigen   varchar(20) NULL,                     -- valor crudo de SaludIPS [S] (05 §8.5)
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
    CodigoAdministradora varchar(20) NULL,              -- código de la administradora (EPS) en SaludIPS
    EsParticular  bit NOT NULL DEFAULT 0,
    Activo        bit NOT NULL DEFAULT 1,
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
    CodigoEstandar varchar(30) NULL,                    -- CUPS (procedimientos) o CUM (suministros)
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
    CodigoCum     varchar(30) NULL,                     -- expediente-consecutivo; enlaza con reg.PrecioMaximo (spec 15)
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
GO
