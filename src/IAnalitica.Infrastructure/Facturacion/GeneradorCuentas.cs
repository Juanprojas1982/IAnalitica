using System.Data;
using System.Text.Json;
using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Domain.Facturacion;
using IAnalitica.Infrastructure.Ingesta;
using Microsoft.Data.SqlClient;

namespace IAnalitica.Infrastructure.Facturacion;

public enum ResultadoGeneracion { Emitida, Retenida, YaExiste, SinCondicion, SinFacturable, TotalCero }

/// <summary>Lo que se calculó para una empresa y un periodo, sin escribir nada.</summary>
public sealed record PlanCuenta(int EmpresaId, int PeriodoKey, bool TieneCondiciones, IReadOnlyList<LineaPlan> Lineas, IReadOnlyList<string> Motivos,
    int CondicionId, short DiasPago, decimal Total);

public sealed record LineaPlan(int CondicionId, int? CanonPlanId, LineaProrrateada Prorrateo);

/// <summary>
/// Genera las cuentas de cobro del mes vencido (08 §32.3, §33). Corre con <c>rol_etl</c>. Es idempotente: un <c>sp_getapplock</c> por empresa y
/// periodo, la comprobación de cuenta viva y <c>UX_FacCC_Periodo</c> (última barrera) impiden duplicados. La suspensión de la empresa NO retiene (D51).
/// </summary>
public sealed class GeneradorCuentas(IFabricaConexionesEtl etl, IColaTrabajos cola, TimeProvider reloj)
{
    private sealed record FilaCuenta(int EmpresaId, int PeriodoKey, string Estado);
    private sealed record FilaEmpresa(int EmpresaId, bool Activa);
    private sealed record FilaParametro(string Clave, string Valor);

    private sealed record Condicion(int CondicionId, DateTime VigenteDesde, DateTime? VigenteHasta, decimal? CanonPactadoCop, short? DiasPago,
        bool? RequiereAprobacion, bool Facturable, int? CanonPlanId, decimal? CanonPlanCop);

    // ------------------------------------------------------------------ cálculo (sin escrituras)
    public async Task<PlanCuenta> PlanearAsync(IDbConnection cn, int empresaId, int periodoKey, IDbTransaction? tx, CancellationToken ct)
    {
        var (primero, ultimo) = Prorrateo.LimitesDelMes(periodoKey);
        var parametros = (await cn.QueryAsync<FilaParametro>(new CommandDefinition("SELECT Clave, Valor FROM fac.Parametro", transaction: tx, cancellationToken: ct)).ConfigureAwait(false))
            .ToDictionary(x => x.Clave, x => x.Valor);

        var existentes = await cn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM fac.CondicionComercial WHERE EmpresaId = @empresaId", new { empresaId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        var condiciones = (await cn.QueryAsync<Condicion>(new CommandDefinition("""
            SELECT c.CondicionId, c.VigenteDesde, c.VigenteHasta, c.CanonPactadoCop, c.DiasPago, c.RequiereAprobacion, c.Facturable,
                   cp.CanonPlanId, cp.CanonMensualCop AS CanonPlanCop
            FROM fac.CondicionComercial c
            OUTER APPLY (SELECT TOP (1) x.CanonPlanId, x.CanonMensualCop FROM fac.CanonPlan x
                         WHERE x.PlanId = c.PlanId AND x.VigenteDesde <= @primero ORDER BY x.VigenteDesde DESC) cp
            WHERE c.EmpresaId = @empresaId AND c.VigenteDesde <= @ultimo AND (c.VigenteHasta IS NULL OR c.VigenteHasta >= @primero)
            ORDER BY c.VigenteDesde
            """, new { empresaId, primero = primero.ToDateTime(TimeOnly.MinValue), ultimo = ultimo.ToDateTime(TimeOnly.MinValue) }, tx, cancellationToken: ct)).ConfigureAwait(false)).ToList();

        var facturables = condiciones.Where(c => c.Facturable).ToList();
        var motivos = new List<string>();
        var lineas = new List<LineaPlan>();
        foreach (var c in facturables)
        {
            var canon = Canon.DelPeriodo(c.CanonPactadoCop, c.CanonPlanCop);
            if (canon is null) { if (!motivos.Contains(Alertas.SinCanon)) motivos.Add(Alertas.SinCanon); continue; }
            var hasta = c.VigenteHasta is { } h ? DateOnly.FromDateTime(h) : DateOnly.MaxValue;
            foreach (var l in Prorrateo.Calcular(periodoKey, new[] { new TramoCanon(DateOnly.FromDateTime(c.VigenteDesde), hasta, canon.Value) }))
                lineas.Add(new LineaPlan(c.CondicionId, c.CanonPactadoCop is null ? c.CanonPlanId : null, l));
        }

        var total = lineas.Sum(l => l.Prorrateo.ValorCop);
        var ultima = facturables.LastOrDefault();
        var destinatarios = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM seg.EmpresaContacto WHERE EmpresaId = @empresaId AND Tipo = 'FACTURACION' AND LEN(Email) > 0", new { empresaId }, tx, cancellationToken: ct)).ConfigureAwait(false);
        var anterior = await cn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
            "SELECT SUM(TotalCop) FROM fac.CuentaCobro WHERE EmpresaId = @empresaId AND PeriodoKey = @ant AND Estado IN ('BORRADOR', 'EMITIDA', 'PAGADA')",
            new { empresaId, ant = Prorrateo.PeriodoAnterior(periodoKey) }, tx, cancellationToken: ct)).ConfigureAwait(false);

        var globalAprobacion = parametros.GetValueOrDefault("REQUIERE_APROBACION") == "1";
        var variacionMax = decimal.TryParse(parametros.GetValueOrDefault("VARIACION_MAX_PCT"), out var v) ? v : 50m;
        var diasDefecto = short.TryParse(parametros.GetValueOrDefault("DIAS_PAGO_DEFECTO"), out var d) ? d : (short)30;

        // El sin-canon ya está en 'motivos'; las demás reglas van por el dominio y se agregan sin duplicar.
        foreach (var m in ReglasEmision.MotivosRetencion(new EntradaReglasEmision(
                     motivos.Contains(Alertas.SinCanon) ? null : total, destinatarios, total, anterior, variacionMax,
                     ReglasEmision.RequiereAprobacion(ultima?.RequiereAprobacion, globalAprobacion), Array.Empty<string>())))
            if (!motivos.Contains(m)) motivos.Add(m);

        return new PlanCuenta(empresaId, periodoKey, existentes > 0, lineas, motivos, ultima?.CondicionId ?? 0, ultima?.DiasPago ?? diasDefecto, total);
    }

    // ------------------------------------------------------------------ generación
    public async Task<ResultadoGeneracion> GenerarAsync(int empresaId, int periodoKey, CancellationToken ct, int? reemplazaA = null)
    {
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var bloqueo = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            DECLARE @r int; EXEC @r = sp_getapplock @Resource = @recurso, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; SELECT @r
            """, new { recurso = $"fac:{empresaId}:{periodoKey}" }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (bloqueo < 0) throw new InvalidOperationException("No se obtuvo el bloqueo de generación de la cuenta.");

        if (await cn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM fac.CuentaCobro WHERE EmpresaId = @empresaId AND PeriodoKey = @periodoKey AND Estado IN ('BORRADOR', 'EMITIDA', 'PAGADA')",
                new { empresaId, periodoKey }, tx, cancellationToken: ct)).ConfigureAwait(false) > 0)
            return ResultadoGeneracion.YaExiste;

        var plan = await PlanearAsync(cn, empresaId, periodoKey, tx, ct).ConfigureAwait(false);
        if (plan.CondicionId == 0)
        {
            // Sin condición facturable en el mes: la empresa terminó, aún no inicia o es piloto sin cobro. Sin condición alguna se alerta.
            if (!plan.TieneCondiciones)
            {
                await AlertarAsync(cn, tx, empresaId, periodoKey, null, Alertas.SinCondicion, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return ResultadoGeneracion.SinCondicion;
            }
            return ResultadoGeneracion.SinFacturable;
        }
        if (plan.Total <= 0 && !plan.Motivos.Contains(Alertas.SinCanon))
        {
            await AlertarAsync(cn, tx, empresaId, periodoKey, null, Alertas.TotalCero, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return ResultadoGeneracion.TotalCero;
        }

        var cuentaId = await cn.ExecuteScalarAsync<int>(new CommandDefinition("""
            INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, DiasPago, TotalCop, MotivosRetencion, ReemplazaA)
            VALUES (@EmpresaId, @PeriodoKey, @CondicionId, @DiasPago, @Total, @Motivos, @reemplazaA);
            SELECT CAST(SCOPE_IDENTITY() AS int)
            """, new { plan.EmpresaId, plan.PeriodoKey, plan.CondicionId, plan.DiasPago, plan.Total, reemplazaA, Motivos = plan.Motivos.Count == 0 ? null : string.Join(',', plan.Motivos) },
            tx, cancellationToken: ct)).ConfigureAwait(false);

        var orden = (byte)0;
        foreach (var l in plan.Lineas)
        {
            var p = l.Prorrateo;
            var concepto = p.DiasFacturados == p.DiasPeriodo
                ? $"Canon mensual · {Mes(periodoKey)}"
                : $"Canon mensual · {p.Desde:dd-MM-yyyy} a {p.Hasta:dd-MM-yyyy} ({p.DiasFacturados}/{p.DiasPeriodo} días)";
            await cn.ExecuteAsync(new CommandDefinition("""
                INSERT fac.CuentaCobroLinea (CuentaCobroId, EmpresaId, Orden, Tipo, Concepto, PeriodoServicioKey, CondicionId, FechaDesde, FechaHasta, DiasFacturados, DiasPeriodo, CanonMensualCop, ValorCop, CanonPlanId)
                VALUES (@cuentaId, @empresaId, @orden, 'CANON', @concepto, @periodoKey, @CondicionId, @Desde, @Hasta, @Dias, @DiasPeriodo, @Canon, @Valor, @CanonPlanId)
                """, new
            {
                cuentaId, empresaId, orden = ++orden, concepto, periodoKey, l.CondicionId, l.CanonPlanId,
                Desde = p.Desde.ToDateTime(TimeOnly.MinValue), Hasta = p.Hasta.ToDateTime(TimeOnly.MinValue), Dias = p.DiasFacturados, p.DiasPeriodo, Canon = p.CanonMensualCop, Valor = p.ValorCop,
            }, tx, cancellationToken: ct)).ConfigureAwait(false);
        }

        ResultadoGeneracion resultado;
        if (plan.Motivos.Count == 0)
        {
            await cn.ExecuteAsync(new CommandDefinition("fac.usp_EmitirCuentaCobro", new { CuentaCobroId = cuentaId, VersionFila = (byte[]?)null, UsuarioId = (int?)null },
                tx, commandType: CommandType.StoredProcedure, cancellationToken: ct)).ConfigureAwait(false);
            resultado = ResultadoGeneracion.Emitida;
        }
        else
        {
            foreach (var m in plan.Motivos) await AlertarAsync(cn, tx, empresaId, periodoKey, cuentaId, m, ct).ConfigureAwait(false);
            resultado = ResultadoGeneracion.Retenida;
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return resultado;
    }

    /// <summary>Recalcula un borrador retenido (p. ej. tras definir el canon o agregar un contacto). Solo BORRADOR; descarta el anterior y genera de nuevo.</summary>
    public async Task<ResultadoGeneracion?> RegenerarBorradorAsync(int cuentaCobroId, CancellationToken ct)
    {
        int empresaId, periodoKey;
        await using (var cn = await etl.AbrirAsync(ct).ConfigureAwait(false))
        {
            await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct).ConfigureAwait(false);
            var fila = await cn.QueryFirstOrDefaultAsync<FilaCuenta>(new CommandDefinition(
                "SELECT EmpresaId, PeriodoKey, Estado FROM fac.CuentaCobro WITH (UPDLOCK, HOLDLOCK) WHERE CuentaCobroId = @cuentaCobroId", new { cuentaCobroId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            if (fila is null || fila.Estado != EstadosCuentaCobro.Borrador) return null;
            (empresaId, periodoKey) = (fila.EmpresaId, fila.PeriodoKey);
            // Se borra el borrador (sin número, nunca se envió) y sus alertas abiertas; la generación vuelve a crearlas si siguen vigentes.
            await cn.ExecuteAsync(new CommandDefinition("""
                DELETE FROM fac.Alerta WHERE CuentaCobroId = @cuentaCobroId;
                DELETE FROM fac.CuentaCobroLinea WHERE CuentaCobroId = @cuentaCobroId;
                DELETE FROM fac.CuentaCobro WHERE CuentaCobroId = @cuentaCobroId;
                """, new { cuentaCobroId }, tx, cancellationToken: ct)).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        return await GenerarAsync(empresaId, periodoKey, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ trabajos
    /// <summary>GENERAR_CUENTAS_COBRO: encola una GENERAR_CUENTA por empresa con condición facturable en el periodo y alerta las que no tienen condición.</summary>
    public async Task<int> EncolarPeriodoAsync(int periodoKey, CancellationToken ct)
    {
        var (primero, ultimo) = Prorrateo.LimitesDelMes(periodoKey);
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        var conCondicion = (await cn.QueryAsync<int>(new CommandDefinition("""
            SELECT DISTINCT EmpresaId FROM fac.CondicionComercial
            WHERE Facturable = 1 AND VigenteDesde <= @ultimo AND (VigenteHasta IS NULL OR VigenteHasta >= @primero)
            """, new { primero = primero.ToDateTime(TimeOnly.MinValue), ultimo = ultimo.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        foreach (var e in conCondicion)
            await cola.EncolarAsync(new TrabajoPendiente(TiposTrabajo.GenerarCuenta, e, $"FAC:CC|EMP:{e}|PER:{periodoKey}",
                JsonSerializer.Serialize(new { empresaId = e, periodo = periodoKey }), Prioridad: 4), ct).ConfigureAwait(false);

        var sinNinguna = (await cn.QueryAsync<int>(new CommandDefinition(
            "SELECT EmpresaId FROM seg.Empresa e WHERE Activa = 1 AND NOT EXISTS (SELECT 1 FROM fac.CondicionComercial c WHERE c.EmpresaId = e.EmpresaId)", cancellationToken: ct)).ConfigureAwait(false)).ToList();
        foreach (var e in sinNinguna) await AlertarAsync(cn, null, e, periodoKey, null, Alertas.SinCondicion, ct).ConfigureAwait(false);
        return conCondicion.Count;
    }

    /// <summary>PREVALIDAR_FACTURACION (día 25): calcula sin escribir cuentas y alerta lo que se resolvería mal el día 1.</summary>
    public async Task<int> PrevalidarAsync(int periodoKey, CancellationToken ct)
    {
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        var empresas = (await cn.QueryAsync<FilaEmpresa>(new CommandDefinition("SELECT EmpresaId, Activa FROM seg.Empresa", cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var alertas = 0;
        foreach (var (empresaId, activa) in empresas.Select(x => (x.EmpresaId, x.Activa)))
        {
            var plan = await PlanearAsync(cn, empresaId, periodoKey, null, ct).ConfigureAwait(false);
            if (!plan.TieneCondiciones) { if (activa) { await AlertarAsync(cn, null, empresaId, periodoKey, null, Alertas.SinCondicion, ct).ConfigureAwait(false); alertas++; } continue; }
            if (plan.CondicionId == 0) continue;   // terminada o piloto sin cobro
            foreach (var m in plan.Motivos.Where(m => m is Alertas.SinCanon or Alertas.SinDestinatario or Alertas.VariacionAnomala))
            { await AlertarAsync(cn, null, empresaId, periodoKey, null, m, ct).ConfigureAwait(false); alertas++; }
        }
        return alertas;
    }

    private static Task AlertarAsync(IDbConnection cn, IDbTransaction? tx, int empresaId, int periodoKey, int? cuentaId, string codigo, CancellationToken ct)
        => cn.ExecuteAsync(new CommandDefinition("""
            IF NOT EXISTS (SELECT 1 FROM fac.Alerta WHERE EmpresaId = @empresaId AND PeriodoKey = @periodoKey AND Codigo = @codigo AND AtendidaEn IS NULL)
                INSERT fac.Alerta (EmpresaId, PeriodoKey, CuentaCobroId, Codigo) VALUES (@empresaId, @periodoKey, @cuentaId, @codigo)
            """, new { empresaId, periodoKey, cuentaId, codigo }, tx, cancellationToken: ct));

    private static readonly string[] Meses = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"];
    internal static string Mes(int periodoKey) => $"{Meses[periodoKey % 100 - 1]}-{periodoKey / 100}";

    /// <summary>Periodo (AAAAMM) del mes en curso en hora Colombia.</summary>
    internal int PeriodoActual()
    {
        var hoy = reloj.GetUtcNow().UtcDateTime.AddHours(-5);
        return hoy.Year * 100 + hoy.Month;
    }
}

internal sealed class ManejadorPrevalidarFacturacion(GeneradorCuentas generador) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.PrevalidarFacturacion;
    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        await generador.PrevalidarAsync(PeriodoDe(trabajo), ct).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
    internal static int PeriodoDe(TrabajoTomado t)
    {
        using var doc = JsonDocument.Parse(t.Payload ?? throw new InvalidOperationException("El trabajo no trae payload."));
        return doc.RootElement.GetProperty("periodo").GetInt32();
    }
}

internal sealed class ManejadorGenerarCuentasCobro(GeneradorCuentas generador) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.GenerarCuentasCobro;
    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        await generador.EncolarPeriodoAsync(ManejadorPrevalidarFacturacion.PeriodoDe(trabajo), ct).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
}

internal sealed class ManejadorGenerarCuenta(GeneradorCuentas generador) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.GenerarCuenta;
    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(trabajo.Payload ?? throw new InvalidOperationException("El trabajo no trae payload."));
        int? reemplazaA = doc.RootElement.TryGetProperty("reemplazaA", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : null;
        await generador.GenerarAsync(doc.RootElement.GetProperty("empresaId").GetInt32(), doc.RootElement.GetProperty("periodo").GetInt32(), ct, reemplazaA).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
}
