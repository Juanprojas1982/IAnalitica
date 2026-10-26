/* 0150 · Funciones y procedimientos (cartera, costos, cuota y cierre de IA). */
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
/* ============================ TARIFA VIGENTE ============================ */
CREATE OR ALTER FUNCTION ia.fn_TarifaVigente (@EmpresaId int, @Fecha date)
RETURNS TABLE
AS RETURN
    SELECT TOP (1) t.TarifaId, t.ValorMTokEntradaCop, t.ValorMTokSalidaCop, t.ValorMTokCacheLecturaCop
    FROM ia.Tarifa t
    WHERE (t.EmpresaId = @EmpresaId OR t.EmpresaId IS NULL) AND t.VigenteDesde <= @Fecha
    ORDER BY CASE WHEN t.EmpresaId IS NULL THEN 1 ELSE 0 END, t.VigenteDesde DESC;
GO

/* ============================ RESERVA DE CUOTA (dos niveles) ============================ */
-- @Resultado: OK | OK_EXCEDENTE | CUPO_AGOTADO | TOPE_EXCEDENTE | TOPE_USUARIO | TOPE_PLATAFORMA | SIN_CONSENTIMIENTO | DESHABILITADO
CREATE OR ALTER PROCEDURE ia.usp_ReservarCuota
    @EmpresaId int, @UsuarioId int, @ModeloId smallint,
    @MontoCop decimal(16,2), @MontoUsd decimal(14,6),
    @ReservaId uniqueidentifier OUTPUT, @Resultado varchar(20) OUTPUT
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;
    DECLARE @Ahora datetime2(0) = SYSUTCDATETIME();
    DECLARE @Periodo int = YEAR(@Ahora) * 100 + MONTH(@Ahora);
    DECLARE @Min int = (SELECT CAST(Valor AS int) FROM ia.Parametro WHERE Clave = 'RESERVA_MINUTOS');
    DECLARE @Habilitado bit, @Cupo decimal(14,2), @Limite decimal(16,2), @TopeUsd decimal(12,4), @TopeUsuario decimal(14,2);
    SET @ReservaId = NULL;

    SELECT @Habilitado = ec.Habilitado,
           @Cupo = COALESCE(ec.CupoIncluidoCop, p.CupoIncluidoCop),
           @Limite = COALESCE(ec.CupoIncluidoCop, p.CupoIncluidoCop) + CASE WHEN ec.ExcedenteAutorizado = 1 THEN ec.TopeExcedenteCop ELSE 0 END,
           @TopeUsd = ec.TopeCostoProveedorUsd, @TopeUsuario = ec.TopeUsuarioDiarioCop
    FROM ia.EmpresaConfig ec JOIN ia.PlanComercial p ON p.PlanId = ec.PlanId
    WHERE ec.EmpresaId = @EmpresaId;

    IF ISNULL(@Habilitado, 0) = 0 BEGIN SET @Resultado = 'DESHABILITADO'; RETURN; END;

    IF EXISTS (SELECT 1 FROM ia.Modelo m JOIN ia.Proveedor pr ON pr.ProveedorId = m.ProveedorId
               WHERE m.ModeloId = @ModeloId AND pr.RequiereConsentimientoEspecifico = 1
                 AND NOT EXISTS (SELECT 1 FROM ia.Consentimiento c
                                 WHERE c.EmpresaId = @EmpresaId AND c.ProveedorId = pr.ProveedorId AND c.RevocadoEn IS NULL))
    BEGIN SET @Resultado = 'SIN_CONSENTIMIENTO'; RETURN; END;

    IF @TopeUsuario IS NOT NULL AND
       (SELECT ISNULL(SUM(u.ValorVentaCop), 0) FROM ia.UsoLlamada u
        WHERE u.EmpresaId = @EmpresaId AND u.UsuarioId = @UsuarioId AND u.Fecha >= CAST(@Ahora AS date)) + @MontoCop > @TopeUsuario
    BEGIN SET @Resultado = 'TOPE_USUARIO'; RETURN; END;

    BEGIN TRAN;
    -- liberar reservas vencidas
    UPDATE cp SET ReservadoCop = cp.ReservadoCop - r.Cop, ReservadoUsd = cp.ReservadoUsd - r.Usd
    FROM ia.ConsumoPeriodo cp
    JOIN (SELECT PeriodoKey, SUM(MontoCop) AS Cop, SUM(MontoUsd) AS Usd FROM ia.Reserva
          WHERE EmpresaId = @EmpresaId AND ExpiraEn < @Ahora GROUP BY PeriodoKey) r
      ON cp.EmpresaId = @EmpresaId AND cp.PeriodoKey = r.PeriodoKey;
    DELETE ia.Reserva WHERE EmpresaId = @EmpresaId AND ExpiraEn < @Ahora;

    IF NOT EXISTS (SELECT 1 FROM ia.ConsumoPeriodo WITH (UPDLOCK, HOLDLOCK) WHERE EmpresaId = @EmpresaId AND PeriodoKey = @Periodo)
        INSERT ia.ConsumoPeriodo (EmpresaId, PeriodoKey, CupoIncluidoCop) VALUES (@EmpresaId, @Periodo, @Cupo);

    UPDATE ia.ConsumoPeriodo
       SET ReservadoCop = ReservadoCop + @MontoCop,
           ReservadoUsd = ReservadoUsd + @MontoUsd
     WHERE EmpresaId = @EmpresaId AND PeriodoKey = @Periodo
       AND ValorConsumoCop + ReservadoCop + @MontoCop <= @Limite
       AND CostoProveedorUsd + ReservadoUsd + @MontoUsd <= @TopeUsd;

    IF @@ROWCOUNT = 1
    BEGIN
        SET @ReservaId = NEWID();
        INSERT ia.Reserva (ReservaId, EmpresaId, PeriodoKey, UsuarioId, MontoCop, MontoUsd, ExpiraEn)
        VALUES (@ReservaId, @EmpresaId, @Periodo, @UsuarioId, @MontoCop, @MontoUsd, DATEADD(MINUTE, @Min, @Ahora));
        SET @Resultado = CASE WHEN (SELECT ValorConsumoCop + ReservadoCop FROM ia.ConsumoPeriodo
                                    WHERE EmpresaId = @EmpresaId AND PeriodoKey = @Periodo) > @Cupo
                              THEN 'OK_EXCEDENTE' ELSE 'OK' END;
    END
    ELSE
        SELECT @Resultado = CASE
                 WHEN CostoProveedorUsd + ReservadoUsd + @MontoUsd > @TopeUsd THEN 'TOPE_PLATAFORMA'
                 WHEN @Limite = @Cupo THEN 'CUPO_AGOTADO'          -- sin excedente autorizado
                 ELSE 'TOPE_EXCEDENTE' END
        FROM ia.ConsumoPeriodo WHERE EmpresaId = @EmpresaId AND PeriodoKey = @Periodo;
    COMMIT;
END;
GO

/* ============================ LIQUIDACIÓN ============================ */
CREATE OR ALTER PROCEDURE ia.usp_LiquidarReserva
    @EmpresaId int, @ReservaId uniqueidentifier,
    @TokensEntrada bigint, @TokensSalida bigint, @TokensCache bigint,
    @CostoUsd decimal(14,6), @ValorCop decimal(16,2)
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;
    DECLARE @Ahora datetime2(0) = SYSUTCDATETIME();
    DECLARE @Periodo int = YEAR(@Ahora) * 100 + MONTH(@Ahora), @MontoCop decimal(16,2) = 0, @MontoUsd decimal(14,6) = 0;
    BEGIN TRAN;
    SELECT @Periodo = PeriodoKey, @MontoCop = MontoCop, @MontoUsd = MontoUsd
    FROM ia.Reserva WITH (UPDLOCK) WHERE ReservaId = @ReservaId AND EmpresaId = @EmpresaId;
    DELETE ia.Reserva WHERE ReservaId = @ReservaId AND EmpresaId = @EmpresaId;

    IF NOT EXISTS (SELECT 1 FROM ia.ConsumoPeriodo WITH (UPDLOCK, HOLDLOCK) WHERE EmpresaId = @EmpresaId AND PeriodoKey = @Periodo)
        INSERT ia.ConsumoPeriodo (EmpresaId, PeriodoKey, CupoIncluidoCop)
        SELECT @EmpresaId, @Periodo, COALESCE(ec.CupoIncluidoCop, p.CupoIncluidoCop)
        FROM ia.EmpresaConfig ec JOIN ia.PlanComercial p ON p.PlanId = ec.PlanId WHERE ec.EmpresaId = @EmpresaId;

    UPDATE ia.ConsumoPeriodo
       SET TokensEntrada = TokensEntrada + @TokensEntrada,
           TokensSalida  = TokensSalida  + @TokensSalida,
           TokensCache   = TokensCache   + @TokensCache,
           ValorConsumoCop   = ValorConsumoCop + @ValorCop,
           CostoProveedorUsd = CostoProveedorUsd + @CostoUsd,
           ReservadoCop = CASE WHEN ReservadoCop >= @MontoCop THEN ReservadoCop - @MontoCop ELSE 0 END,
           ReservadoUsd = CASE WHEN ReservadoUsd >= @MontoUsd THEN ReservadoUsd - @MontoUsd ELSE 0 END,
           EnExcedenteDesde = CASE WHEN EnExcedenteDesde IS NULL AND ValorConsumoCop + @ValorCop > CupoIncluidoCop
                                   THEN @Ahora ELSE EnExcedenteDesde END
     WHERE EmpresaId = @EmpresaId AND PeriodoKey = @Periodo;
    COMMIT;
END;
GO

/* ============================ CIERRE MENSUAL ============================ */
CREATE OR ALTER PROCEDURE ia.usp_CerrarPeriodo @PeriodoKey int
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;
    DECLARE @Trm decimal(12,2) = (SELECT CAST(Valor AS decimal(12,2)) FROM ia.Parametro WHERE Clave = 'TRM_USD_COP');
    INSERT ia.CorteMensual (EmpresaId, PeriodoKey, CupoIncluidoCop, ValorConsumoCop, ExcedenteCop, CostoProveedorUsd, TrmUsdCop)
    SELECT cp.EmpresaId, cp.PeriodoKey, cp.CupoIncluidoCop, cp.ValorConsumoCop,
           CASE WHEN cp.ValorConsumoCop > cp.CupoIncluidoCop THEN cp.ValorConsumoCop - cp.CupoIncluidoCop ELSE 0 END,
           cp.CostoProveedorUsd, @Trm
    FROM ia.ConsumoPeriodo cp
    WHERE cp.PeriodoKey = @PeriodoKey
      AND NOT EXISTS (SELECT 1 FROM ia.CorteMensual c WHERE c.EmpresaId = cp.EmpresaId AND c.PeriodoKey = cp.PeriodoKey);
END;
GO
