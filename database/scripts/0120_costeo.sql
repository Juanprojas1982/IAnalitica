/* 0120 · Costeo: bases, reglas y resultados de la distribución (10). */
CREATE TABLE cfg.CostoBase (                           -- catálogo de bases de distribución
    BaseCodigo   varchar(20) NOT NULL CONSTRAINT PK_CostoBase PRIMARY KEY,
    Nombre       nvarchar(100) NOT NULL,
    Origen       varchar(10) NOT NULL CHECK (Origen IN ('CALCULADA','MANUAL'))
);
INSERT cfg.CostoBase (BaseCodigo, Nombre, Origen) VALUES
 ('COSTO_PERSONAL', N'Costo de personal del CC (nómina)', 'CALCULADA'),
 ('INGRESO',        N'Ingreso facturado del CC',           'CALCULADA'),
 ('ACTIVIDADES',    N'Número de prestaciones del CC',       'CALCULADA'),
 ('ATENCIONES',     N'Admisiones atendidas en el CC',       'CALCULADA'),
 ('M2',             N'Metros cuadrados',                    'MANUAL'),
 ('KILOS_ROPA',     N'Kilos de ropa lavada',                'MANUAL'),
 ('CONSUMO_INSUMOS',N'Salidas de almacén valoradas',        'CALCULADA'),
 ('PORCENTAJE',     N'Porcentaje fijo definido',            'MANUAL');

-- Regla: qué CC se distribuye, en qué orden y con qué base (por empresa; vigente desde un periodo)
CREATE TABLE cfg.CostoRegla (
    EmpresaId     int NOT NULL CONSTRAINT FK_CR_Empresa REFERENCES seg.Empresa,
    CentroCostoId int NOT NULL CONSTRAINT FK_CR_CC REFERENCES dim.CentroCosto,
    Orden         smallint NOT NULL,                   -- 1 = primero en distribuirse
    BaseCodigo    varchar(20) NOT NULL CONSTRAINT FK_CR_Base REFERENCES cfg.CostoBase,
    VigenteDesde  int NOT NULL,                        -- PeriodoKey
    CONSTRAINT PK_CostoRegla PRIMARY KEY (EmpresaId, CentroCostoId, VigenteDesde)
);

-- Valores de la base por CC destino y periodo (calculados por usp o cargados por Excel)
CREATE TABLE fact.CostoBaseValor (
    EmpresaId     int NOT NULL CONSTRAINT FK_CBV_Empresa REFERENCES seg.Empresa,
    PeriodoKey    int NOT NULL,
    BaseCodigo    varchar(20) NOT NULL CONSTRAINT FK_CBV_Base REFERENCES cfg.CostoBase,
    CentroCostoId int NOT NULL CONSTRAINT FK_CBV_CC REFERENCES dim.CentroCosto,
    Valor         decimal(19,4) NOT NULL CHECK (Valor >= 0),
    CONSTRAINT PK_CostoBaseValor PRIMARY KEY (EmpresaId, PeriodoKey, BaseCodigo, CentroCostoId)
);

-- Resultado: cada asignación origen -> destino
CREATE TABLE fact.CostoDistribuido (
    EmpresaId          int NOT NULL CONSTRAINT FK_CD_Empresa REFERENCES seg.Empresa,
    PeriodoKey         int NOT NULL,
    Paso               smallint NOT NULL,
    CentroCostoOrigenId  int NOT NULL CONSTRAINT FK_CD_Orig REFERENCES dim.CentroCosto,
    CentroCostoDestinoId int NOT NULL CONSTRAINT FK_CD_Dest REFERENCES dim.CentroCosto,
    BaseCodigo         varchar(20) NOT NULL,
    Valor              decimal(19,4) NOT NULL,
    CalculadoEn        datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_CostoDistribuido PRIMARY KEY (EmpresaId, PeriodoKey, Paso, CentroCostoOrigenId, CentroCostoDestinoId)
);
GO
