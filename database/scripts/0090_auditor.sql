/* 0090 · Hallazgos del auditor de configuración (spec 16). Las tablas saludips.* se crean en F4 (script 1000+). */
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
-- Las tablas saludips.<Tabla> (copia de la configuración, lista blanca de la spec 16 §2) NO van aquí: se generan en F4
-- en un script 1000+ desde docs/fuentes-saludips/diccionario_columnas.csv, con las mismas columnas MENOS las excluidas,
-- más EmpresaId int NOT NULL y CargaId bigint NOT NULL; PK = (EmpresaId, PK original). La RLS las cubre sola (siempre/9000).
GO
