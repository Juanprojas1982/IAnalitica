using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Facturacion;
using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>
/// Facturación, paso 5 (08 §32.3, §33): generación mensual y programador, contra SQL Server real. Cada prueba crea su propia empresa; el periodo
/// de servicio es octubre de 2026 (31 días). Las reglas de retención ya están cubiertas por pruebas unitarias; aquí se prueba la persistencia.
/// </summary>
[Collection(ColeccionBd.Nombre)]
public class FacturacionGeneracionTests(BaseDeDatosFixture bd)
{
    private const int Periodo = 202610;
    private static int _secuencia = 910000000;
    private static readonly SemaphoreSlim Candado = new(1, 1);   // el parámetro REQUIERE_APROBACION es global

    private (ServiceProvider Sp, GeneradorCuentas Generador) Servicios(DateTimeOffset? ahora = null)
    {
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton<TimeProvider>(new FakeTimeProvider(ahora ?? new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero)));
        s.AddProcesamiento(bd.CadenaEtl);
        s.AddProcesamientoFacturacion();
        var sp = s.BuildServiceProvider();
        return (sp, sp.GetRequiredService<GeneradorCuentas>());
    }

    /// <summary>Empresa nueva con una condición abierta (canon pactado) y, opcionalmente, un contacto de facturación.</summary>
    private async Task<int> EmpresaAsync(decimal? canonPactado, string desde = "2026-01-01", bool contacto = true, bool facturable = true, string? hasta = null, bool activa = true)
    {
        var nit = Interlocked.Increment(ref _secuencia).ToString();
        var kit = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido(nit, "Empresa de facturación " + nit));
        var id = kit.EmpresaId;
        await using var c = await Escenarios.AbrirAsync(bd.CadenaApp, ContextoBd.DeSuperadmin(bd.SuperadminId));
        if (contacto)
            await Escenarios.Ejecutar(c, $"INSERT seg.EmpresaContacto (EmpresaId, Tipo, Nombre, Email) VALUES ({id}, 'FACTURACION', N'Tesorería', N'tesoreria{id}@prueba.test')");
        if (!activa) await Escenarios.Ejecutar(c, $"UPDATE seg.Empresa SET Activa = 0 WHERE EmpresaId = {id}");
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"""
            INSERT fac.CondicionComercial (EmpresaId, VigenteDesde, VigenteHasta, PlanId, CanonPactadoCop, Facturable, CreadoPor)
            VALUES ({id}, '{desde}', {(hasta is null ? "NULL" : $"'{hasta}'")}, (SELECT TOP (1) PlanId FROM ia.PlanComercial ORDER BY PlanId),
                    {(canonPactado is null ? "NULL" : canonPactado.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}, {(facturable ? 1 : 0)}, {bd.SuperadminId})
            """);
        return id;
    }

    private async Task<T> Etl<T>(string sql) => await BaseDeDatosFixture.EscalarAsync<T>(bd.CadenaEtl, sql, superadmin: false);

    private async Task<(string Estado, decimal Total, string? Numero)> CuentaAsync(int empresaId, int periodo = Periodo)
    {
        await using var c = new SqlConnection(bd.CadenaEtl);
        await c.OpenAsync();
        return await c.QuerySingleAsync<(string, decimal, string?)>($"SELECT Estado, TotalCop, Numero FROM fac.CuentaCobro WHERE EmpresaId = {empresaId} AND PeriodoKey = {periodo} AND Estado <> 'DESCARTADA'");
    }

    private Task<int> Alertas(int empresaId, string codigo) => Etl<int>($"SELECT COUNT(*) FROM fac.Alerta WHERE EmpresaId = {empresaId} AND Codigo = '{codigo}' AND AtendidaEn IS NULL");

    [Fact]
    public async Task Con_canon_y_destinatario_la_cuenta_se_emite_sola_y_es_idempotente()
    {
        var emp = await EmpresaAsync(1_000_000m);
        var (_, g) = Servicios();

        Assert.Equal(ResultadoGeneracion.Emitida, await g.GenerarAsync(emp, Periodo, default));
        var (estado, total, numero) = await CuentaAsync(emp);
        Assert.Equal("EMITIDA", estado);
        Assert.Equal(1_000_000m, total);
        Assert.Matches(@"^CC-\d{6}$", numero!);
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'GENERAR_PDF_CUENTA_COBRO' AND EmpresaId = {emp}"));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.CuentaCobroLinea WHERE EmpresaId = {emp} AND Tipo = 'CANON' AND DiasFacturados = 31 AND DiasPeriodo = 31"));

        // Dos corridas, una sola cuenta (y el mismo número).
        Assert.Equal(ResultadoGeneracion.YaExiste, await g.GenerarAsync(emp, Periodo, default));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.CuentaCobro WHERE EmpresaId = {emp} AND PeriodoKey = {Periodo}"));
    }

    [Fact]
    public async Task Dos_generaciones_simultaneas_crean_una_sola_cuenta()
    {
        var emp = await EmpresaAsync(500_000m);
        var (_, g) = Servicios();
        var resultados = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => g.GenerarAsync(emp, Periodo, default)));

        Assert.Equal(1, resultados.Count(r => r == ResultadoGeneracion.Emitida));
        Assert.Equal(4, resultados.Count(r => r == ResultadoGeneracion.YaExiste));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.CuentaCobro WHERE EmpresaId = {emp} AND PeriodoKey = {Periodo}"));
    }

    [Fact]
    public async Task Un_alta_a_mitad_de_mes_se_prorratea_por_dias()
    {
        var emp = await EmpresaAsync(900_000m, desde: "2026-10-15");
        var (_, g) = Servicios();

        Assert.Equal(ResultadoGeneracion.Emitida, await g.GenerarAsync(emp, Periodo, default));
        // 17 días (15 al 31) de 31 → 900.000 × 17 / 31 = 493.548,39 → 493.548
        Assert.Equal(493_548m, (await CuentaAsync(emp)).Total);
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.CuentaCobroLinea WHERE EmpresaId = {emp} AND DiasFacturados = 17 AND Concepto LIKE N'%17/31%'"));
    }

    [Fact]
    public async Task Un_cambio_de_condicion_a_mitad_de_mes_genera_una_linea_por_tramo()
    {
        var emp = await EmpresaAsync(800_000m, desde: "2026-01-01", hasta: "2026-10-09");
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"""
            INSERT fac.CondicionComercial (EmpresaId, VigenteDesde, PlanId, CanonPactadoCop, CreadoPor)
            VALUES ({emp}, '2026-10-10', (SELECT TOP (1) PlanId FROM ia.PlanComercial ORDER BY PlanId), 1200000, {bd.SuperadminId})
            """);
        var (_, g) = Servicios();

        Assert.Equal(ResultadoGeneracion.Emitida, await g.GenerarAsync(emp, Periodo, default));
        // 800.000 × 9/31 = 232.258,06 → 232.258  +  1.200.000 × 22/31 = 851.612,90 → 851.613
        Assert.Equal(1_083_871m, (await CuentaAsync(emp)).Total);
        Assert.Equal(2, await Etl<int>($"SELECT COUNT(*) FROM fac.CuentaCobroLinea WHERE EmpresaId = {emp}"));
    }

    [Fact]
    public async Task Sin_contacto_de_facturacion_queda_en_borrador_con_alerta_y_sin_numero()
    {
        var emp = await EmpresaAsync(1_000_000m, contacto: false);
        var (_, g) = Servicios();

        Assert.Equal(ResultadoGeneracion.Retenida, await g.GenerarAsync(emp, Periodo, default));
        var (estado, _, numero) = await CuentaAsync(emp);
        Assert.Equal("BORRADOR", estado);
        Assert.Null(numero);
        Assert.Equal(1, await Alertas(emp, "SIN_DESTINATARIO"));
        Assert.Equal(0, await Etl<int>($"SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'GENERAR_PDF_CUENTA_COBRO' AND EmpresaId = {emp}"));   // nada se envía
    }

    [Fact]
    public async Task Sin_canon_se_retiene_y_regenerar_tras_definirlo_emite()
    {
        var emp = await EmpresaAsync(null);   // plan sin canon vigente y sin pactado
        var (_, g) = Servicios();

        Assert.Equal(ResultadoGeneracion.Retenida, await g.GenerarAsync(emp, Periodo, default));
        var (estado, total, _) = await CuentaAsync(emp);
        Assert.Equal("BORRADOR", estado);
        Assert.Equal(0m, total);
        Assert.Equal(1, await Alertas(emp, "SIN_CANON"));

        // El superadmin define el canon pactado y regenera: el borrador se reemplaza por la cuenta emitida.
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"UPDATE fac.CondicionComercial SET CanonPactadoCop = 700000 WHERE EmpresaId = {emp}");
        var borrador = await Etl<int>($"SELECT CuentaCobroId FROM fac.CuentaCobro WHERE EmpresaId = {emp}");
        Assert.Equal(ResultadoGeneracion.Emitida, await g.RegenerarBorradorAsync(borrador, default));
        Assert.Equal(("EMITIDA", 700_000m), ((await CuentaAsync(emp)).Estado, (await CuentaAsync(emp)).Total));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.CuentaCobro WHERE EmpresaId = {emp}"));
        Assert.Null(await g.RegenerarBorradorAsync(borrador, default));   // ya no es borrador (ni existe)
    }

    [Fact]
    public async Task Una_variacion_anomala_frente_al_mes_anterior_retiene_la_cuenta()
    {
        var emp = await EmpresaAsync(2_000_000m);
        var cond = await Etl<int>($"SELECT CondicionId FROM fac.CondicionComercial WHERE EmpresaId = {emp}");
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"""
            INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, Numero, Estado, TotalCop, DiasPago, FechaEmision, FechaVencimiento, ClienteNit)
            VALUES ({emp}, 202609, {cond}, 'CC-8{emp:D5}', 'EMITIDA', 1000000, 30, '2026-10-01', '2026-10-31', '1')
            """);
        var (_, g) = Servicios();

        Assert.Equal(ResultadoGeneracion.Retenida, await g.GenerarAsync(emp, Periodo, default));   // +100 %
        Assert.Equal("BORRADOR", (await CuentaAsync(emp)).Estado);
        Assert.Equal(1, await Alertas(emp, "VARIACION_ANOMALA"));
    }

    [Fact]
    public async Task El_interruptor_de_aprobacion_retiene_y_el_de_la_empresa_gana_sobre_el_global()
    {
        await Candado.WaitAsync();
        try
        {
            await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, "UPDATE fac.Parametro SET Valor = '1' WHERE Clave = 'REQUIERE_APROBACION'");
            var retenida = await EmpresaAsync(1_000_000m);
            var exenta = await EmpresaAsync(1_000_000m);
            await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"UPDATE fac.CondicionComercial SET RequiereAprobacion = 0 WHERE EmpresaId = {exenta}");
            var (_, g) = Servicios();

            Assert.Equal(ResultadoGeneracion.Retenida, await g.GenerarAsync(retenida, Periodo, default));
            Assert.Equal(1, await Alertas(retenida, "APROBACION_REQUERIDA"));
            Assert.Equal(ResultadoGeneracion.Emitida, await g.GenerarAsync(exenta, Periodo, default));
        }
        finally
        {
            await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, "UPDATE fac.Parametro SET Valor = '0' WHERE Clave = 'REQUIERE_APROBACION'");
            Candado.Release();
        }
    }

    [Fact]
    public async Task Una_empresa_suspendida_se_factura_igual()   // D51
    {
        var emp = await EmpresaAsync(1_000_000m, activa: false);
        var (_, g) = Servicios();
        Assert.Equal(ResultadoGeneracion.Emitida, await g.GenerarAsync(emp, Periodo, default));
        Assert.Equal("EMITIDA", (await CuentaAsync(emp)).Estado);
    }

    [Fact]
    public async Task Sin_condicion_alerta_y_con_condicion_terminada_o_sin_cobro_no_genera_nada()
    {
        var (_, g) = Servicios();

        var nit = Interlocked.Increment(ref _secuencia).ToString();
        var sinCondicion = (await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido(nit, "Sin condición " + nit))).EmpresaId;
        Assert.Equal(ResultadoGeneracion.SinCondicion, await g.GenerarAsync(sinCondicion, Periodo, default));
        Assert.Equal(1, await Alertas(sinCondicion, "SIN_CONDICION"));

        var terminada = await EmpresaAsync(1_000_000m, hasta: "2026-09-30");
        Assert.Equal(ResultadoGeneracion.SinFacturable, await g.GenerarAsync(terminada, Periodo, default));
        var piloto = await EmpresaAsync(1_000_000m, facturable: false);
        Assert.Equal(ResultadoGeneracion.SinFacturable, await g.GenerarAsync(piloto, Periodo, default));

        foreach (var e in new[] { sinCondicion, terminada, piloto })
            Assert.Equal(0, await Etl<int>($"SELECT COUNT(*) FROM fac.CuentaCobro WHERE EmpresaId = {e}"));
    }

    [Fact]
    public async Task Un_canon_en_cero_alerta_total_cero_y_no_genera_cuenta()
    {
        var emp = await EmpresaAsync(0m);
        var (_, g) = Servicios();
        Assert.Equal(ResultadoGeneracion.TotalCero, await g.GenerarAsync(emp, Periodo, default));
        Assert.Equal(1, await Alertas(emp, "TOTAL_CERO"));
        Assert.Equal(0, await Etl<int>($"SELECT COUNT(*) FROM fac.CuentaCobro WHERE EmpresaId = {emp}"));
    }

    [Fact]
    public async Task El_trabajo_del_periodo_encola_una_generacion_por_empresa_facturable()
    {
        var con = await EmpresaAsync(1_000_000m);
        var sinCobro = await EmpresaAsync(1_000_000m, facturable: false);
        var (_, g) = Servicios();

        await g.EncolarPeriodoAsync(Periodo, default);

        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'GENERAR_CUENTA' AND ClaveExclusion = 'FAC:CC|EMP:{con}|PER:{Periodo}'"));
        Assert.Equal(0, await Etl<int>($"SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'GENERAR_CUENTA' AND EmpresaId = {sinCobro}"));
    }

    [Fact]
    public async Task La_prevalidacion_alerta_sin_escribir_cuentas()
    {
        var sinContacto = await EmpresaAsync(1_000_000m, contacto: false);
        var (_, g) = Servicios();

        await g.PrevalidarAsync(Periodo, default);

        Assert.Equal(1, await Alertas(sinContacto, "SIN_DESTINATARIO"));
        Assert.Equal(0, await Etl<int>($"SELECT COUNT(*) FROM fac.CuentaCobro WHERE EmpresaId = {sinContacto}"));
    }

    [Fact]
    public async Task El_programador_encola_a_la_hora_de_Bogota_y_no_repite()
    {
        // 2026-11-01 05:59 en Bogotá (10:59 UTC): aún no.
        var reloj = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 10, 59, 0, TimeSpan.Zero));
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton<TimeProvider>(reloj);
        s.AddProcesamiento(bd.CadenaEtl);
        s.AddProcesamientoFacturacion();
        await using var sp = s.BuildServiceProvider();
        var plan = sp.GetRequiredService<PlanificadorFacturacion>();

        Assert.Equal(0, await plan.EjecutarAsync(default));

        reloj.SetUtcNow(new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero));   // 06:00 Bogotá
        Assert.Equal(1, await plan.EjecutarAsync(default));
        Assert.Equal(1, await Etl<int>("SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'GENERAR_CUENTAS_COBRO' AND ClaveExclusion = 'FAC:GEN|PER:202610'"));
        Assert.Equal(0, await plan.EjecutarAsync(default));   // la tarea ya quedó marcada

        // Día 25 a las 08:00 de Bogotá: prevalidación del mes en curso (y la generación ya hecha no se repite).
        reloj.SetUtcNow(new DateTimeOffset(2026, 11, 25, 13, 0, 0, TimeSpan.Zero));
        Assert.Equal(2, await plan.EjecutarAsync(default));   // prevalidación + resumen diario de alertas (07:30)
        Assert.Equal(1, await Etl<int>("SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'PREVALIDAR_FACTURACION' AND ClaveExclusion = 'FAC:PRE|PER:202611'"));
        Assert.Equal(1, await Etl<int>("SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'RESUMEN_ALERTAS' AND ClaveExclusion = 'FAC:RES|DIA:20261125'"));
        Assert.Equal(0, await plan.EjecutarAsync(default));

        // El 1 de enero a las 00:30 Bogotá (05:30 UTC) la generación del mes vencido (diciembre) todavía espera a las 06:00.
        reloj.SetUtcNow(new DateTimeOffset(2027, 1, 1, 5, 30, 0, TimeSpan.Zero));
        Assert.Equal(0, await plan.EjecutarAsync(default));
        reloj.SetUtcNow(new DateTimeOffset(2027, 1, 1, 11, 0, 0, TimeSpan.Zero));
        Assert.Equal(1, await plan.EjecutarAsync(default));
        Assert.Equal(1, await Etl<int>("SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'GENERAR_CUENTAS_COBRO' AND ClaveExclusion = 'FAC:GEN|PER:202612'"));
    }
}
