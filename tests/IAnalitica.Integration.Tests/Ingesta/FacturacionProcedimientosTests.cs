using System.Data;
using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.Data.SqlClient;
using static IAnalitica.Integration.Tests.Infra.Escenarios;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>
/// Facturación, paso 2 (08 §30.3, §35): procedimientos de numeración, emisión, pagos, anulación y configuración, contra SQL Server real.
/// Usa una empresa propia (NIT 900100200) para no alterar los conteos de las pruebas de aislamiento.
/// </summary>
[Collection(ColeccionBd.Nombre)]
public class FacturacionProcedimientosTests(BaseDeDatosFixture bd)
{
    private const int NoAutorizado = 50100;
    private const int Restriccion = 547;
    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static (int EmpresaId, int CondicionId)? _empresa;
    private static int _siguientePeriodo = 203001;

    private async Task<(int EmpresaId, int CondicionId)> EmpresaAsync()
    {
        await Candado.WaitAsync();
        try
        {
            if (_empresa is not null) return _empresa.Value;
            await CrearComoSuperadminAsync(bd, ComandoValido("900100200", "Empresa Facturable SAS"));
            var id = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT EmpresaId FROM seg.Empresa WHERE Nit = '900100200'", superadmin: true);
            await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"""
                INSERT fac.CondicionComercial (EmpresaId, VigenteDesde, PlanId, CanonPactadoCop, CreadoPor)
                VALUES ({id}, '2026-01-01', (SELECT TOP (1) PlanId FROM ia.PlanComercial ORDER BY PlanId), 600000, {bd.SuperadminId})
                """);
            var cond = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaEtl, $"SELECT CondicionId FROM fac.CondicionComercial WHERE EmpresaId = {id}", superadmin: false);
            _empresa = (id, cond);
            return _empresa.Value;
        }
        finally { Candado.Release(); }
    }

    private static int NuevoPeriodo()
    {
        var p = Interlocked.Increment(ref _siguientePeriodo);
        return p % 100 > 12 ? NuevoPeriodo() : p;   // 203001..203012, 203101...
    }

    /// <summary>Crea un borrador de un canon completo (rol_etl, sin RLS) y devuelve su id.</summary>
    private async Task<int> BorradorAsync(int valor = 600000, int? periodo = null)
    {
        var (emp, cond) = await EmpresaAsync();
        var per = periodo ?? NuevoPeriodo();
        var desde = new DateTime(per / 100, per % 100, 1);
        var dias = DateTime.DaysInMonth(desde.Year, desde.Month);
        await using var c = new SqlConnection(bd.CadenaEtl);
        await c.OpenAsync();
        var id = await Escalar<int>(c, $"""
            INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, DiasPago) VALUES ({emp}, {per}, {cond}, 30);
            SELECT CAST(SCOPE_IDENTITY() AS int)
            """);
        await Ejecutar(c, $"""
            INSERT fac.CuentaCobroLinea (CuentaCobroId, EmpresaId, Orden, Tipo, Concepto, PeriodoServicioKey, CondicionId, FechaDesde, FechaHasta, DiasFacturados, DiasPeriodo, CanonMensualCop, ValorCop)
            VALUES ({id}, {emp}, 1, 'CANON', N'Canon', {per}, {cond}, '{desde:yyyy-MM-dd}', '{desde.AddDays(dias - 1):yyyy-MM-dd}', {dias}, {dias}, {valor}, {valor})
            """);
        return id;
    }

    private async Task<SqlConnection> SuperAsync() => await AbrirAsync(bd.CadenaApp, ContextoBd.DeSuperadmin(bd.SuperadminId));

    private static async Task<object?> ProcAsync(SqlConnection c, string nombre, params (string Nombre, object? Valor)[] parametros)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = nombre;
        cmd.CommandType = CommandType.StoredProcedure;
        foreach (var (n, v) in parametros) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return null;
    }

    private async Task<string> EmitirAsync(int cuentaId)
    {
        await using var c = await SuperAsync();
        await ProcAsync(c, "fac.usp_EmitirCuentaCobro", ("@CuentaCobroId", cuentaId), ("@UsuarioId", bd.SuperadminId));
        return await Escalar<string>(c, $"SELECT Numero FROM fac.CuentaCobro WHERE CuentaCobroId = {cuentaId}");
    }

    private static async Task<int> CodigoAsync(Func<Task> accion) => (await Assert.ThrowsAsync<SqlException>(accion)).Number;

    [Fact]
    public async Task Un_usuario_que_no_es_superadmin_no_puede_ejecutar_los_procedimientos()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        var id = await BorradorAsync();
        var idA = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, $"SELECT EmpresaId FROM seg.Empresa WHERE Nit = '{DosEmpresas.NitA}'", superadmin: true);
        await using var admin = await AbrirAsync(bd.CadenaApp, ContextoBd.DeEmpresa(idA, dos.AdminAId, esAdminPrincipal: true));

        Assert.Equal(NoAutorizado, await CodigoAsync(() => ProcAsync(admin, "fac.usp_EmitirCuentaCobro", ("@CuentaCobroId", id))));
        Assert.Equal(NoAutorizado, await CodigoAsync(() => ProcAsync(admin, "fac.usp_AnularCuentaCobro", ("@CuentaCobroId", id), ("@Motivo", "motivo suficientemente largo"), ("@UsuarioId", 1))));
        Assert.Equal(NoAutorizado, await CodigoAsync(() => ProcAsync(admin, "fac.usp_GuardarParametro", ("@Clave", "REQUIERE_APROBACION"), ("@Valor", "1"), ("@UsuarioId", 1))));
        Assert.Equal(NoAutorizado, await CodigoAsync(() => ProcAsync(admin, "fac.usp_GuardarCanonPlan", ("@PlanId", (short)1), ("@VigenteDesde", new DateTime(2031, 1, 1)), ("@CanonMensualCop", 1m), ("@UsuarioId", 1))));
        Assert.Equal("BORRADOR", await BaseDeDatosFixture.EscalarAsync<string>(bd.CadenaEtl, $"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {id}", superadmin: false));
    }

    [Fact]
    public async Task Emitir_asigna_numero_foto_del_cliente_vencimiento_y_encola_el_pdf()
    {
        var (emp, _) = await EmpresaAsync();
        var id = await BorradorAsync(valor: 600000);
        var numero = await EmitirAsync(id);
        Assert.Matches(@"^CC-\d{6}$", numero);

        await using var c = await SuperAsync();
        Assert.Equal("EMITIDA", await Escalar<string>(c, $"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {id}"));
        Assert.Equal(600000m, await Escalar<decimal>(c, $"SELECT TotalCop FROM fac.CuentaCobro WHERE CuentaCobroId = {id}"));
        Assert.Equal("900100200", await Escalar<string>(c, $"SELECT ClienteNit FROM fac.CuentaCobro WHERE CuentaCobroId = {id}"));
        Assert.Equal(30, await Escalar<int>(c, $"SELECT DATEDIFF(DAY, FechaEmision, FechaVencimiento) FROM fac.CuentaCobro WHERE CuentaCobroId = {id}"));
        Assert.Equal(1, await Escalar<int>(c, $"SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'GENERAR_PDF_CUENTA_COBRO' AND ClaveExclusion = 'FAC:PDF|CC:{id}'"));
        Assert.Equal(1, await Escalar<int>(c, $"SELECT COUNT(*) FROM aud.CambioAdministrativo WHERE Entidad = 'fac.CuentaCobro' AND ClaveEntidad = '{id}' AND Accion = 'EMITIR' AND EmpresaId = {emp}"));

        // Emitir dos veces: la segunda falla (ya no es borrador) y no consume número.
        Assert.Equal(50111, await CodigoAsync(() => ProcAsync(c, "fac.usp_EmitirCuentaCobro", ("@CuentaCobroId", id))));
    }

    [Fact]
    public async Task Veinte_emisiones_concurrentes_dan_numeros_distintos_y_consecutivos()
    {
        var ids = new List<int>();
        for (var i = 0; i < 20; i++) ids.Add(await BorradorAsync(valor: 100000 + i));

        var numeros = await Task.WhenAll(ids.Select(EmitirAsync));
        var enteros = numeros.Select(n => int.Parse(n[3..])).OrderBy(n => n).ToList();

        Assert.Equal(20, enteros.Distinct().Count());
        Assert.Equal(Enumerable.Range(enteros[0], 20), enteros);   // sin huecos
    }

    [Fact]
    public async Task Un_rollback_no_consume_el_numero()
    {
        var id = await BorradorAsync();
        await using var c = await SuperAsync();
        string perdido;
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "fac.usp_EmitirCuentaCobro";
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.Parameters.AddWithValue("@CuentaCobroId", id);
            await cmd.ExecuteNonQueryAsync();
            await using var lee = c.CreateCommand();
            lee.Transaction = tx;
            lee.CommandText = $"SELECT Numero FROM fac.CuentaCobro WHERE CuentaCobroId = {id}";
            perdido = (string)(await lee.ExecuteScalarAsync())!;
            await tx.RollbackAsync();
        }
        Assert.Equal("BORRADOR", await Escalar<string>(c, $"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {id}"));
        Assert.Equal(perdido, await EmitirAsync(id));   // el número devuelto por el rollback es el que se asigna después
    }

    [Fact]
    public async Task Un_borrador_sin_lineas_no_se_emite()
    {
        var (emp, cond) = await EmpresaAsync();
        var per = NuevoPeriodo();
        var id = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaEtl,
            $"INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, DiasPago) VALUES ({emp}, {per}, {cond}, 30); SELECT CAST(SCOPE_IDENTITY() AS int)", superadmin: false);
        await using var c = await SuperAsync();
        Assert.Equal(50113, await CodigoAsync(() => ProcAsync(c, "fac.usp_EmitirCuentaCobro", ("@CuentaCobroId", id))));
    }

    [Fact]
    public async Task Aplicar_pagos_no_sobreaplica_y_el_reverso_reabre_la_cuenta()
    {
        var (emp, _) = await EmpresaAsync();
        var cuenta = await BorradorAsync(valor: 600000);
        await EmitirAsync(cuenta);
        await using var c = await SuperAsync();

        await using var cmdPago = c.CreateCommand();
        cmdPago.CommandText = "fac.usp_RegistrarPago";
        cmdPago.CommandType = CommandType.StoredProcedure;
        cmdPago.Parameters.AddWithValue("@EmpresaId", emp);
        cmdPago.Parameters.AddWithValue("@Clase", "RECAUDO");
        cmdPago.Parameters.AddWithValue("@Medio", "TRANSFERENCIA");
        cmdPago.Parameters.AddWithValue("@FechaPago", new DateTime(2026, 2, 10));
        cmdPago.Parameters.AddWithValue("@ValorCop", 1000000m);
        cmdPago.Parameters.AddWithValue("@UsuarioId", bd.SuperadminId);
        var salida = cmdPago.Parameters.Add("@PagoId", SqlDbType.Int);
        salida.Direction = ParameterDirection.Output;
        await cmdPago.ExecuteNonQueryAsync();
        var pago = (int)salida.Value;

        // Más que el saldo de la cuenta: rechazado.
        Assert.Equal(50137, await CodigoAsync(() => ProcAsync(c, "fac.usp_AplicarPago", ("@PagoId", pago), ("@CuentaCobroId", cuenta), ("@ValorCop", 700000m), ("@UsuarioId", 1))));
        // Exacto: la cuenta queda PAGADA y al pago le sobran 400.000 (saldo a favor).
        await ProcAsync(c, "fac.usp_AplicarPago", ("@PagoId", pago), ("@CuentaCobroId", cuenta), ("@ValorCop", 600000m), ("@UsuarioId", bd.SuperadminId));
        Assert.Equal("PAGADA", await Escalar<string>(c, $"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));
        Assert.Equal(400000m, await Escalar<decimal>(c, $"SELECT SaldoSinAplicarCop FROM fac.Pago WHERE PagoId = {pago}"));
        // Una cuenta PAGADA ya no recibe aplicaciones.
        Assert.Equal(50132, await CodigoAsync(() => ProcAsync(c, "fac.usp_AplicarPago", ("@PagoId", pago), ("@CuentaCobroId", cuenta), ("@ValorCop", 1m), ("@UsuarioId", 1))));

        // Reverso: vuelve a EMITIDA y el pago recupera todo su saldo.
        var apl = await Escalar<int>(c, $"SELECT AplicacionId FROM fac.AplicacionPago WHERE PagoId = {pago} AND ReversadaEn IS NULL");
        Assert.Equal(50120, await CodigoAsync(() => ProcAsync(c, "fac.usp_ReversarAplicacion", ("@AplicacionId", apl), ("@Motivo", "corto"), ("@UsuarioId", 1))));
        await ProcAsync(c, "fac.usp_ReversarAplicacion", ("@AplicacionId", apl), ("@Motivo", "Error de imputación del pago"), ("@UsuarioId", bd.SuperadminId));
        Assert.Equal("EMITIDA", await Escalar<string>(c, $"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));
        Assert.Equal(1000000m, await Escalar<decimal>(c, $"SELECT SaldoSinAplicarCop FROM fac.Pago WHERE PagoId = {pago}"));
        Assert.Equal(50136, await CodigoAsync(() => ProcAsync(c, "fac.usp_ReversarAplicacion", ("@AplicacionId", apl), ("@Motivo", "Segundo reverso del mismo"), ("@UsuarioId", 1))));
    }

    [Fact]
    public async Task Anular_una_cuenta_pagada_reversa_sus_aplicaciones_y_conserva_el_numero()
    {
        var (emp, _) = await EmpresaAsync();
        var cuenta = await BorradorAsync(valor: 250000);
        var numero = await EmitirAsync(cuenta);
        await using var c = await SuperAsync();
        var pago = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaEtl, $"""
            INSERT fac.Pago (EmpresaId, Clase, Medio, FechaPago, ValorCop, RegistradoPor) VALUES ({emp}, 'RECAUDO', 'PSE', '2026-03-01', 250000, {bd.SuperadminId});
            SELECT CAST(SCOPE_IDENTITY() AS int)
            """, superadmin: false);
        await ProcAsync(c, "fac.usp_AplicarPago", ("@PagoId", pago), ("@CuentaCobroId", cuenta), ("@ValorCop", 250000m), ("@UsuarioId", bd.SuperadminId));
        Assert.Equal("PAGADA", await Escalar<string>(c, $"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));

        Assert.Equal(50120, await CodigoAsync(() => ProcAsync(c, "fac.usp_AnularCuentaCobro", ("@CuentaCobroId", cuenta), ("@Motivo", "corto"), ("@UsuarioId", 1))));
        await ProcAsync(c, "fac.usp_AnularCuentaCobro", ("@CuentaCobroId", cuenta), ("@Motivo", "Valor errado, se reemite"), ("@UsuarioId", bd.SuperadminId));

        Assert.Equal("ANULADA", await Escalar<string>(c, $"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));
        Assert.Equal(numero, await Escalar<string>(c, $"SELECT Numero FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));
        Assert.Equal(250000m, await Escalar<decimal>(c, $"SELECT SaldoSinAplicarCop FROM fac.Pago WHERE PagoId = {pago}"));   // saldo a favor
        Assert.Equal(50114, await CodigoAsync(() => ProcAsync(c, "fac.usp_AnularCuentaCobro", ("@CuentaCobroId", cuenta), ("@Motivo", "Anulación repetida de la cuenta"), ("@UsuarioId", 1))));
        // El periodo queda libre para la cuenta que la reemplaza.
        var periodo = await Escalar<int>(c, $"SELECT PeriodoKey FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}");
        _ = await BorradorAsync(periodo: periodo);
    }

    [Fact]
    public async Task Anular_un_pago_reversa_sus_aplicaciones()
    {
        var (emp, _) = await EmpresaAsync();
        var cuenta = await BorradorAsync(valor: 100000);
        await EmitirAsync(cuenta);
        var pago = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaEtl, $"""
            INSERT fac.Pago (EmpresaId, Clase, Medio, FechaPago, ValorCop, RegistradoPor) VALUES ({emp}, 'RECAUDO', 'CHEQUE', '2026-03-02', 100000, {bd.SuperadminId});
            SELECT CAST(SCOPE_IDENTITY() AS int)
            """, superadmin: false);
        await using var c = await SuperAsync();
        await ProcAsync(c, "fac.usp_AplicarPago", ("@PagoId", pago), ("@CuentaCobroId", cuenta), ("@ValorCop", 100000m), ("@UsuarioId", bd.SuperadminId));

        await ProcAsync(c, "fac.usp_AnularPago", ("@PagoId", pago), ("@Motivo", "Cheque devuelto por el banco"), ("@UsuarioId", bd.SuperadminId));

        Assert.Equal("ANULADO", await Escalar<string>(c, $"SELECT Estado FROM fac.Pago WHERE PagoId = {pago}"));
        Assert.Equal("EMITIDA", await Escalar<string>(c, $"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));
        Assert.Equal(0m, await Escalar<decimal>(c, $"SELECT PagadoCop FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));
    }

    [Fact]
    public async Task Parametros_validan_el_valor_y_quedan_auditados()
    {
        await using var c = await SuperAsync();
        Assert.Equal(50150, await CodigoAsync(() => ProcAsync(c, "fac.usp_GuardarParametro", ("@Clave", "NO_EXISTE"), ("@Valor", "1"), ("@UsuarioId", bd.SuperadminId))));
        Assert.Equal(50151, await CodigoAsync(() => ProcAsync(c, "fac.usp_GuardarParametro", ("@Clave", "REQUIERE_APROBACION"), ("@Valor", "2"), ("@UsuarioId", bd.SuperadminId))));
        Assert.Equal(50153, await CodigoAsync(() => ProcAsync(c, "fac.usp_GuardarParametro", ("@Clave", "DIA_PREVALIDACION"), ("@Valor", "31"), ("@UsuarioId", bd.SuperadminId))));
        Assert.Equal(50154, await CodigoAsync(() => ProcAsync(c, "fac.usp_GuardarParametro", ("@Clave", "HORA_GENERACION"), ("@Valor", "mañana"), ("@UsuarioId", bd.SuperadminId))));

        await ProcAsync(c, "fac.usp_GuardarParametro", ("@Clave", "REQUIERE_APROBACION"), ("@Valor", "1"), ("@UsuarioId", bd.SuperadminId));
        Assert.Equal("1", await Escalar<string>(c, "SELECT Valor FROM fac.Parametro WHERE Clave = 'REQUIERE_APROBACION'"));
        Assert.True(await Escalar<int>(c, "SELECT COUNT(*) FROM aud.CambioAdministrativo WHERE Entidad = 'fac.Parametro' AND ClaveEntidad = 'REQUIERE_APROBACION' AND Accion = 'CAMBIAR_PARAMETRO'") >= 1);
        await ProcAsync(c, "fac.usp_GuardarParametro", ("@Clave", "REQUIERE_APROBACION"), ("@Valor", "0"), ("@UsuarioId", bd.SuperadminId));   // deja el valor por defecto
    }

    [Fact]
    public async Task Condiciones_cierran_la_anterior_sin_solapar_y_el_canon_de_plan_se_edita_hasta_facturarse()
    {
        await CrearComoSuperadminAsync(bd, ComandoValido("900100300", "Empresa Condiciones SAS"));
        var emp = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT EmpresaId FROM seg.Empresa WHERE Nit = '900100300'", superadmin: true);
        var plan = await BaseDeDatosFixture.EscalarAsync<short>(bd.CadenaEtl, "SELECT TOP (1) PlanId FROM ia.PlanComercial ORDER BY PlanId", superadmin: false);
        await using var c = await SuperAsync();

        Task Condicion(DateTime desde) => ProcAsync(c, "fac.usp_GuardarCondicion", ("@EmpresaId", emp), ("@VigenteDesde", desde), ("@PlanId", plan),
            ("@CanonPactadoCop", 800000m), ("@Facturable", true), ("@UsuarioId", bd.SuperadminId));

        await Condicion(new DateTime(2026, 3, 15));   // alta a mitad de mes
        await Condicion(new DateTime(2026, 6, 10));   // cambio: cierra la anterior el 09-jun
        Assert.Equal(new DateTime(2026, 6, 9), await Escalar<DateTime>(c, $"SELECT VigenteHasta FROM fac.CondicionComercial WHERE EmpresaId = {emp} AND VigenteDesde = '2026-03-15'"));
        Assert.Equal(1, await Escalar<int>(c, $"SELECT COUNT(*) FROM fac.CondicionComercial WHERE EmpresaId = {emp} AND VigenteHasta IS NULL"));
        // No se retrocede sobre la abierta.
        Assert.Equal(50160, await CodigoAsync(() => Condicion(new DateTime(2026, 6, 10))));
        Assert.Equal(50160, await CodigoAsync(() => Condicion(new DateTime(2026, 5, 1))));

        // Un periodo ya con cuenta viva no se reescribe.
        var cond = await Escalar<int>(c, $"SELECT CondicionId FROM fac.CondicionComercial WHERE EmpresaId = {emp} AND VigenteHasta IS NULL");
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, DiasPago) VALUES ({emp}, 209901, {cond}, 30)");
        Assert.Equal(50161, await CodigoAsync(() => Condicion(new DateTime(2099, 1, 10))));

        // Canon de plan: se edita mientras no se haya facturado; el día debe ser 1.
        await ProcAsync(c, "fac.usp_GuardarCanonPlan", ("@PlanId", plan), ("@VigenteDesde", new DateTime(2031, 1, 1)), ("@CanonMensualCop", 500000m), ("@UsuarioId", bd.SuperadminId));
        await ProcAsync(c, "fac.usp_GuardarCanonPlan", ("@PlanId", plan), ("@VigenteDesde", new DateTime(2031, 1, 1)), ("@CanonMensualCop", 550000m), ("@UsuarioId", bd.SuperadminId));
        Assert.Equal(550000m, await Escalar<decimal>(c, $"SELECT CanonMensualCop FROM fac.CanonPlan WHERE PlanId = {plan} AND VigenteDesde = '2031-01-01'"));
        Assert.Equal(Restriccion, await CodigoAsync(() => ProcAsync(c, "fac.usp_GuardarCanonPlan", ("@PlanId", plan), ("@VigenteDesde", new DateTime(2031, 2, 15)), ("@CanonMensualCop", 1m), ("@UsuarioId", 1))));
    }
}
