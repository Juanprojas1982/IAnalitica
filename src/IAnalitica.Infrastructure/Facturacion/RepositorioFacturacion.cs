using System.Data;
using System.Text.Json;
using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Facturacion;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.Data.SqlClient;

namespace IAnalitica.Infrastructure.Facturacion;

/// <summary>
/// Cuentas de cobro, pagos, envíos y alertas con <c>rol_app</c> y el contexto de sesión (RLS: el Admin{NIT} solo lee lo suyo; escribe solo el superadmin).
/// Las escrituras delicadas pasan por <c>fac.usp_*</c> (validan el actor y auditan). Lo que necesita el worker (PDF, correo, generación) se pide con un
/// trabajo de <c>etl.Trabajo</c> en la misma transacción (outbox).
/// </summary>
internal sealed class RepositorioFacturacion(IFabricaConexiones conexiones) : IRepositorioFacturacion
{
    private const string ColumnasCuenta = """
        c.CuentaCobroId, c.EmpresaId, e.RazonSocial AS EmpresaNombre, c.PeriodoKey, c.Numero, c.Estado, c.TotalCop, c.PagadoCop, c.SaldoCop,
        c.FechaEmision, c.FechaVencimiento, CAST(0 AS bit) AS EnMora, CAST(NULL AS varchar(10)) AS Edad, c.MotivosRetencion
        """;

    private sealed record FilaExtra(int? ReemplazaA, bool Pdf, string? MotivoAnulacion);
    private sealed record FilaPeriodo(int EmpresaId, int PeriodoKey);
    private sealed record FilaSaldo(int CuentaCobroId, decimal Saldo);

    // ------------------------------------------------------------------ consultas
    public async Task<IReadOnlyList<CuentaCobroResumen>> ListarCuentasAsync(FiltroCuentas f, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<CuentaCobroResumen>(new CommandDefinition($"""
            SELECT TOP (500) {ColumnasCuenta}
            FROM fac.CuentaCobro c JOIN seg.Empresa e ON e.EmpresaId = c.EmpresaId
            WHERE (@empresaId IS NULL OR c.EmpresaId = @empresaId) AND (@periodo IS NULL OR c.PeriodoKey = @periodo) AND (@estado IS NULL OR c.Estado = @estado)
              AND (@visibles = 0 OR c.Estado IN ('EMITIDA', 'PAGADA', 'ANULADA')) AND (@conSaldo = 0 OR c.SaldoCop > 0)
            ORDER BY c.PeriodoKey DESC, c.CuentaCobroId DESC
            """, new { empresaId = f.EmpresaId, periodo = f.PeriodoKey, estado = f.Estado, visibles = f.SoloEmitidasYPagadas, conSaldo = f.SoloConSaldo }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<CuentaCobroDetalle?> ObtenerCuentaAsync(int cuentaCobroId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        var cuenta = await cn.QueryFirstOrDefaultAsync<CuentaCobroResumen>(new CommandDefinition($"""
            SELECT {ColumnasCuenta} FROM fac.CuentaCobro c JOIN seg.Empresa e ON e.EmpresaId = c.EmpresaId WHERE c.CuentaCobroId = @cuentaCobroId
            """, new { cuentaCobroId }, cancellationToken: ct)).ConfigureAwait(false);
        if (cuenta is null) return null;

        var extra = await cn.QueryFirstAsync<FilaExtra>(new CommandDefinition(
            "SELECT ReemplazaA, CAST(IIF(PdfRuta IS NULL, 0, 1) AS bit) AS Pdf, MotivoAnulacion FROM fac.CuentaCobro WHERE CuentaCobroId = @cuentaCobroId", new { cuentaCobroId }, cancellationToken: ct)).ConfigureAwait(false);
        var lineas = (await cn.QueryAsync<LineaVista>(new CommandDefinition(
            "SELECT Concepto, DiasFacturados, DiasPeriodo, ValorCop FROM fac.CuentaCobroLinea WHERE CuentaCobroId = @cuentaCobroId ORDER BY Orden", new { cuentaCobroId }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var envios = (await cn.QueryAsync<EnvioVista>(new CommandDefinition("""
            SELECT EnvioId, CuentaCobroId, CAST(NULL AS varchar(20)) AS CuentaNumero, Tipo, Hito, Estado, EsReenvio, ConAdjunto, Intentos, CodigoError, CreadoEn, EnviadoEn
            FROM fac.Envio WHERE CuentaCobroId = @cuentaCobroId ORDER BY CreadoEn DESC
            """, new { cuentaCobroId }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var aplicaciones = (await cn.QueryAsync<AplicacionVista>(new CommandDefinition("""
            SELECT AplicacionId, PagoId, ValorCop, AplicadaEn, CAST(IIF(ReversadaEn IS NULL, 0, 1) AS bit) AS Reversada
            FROM fac.AplicacionPago WHERE CuentaCobroId = @cuentaCobroId ORDER BY AplicacionId
            """, new { cuentaCobroId }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        return new CuentaCobroDetalle(cuenta, lineas, envios, aplicaciones, extra.ReemplazaA, extra.Pdf, extra.MotivoAnulacion);
    }

    public async Task<IReadOnlyList<PagoVista>> ListarPagosAsync(int? empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<PagoVista>(new CommandDefinition("""
            SELECT TOP (500) p.PagoId, p.EmpresaId, e.RazonSocial AS EmpresaNombre, p.Clase, p.Medio, p.FechaPago, p.ValorCop, p.Referencia,
                   p.ValorAplicadoCop, p.SaldoSinAplicarCop, p.Estado, p.Observacion
            FROM fac.Pago p JOIN seg.Empresa e ON e.EmpresaId = p.EmpresaId
            WHERE @empresaId IS NULL OR p.EmpresaId = @empresaId ORDER BY p.FechaPago DESC, p.PagoId DESC
            """, new { empresaId }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<decimal> SaldoAFavorAsync(int? empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
            "SELECT SUM(SaldoSinAplicarCop) FROM fac.Pago WHERE Estado = 'VIGENTE' AND (@empresaId IS NULL OR EmpresaId = @empresaId)", new { empresaId }, cancellationToken: ct)).ConfigureAwait(false) ?? 0m;
    }

    public async Task<IReadOnlyList<EnvioVista>> ListarEnviosAsync(bool soloFallidos, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<EnvioVista>(new CommandDefinition("""
            SELECT TOP (200) v.EnvioId, v.CuentaCobroId, c.Numero AS CuentaNumero, v.Tipo, v.Hito, v.Estado, v.EsReenvio, v.ConAdjunto, v.Intentos, v.CodigoError, v.CreadoEn, v.EnviadoEn
            FROM fac.Envio v JOIN fac.CuentaCobro c ON c.CuentaCobroId = v.CuentaCobroId
            WHERE @soloFallidos = 0 OR v.Estado IN ('FALLIDO', 'REBOTADO') ORDER BY v.CreadoEn DESC
            """, new { soloFallidos }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<IReadOnlyList<AlertaVista>> ListarAlertasAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<AlertaVista>(new CommandDefinition("""
            SELECT TOP (300) a.AlertaId, a.EmpresaId, e.RazonSocial AS EmpresaNombre, a.PeriodoKey, a.CuentaCobroId, a.Codigo, a.Detalle, a.CreadaEn
            FROM fac.Alerta a LEFT JOIN seg.Empresa e ON e.EmpresaId = a.EmpresaId WHERE a.AtendidaEn IS NULL ORDER BY a.CreadaEn DESC
            """, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    // ------------------------------------------------------------------ cuentas de cobro
    public async Task<Result<string>> EmitirAsync(int cuentaCobroId, byte[]? versionFila, int usuarioId, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("CuentaCobroId", cuentaCobroId); p.Add("VersionFila", versionFila, DbType.Binary, size: 8); p.Add("UsuarioId", usuarioId);
        p.Add("Numero", dbType: DbType.AnsiString, size: 20, direction: ParameterDirection.Output);
        try
        {
            await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
            await cn.ExecuteAsync(new CommandDefinition("fac.usp_EmitirCuentaCobro", p, commandType: CommandType.StoredProcedure, cancellationToken: ct)).ConfigureAwait(false);
            return p.Get<string>("Numero");
        }
        catch (SqlException e) when (RepositorioFacturacionConfig.EsNegocio(e)) { return Result<string>.Falla(RepositorioFacturacionConfig.Traducir(e)); }
    }

    public async Task<Result> DescartarBorradorAsync(int cuentaCobroId, string motivo, int usuarioId, CancellationToken ct)
        => await EnTransaccionAsync(async (cn, tx) =>
        {
            var empresa = await cn.ExecuteScalarAsync<int?>(new CommandDefinition("""
                UPDATE fac.CuentaCobro SET Estado = 'DESCARTADA' OUTPUT inserted.EmpresaId WHERE CuentaCobroId = @cuentaCobroId AND Estado = 'BORRADOR'
                """, new { cuentaCobroId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (empresa is null) return Result.Falla(Error.Conflicto(CodigosFacturacion.Estado, "Solo se descarta una cuenta en BORRADOR."));
            await cn.ExecuteAsync(new CommandDefinition("UPDATE fac.Alerta SET AtendidaPor = @usuarioId, AtendidaEn = SYSUTCDATETIME() WHERE CuentaCobroId = @cuentaCobroId AND AtendidaEn IS NULL",
                new { cuentaCobroId, usuarioId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            await AuditarAsync(cn, tx, empresa, usuarioId, "fac.CuentaCobro", cuentaCobroId.ToString(), "DESCARTAR", motivo, ct).ConfigureAwait(false);
            return Result.Ok();
        }, ct).ConfigureAwait(false);

    public async Task<Result> RegenerarBorradorAsync(int cuentaCobroId, int usuarioId, CancellationToken ct)
        => await EnTransaccionAsync(async (cn, tx) =>
        {
            var empresa = await cn.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT EmpresaId FROM fac.CuentaCobro WHERE CuentaCobroId = @cuentaCobroId AND Estado = 'BORRADOR'",
                new { cuentaCobroId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (empresa is null) return Result.Falla(Error.Conflicto(CodigosFacturacion.Estado, "Solo se regenera una cuenta en BORRADOR."));
            await EncolarAsync(cn, tx, TiposTrabajo.RegenerarBorrador, empresa, usuarioId, $"FAC:REG|CC:{cuentaCobroId}", new { cuentaCobroId }, ct).ConfigureAwait(false);
            await AuditarAsync(cn, tx, empresa, usuarioId, "fac.CuentaCobro", cuentaCobroId.ToString(), "REGENERAR", null, ct).ConfigureAwait(false);
            return Result.Ok();
        }, ct).ConfigureAwait(false);

    public async Task<Result> AnularAsync(int cuentaCobroId, string motivo, bool emitirReemplazo, int usuarioId, CancellationToken ct)
        => await EnTransaccionAsync(async (cn, tx) =>
        {
            var c = await cn.QueryFirstOrDefaultAsync<FilaPeriodo>(new CommandDefinition(
                "SELECT EmpresaId, PeriodoKey FROM fac.CuentaCobro WHERE CuentaCobroId = @cuentaCobroId", new { cuentaCobroId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (c is null) return Result.Falla(Error.NoEncontrado("La cuenta de cobro no existe."));
            await cn.ExecuteAsync(new CommandDefinition("fac.usp_AnularCuentaCobro", new { CuentaCobroId = cuentaCobroId, Motivo = motivo, UsuarioId = usuarioId }, tx,
                commandType: CommandType.StoredProcedure, cancellationToken: ct)).ConfigureAwait(false);
            // Aviso de anulación al cliente (sin adjunto) y, si se pide, la CC que la reemplaza (el periodo quedó libre).
            await EncolarAsync(cn, tx, TiposTrabajo.EncolarEnvio, c.EmpresaId, usuarioId, $"FAC:ANU|CC:{cuentaCobroId}",
                new { cuentaCobroId, tipo = "ANULACION", esReenvio = false }, ct).ConfigureAwait(false);
            if (emitirReemplazo)
                await EncolarAsync(cn, tx, TiposTrabajo.GenerarCuenta, c.EmpresaId, usuarioId, $"FAC:REEMP|CC:{cuentaCobroId}",
                    new { empresaId = c.EmpresaId, periodo = c.PeriodoKey, reemplazaA = cuentaCobroId }, ct).ConfigureAwait(false);
            return Result.Ok();
        }, ct).ConfigureAwait(false);

    // ------------------------------------------------------------------ envíos y alertas
    public async Task<Result> ReintentarEnvioAsync(Guid envioId, int usuarioId, CancellationToken ct)
        => await EnTransaccionAsync(async (cn, tx) =>
        {
            var empresa = await cn.ExecuteScalarAsync<int?>(new CommandDefinition("""
                UPDATE fac.Envio SET Estado = 'PENDIENTE', CodigoError = NULL, CodigoSmtp = NULL OUTPUT inserted.EmpresaId
                WHERE EnvioId = @envioId AND Estado IN ('FALLIDO', 'REBOTADO') AND EXISTS (SELECT 1 FROM fac.EnvioDestinatario d WHERE d.EnvioId = @envioId)
                """, new { envioId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (empresa is null) return Result.Falla(Error.Conflicto(CodigosFacturacion.Estado, "Solo se reintenta un envío fallido con destinatarios; si no los tiene, use «Reenviar»."));
            await EncolarAsync(cn, tx, TiposTrabajo.EnviarCorreo, empresa, usuarioId, $"CORREO:{envioId}", new { envioId }, ct).ConfigureAwait(false);
            await AuditarAsync(cn, tx, empresa, usuarioId, "fac.Envio", envioId.ToString(), "REINTENTAR", null, ct).ConfigureAwait(false);
            return Result.Ok();
        }, ct).ConfigureAwait(false);

    public async Task<Result> ReenviarAsync(int cuentaCobroId, int usuarioId, CancellationToken ct)
        => await EnTransaccionAsync(async (cn, tx) =>
        {
            var empresa = await cn.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT EmpresaId FROM fac.CuentaCobro WHERE CuentaCobroId = @cuentaCobroId AND Estado IN ('EMITIDA', 'PAGADA')",
                new { cuentaCobroId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (empresa is null) return Result.Falla(Error.Conflicto(CodigosFacturacion.Estado, "Solo se reenvía una cuenta EMITIDA o PAGADA."));
            await EncolarAsync(cn, tx, TiposTrabajo.EncolarEnvio, empresa, usuarioId, $"FAC:ENV|CC:{cuentaCobroId}|{Guid.NewGuid():N}",
                new { cuentaCobroId, tipo = "CUENTA_COBRO", esReenvio = true }, ct).ConfigureAwait(false);   // envío nuevo con los destinatarios actuales
            await AuditarAsync(cn, tx, empresa, usuarioId, "fac.CuentaCobro", cuentaCobroId.ToString(), "REENVIAR", null, ct).ConfigureAwait(false);
            return Result.Ok();
        }, ct).ConfigureAwait(false);

    public async Task<Result> MarcarEnvioRebotadoAsync(Guid envioId, string codigoError, int usuarioId, CancellationToken ct)
        => await EnTransaccionAsync(async (cn, tx) =>
        {
            var empresa = await cn.ExecuteScalarAsync<int?>(new CommandDefinition(
                "UPDATE fac.Envio SET Estado = 'REBOTADO', CodigoError = LEFT(@codigoError, 40) OUTPUT inserted.EmpresaId WHERE EnvioId = @envioId AND Estado = 'ENVIADO'",
                new { envioId, codigoError }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (empresa is null) return Result.Falla(Error.Conflicto(CodigosFacturacion.Estado, "Solo se marca como rebotado un envío ENVIADO."));
            await AuditarAsync(cn, tx, empresa, usuarioId, "fac.Envio", envioId.ToString(), "REBOTADO", null, ct).ConfigureAwait(false);
            return Result.Ok();
        }, ct).ConfigureAwait(false);

    public async Task<Result> AtenderAlertaAsync(int alertaId, int usuarioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        var n = await cn.ExecuteAsync(new CommandDefinition("UPDATE fac.Alerta SET AtendidaPor = @usuarioId, AtendidaEn = SYSUTCDATETIME() WHERE AlertaId = @alertaId AND AtendidaEn IS NULL",
            new { alertaId, usuarioId }, cancellationToken: ct)).ConfigureAwait(false);
        return n == 1 ? Result.Ok() : Result.Falla(Error.NoEncontrado("La alerta no existe o ya fue atendida."));
    }

    // ------------------------------------------------------------------ pagos
    public async Task<Result<int>> RegistrarPagoAsync(RegistrarPagoCommand c, int usuarioId, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("EmpresaId", c.EmpresaId); p.Add("Clase", c.Clase); p.Add("Medio", c.Medio); p.Add("FechaPago", c.FechaPago.ToDateTime(TimeOnly.MinValue)); p.Add("ValorCop", c.ValorCop);
        p.Add("Referencia", c.Referencia); p.Add("Observacion", c.Observacion); p.Add("UsuarioId", usuarioId);
        p.Add("PagoId", dbType: DbType.Int32, direction: ParameterDirection.Output);
        try
        {
            await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
            await cn.ExecuteAsync(new CommandDefinition("fac.usp_RegistrarPago", p, commandType: CommandType.StoredProcedure, cancellationToken: ct)).ConfigureAwait(false);
            return p.Get<int>("PagoId");
        }
        catch (SqlException e) when (RepositorioFacturacionConfig.EsNegocio(e)) { return Result<int>.Falla(RepositorioFacturacionConfig.Traducir(e)); }
    }

    public Task<Result> AplicarPagoAsync(int pagoId, int cuentaCobroId, decimal valorCop, int usuarioId, CancellationToken ct)
        => ProcAsync("fac.usp_AplicarPago", new { PagoId = pagoId, CuentaCobroId = cuentaCobroId, ValorCop = valorCop, UsuarioId = usuarioId }, ct);

    /// <summary>FIFO por vencimiento sobre las cuentas EMITIDA de la empresa del pago; lo que sobra queda como saldo a favor.</summary>
    public async Task<Result> AplicarAutomaticoAsync(int pagoId, int usuarioId, CancellationToken ct)
        => await EnTransaccionAsync(async (cn, tx) =>
        {
            var libre = await cn.ExecuteScalarAsync<decimal?>(new CommandDefinition("SELECT SaldoSinAplicarCop FROM fac.Pago WHERE PagoId = @pagoId AND Estado = 'VIGENTE'", new { pagoId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (libre is null) return Result.Falla(Error.NoEncontrado("El pago no existe o está anulado."));
            var cuentas = (await cn.QueryAsync<FilaSaldo>(new CommandDefinition("""
                SELECT c.CuentaCobroId, c.SaldoCop AS Saldo FROM fac.CuentaCobro c JOIN fac.Pago p ON p.EmpresaId = c.EmpresaId
                WHERE p.PagoId = @pagoId AND c.Estado = 'EMITIDA' AND c.SaldoCop > 0 ORDER BY c.FechaVencimiento, c.CuentaCobroId
                """, new { pagoId }, tx, cancellationToken: ct)).ConfigureAwait(false)).ToList();
            foreach (var (cuenta, saldo) in cuentas.Select(x => (x.CuentaCobroId, x.Saldo)))
            {
                if (libre <= 0) break;
                var valor = Math.Min(libre.Value, saldo);
                await cn.ExecuteAsync(new CommandDefinition("fac.usp_AplicarPago", new { PagoId = pagoId, CuentaCobroId = cuenta, ValorCop = valor, UsuarioId = usuarioId }, tx,
                    commandType: CommandType.StoredProcedure, cancellationToken: ct)).ConfigureAwait(false);
                libre -= valor;
            }
            return Result.Ok();
        }, ct).ConfigureAwait(false);

    public Task<Result> ReversarAplicacionAsync(int aplicacionId, string motivo, int usuarioId, CancellationToken ct)
        => ProcAsync("fac.usp_ReversarAplicacion", new { AplicacionId = aplicacionId, Motivo = motivo, UsuarioId = usuarioId }, ct);

    public Task<Result> AnularPagoAsync(int pagoId, string motivo, int usuarioId, CancellationToken ct)
        => ProcAsync("fac.usp_AnularPago", new { PagoId = pagoId, Motivo = motivo, UsuarioId = usuarioId }, ct);

    // ------------------------------------------------------------------ utilidades
    private async Task<Result> ProcAsync(string procedimiento, object parametros, CancellationToken ct)
    {
        try
        {
            await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
            await cn.ExecuteAsync(new CommandDefinition(procedimiento, parametros, commandType: CommandType.StoredProcedure, cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (SqlException e) when (RepositorioFacturacionConfig.EsNegocio(e)) { return Result.Falla(RepositorioFacturacionConfig.Traducir(e)); }
    }

    private async Task<Result> EnTransaccionAsync(Func<SqlConnection, SqlTransaction, Task<Result>> accion, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await accion(cn, tx).ConfigureAwait(false);
            if (r.EsExitoso) await tx.CommitAsync(ct).ConfigureAwait(false); else await tx.RollbackAsync(ct).ConfigureAwait(false);
            return r;
        }
        catch (SqlException e) when (RepositorioFacturacionConfig.EsNegocio(e))
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return Result.Falla(RepositorioFacturacionConfig.Traducir(e));
        }
    }

    private static Task EncolarAsync(SqlConnection cn, SqlTransaction tx, string tipo, int? empresaId, int usuarioId, string clave, object payload, CancellationToken ct)
        => cn.ExecuteAsync(new CommandDefinition(
            "INSERT etl.Trabajo (Tipo, EmpresaId, UsuarioId, ClaveExclusion, Prioridad, Payload) VALUES (@tipo, @empresaId, @usuarioId, @clave, 4, @payload)",
            new { tipo, empresaId, usuarioId, clave, payload = JsonSerializer.Serialize(payload) }, tx, cancellationToken: ct));

    private static Task AuditarAsync(SqlConnection cn, SqlTransaction tx, int? empresaId, int usuarioId, string entidad, string clave, string accion, string? motivo, CancellationToken ct)
        => cn.ExecuteAsync(new CommandDefinition(
            "INSERT aud.CambioAdministrativo (EmpresaId, UsuarioId, Entidad, ClaveEntidad, Accion, Motivo) VALUES (@empresaId, @usuarioId, @entidad, @clave, @accion, @motivo)",
            new { empresaId, usuarioId, entidad, clave, accion, motivo }, tx, cancellationToken: ct));
}
