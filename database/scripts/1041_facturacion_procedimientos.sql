/* 1041 · Facturación: procedimientos (08 §30.3). Requiere 1040.
   Los procedimientos son de dbo, igual que las tablas: el encadenamiento de propiedad salta los DENY de rol_app, así que CADA UNO valida
   el actor (superadmin o rol_etl). Errores de negocio con THROW 501xx (la aplicación los traduce a Result):
     50100 actor no autorizado · 50101 exige transacción · 50110-50119 emisión/anulación · 50120 motivo · 50130-50139 pagos
     50140 canon · 50150 parámetro · 50160-50169 condición comercial.
   Auditoría: aud.CambioAdministrativo (UsuarioId 0 = proceso automático del sistema [S]). */

-- ---------------------------------------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE fac.usp_SiguienteNumero @Numero varchar(20) OUTPUT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF IS_MEMBER(N'rol_etl') = 0 AND ISNULL(CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit), 0) = 0 THROW 50100, 'Solo superadmin.', 1;
    IF @@TRANCOUNT = 0 THROW 50101, 'La numeración exige una transacción abierta (el número se devuelve con el rollback).', 1;
    DECLARE @n int;
    UPDATE fac.Consecutivo SET @n = Ultimo = Ultimo + 1 WHERE Serie = 'CC';   -- el bloqueo de fila serializa; sin huecos por fallos
    IF @n IS NULL THROW 50102, 'No existe la serie CC.', 1;
    SET @Numero = 'CC-' + RIGHT('000000' + CAST(@n AS varchar(10)), 6);
END;
GO

-- ---------------------------------------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE fac.usp_EmitirCuentaCobro
    @CuentaCobroId int, @VersionFila binary(8) = NULL, @UsuarioId int = NULL, @Numero varchar(20) = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF IS_MEMBER(N'rol_etl') = 0 AND ISNULL(CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit), 0) = 0 THROW 50100, 'Solo superadmin.', 1;
    DECLARE @propia bit = IIF(@@TRANCOUNT = 0, 1, 0);
    IF @propia = 1 BEGIN TRAN;

    DECLARE @empresa int, @estado varchar(10), @version binary(8), @dias smallint, @periodo int;
    SELECT @empresa = EmpresaId, @estado = Estado, @version = CAST(VersionFila AS binary(8)), @dias = DiasPago, @periodo = PeriodoKey
    FROM fac.CuentaCobro WITH (UPDLOCK, HOLDLOCK) WHERE CuentaCobroId = @CuentaCobroId;
    IF @empresa IS NULL THROW 50110, 'La cuenta de cobro no existe.', 1;
    IF @estado <> 'BORRADOR' THROW 50111, 'Solo se emite una cuenta en BORRADOR.', 1;
    IF @VersionFila IS NOT NULL AND @VersionFila <> @version THROW 50112, 'La cuenta cambió desde que la consultó; recárguela.', 1;

    DECLARE @total decimal(16,2) = (SELECT ISNULL(SUM(ValorCop), 0) FROM fac.CuentaCobroLinea WHERE CuentaCobroId = @CuentaCobroId);
    IF @total <= 0 THROW 50113, 'La cuenta no tiene líneas o su total es cero.', 1;

    EXEC fac.usp_SiguienteNumero @Numero = @Numero OUTPUT;   -- falla si no hay transacción; aquí siempre la hay

    DECLARE @hoy date = CAST(DATEADD(HOUR, -5, SYSUTCDATETIME()) AS date);   -- America/Bogota = UTC-5 sin horario de verano
    UPDATE c SET
        Estado = 'EMITIDA', Numero = @Numero, TotalCop = @total,
        FechaEmision = @hoy, FechaVencimiento = DATEADD(DAY, @dias, @hoy),
        ClienteRazonSocial = e.RazonSocial, ClienteNit = e.Nit, ClienteDv = e.DigitoVerificacion, ClienteDireccion = e.Direccion,
        EmitidaPor = @UsuarioId, EmitidaEn = SYSUTCDATETIME()
    FROM fac.CuentaCobro c JOIN seg.Empresa e ON e.EmpresaId = c.EmpresaId
    WHERE c.CuentaCobroId = @CuentaCobroId;

    -- Con línea de excedente de IA, el corte queda facturado.
    UPDATE ia.CorteMensual SET Estado = 'FACTURADO'
    WHERE EmpresaId = @empresa AND Estado = 'CERRADO'
      AND PeriodoKey IN (SELECT PeriodoServicioKey FROM fac.CuentaCobroLinea WHERE CuentaCobroId = @CuentaCobroId AND Tipo = 'EXCEDENTE_IA');

    -- Outbox: el PDF y el correo los genera el worker (solo identificadores en el payload).
    INSERT etl.Trabajo (Tipo, EmpresaId, UsuarioId, ClaveExclusion, Prioridad, Payload)
    VALUES ('GENERAR_PDF_CUENTA_COBRO', @empresa, NULL, CONCAT('FAC:PDF|CC:', @CuentaCobroId), 4, CONCAT('{"cuentaCobroId":', @CuentaCobroId, '}'));

    INSERT aud.CambioAdministrativo (EmpresaId, UsuarioId, Entidad, ClaveEntidad, Accion, ValorNuevoJson)
    VALUES (@empresa, ISNULL(@UsuarioId, 0), 'fac.CuentaCobro', CAST(@CuentaCobroId AS nvarchar(200)), 'EMITIR',
            CONCAT('{"numero":"', @Numero, '","periodo":', @periodo, ',"total":', @total, '}'));

    IF @propia = 1 COMMIT;
END;
GO

-- ---------------------------------------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE fac.usp_ReversarAplicacion @AplicacionId int, @Motivo nvarchar(300), @UsuarioId int = NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF IS_MEMBER(N'rol_etl') = 0 AND ISNULL(CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit), 0) = 0 THROW 50100, 'Solo superadmin.', 1;
    IF LEN(LTRIM(RTRIM(ISNULL(@Motivo, N'')))) < 10 THROW 50120, 'El motivo debe tener al menos 10 caracteres.', 1;
    DECLARE @propia bit = IIF(@@TRANCOUNT = 0, 1, 0);
    IF @propia = 1 BEGIN TRAN;

    DECLARE @pago int, @cuenta int, @empresa int, @valor decimal(16,2);
    SELECT @pago = PagoId, @cuenta = CuentaCobroId, @empresa = EmpresaId, @valor = ValorCop FROM fac.AplicacionPago WHERE AplicacionId = @AplicacionId;
    IF @pago IS NULL THROW 50135, 'La aplicación no existe.', 1;

    -- Orden de bloqueo fijo: pago y luego cuenta (igual que usp_AplicarPago).
    SELECT PagoId FROM fac.Pago WITH (UPDLOCK, HOLDLOCK) WHERE PagoId = @pago;
    SELECT CuentaCobroId FROM fac.CuentaCobro WITH (UPDLOCK, HOLDLOCK) WHERE CuentaCobroId = @cuenta;
    IF NOT EXISTS (SELECT 1 FROM fac.AplicacionPago WITH (UPDLOCK) WHERE AplicacionId = @AplicacionId AND ReversadaEn IS NULL)
        THROW 50136, 'La aplicación ya fue reversada.', 1;

    UPDATE fac.AplicacionPago SET ReversadaPor = @UsuarioId, ReversadaEn = SYSUTCDATETIME(), MotivoReverso = @Motivo WHERE AplicacionId = @AplicacionId;
    UPDATE fac.Pago SET ValorAplicadoCop = ValorAplicadoCop - @valor WHERE PagoId = @pago;
    UPDATE fac.CuentaCobro SET PagadoCop = PagadoCop - @valor,
                               Estado = CASE WHEN Estado = 'PAGADA' THEN 'EMITIDA' ELSE Estado END
    WHERE CuentaCobroId = @cuenta;

    INSERT aud.CambioAdministrativo (EmpresaId, UsuarioId, Entidad, ClaveEntidad, Accion, ValorNuevoJson, Motivo)
    VALUES (@empresa, ISNULL(@UsuarioId, 0), 'fac.AplicacionPago', CAST(@AplicacionId AS nvarchar(200)), 'REVERSAR',
            CONCAT('{"pagoId":', @pago, ',"cuentaCobroId":', @cuenta, ',"valor":', @valor, '}'), @Motivo);
    IF @propia = 1 COMMIT;
END;
GO

-- ---------------------------------------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE fac.usp_AplicarPago
    @PagoId int, @CuentaCobroId int, @ValorCop decimal(16,2), @UsuarioId int = NULL, @AplicacionId int = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF IS_MEMBER(N'rol_etl') = 0 AND ISNULL(CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit), 0) = 0 THROW 50100, 'Solo superadmin.', 1;
    IF @ValorCop IS NULL OR @ValorCop <= 0 THROW 50130, 'El valor a aplicar debe ser mayor que cero.', 1;
    DECLARE @propia bit = IIF(@@TRANCOUNT = 0, 1, 0);
    IF @propia = 1 BEGIN TRAN;

    -- Orden de bloqueo fijo: pago y luego cuenta.
    DECLARE @empPago int, @estPago varchar(8), @libre decimal(16,2);
    SELECT @empPago = EmpresaId, @estPago = Estado, @libre = ValorCop - ValorAplicadoCop FROM fac.Pago WITH (UPDLOCK, HOLDLOCK) WHERE PagoId = @PagoId;
    DECLARE @empCuenta int, @estCuenta varchar(10), @saldo decimal(16,2);
    SELECT @empCuenta = EmpresaId, @estCuenta = Estado, @saldo = TotalCop - PagadoCop FROM fac.CuentaCobro WITH (UPDLOCK, HOLDLOCK) WHERE CuentaCobroId = @CuentaCobroId;

    IF @empPago IS NULL OR @estPago <> 'VIGENTE' THROW 50131, 'El pago no existe o está anulado.', 1;
    IF @empCuenta IS NULL OR @estCuenta <> 'EMITIDA' THROW 50132, 'Solo se aplica a una cuenta EMITIDA con saldo.', 1;
    IF @empPago <> @empCuenta THROW 50133, 'El pago y la cuenta son de empresas distintas.', 1;
    IF @ValorCop > @libre THROW 50134, 'El valor supera el saldo sin aplicar del pago.', 1;
    IF @ValorCop > @saldo THROW 50137, 'El valor supera el saldo de la cuenta.', 1;

    INSERT fac.AplicacionPago (EmpresaId, PagoId, CuentaCobroId, ValorCop, AplicadaPor) VALUES (@empPago, @PagoId, @CuentaCobroId, @ValorCop, @UsuarioId);
    SET @AplicacionId = SCOPE_IDENTITY();
    UPDATE fac.Pago SET ValorAplicadoCop = ValorAplicadoCop + @ValorCop WHERE PagoId = @PagoId;
    UPDATE fac.CuentaCobro SET PagadoCop = PagadoCop + @ValorCop,
                               Estado = CASE WHEN TotalCop - (PagadoCop + @ValorCop) = 0 THEN 'PAGADA' ELSE Estado END
    WHERE CuentaCobroId = @CuentaCobroId;

    INSERT aud.CambioAdministrativo (EmpresaId, UsuarioId, Entidad, ClaveEntidad, Accion, ValorNuevoJson)
    VALUES (@empPago, ISNULL(@UsuarioId, 0), 'fac.AplicacionPago', CAST(@AplicacionId AS nvarchar(200)), 'APLICAR',
            CONCAT('{"pagoId":', @PagoId, ',"cuentaCobroId":', @CuentaCobroId, ',"valor":', @ValorCop, '}'));
    IF @propia = 1 COMMIT;
END;
GO

-- ---------------------------------------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE fac.usp_RegistrarPago
    @EmpresaId int, @Clase varchar(10), @Medio varchar(14) = NULL, @FechaPago date, @ValorCop decimal(16,2),
    @Referencia nvarchar(60) = NULL, @SoporteRuta nvarchar(400) = NULL, @Observacion nvarchar(300) = NULL, @UsuarioId int,
    @PagoId int = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF IS_MEMBER(N'rol_etl') = 0 AND ISNULL(CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit), 0) = 0 THROW 50100, 'Solo superadmin.', 1;
    IF NOT EXISTS (SELECT 1 FROM seg.Empresa WHERE EmpresaId = @EmpresaId) THROW 50138, 'La empresa no existe.', 1;
    IF @FechaPago > CAST(DATEADD(HOUR, -5, SYSUTCDATETIME()) AS date) THROW 50139, 'La fecha del pago no puede ser futura.', 1;
    DECLARE @propia bit = IIF(@@TRANCOUNT = 0, 1, 0);
    IF @propia = 1 BEGIN TRAN;

    INSERT fac.Pago (EmpresaId, Clase, Medio, FechaPago, ValorCop, Referencia, SoporteRuta, Observacion, RegistradoPor)
    VALUES (@EmpresaId, @Clase, @Medio, @FechaPago, @ValorCop, @Referencia, @SoporteRuta, @Observacion, @UsuarioId);   -- CHECK/UX validan clase, medio, valor y referencia duplicada
    SET @PagoId = SCOPE_IDENTITY();

    INSERT aud.CambioAdministrativo (EmpresaId, UsuarioId, Entidad, ClaveEntidad, Accion, ValorNuevoJson)
    VALUES (@EmpresaId, @UsuarioId, 'fac.Pago', CAST(@PagoId AS nvarchar(200)), 'REGISTRAR_PAGO',
            CONCAT('{"clase":"', @Clase, '","fecha":"', CONVERT(char(10), @FechaPago, 23), '","valor":', @ValorCop, '}'));
    IF @propia = 1 COMMIT;
END;
GO

-- ---------------------------------------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE fac.usp_AnularPago @PagoId int, @Motivo nvarchar(300), @UsuarioId int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF IS_MEMBER(N'rol_etl') = 0 AND ISNULL(CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit), 0) = 0 THROW 50100, 'Solo superadmin.', 1;
    IF LEN(LTRIM(RTRIM(ISNULL(@Motivo, N'')))) < 10 THROW 50120, 'El motivo debe tener al menos 10 caracteres.', 1;
    DECLARE @propia bit = IIF(@@TRANCOUNT = 0, 1, 0);
    IF @propia = 1 BEGIN TRAN;

    DECLARE @empresa int, @estado varchar(8);
    SELECT @empresa = EmpresaId, @estado = Estado FROM fac.Pago WITH (UPDLOCK, HOLDLOCK) WHERE PagoId = @PagoId;
    IF @empresa IS NULL OR @estado <> 'VIGENTE' THROW 50131, 'El pago no existe o ya está anulado.', 1;

    DECLARE @apl int;   -- primero se reversan sus aplicaciones vigentes
    WHILE 1 = 1
    BEGIN
        SET @apl = (SELECT TOP (1) AplicacionId FROM fac.AplicacionPago WHERE PagoId = @PagoId AND ReversadaEn IS NULL ORDER BY AplicacionId);
        IF @apl IS NULL BREAK;
        EXEC fac.usp_ReversarAplicacion @AplicacionId = @apl, @Motivo = @Motivo, @UsuarioId = @UsuarioId;
    END;

    UPDATE fac.Pago SET Estado = 'ANULADO', AnuladoPor = @UsuarioId, AnuladoEn = SYSUTCDATETIME(), MotivoAnulacion = @Motivo WHERE PagoId = @PagoId;
    INSERT aud.CambioAdministrativo (EmpresaId, UsuarioId, Entidad, ClaveEntidad, Accion, Motivo)
    VALUES (@empresa, @UsuarioId, 'fac.Pago', CAST(@PagoId AS nvarchar(200)), 'ANULAR_PAGO', @Motivo);
    IF @propia = 1 COMMIT;
END;
GO

-- ---------------------------------------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE fac.usp_AnularCuentaCobro @CuentaCobroId int, @Motivo nvarchar(500), @UsuarioId int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF IS_MEMBER(N'rol_etl') = 0 AND ISNULL(CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit), 0) = 0 THROW 50100, 'Solo superadmin.', 1;
    IF LEN(LTRIM(RTRIM(ISNULL(@Motivo, N'')))) < 10 THROW 50120, 'El motivo debe tener al menos 10 caracteres.', 1;
    DECLARE @propia bit = IIF(@@TRANCOUNT = 0, 1, 0);
    IF @propia = 1 BEGIN TRAN;

    DECLARE @empresa int, @estado varchar(10), @numero varchar(20);
    SELECT @empresa = EmpresaId, @estado = Estado, @numero = Numero FROM fac.CuentaCobro WITH (UPDLOCK, HOLDLOCK) WHERE CuentaCobroId = @CuentaCobroId;
    IF @empresa IS NULL THROW 50110, 'La cuenta de cobro no existe.', 1;
    IF @estado NOT IN ('EMITIDA', 'PAGADA') THROW 50114, 'Solo se anula una cuenta EMITIDA o PAGADA; un borrador se descarta.', 1;

    DECLARE @apl int;   -- primero se reversan las aplicaciones (el pago queda como saldo a favor del cliente)
    WHILE 1 = 1
    BEGIN
        SET @apl = (SELECT TOP (1) AplicacionId FROM fac.AplicacionPago WHERE CuentaCobroId = @CuentaCobroId AND ReversadaEn IS NULL ORDER BY AplicacionId);
        IF @apl IS NULL BREAK;
        EXEC fac.usp_ReversarAplicacion @AplicacionId = @apl, @Motivo = @Motivo, @UsuarioId = @UsuarioId;
    END;

    UPDATE fac.CuentaCobro SET Estado = 'ANULADA', AnuladaPor = @UsuarioId, AnuladaEn = SYSUTCDATETIME(), MotivoAnulacion = @Motivo WHERE CuentaCobroId = @CuentaCobroId;
    UPDATE fac.Envio SET Estado = 'CANCELADO' WHERE CuentaCobroId = @CuentaCobroId AND Estado = 'PENDIENTE' AND Tipo IN ('CUENTA_COBRO', 'RECORDATORIO', 'AVISO_MORA');

    INSERT aud.CambioAdministrativo (EmpresaId, UsuarioId, Entidad, ClaveEntidad, Accion, ValorNuevoJson, Motivo)
    VALUES (@empresa, @UsuarioId, 'fac.CuentaCobro', CAST(@CuentaCobroId AS nvarchar(200)), 'ANULAR', CONCAT('{"numero":"', @numero, '"}'), @Motivo);
    IF @propia = 1 COMMIT;
END;
GO

-- ---------------------------------------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE fac.usp_GuardarCanonPlan @PlanId smallint, @VigenteDesde date, @CanonMensualCop decimal(14,2), @UsuarioId int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF ISNULL(CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit), 0) = 0 THROW 50100, 'Solo superadmin.', 1;
    DECLARE @propia bit = IIF(@@TRANCOUNT = 0, 1, 0);
    IF @propia = 1 BEGIN TRAN;

    DECLARE @id int, @antes decimal(14,2);
    SELECT @id = CanonPlanId, @antes = CanonMensualCop FROM fac.CanonPlan WITH (UPDLOCK, HOLDLOCK) WHERE PlanId = @PlanId AND VigenteDesde = @VigenteDesde;
    IF @id IS NULL
        INSERT fac.CanonPlan (PlanId, VigenteDesde, CanonMensualCop, CreadoPor) VALUES (@PlanId, @VigenteDesde, @CanonMensualCop, @UsuarioId);
    ELSE
    BEGIN
        IF EXISTS (SELECT 1 FROM fac.CuentaCobroLinea WHERE CanonPlanId = @id) THROW 50140, 'Ese canon ya se facturó; cree una vigencia nueva.', 1;
        UPDATE fac.CanonPlan SET CanonMensualCop = @CanonMensualCop WHERE CanonPlanId = @id;
    END;

    INSERT aud.CambioAdministrativo (EmpresaId, UsuarioId, Entidad, ClaveEntidad, Accion, ValorAnteriorJson, ValorNuevoJson)
    VALUES (NULL, @UsuarioId, 'fac.CanonPlan', CONCAT(@PlanId, '|', CONVERT(char(10), @VigenteDesde, 23)), 'CAMBIAR_CANON',
            IIF(@antes IS NULL, NULL, CONCAT('{"canon":', @antes, '}')), CONCAT('{"canon":', @CanonMensualCop, '}'));
    IF @propia = 1 COMMIT;
END;
GO

-- ---------------------------------------------------------------------------------------------------------------------------------
CREATE OR ALTER PROCEDURE fac.usp_GuardarParametro @Clave varchar(40), @Valor nvarchar(500), @UsuarioId int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF ISNULL(CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit), 0) = 0 THROW 50100, 'Solo superadmin.', 1;
    DECLARE @antes nvarchar(500) = (SELECT Valor FROM fac.Parametro WITH (UPDLOCK, HOLDLOCK) WHERE Clave = @Clave);
    IF @antes IS NULL THROW 50150, 'El parámetro no existe.', 1;
    IF @Clave IN ('REQUIERE_APROBACION', 'INCLUIR_EXCEDENTE_IA') AND @Valor NOT IN (N'0', N'1') THROW 50151, 'El valor debe ser 0 o 1.', 1;
    IF @Clave IN ('DIAS_PAGO_DEFECTO', 'DIA_PREVALIDACION', 'UMBRAL_CUADRE_IA_COP', 'VARIACION_MAX_PCT', 'RECORDATORIO_DIAS_ANTES')
       AND TRY_CAST(@Valor AS int) IS NULL THROW 50152, 'El valor debe ser un número entero.', 1;
    IF @Clave = 'DIA_PREVALIDACION' AND TRY_CAST(@Valor AS int) NOT BETWEEN 1 AND 28 THROW 50153, 'El día debe estar entre 1 y 28.', 1;
    IF @Clave IN ('HORA_CIERRE_IA', 'HORA_GENERACION') AND TRY_CAST(@Valor AS time(0)) IS NULL THROW 50154, 'Use el formato HH:MM.', 1;

    UPDATE fac.Parametro SET Valor = @Valor WHERE Clave = @Clave;
    INSERT aud.CambioAdministrativo (EmpresaId, UsuarioId, Entidad, ClaveEntidad, Accion, ValorAnteriorJson, ValorNuevoJson)
    VALUES (NULL, @UsuarioId, 'fac.Parametro', @Clave, 'CAMBIAR_PARAMETRO',
            (SELECT @antes AS valor FOR JSON PATH, WITHOUT_ARRAY_WRAPPER), (SELECT @Valor AS valor FOR JSON PATH, WITHOUT_ARRAY_WRAPPER));
END;
GO

-- ---------------------------------------------------------------------------------------------------------------------------------
-- Abre una condición comercial desde @VigenteDesde (puede ser a mitad de mes: se prorratea) y cierra la abierta el día anterior, sin solapamiento.
CREATE OR ALTER PROCEDURE fac.usp_GuardarCondicion
    @EmpresaId int, @VigenteDesde date, @PlanId smallint, @CanonPactadoCop decimal(14,2) = NULL, @DiasPago smallint = NULL,
    @RequiereAprobacion bit = NULL, @Facturable bit = 1, @ReferenciaContrato nvarchar(60) = NULL, @UsuarioId int,
    @CondicionId int = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    IF ISNULL(CAST(SESSION_CONTEXT(N'EsSuperAdmin') AS bit), 0) = 0 THROW 50100, 'Solo superadmin.', 1;
    IF NOT EXISTS (SELECT 1 FROM seg.Empresa WHERE EmpresaId = @EmpresaId) THROW 50138, 'La empresa no existe.', 1;
    DECLARE @propia bit = IIF(@@TRANCOUNT = 0, 1, 0);
    IF @propia = 1 BEGIN TRAN;

    DECLARE @abiertaId int, @abiertaDesde date, @ultimoHasta date;
    SELECT @abiertaId = CondicionId, @abiertaDesde = VigenteDesde FROM fac.CondicionComercial WITH (UPDLOCK, HOLDLOCK) WHERE EmpresaId = @EmpresaId AND VigenteHasta IS NULL;
    SELECT @ultimoHasta = MAX(VigenteHasta) FROM fac.CondicionComercial WITH (UPDLOCK, HOLDLOCK) WHERE EmpresaId = @EmpresaId;

    IF @abiertaId IS NOT NULL AND @VigenteDesde <= @abiertaDesde THROW 50160, 'La nueva vigencia debe empezar después de la condición abierta.', 1;
    IF @abiertaId IS NULL AND @ultimoHasta IS NOT NULL AND @VigenteDesde <= @ultimoHasta THROW 50160, 'La nueva vigencia se solapa con una anterior.', 1;
    -- No se reescribe un mes ya facturado: la nueva vigencia no puede caer en un periodo que ya tiene cuenta viva.
    IF EXISTS (SELECT 1 FROM fac.CuentaCobro WHERE EmpresaId = @EmpresaId AND Estado IN ('BORRADOR', 'EMITIDA', 'PAGADA')
                 AND PeriodoKey >= YEAR(@VigenteDesde) * 100 + MONTH(@VigenteDesde))
        THROW 50161, 'Ya existe una cuenta de cobro de ese periodo o posterior; anúlela o descártela antes.', 1;

    IF @abiertaId IS NOT NULL UPDATE fac.CondicionComercial SET VigenteHasta = DATEADD(DAY, -1, @VigenteDesde) WHERE CondicionId = @abiertaId;
    INSERT fac.CondicionComercial (EmpresaId, VigenteDesde, PlanId, CanonPactadoCop, DiasPago, RequiereAprobacion, Facturable, ReferenciaContrato, CreadoPor)
    VALUES (@EmpresaId, @VigenteDesde, @PlanId, @CanonPactadoCop, @DiasPago, @RequiereAprobacion, @Facturable, @ReferenciaContrato, @UsuarioId);
    SET @CondicionId = SCOPE_IDENTITY();

    INSERT aud.CambioAdministrativo (EmpresaId, UsuarioId, Entidad, ClaveEntidad, Accion, ValorNuevoJson)
    VALUES (@EmpresaId, @UsuarioId, 'fac.CondicionComercial', CAST(@CondicionId AS nvarchar(200)), 'CAMBIAR_CONDICION',
            CONCAT('{"desde":"', CONVERT(char(10), @VigenteDesde, 23), '","plan":', @PlanId, ',"facturable":', CAST(@Facturable AS int), '}'));
    IF @propia = 1 COMMIT;
END;
GO
