/* =====================================================================
   Hechos operativos + motor de distribución de costos  (complemento de v2)
   Diseño: 09_catalogo_kpi_gerencial.md · Extracción: 11_extraccion_operativa_saludips.sql
   NO EJECUTADO. Scripts DbUp 0060 (hechos) y 0120 (costeo); procedimientos en 0140 y vistas en 0150 (24_SPEC_MAESTRA §6.2).
   ===================================================================== */

/* dim.Especialidad y las columnas TerceroCodigo/EspecialidadId de dim.Profesional están en 01_esquema_analitico_salud_v2.sql. */

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

/* ============================ MOTOR DE DISTRIBUCIÓN DE COSTOS ============================ */
-- Método escalonado (step-down): los CC de apoyo y administrativos se distribuyen en orden;
-- cada CC reparte su costo directo + lo recibido entre los CC que van después y los productivos,
-- según una base. Se conserva el total (prueba de cuadre al final).

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

-- Bases calculables desde los hechos
CREATE OR ALTER PROCEDURE etl.usp_CalcularBasesCosto @EmpresaId int, @PeriodoKey int
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;
    BEGIN TRAN;
    DELETE fact.CostoBaseValor
    WHERE EmpresaId = @EmpresaId AND PeriodoKey = @PeriodoKey
      AND BaseCodigo IN (SELECT BaseCodigo FROM cfg.CostoBase WHERE Origen = 'CALCULADA');

    INSERT fact.CostoBaseValor (EmpresaId, PeriodoKey, BaseCodigo, CentroCostoId, Valor)
    SELECT @EmpresaId, @PeriodoKey, 'COSTO_PERSONAL', n.CentroCostoId, SUM(n.Valor)
    FROM fact.NominaCosto n
    WHERE n.EmpresaId = @EmpresaId AND n.PeriodoKey = @PeriodoKey AND n.EsCosto = 1 AND n.CentroCostoId IS NOT NULL
    GROUP BY n.CentroCostoId HAVING SUM(n.Valor) > 0
    UNION ALL
    SELECT @EmpresaId, @PeriodoKey, 'INGRESO', f.CentroCostoId, SUM(f.ValorNeto)
    FROM fact.FacturaLinea f
    WHERE f.EmpresaId = @EmpresaId AND f.FechaEmisionKey / 100 = @PeriodoKey AND f.Anulada = 0 AND f.CentroCostoId IS NOT NULL
    GROUP BY f.CentroCostoId HAVING SUM(f.ValorNeto) > 0
    UNION ALL
    SELECT @EmpresaId, @PeriodoKey, 'ACTIVIDADES', p.CentroCostoId, SUM(p.Cantidad)
    FROM fact.Prestacion p
    WHERE p.EmpresaId = @EmpresaId AND p.FechaRealizacionKey / 100 = @PeriodoKey AND p.Anulado = 0 AND p.CentroCostoId IS NOT NULL
    GROUP BY p.CentroCostoId HAVING SUM(p.Cantidad) > 0
    UNION ALL
    SELECT @EmpresaId, @PeriodoKey, 'ATENCIONES', p.CentroCostoId, COUNT(DISTINCT p.AdmisionSourceKey)
    FROM fact.Prestacion p
    WHERE p.EmpresaId = @EmpresaId AND p.FechaRealizacionKey / 100 = @PeriodoKey AND p.Anulado = 0 AND p.CentroCostoId IS NOT NULL
    GROUP BY p.CentroCostoId
    UNION ALL
    SELECT @EmpresaId, @PeriodoKey, 'CONSUMO_INSUMOS', m.CentroCostoId, SUM(m.ValorSalida)
    FROM fact.MovimientoInventario m
    WHERE m.EmpresaId = @EmpresaId AND m.FechaKey / 100 = @PeriodoKey AND m.Estado = 1 AND m.CentroCostoId IS NOT NULL
    GROUP BY m.CentroCostoId HAVING SUM(m.ValorSalida) > 0;
    COMMIT;
END;
GO

-- Distribución escalonada. Costo directo = Saldo P&G clases 5, 6 y 7 (sin cierre) por CC.
CREATE OR ALTER PROCEDURE etl.usp_DistribuirCostos @EmpresaId int, @PeriodoKey int
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;

    -- costo acumulado por CC (directo; luego recibe asignaciones)
    CREATE TABLE #costo (CentroCostoId int PRIMARY KEY, Valor decimal(19,4) NOT NULL);
    INSERT #costo (CentroCostoId, Valor)
    SELECT r.CentroCostoId, SUM(r.Saldo)
    FROM rpt.vw_EstadoResultados r
    WHERE r.EmpresaId = @EmpresaId AND r.PeriodoKey = @PeriodoKey AND r.ClasePuc IN ('5','6','7') AND r.CentroCostoId IS NOT NULL
    GROUP BY r.CentroCostoId;

    DECLARE @TotalInicial decimal(19,4) = (SELECT SUM(Valor) FROM #costo);

    -- reglas vigentes (la más reciente <= periodo por CC)
    SELECT x.CentroCostoId, x.Orden, x.BaseCodigo
    INTO #regla
    FROM (SELECT r.*, ROW_NUMBER() OVER (PARTITION BY r.CentroCostoId ORDER BY r.VigenteDesde DESC) AS rn
          FROM cfg.CostoRegla r WHERE r.EmpresaId = @EmpresaId AND r.VigenteDesde <= @PeriodoKey) x
    WHERE x.rn = 1;

    BEGIN TRAN;
    DELETE fact.CostoDistribuido WHERE EmpresaId = @EmpresaId AND PeriodoKey = @PeriodoKey;

    DECLARE @Paso smallint = 0, @Origen int, @Base varchar(20), @Orden smallint, @Monto decimal(19,4), @TotalBase decimal(19,4);
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT CentroCostoId, Orden, BaseCodigo FROM #regla ORDER BY Orden, CentroCostoId;
    OPEN c; FETCH NEXT FROM c INTO @Origen, @Orden, @Base;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @Paso += 1;
        SET @Monto = ISNULL((SELECT Valor FROM #costo WHERE CentroCostoId = @Origen), 0);

        IF @Monto <> 0
        BEGIN
            -- destinos: CC con base > 0 que no se han distribuido aún (no son origen anterior ni el mismo)
            SELECT b.CentroCostoId, b.Valor
            INTO #dest
            FROM fact.CostoBaseValor b
            WHERE b.EmpresaId = @EmpresaId AND b.PeriodoKey = @PeriodoKey AND b.BaseCodigo = @Base AND b.Valor > 0
              AND b.CentroCostoId <> @Origen
              AND NOT EXISTS (SELECT 1 FROM #regla r WHERE r.CentroCostoId = b.CentroCostoId
                              AND (r.Orden < @Orden OR (r.Orden = @Orden AND r.CentroCostoId < @Origen)));

            SET @TotalBase = (SELECT SUM(Valor) FROM #dest);

            IF ISNULL(@TotalBase, 0) > 0
            BEGIN
                INSERT fact.CostoDistribuido (EmpresaId, PeriodoKey, Paso, CentroCostoOrigenId, CentroCostoDestinoId, BaseCodigo, Valor)
                SELECT @EmpresaId, @PeriodoKey, @Paso, @Origen, d.CentroCostoId, @Base, ROUND(@Monto * d.Valor / @TotalBase, 4)
                FROM #dest d;

                -- residuo de redondeo al destino con mayor base
                UPDATE TOP (1) cd SET Valor = cd.Valor + (@Monto - (SELECT SUM(Valor) FROM fact.CostoDistribuido
                                                                     WHERE EmpresaId = @EmpresaId AND PeriodoKey = @PeriodoKey AND Paso = @Paso))
                FROM fact.CostoDistribuido cd
                JOIN #dest d ON d.CentroCostoId = cd.CentroCostoDestinoId
                WHERE cd.EmpresaId = @EmpresaId AND cd.PeriodoKey = @PeriodoKey AND cd.Paso = @Paso
                  AND d.Valor = (SELECT MAX(Valor) FROM #dest);

                MERGE #costo AS t
                USING (SELECT CentroCostoDestinoId, Valor FROM fact.CostoDistribuido
                       WHERE EmpresaId = @EmpresaId AND PeriodoKey = @PeriodoKey AND Paso = @Paso) AS s
                   ON t.CentroCostoId = s.CentroCostoDestinoId
                WHEN MATCHED THEN UPDATE SET Valor = t.Valor + s.Valor
                WHEN NOT MATCHED THEN INSERT (CentroCostoId, Valor) VALUES (s.CentroCostoDestinoId, s.Valor);

                UPDATE #costo SET Valor = 0 WHERE CentroCostoId = @Origen;
            END
            -- sin base: el costo se queda en el origen y se reporta como NO_DISTRIBUIDO en la vista
            DROP TABLE #dest;
        END;
        FETCH NEXT FROM c INTO @Origen, @Orden, @Base;
    END;
    CLOSE c; DEALLOCATE c;

    -- prueba de conservación
    IF ABS(ISNULL((SELECT SUM(Valor) FROM #costo), 0) - ISNULL(@TotalInicial, 0)) > 1
        THROW 50010, 'La distribución no conserva el costo total.', 1;
    COMMIT;
END;
GO

/* ============================ VISTAS DE DECISIÓN ============================ */
-- Rentabilidad por CC con distribución
CREATE OR ALTER VIEW rpt.vw_RentabilidadCentroCosto AS
WITH directo AS (
    SELECT EmpresaId, PeriodoKey, CentroCostoId,
           SUM(CASE WHEN ClasePuc = '4' THEN Saldo ELSE 0 END) AS Ingreso,
           SUM(CASE WHEN ClasePuc IN ('5','6','7') THEN Saldo ELSE 0 END) AS CostoDirecto
    FROM rpt.vw_EstadoResultados WHERE CentroCostoId IS NOT NULL
    GROUP BY EmpresaId, PeriodoKey, CentroCostoId),
recibido AS (
    SELECT EmpresaId, PeriodoKey, CentroCostoDestinoId AS CentroCostoId, SUM(Valor) AS Recibido
    FROM fact.CostoDistribuido GROUP BY EmpresaId, PeriodoKey, CentroCostoDestinoId),
entregado AS (
    SELECT EmpresaId, PeriodoKey, CentroCostoOrigenId AS CentroCostoId, SUM(Valor) AS Entregado
    FROM fact.CostoDistribuido GROUP BY EmpresaId, PeriodoKey, CentroCostoOrigenId)
SELECT d.EmpresaId, d.PeriodoKey, d.CentroCostoId, cc.TipoCC,
       d.Ingreso, d.CostoDirecto,
       ISNULL(r.Recibido, 0)  AS CostoRecibido,
       ISNULL(e.Entregado, 0) AS CostoEntregado,
       d.CostoDirecto + ISNULL(r.Recibido, 0) - ISNULL(e.Entregado, 0) AS CostoTotal,
       d.Ingreso - (d.CostoDirecto + ISNULL(r.Recibido, 0) - ISNULL(e.Entregado, 0)) AS Margen
FROM directo d
JOIN dim.CentroCosto cc ON cc.CentroCostoId = d.CentroCostoId
LEFT JOIN recibido r  ON r.EmpresaId = d.EmpresaId AND r.PeriodoKey = d.PeriodoKey AND r.CentroCostoId = d.CentroCostoId
LEFT JOIN entregado e ON e.EmpresaId = d.EmpresaId AND e.PeriodoKey = d.PeriodoKey AND e.CentroCostoId = d.CentroCostoId;
GO

-- Costo unitario aproximado por prestación (costo total del CC repartido por valor de tarifa: método por precio relativo).
-- Es una aproximación hasta tener la ficha de costo estándar por CUPS.
CREATE OR ALTER VIEW rpt.vw_MargenServicio AS
WITH cc AS (
    SELECT EmpresaId, PeriodoKey, CentroCostoId, CostoTotal FROM rpt.vw_RentabilidadCentroCosto),
pr AS (
    SELECT EmpresaId, FechaRealizacionKey / 100 AS PeriodoKey, CentroCostoId, ServicioId, PagadorId,
           SUM(Cantidad) AS Cantidad, SUM(ValorTotal) AS Ingreso, SUM(ISNULL(CostoInsumos, 0)) AS Insumos
    FROM fact.Prestacion WHERE Anulado = 0
    GROUP BY EmpresaId, FechaRealizacionKey / 100, CentroCostoId, ServicioId, PagadorId),
tot AS (
    SELECT EmpresaId, PeriodoKey, CentroCostoId, SUM(Ingreso) AS IngresoCC FROM pr GROUP BY EmpresaId, PeriodoKey, CentroCostoId)
SELECT pr.EmpresaId, pr.PeriodoKey, pr.CentroCostoId, pr.ServicioId, pr.PagadorId, pr.Cantidad, pr.Ingreso,
       CAST(cc.CostoTotal * pr.Ingreso / NULLIF(tot.IngresoCC, 0) AS decimal(19,4)) AS CostoAsignado,
       pr.Ingreso - CAST(cc.CostoTotal * pr.Ingreso / NULLIF(tot.IngresoCC, 0) AS decimal(19,4)) AS Margen
FROM pr
JOIN tot ON tot.EmpresaId = pr.EmpresaId AND tot.PeriodoKey = pr.PeriodoKey AND tot.CentroCostoId = pr.CentroCostoId
LEFT JOIN cc ON cc.EmpresaId = pr.EmpresaId AND cc.PeriodoKey = pr.PeriodoKey AND cc.CentroCostoId = pr.CentroCostoId;
GO

-- Agenda: ocupación, inasistencia y costo de capacidad ociosa
CREATE OR ALTER VIEW rpt.vw_AgendaOcupacion AS
WITH cap AS (
    SELECT EmpresaId, FechaKey, ProfesionalId, EspecialidadId, SedeId,
           SUM(CuposOfertados) AS Cupos, SUM(MinutosOfertados) AS Minutos
    FROM fact.AgendaCapacidad GROUP BY EmpresaId, FechaKey, ProfesionalId, EspecialidadId, SedeId),
cit AS (
    SELECT EmpresaId, FechaCitaKey AS FechaKey, ProfesionalId,
           SUM(CASE WHEN Estado <> 'CANCELADA' THEN 1 ELSE 0 END) AS Asignadas,
           SUM(CASE WHEN Estado = 'ASISTIO' THEN 1 ELSE 0 END)    AS Atendidas,
           SUM(CASE WHEN Estado = 'NO_ASISTIO' THEN 1 ELSE 0 END) AS Inasistencias,
           SUM(CASE WHEN Estado = 'CANCELADA' THEN 1 ELSE 0 END)  AS Canceladas
    FROM fact.Cita GROUP BY EmpresaId, FechaCitaKey, ProfesionalId),
hora AS (   -- costo por hora contratada del profesional en el mes (si está en nómina)
    SELECT n.EmpresaId, n.PeriodoKey, p.ProfesionalId,
           SUM(CASE WHEN n.EsCosto = 1 THEN n.Valor ELSE 0 END) / NULLIF(MAX(n.HorasContratoMes), 0) AS CostoHora
    FROM fact.NominaCosto n
    JOIN dim.Profesional p ON p.EmpresaId = n.EmpresaId AND p.TerceroCodigo = n.EmpleadoTerceroCodigo
    GROUP BY n.EmpresaId, n.PeriodoKey, p.ProfesionalId)
SELECT cap.EmpresaId, cap.FechaKey, cap.FechaKey / 100 AS PeriodoKey, cap.ProfesionalId, cap.EspecialidadId, cap.SedeId,
       cap.Cupos, ISNULL(cit.Asignadas, 0) AS Asignadas, ISNULL(cit.Atendidas, 0) AS Atendidas,
       ISNULL(cit.Inasistencias, 0) AS Inasistencias, ISNULL(cit.Canceladas, 0) AS Canceladas,
       CAST(ISNULL(cit.Asignadas, 0) * 1.0 / NULLIF(cap.Cupos, 0) AS decimal(7,4)) AS Ocupacion,
       -- cupos que no produjeron atención: no asignados + inasistidos
       cap.Cupos - ISNULL(cit.Atendidas, 0) AS CuposPerdidos,
       CAST((cap.Cupos - ISNULL(cit.Atendidas, 0)) * (cap.Minutos * 1.0 / NULLIF(cap.Cupos, 0)) / 60.0 * h.CostoHora AS decimal(19,2)) AS CostoCapacidadOciosa
FROM cap
LEFT JOIN cit  ON cit.EmpresaId = cap.EmpresaId AND cit.FechaKey = cap.FechaKey AND cit.ProfesionalId = cap.ProfesionalId
LEFT JOIN hora h ON h.EmpresaId = cap.EmpresaId AND h.PeriodoKey = cap.FechaKey / 100 AND h.ProfesionalId = cap.ProfesionalId;
GO

-- Ciclo del ingreso: dinero detenido por etapa y días entre etapas
CREATE OR ALTER VIEW rpt.vw_CicloIngreso AS
SELECT p.EmpresaId, p.SedeId, p.PagadorId, p.LineaServicioId, p.UsuarioOpId,
       CASE WHEN p.NumeroFactura IS NULL THEN 'SIN_FACTURAR'
            WHEN p.FechaRadicacion IS NULL THEN 'SIN_RADICAR'
            ELSE 'RADICADO' END AS Etapa,
       COUNT(*) AS Prestaciones,
       SUM(p.ValorTotal) AS Valor,
       AVG(CAST(DATEDIFF(DAY, COALESCE(p.FechaSalida, p.FechaRealizacion), p.FechaFactura) AS decimal(9,2))) AS DiasAtencionAFactura,
       AVG(CAST(DATEDIFF(DAY, p.FechaFactura, p.FechaRadicacion) AS decimal(9,2))) AS DiasFacturaARadicacion,
       AVG(CAST(DATEDIFF(DAY, COALESCE(p.FechaSalida, p.FechaRealizacion), SYSDATETIME()) AS decimal(9,2))) AS AntiguedadPromedio
FROM fact.Prestacion p
WHERE p.Anulado = 0
GROUP BY p.EmpresaId, p.SedeId, p.PagadorId, p.LineaServicioId, p.UsuarioOpId,
         CASE WHEN p.NumeroFactura IS NULL THEN 'SIN_FACTURAR' WHEN p.FechaRadicacion IS NULL THEN 'SIN_RADICAR' ELSE 'RADICADO' END;
GO

/* RLS: la política única (23_ddl_complementario.sql §12, script 0900) cubre estas tablas automáticamente. */
