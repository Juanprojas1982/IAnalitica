/* 0160 · Procesamiento de cargas de hechos (F2): asientos y facturas. Los catálogos los procesa el worker con SQL generado
   desde sus definiciones (IAnalitica.Infrastructure/Ingesta). Contrato 05 §3–§6; spec 20 §3.
   Cada procedimiento es atómico por carga (una transacción) y reporta filas insertadas/actualizadas/eliminadas/rechazadas.
   Rechazo por fila: etl.Rechazo (sin el contenido de la fila, solo SourceKey y motivo). «Eliminadas» = filas de la ventana que el origen
   ya no envía; una fila rechazada conserva su versión anterior (no se borra por haber fallado la validación). */

CREATE OR ALTER PROCEDURE etl.usp_ProcesarAsientos
    @CargaId bigint, @Ins int OUTPUT, @Upd int OUTPUT, @Del int OUTPUT, @Rech int OUTPUT
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;
    DECLARE @Emp int, @Modo varchar(12), @Desde date, @Hasta date;
    SELECT @Emp = EmpresaId, @Modo = Modo, @Desde = VentanaDesde, @Hasta = VentanaHasta FROM etl.Carga WHERE CargaId = @CargaId;
    IF @Emp IS NULL THROW 50001, 'Carga inexistente.', 1;
    SELECT @Ins = 0, @Upd = 0, @Del = 0, @Rech = 0;
    DECLARE @ConVentana bit = CASE WHEN @Modo IN ('VENTANA', 'INICIAL') THEN 1 ELSE 0 END;

    CREATE TABLE #a (
        Ord int IDENTITY(1,1) PRIMARY KEY,
        SourceKey nvarchar(100) COLLATE DATABASE_DEFAULT NULL, RelKey nvarchar(100) COLLATE DATABASE_DEFAULT NULL, MovKey nvarchar(100) COLLATE DATABASE_DEFAULT NULL, Fecha date NULL,
        TipoCod varchar(20) COLLATE DATABASE_DEFAULT NULL, Numero varchar(30) COLLATE DATABASE_DEFAULT NULL, NumDoc varchar(40) COLLATE DATABASE_DEFAULT NULL, CuentaCod varchar(25) COLLATE DATABASE_DEFAULT NULL,
        CcCod varchar(30) COLLATE DATABASE_DEFAULT NULL, SedeCod varchar(30) COLLATE DATABASE_DEFAULT NULL, TerCod varchar(30) COLLATE DATABASE_DEFAULT NULL, CbCod varchar(30) COLLATE DATABASE_DEFAULT NULL,
        ArtCod varchar(40) COLLATE DATABASE_DEFAULT NULL, UsrCod varchar(40) COLLATE DATABASE_DEFAULT NULL, Debito decimal(19,4) NULL, Credito decimal(19,4) NULL,
        FechaCartera date NULL, FechaVenc date NULL, Estado tinyint NULL, Descripcion nvarchar(300) COLLATE DATABASE_DEFAULT NULL,
        FechaKey int NULL, Motivo nvarchar(400) COLLATE DATABASE_DEFAULT NULL);

    INSERT #a (SourceKey, RelKey, MovKey, Fecha, TipoCod, Numero, NumDoc, CuentaCod, CcCod, SedeCod, TerCod, CbCod, ArtCod, UsrCod,
               Debito, Credito, FechaCartera, FechaVenc, Estado, Descripcion)
    SELECT j.sourceKey, j.relacionadoSourceKey, j.movimientoSourceKey, j.fecha, j.tipoComprobanteCodigo, j.numeroComprobante, j.numDocumento,
           j.cuentaCodigo, j.centroCostoCodigo, j.sedeCodigo, j.terceroCodigo, j.cuentaBancariaCodigo, j.articuloCodigo, j.usuarioCodigo,
           j.debito, j.credito, j.fechaCartera, j.fechaVencimiento, j.estado, j.descripcion
    FROM stg.Payload p
    CROSS APPLY OPENJSON(p.Json) WITH (
        sourceKey nvarchar(100), relacionadoSourceKey nvarchar(100), movimientoSourceKey nvarchar(100), fecha date,
        tipoComprobanteCodigo varchar(20), numeroComprobante varchar(30), numDocumento varchar(40), cuentaCodigo varchar(25),
        centroCostoCodigo varchar(30), sedeCodigo varchar(30), terceroCodigo varchar(30), cuentaBancariaCodigo varchar(30),
        articuloCodigo varchar(40), usuarioCodigo varchar(40), debito decimal(19,4), credito decimal(19,4),
        fechaCartera date, fechaVencimiento date, estado tinyint, descripcion nvarchar(300)) j
    WHERE p.CargaId = @CargaId
    ORDER BY p.NumeroLote;

    -- 1. Validación por fila (la primera causa que aplique)
    UPDATE #a SET Motivo = 'CAMPO_REQUERIDO'
    WHERE SourceKey IS NULL OR MovKey IS NULL OR TipoCod IS NULL OR Numero IS NULL OR CuentaCod IS NULL
       OR Debito IS NULL OR Credito IS NULL OR Estado IS NULL OR Fecha IS NULL;
    UPDATE a SET FechaKey = f.FechaKey FROM #a a JOIN dim.Fecha f ON f.Fecha = a.Fecha;
    UPDATE #a SET Motivo = 'FECHA_INVALIDA' WHERE Motivo IS NULL AND FechaKey IS NULL;
    UPDATE #a SET Motivo = 'FUERA_DE_VENTANA' WHERE Motivo IS NULL AND @ConVentana = 1 AND (Fecha < @Desde OR Fecha > @Hasta);
    UPDATE #a SET Motivo = 'VALORES_INVALIDOS' WHERE Motivo IS NULL AND (Debito < 0 OR Credito < 0 OR (Debito > 0 AND Credito > 0));
    UPDATE #a SET Motivo = 'ESTADO_INVALIDO' WHERE Motivo IS NULL AND Estado NOT IN (1, 2);
    WITH d AS (SELECT Motivo, ROW_NUMBER() OVER (PARTITION BY SourceKey ORDER BY Ord DESC) AS rn FROM #a WHERE Motivo IS NULL)
    UPDATE d SET Motivo = 'DUPLICADO_EN_CARGA' WHERE rn > 1;
    UPDATE a SET Motivo = 'REFERENCIA_INEXISTENTE:tipoComprobanteCodigo' FROM #a a
    WHERE Motivo IS NULL AND NOT EXISTS (SELECT 1 FROM dim.TipoComprobante t WHERE t.EmpresaId = @Emp AND t.CodigoOrigen = a.TipoCod);
    UPDATE a SET Motivo = 'REFERENCIA_INEXISTENTE:cuentaCodigo' FROM #a a
    WHERE Motivo IS NULL AND NOT EXISTS (SELECT 1 FROM dim.CuentaContable c WHERE c.EmpresaId = @Emp AND c.Codigo = a.CuentaCod);

    -- 2. Un comprobante con alguna línea rechazada no entra incompleto
    UPDATE a SET Motivo = 'COMPROBANTE_INCOMPLETO' FROM #a a
    WHERE a.Motivo IS NULL AND EXISTS (SELECT 1 FROM #a r WHERE r.MovKey = a.MovKey AND r.Motivo IS NOT NULL);

    -- 3. Cuadre por comprobante (solo líneas confirmadas), tolerancia 0,01
    UPDATE a SET Motivo = 'COMPROBANTE_DESCUADRADO' FROM #a a
    WHERE a.Motivo IS NULL AND a.MovKey IN (
        SELECT MovKey FROM #a WHERE Motivo IS NULL AND Estado = 1 GROUP BY MovKey HAVING ABS(SUM(Debito) - SUM(Credito)) > 0.01);

    INSERT etl.Rechazo (CargaId, EmpresaId, SourceKey, Motivo)
    SELECT @CargaId, @Emp, SourceKey, Motivo FROM #a WHERE Motivo IS NOT NULL;
    SET @Rech = @@ROWCOUNT;

    SELECT a.SourceKey, a.RelKey, a.MovKey, a.FechaKey, f.PeriodoKey, tc.TipoComprobanteId, a.Numero, a.NumDoc, c.CuentaId,
           cc.CentroCostoId, s.SedeId, t.TerceroId, cb.CuentaBancariaId, ar.ArticuloId, u.UsuarioOpId,
           a.Debito, a.Credito, a.FechaCartera, a.FechaVenc, a.Estado, a.Descripcion
    INTO #ok
    FROM #a a
    JOIN dim.Fecha f ON f.FechaKey = a.FechaKey
    JOIN dim.TipoComprobante tc ON tc.EmpresaId = @Emp AND tc.CodigoOrigen = a.TipoCod
    JOIN dim.CuentaContable c ON c.EmpresaId = @Emp AND c.Codigo = a.CuentaCod
    LEFT JOIN dim.CentroCosto cc ON cc.EmpresaId = @Emp AND cc.CodigoOrigen = a.CcCod
    LEFT JOIN dim.Sede s ON s.EmpresaId = @Emp AND s.CodigoOrigen = a.SedeCod
    LEFT JOIN dim.Tercero t ON t.EmpresaId = @Emp AND t.CodigoOrigen = a.TerCod
    LEFT JOIN dim.CuentaBancaria cb ON cb.EmpresaId = @Emp AND cb.CodigoOrigen = a.CbCod
    LEFT JOIN dim.Articulo ar ON ar.EmpresaId = @Emp AND ar.CodigoOrigen = a.ArtCod
    LEFT JOIN dim.UsuarioOperativo u ON u.EmpresaId = @Emp AND u.CodigoOrigen = a.UsrCod
    WHERE a.Motivo IS NULL;
    CREATE UNIQUE CLUSTERED INDEX CX ON #ok (SourceKey);

    BEGIN TRAN;
    UPDATE t SET RelacionadoSourceKey = o.RelKey, MovimientoSourceKey = o.MovKey, FechaKey = o.FechaKey, PeriodoKey = o.PeriodoKey,
                 TipoComprobanteId = o.TipoComprobanteId, NumeroComprobante = o.Numero, NumDocumento = o.NumDoc, CuentaId = o.CuentaId,
                 CentroCostoId = o.CentroCostoId, SedeId = o.SedeId, TerceroId = o.TerceroId, CuentaBancariaId = o.CuentaBancariaId,
                 ArticuloId = o.ArticuloId, UsuarioOpId = o.UsuarioOpId, Debito = o.Debito, Credito = o.Credito,
                 FechaCartera = o.FechaCartera, FechaVencimiento = o.FechaVenc, Estado = o.Estado, Descripcion = o.Descripcion, CargaId = @CargaId
    FROM fact.AsientoLinea t JOIN #ok o ON t.EmpresaId = @Emp AND t.SourceKey = o.SourceKey;
    SET @Upd = @@ROWCOUNT;

    INSERT fact.AsientoLinea (EmpresaId, SourceKey, RelacionadoSourceKey, MovimientoSourceKey, FechaKey, PeriodoKey, TipoComprobanteId,
           NumeroComprobante, NumDocumento, CuentaId, CentroCostoId, SedeId, TerceroId, CuentaBancariaId, ArticuloId, UsuarioOpId,
           Debito, Credito, FechaCartera, FechaVencimiento, Estado, Descripcion, CargaId)
    SELECT @Emp, o.SourceKey, o.RelKey, o.MovKey, o.FechaKey, o.PeriodoKey, o.TipoComprobanteId, o.Numero, o.NumDoc, o.CuentaId,
           o.CentroCostoId, o.SedeId, o.TerceroId, o.CuentaBancariaId, o.ArticuloId, o.UsuarioOpId, o.Debito, o.Credito,
           o.FechaCartera, o.FechaVenc, o.Estado, o.Descripcion, @CargaId
    FROM #ok o
    WHERE NOT EXISTS (SELECT 1 FROM fact.AsientoLinea t WHERE t.EmpresaId = @Emp AND t.SourceKey = o.SourceKey);
    SET @Ins = @@ROWCOUNT;

    IF @ConVentana = 1
    BEGIN
        DECLARE @kd int = YEAR(@Desde) * 10000 + MONTH(@Desde) * 100 + DAY(@Desde);
        DECLARE @kh int = YEAR(@Hasta) * 10000 + MONTH(@Hasta) * 100 + DAY(@Hasta);
        DELETE t FROM fact.AsientoLinea t
        WHERE t.EmpresaId = @Emp AND t.FechaKey BETWEEN @kd AND @kh
          AND NOT EXISTS (SELECT 1 FROM #a a WHERE a.SourceKey = t.SourceKey);
        SET @Del = @@ROWCOUNT;
    END
    COMMIT;
END;
GO

CREATE OR ALTER PROCEDURE etl.usp_ProcesarFacturas
    @CargaId bigint, @Ins int OUTPUT, @Upd int OUTPUT, @Del int OUTPUT, @Rech int OUTPUT
AS
BEGIN
    SET NOCOUNT, XACT_ABORT ON;
    DECLARE @Emp int, @Modo varchar(12), @Desde date, @Hasta date;
    SELECT @Emp = EmpresaId, @Modo = Modo, @Desde = VentanaDesde, @Hasta = VentanaHasta FROM etl.Carga WHERE CargaId = @CargaId;
    IF @Emp IS NULL THROW 50001, 'Carga inexistente.', 1;
    SELECT @Ins = 0, @Upd = 0, @Del = 0, @Rech = 0;
    DECLARE @ConVentana bit = CASE WHEN @Modo IN ('VENTANA', 'INICIAL') THEN 1 ELSE 0 END;

    CREATE TABLE #f (
        Ord int IDENTITY(1,1) PRIMARY KEY,
        SourceKey nvarchar(100) COLLATE DATABASE_DEFAULT NULL, NumDoc varchar(30) COLLATE DATABASE_DEFAULT NULL, AdmKey varchar(30) COLLATE DATABASE_DEFAULT NULL, FechaEmision date NULL, FechaPrest date NULL, FechaRips date NULL,
        CuentaCobro varchar(30) COLLATE DATABASE_DEFAULT NULL, PagCod varchar(30) COLLATE DATABASE_DEFAULT NULL, ConCod varchar(30) COLLATE DATABASE_DEFAULT NULL, SedeCod varchar(30) COLLATE DATABASE_DEFAULT NULL, AlmCod varchar(30) COLLATE DATABASE_DEFAULT NULL,
        CcCod varchar(30) COLLATE DATABASE_DEFAULT NULL, LsCod varchar(30) COLLATE DATABASE_DEFAULT NULL, SrvCod varchar(40) COLLATE DATABASE_DEFAULT NULL, ProCod varchar(30) COLLATE DATABASE_DEFAULT NULL, UsrCod varchar(40) COLLATE DATABASE_DEFAULT NULL,
        Cantidad decimal(18,4) NULL, ValorServicio decimal(19,4) NULL, Descuento decimal(19,4) NULL, CuotaModeradora decimal(19,4) NULL,
        Iva decimal(19,4) NULL, ValorNeto decimal(19,4) NULL, Costo decimal(19,4) NULL, Anulada bit NULL, EstadoFE tinyint NULL,
        EmisionKey int NULL, PrestKey int NULL, RipsKey int NULL, Motivo nvarchar(400) COLLATE DATABASE_DEFAULT NULL);

    INSERT #f (SourceKey, NumDoc, AdmKey, FechaEmision, FechaPrest, FechaRips, CuentaCobro, PagCod, ConCod, SedeCod, AlmCod, CcCod, LsCod,
               SrvCod, ProCod, UsrCod, Cantidad, ValorServicio, Descuento, CuotaModeradora, Iva, ValorNeto, Costo, Anulada, EstadoFE)
    SELECT j.sourceKey, j.numeroDocumento, j.admisionSourceKey, j.fechaEmision, j.fechaPrestacion, j.fechaEnvioRips, j.cuentaCobro,
           j.pagadorCodigo, j.contratoCodigo, j.sedeCodigo, j.almacenCodigo, j.centroCostoCodigo, j.lineaServicioCodigo, j.servicioCodigo,
           j.profesionalCodigo, j.usuarioCodigo, j.cantidad, j.valorServicio, j.descuento, j.cuotaModeradora, j.iva, j.valorNeto,
           j.vrCostoOrigen, j.anulada, j.estadoFE
    FROM stg.Payload p
    CROSS APPLY OPENJSON(p.Json) WITH (
        sourceKey nvarchar(100), numeroDocumento varchar(30), admisionSourceKey varchar(30), fechaEmision date, fechaPrestacion date,
        fechaEnvioRips date, cuentaCobro varchar(30), pagadorCodigo varchar(30), contratoCodigo varchar(30), sedeCodigo varchar(30),
        almacenCodigo varchar(30), centroCostoCodigo varchar(30), lineaServicioCodigo varchar(30), servicioCodigo varchar(40),
        profesionalCodigo varchar(30), usuarioCodigo varchar(40), cantidad decimal(18,4), valorServicio decimal(19,4),
        descuento decimal(19,4), cuotaModeradora decimal(19,4), iva decimal(19,4), valorNeto decimal(19,4), vrCostoOrigen decimal(19,4),
        anulada bit, estadoFE tinyint) j
    WHERE p.CargaId = @CargaId
    ORDER BY p.NumeroLote;

    UPDATE #f SET Motivo = 'CAMPO_REQUERIDO'
    WHERE SourceKey IS NULL OR NumDoc IS NULL OR FechaEmision IS NULL OR PagCod IS NULL OR SedeCod IS NULL
       OR Cantidad IS NULL OR ValorServicio IS NULL OR ValorNeto IS NULL OR Anulada IS NULL;
    UPDATE f SET EmisionKey = d.FechaKey FROM #f f JOIN dim.Fecha d ON d.Fecha = f.FechaEmision;
    UPDATE f SET PrestKey = d.FechaKey FROM #f f JOIN dim.Fecha d ON d.Fecha = f.FechaPrest;
    UPDATE f SET RipsKey = d.FechaKey FROM #f f JOIN dim.Fecha d ON d.Fecha = f.FechaRips;
    UPDATE #f SET Motivo = 'FECHA_INVALIDA' WHERE Motivo IS NULL AND EmisionKey IS NULL;
    UPDATE #f SET Motivo = 'FUERA_DE_VENTANA' WHERE Motivo IS NULL AND @ConVentana = 1 AND (FechaEmision < @Desde OR FechaEmision > @Hasta);
    WITH d AS (SELECT Motivo, ROW_NUMBER() OVER (PARTITION BY SourceKey ORDER BY Ord DESC) AS rn FROM #f WHERE Motivo IS NULL)
    UPDATE d SET Motivo = 'DUPLICADO_EN_CARGA' WHERE rn > 1;
    UPDATE f SET Motivo = 'REFERENCIA_INEXISTENTE:pagadorCodigo' FROM #f f
    WHERE Motivo IS NULL AND NOT EXISTS (SELECT 1 FROM dim.Pagador p WHERE p.EmpresaId = @Emp AND p.CodigoOrigen = f.PagCod);
    UPDATE f SET Motivo = 'REFERENCIA_INEXISTENTE:sedeCodigo' FROM #f f
    WHERE Motivo IS NULL AND NOT EXISTS (SELECT 1 FROM dim.Sede s WHERE s.EmpresaId = @Emp AND s.CodigoOrigen = f.SedeCod);

    INSERT etl.Rechazo (CargaId, EmpresaId, SourceKey, Motivo)
    SELECT @CargaId, @Emp, SourceKey, Motivo FROM #f WHERE Motivo IS NOT NULL;
    SET @Rech = @@ROWCOUNT;

    SELECT f.SourceKey, f.NumDoc, f.AdmKey, f.EmisionKey, f.PrestKey, f.RipsKey, f.CuentaCobro, p.PagadorId, c.ContratoId, s.SedeId,
           al.AlmacenId, cc.CentroCostoId, ls.LineaServicioId, sv.ServicioId, pr.ProfesionalId, u.UsuarioOpId,
           f.Cantidad, f.ValorServicio, ISNULL(f.Descuento, 0) AS Descuento, ISNULL(f.CuotaModeradora, 0) AS CuotaModeradora,
           ISNULL(f.Iva, 0) AS Iva, f.ValorNeto, f.Costo, f.Anulada, f.EstadoFE
    INTO #ok
    FROM #f f
    JOIN dim.Pagador p ON p.EmpresaId = @Emp AND p.CodigoOrigen = f.PagCod
    JOIN dim.Sede s ON s.EmpresaId = @Emp AND s.CodigoOrigen = f.SedeCod
    LEFT JOIN dim.Contrato c ON c.EmpresaId = @Emp AND c.CodigoOrigen = f.ConCod
    LEFT JOIN dim.Almacen al ON al.EmpresaId = @Emp AND al.CodigoOrigen = f.AlmCod
    LEFT JOIN dim.CentroCosto cc ON cc.EmpresaId = @Emp AND cc.CodigoOrigen = f.CcCod
    LEFT JOIN dim.LineaServicio ls ON ls.EmpresaId = @Emp AND ls.CodigoOrigen = f.LsCod
    LEFT JOIN dim.Servicio sv ON sv.EmpresaId = @Emp AND sv.CodigoOrigen = f.SrvCod
    LEFT JOIN dim.Profesional pr ON pr.EmpresaId = @Emp AND pr.CodigoOrigen = f.ProCod
    LEFT JOIN dim.UsuarioOperativo u ON u.EmpresaId = @Emp AND u.CodigoOrigen = f.UsrCod
    WHERE f.Motivo IS NULL;
    CREATE UNIQUE CLUSTERED INDEX CX ON #ok (SourceKey);

    BEGIN TRAN;
    UPDATE t SET NumeroDocumento = o.NumDoc, AdmisionSourceKey = o.AdmKey, FechaEmisionKey = o.EmisionKey, FechaPrestacionKey = o.PrestKey,
                 FechaRadicacionKey = o.RipsKey, CuentaCobro = o.CuentaCobro, PagadorId = o.PagadorId, ContratoId = o.ContratoId,
                 SedeId = o.SedeId, AlmacenId = o.AlmacenId, CentroCostoId = o.CentroCostoId, LineaServicioId = o.LineaServicioId,
                 ServicioId = o.ServicioId, ProfesionalId = o.ProfesionalId, UsuarioOpId = o.UsuarioOpId, Cantidad = o.Cantidad,
                 ValorServicio = o.ValorServicio, Descuento = o.Descuento, CuotaModeradora = o.CuotaModeradora, Iva = o.Iva,
                 ValorNeto = o.ValorNeto, CostoDirecto = o.Costo, Anulada = o.Anulada, EstadoFE = o.EstadoFE, CargaId = @CargaId
    FROM fact.FacturaLinea t JOIN #ok o ON t.EmpresaId = @Emp AND t.SourceKey = o.SourceKey;
    SET @Upd = @@ROWCOUNT;

    INSERT fact.FacturaLinea (EmpresaId, SourceKey, NumeroDocumento, AdmisionSourceKey, FechaEmisionKey, FechaPrestacionKey, FechaRadicacionKey,
           CuentaCobro, PagadorId, ContratoId, SedeId, AlmacenId, CentroCostoId, LineaServicioId, ServicioId, ProfesionalId, UsuarioOpId,
           Cantidad, ValorServicio, Descuento, CuotaModeradora, Iva, ValorNeto, CostoDirecto, Anulada, EstadoFE, CargaId)
    SELECT @Emp, o.SourceKey, o.NumDoc, o.AdmKey, o.EmisionKey, o.PrestKey, o.RipsKey, o.CuentaCobro, o.PagadorId, o.ContratoId, o.SedeId,
           o.AlmacenId, o.CentroCostoId, o.LineaServicioId, o.ServicioId, o.ProfesionalId, o.UsuarioOpId, o.Cantidad, o.ValorServicio,
           o.Descuento, o.CuotaModeradora, o.Iva, o.ValorNeto, o.Costo, o.Anulada, o.EstadoFE, @CargaId
    FROM #ok o
    WHERE NOT EXISTS (SELECT 1 FROM fact.FacturaLinea t WHERE t.EmpresaId = @Emp AND t.SourceKey = o.SourceKey);
    SET @Ins = @@ROWCOUNT;

    IF @ConVentana = 1
    BEGIN
        DECLARE @kd int = YEAR(@Desde) * 10000 + MONTH(@Desde) * 100 + DAY(@Desde);
        DECLARE @kh int = YEAR(@Hasta) * 10000 + MONTH(@Hasta) * 100 + DAY(@Hasta);
        DELETE t FROM fact.FacturaLinea t
        WHERE t.EmpresaId = @Emp AND t.FechaEmisionKey BETWEEN @kd AND @kh
          AND NOT EXISTS (SELECT 1 FROM #f f WHERE f.SourceKey = t.SourceKey);
        SET @Del = @@ROWCOUNT;
    END
    COMMIT;
END;
GO
