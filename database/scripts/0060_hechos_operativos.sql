/* 0060 · Hechos operativos (agenda, prestación, diagnóstico, nómina, envíos, autorizaciones). */
/* ============================ AGENDA Y CITAS ============================ */
-- Capacidad ofertada: plantilla dCitasHorarios expandida por día (la expande el agente)
CREATE TABLE fact.AgendaCapacidad (
    EmpresaId      int NOT NULL CONSTRAINT FK_AgC_Empresa REFERENCES seg.Empresa,
    FechaKey       int NOT NULL CONSTRAINT FK_AgC_Fecha REFERENCES dim.Fecha,
    ProfesionalId  int NOT NULL CONSTRAINT FK_AgC_Prof REFERENCES dim.Profesional,
    EspecialidadId int NULL CONSTRAINT FK_AgC_Esp REFERENCES dim.Especialidad,
    SedeId         int NULL CONSTRAINT FK_AgC_Sede REFERENCES dim.Sede,
    Jornada        varchar(10) NOT NULL,
    HoraInicio     time(0) NULL,
    CuposOfertados smallint NOT NULL,
    MinutosPorCupo smallint NOT NULL,
    MinutosOfertados AS (CAST(CuposOfertados AS int) * MinutosPorCupo) PERSISTED,
    CargaId        bigint NOT NULL CONSTRAINT FK_AgC_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_AgendaCapacidad PRIMARY KEY (EmpresaId, FechaKey, ProfesionalId, Jornada)
);

CREATE TABLE fact.Cita (
    CitaId          bigint IDENTITY(1,1) NOT NULL,
    EmpresaId       int NOT NULL CONSTRAINT FK_Cita_Empresa REFERENCES seg.Empresa,
    SourceKey       nvarchar(100) NOT NULL,          -- 'C:' + dCitas.EMConsecutivo | 'X:' + dCitasCanceladas.EMConsecutivo
    Estado          varchar(12) NOT NULL CHECK (Estado IN ('ASISTIO','NO_ASISTIO','PENDIENTE','CANCELADA')),
    FechaCitaKey    int NOT NULL CONSTRAINT FK_Cita_Fecha REFERENCES dim.Fecha,
    FechaCita       date NOT NULL,
    HoraCita        time(0) NULL,
    FechaSolicitud  date NULL,                       -- dCitas.FechaUsuario ("Fecha Solicitud Usuario")
    FechaAsignacion datetime2(0) NULL,               -- dCitas.FechaDigitacion
    FechaCancelacion datetime2(0) NULL,
    MotivoCancelacion varchar(60) NULL,
    Reprogramada    bit NULL,
    HoraLlegada     time(0) NULL,                    -- dCitas.HoraAtencion (se fija al marcar asistencia) [S]
    InicioAtencion  datetime2(0) NULL,               -- mAdmisiones.FechaAtencion [S]
    FinAtencion     datetime2(0) NULL,               -- mAdmisiones.FechaSalidaHC [S]
    Confirmada      bit NULL,
    ProfesionalId   int NULL CONSTRAINT FK_Cita_Prof REFERENCES dim.Profesional,
    EspecialidadId  int NULL CONSTRAINT FK_Cita_Esp REFERENCES dim.Especialidad,
    SedeId          int NULL CONSTRAINT FK_Cita_Sede REFERENCES dim.Sede,
    PagadorId       int NULL CONSTRAINT FK_Cita_Pag REFERENCES dim.Pagador,
    TipoConsulta    varchar(30) NULL,
    TipoSolicitud   varchar(30) NULL,
    PacienteHash    char(64) NULL,                   -- para pacientes únicos; nunca identificable
    UsuarioOpId     int NULL CONSTRAINT FK_Cita_UOp REFERENCES dim.UsuarioOperativo,   -- quien asignó
    AdmisionSourceKey varchar(30) NULL,
    -- métricas derivadas en la carga (días / minutos)
    DiasOportunidad AS (DATEDIFF(DAY, FechaSolicitud, FechaCita)),       -- definición normativa (desde la solicitud)
    DiasDesdeAsignacion AS (DATEDIFF(DAY, CAST(FechaAsignacion AS date), FechaCita)),  -- lo que mide hoy SIPS_014
    CargaId         bigint NOT NULL CONSTRAINT FK_Cita_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_Cita PRIMARY KEY NONCLUSTERED (CitaId),
    CONSTRAINT UQ_Cita UNIQUE (EmpresaId, SourceKey)
);
-- Columnstore no admite columnas calculadas en todas las versiones: índice rowstore por (EmpresaId, FechaCitaKey)
CREATE CLUSTERED INDEX CX_Cita ON fact.Cita (EmpresaId, FechaCitaKey);
GO

/* ============================ PRESTACIÓN (ciclo del ingreso) ============================ */
-- Grano: servicio (mServicios). Une orden, realización, factura, envío y anulación con sus tiempos.
CREATE TABLE fact.Prestacion (
    PrestacionId     bigint IDENTITY(1,1) NOT NULL,
    EmpresaId        int NOT NULL CONSTRAINT FK_Pre_Empresa REFERENCES seg.Empresa,
    SourceKey        nvarchar(100) NOT NULL,          -- mServicios.IdServicio
    AdmisionSourceKey varchar(30) NOT NULL,
    TipoAtencion     nvarchar(60) NULL,
    FechaIngreso     datetime2(0) NULL,
    FechaOrden       datetime2(0) NULL,
    FechaRealizacion datetime2(0) NULL,
    FechaRealizacionKey int NULL CONSTRAINT FK_Pre_FechaR REFERENCES dim.Fecha,
    FechaSalida      datetime2(0) NULL,               -- mAdmisiones.FechaSalidaHC
    NumeroFactura    varchar(30) NULL,
    FechaFactura     datetime2(0) NULL,
    FechaRadicacion  datetime2(0) NULL,               -- mRipsEnvios.FechaEnvio [S]
    EnlaceFactura    varchar(10) NULL CHECK (EnlaceFactura IN ('DETALLE','ADMISION')),   -- cómo se resolvió la factura
    Anulado          bit NOT NULL,
    FechaAnulacion   datetime2(0) NULL,
    UsuarioAnulaId   int NULL CONSTRAINT FK_Pre_UAnula REFERENCES dim.UsuarioOperativo,
    ServicioId       int NULL CONSTRAINT FK_Pre_Serv REFERENCES dim.Servicio,
    LineaServicioId  int NULL CONSTRAINT FK_Pre_LS REFERENCES dim.LineaServicio,
    CentroCostoId    int NULL CONSTRAINT FK_Pre_CC REFERENCES dim.CentroCosto,
    SedeId           int NULL CONSTRAINT FK_Pre_Sede REFERENCES dim.Sede,
    PagadorId        int NULL CONSTRAINT FK_Pre_Pag REFERENCES dim.Pagador,
    ProfesionalId    int NULL CONSTRAINT FK_Pre_Prof REFERENCES dim.Profesional,   -- realiza
    ProfesionalOrdenaId int NULL CONSTRAINT FK_Pre_ProfO REFERENCES dim.Profesional,
    EspecialidadId   int NULL CONSTRAINT FK_Pre_Esp REFERENCES dim.Especialidad,
    UsuarioOpId      int NULL CONSTRAINT FK_Pre_UOp REFERENCES dim.UsuarioOperativo,  -- quien cargó
    TieneAutorizacion bit NOT NULL DEFAULT 0,
    Cantidad         decimal(18,4) NOT NULL,
    ValorUnitario    decimal(19,4) NOT NULL,
    ValorTotal       decimal(19,4) NOT NULL,
    Descuento        decimal(19,4) NOT NULL DEFAULT 0,
    TarifaPactada    decimal(19,4) NULL,              -- pTarifasProcedimientos (manual/plan del pagador) [S]
    CostoInsumos     decimal(19,4) NULL,              -- mServicios.VrCosto [S unitario/total]
    CargaId          bigint NOT NULL CONSTRAINT FK_Pre_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_Prestacion PRIMARY KEY NONCLUSTERED (PrestacionId),
    CONSTRAINT UQ_Prestacion UNIQUE (EmpresaId, SourceKey)
);
CREATE CLUSTERED COLUMNSTORE INDEX CCI_Prestacion ON fact.Prestacion;
GO

/* ============================ APOYO DIAGNÓSTICO ============================ */
CREATE TABLE fact.EstudioDiagnostico (
    EmpresaId        int NOT NULL CONSTRAINT FK_Est_Empresa REFERENCES seg.Empresa,
    SourceKey        nvarchar(100) NOT NULL,          -- 'R:' + IdServicio (imagen) | 'L:' + IdServicio + CodLabExamen
    Tipo             varchar(12) NOT NULL CHECK (Tipo IN ('IMAGEN','LABORATORIO')),
    PrestacionSourceKey nvarchar(100) NOT NULL,       -- mServicios.IdServicio
    FechaRealizacion datetime2(0) NULL,
    FechaToma        datetime2(0) NULL,
    FechaInforme     datetime2(0) NULL,
    FechaEntrega     datetime2(0) NULL,
    Entregado        bit NULL,
    ImagenesRechazadas smallint NULL,
    ImagenesRepetidas  smallint NULL,
    MotivoRechazo    nvarchar(200) NULL,
    MotivoRepeticion nvarchar(200) NULL,
    DosisReferencia  nvarchar(60) NULL,
    ProfesionalInformaId int NULL CONSTRAINT FK_Est_Prof REFERENCES dim.Profesional,
    ProfesionalSolicitaId int NULL CONSTRAINT FK_Est_ProfS REFERENCES dim.Profesional,
    SedeId           int NULL CONSTRAINT FK_Est_Sede REFERENCES dim.Sede,
    CargaId          bigint NOT NULL CONSTRAINT FK_Est_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_EstudioDiagnostico PRIMARY KEY (EmpresaId, SourceKey)
);
GO

/* ============================ NÓMINA (costo de personal) ============================ */
-- Grano: línea de Nomina.Historicos. ES COSTO solo si la cuenta contable del concepto es de clase 5, 6 o 7;
-- las deducciones del empleado no son costo. La nómina ya está en contabilidad: esta tabla NO se suma al P&G,
-- se usa para costo por hora, costo por profesional y bases de distribución.
CREATE TABLE fact.NominaCosto (
    EmpresaId        int NOT NULL CONSTRAINT FK_Nom_Empresa REFERENCES seg.Empresa,
    SourceKey        nvarchar(100) NOT NULL,          -- Historicos.Id
    PeriodoKey       int NOT NULL,                    -- de DocumentosNominas.FinalPeriodo
    ContratoCodigo   varchar(30) NOT NULL,
    EmpleadoTerceroCodigo varchar(30) NOT NULL,       -- enlaza con dim.Profesional.TerceroCodigo
    Cargo            nvarchar(120) NULL,
    ConceptoCodigo   varchar(20) NOT NULL,
    Concepto         nvarchar(150) NOT NULL,
    CuentaCodigo     varchar(25) NULL,
    EsCosto          bit NOT NULL,                    -- cuenta clase 5/6/7
    CentroCostoId    int NULL CONSTRAINT FK_Nom_CC REFERENCES dim.CentroCosto,
    Valor            decimal(19,4) NOT NULL,          -- Debito - Credito
    Horas            decimal(9,2) NULL,
    Dias             decimal(9,2) NULL,
    HorasContratoMes decimal(9,2) NULL,
    CargaId          bigint NOT NULL CONSTRAINT FK_Nom_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_NominaCosto PRIMARY KEY (EmpresaId, SourceKey)
);
CREATE INDEX IX_Nom_Periodo ON fact.NominaCosto (EmpresaId, PeriodoKey, EmpleadoTerceroCodigo) INCLUDE (Valor, EsCosto, HorasContratoMes);
GO

/* ============================ ENVÍOS Y AUTORIZACIONES ============================ */
CREATE TABLE fact.EnvioDocumento (
    EmpresaId       int NOT NULL CONSTRAINT FK_Env_Empresa REFERENCES seg.Empresa,
    SourceKey       nvarchar(100) NOT NULL,           -- 'FE:' / 'RIPS:' + Id
    Canal           varchar(5) NOT NULL CHECK (Canal IN ('FE','RIPS')),
    TipoDocumento   tinyint NULL,
    NumeroFactura   varchar(30) NOT NULL,
    SedeId          int NULL CONSTRAINT FK_Env_Sede REFERENCES dim.Sede,
    FechaEnvio      datetime2(0) NULL,
    Estado          varchar(12) NOT NULL,             -- FE: ENVIADA, CARGADA, ERROR, SIN_ENVIAR ; RIPS: [S]
    CodigoUnico     varchar(120) NULL,                -- CUFE / CUV
    Valor           decimal(19,4) NULL,
    CargaId         bigint NOT NULL CONSTRAINT FK_Env_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_EnvioDocumento PRIMARY KEY (EmpresaId, SourceKey)
);

CREATE TABLE fact.Autorizacion (
    EmpresaId       int NOT NULL CONSTRAINT FK_Aut_Empresa REFERENCES seg.Empresa,
    SourceKey       nvarchar(100) NOT NULL,           -- Vigencia|ConAutorizacion|EMConsecutivo
    FechaSolicitud  datetime2(0) NULL,
    FechaOrden      datetime2(0) NULL,
    FechaRegistro   datetime2(0) NULL,
    Estado          varchar(20) NULL,
    NumeroAutorizacion varchar(40) NULL,
    ServicioId      int NULL CONSTRAINT FK_Aut_Serv REFERENCES dim.Servicio,
    Cantidad        int NULL,
    PagadorId       int NULL CONSTRAINT FK_Aut_Pag REFERENCES dim.Pagador,
    SedeId          int NULL CONSTRAINT FK_Aut_Sede REFERENCES dim.Sede,
    Prioritaria     bit NULL,
    CargaId         bigint NOT NULL CONSTRAINT FK_Aut_Carga REFERENCES etl.Carga,
    CONSTRAINT PK_Autorizacion PRIMARY KEY (EmpresaId, SourceKey)
);
GO
