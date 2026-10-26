/* 0080 · Catálogo regulatorio (global, sin EmpresaId) y hallazgos tarifarios (spec 15 §2.4). */
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
    Regla varchar(5) NOT NULL, Severidad varchar(10) NOT NULL,
    Estado varchar(14) NOT NULL DEFAULT 'NUEVO' CHECK (Estado IN ('NUEVO','ABIERTO','CORREGIDO','ACEPTADO','FALSO_POSITIVO')),  -- mismo ciclo que el auditor (§5.5)
    Cum varchar(30) NULL, ProductoId int NULL, PagadorId int NULL, ContratoId int NULL,
    ValorObservado decimal(19,4) NULL, ValorLimite decimal(19,4) NULL,
    ExcesoUnitario decimal(19,4) NULL, UnidadesAfectadas decimal(18,4) NULL, MontoEnRiesgo decimal(19,4) NULL,
    CircularId int NULL REFERENCES reg.Circular, FactorValidado bit NOT NULL DEFAULT 0,
    DetectadoEn datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(), CerradoEn datetime2(0) NULL, CerradoPor int NULL,
    CONSTRAINT PK_HallazgoTarifario PRIMARY KEY (EmpresaId, HallazgoId));
GO
