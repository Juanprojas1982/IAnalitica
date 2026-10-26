/* 0070 · Hechos ESE (spec 17). */
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
GO
