using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Facturacion;
using IAnalitica.Application.Mediacion;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Facturacion;
using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>
/// Facturación, pasos 8-9: pagos, vista del cliente, anulación con reemplazo y mora, contra SQL Server real. Cada prueba usa su propia empresa.
/// Las acciones se ejecutan por el mediador con la sesión y el contexto de BD reales (RLS), como lo hace el portal.
/// </summary>
[Collection(ColeccionBd.Nombre)]
public class FacturacionCarteraTests(BaseDeDatosFixture bd)
{
    private static int _secuencia = 930000000;
    private static int _periodo = 203400;

    private async Task<TResp> SuperAsync<TResp>(IRequest<TResp> request)
        => await EnviarAsync(new SesionPrueba(bd.SuperadminId, null, true, "fac.administrar", "fac.consultar"), ContextoBd.DeSuperadmin(bd.SuperadminId), request);

    private async Task<TResp> ClienteAsync<TResp>(int empresaId, IRequest<TResp> request)
        => await EnviarAsync(new SesionPrueba(990001, empresaId, false, "fac.consultar"), ContextoBd.DeEmpresa(empresaId, 990001, esAdminPrincipal: true), request);

    private async Task<TResp> EnviarAsync<TResp>(ISesionUsuario sesion, ContextoBd contexto, IRequest<TResp> request)
    {
        await using var sp = bd.Construir(bd.CadenaApp, sesion, s => s.AddFacturacion());
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(contexto);
        return await alcance.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private async Task<T> Etl<T>(string sql) => await BaseDeDatosFixture.EscalarAsync<T>(bd.CadenaEtl, sql, superadmin: false);

    private async Task<int> NuevaEmpresaAsync()
    {
        var nit = Interlocked.Increment(ref _secuencia).ToString();
        var id = (await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido(nit, "Cliente de cartera " + nit))).EmpresaId;
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"""
            INSERT fac.CondicionComercial (EmpresaId, VigenteDesde, PlanId, CanonPactadoCop, CreadoPor)
            VALUES ({id}, '2026-01-01', (SELECT TOP (1) PlanId FROM ia.PlanComercial ORDER BY PlanId), 600000, {bd.SuperadminId})
            """);
        return id;
    }

    /// <summary>Crea un borrador de 600.000 en un periodo propio (cada cuenta usa uno distinto) y, si se pide, lo emite por el comando real.</summary>
    private async Task<int> CuentaAsync(int empresaId, bool emitir = true, int valor = 600_000)
    {
        var periodo = Interlocked.Increment(ref _periodo);
        if (periodo % 100 > 12) periodo = Interlocked.Add(ref _periodo, 88);   // 203413 → 203501
        var cond = await Etl<int>($"SELECT CondicionId FROM fac.CondicionComercial WHERE EmpresaId = {empresaId} AND VigenteHasta IS NULL");
        var anio = periodo / 100; var mes = periodo % 100;
        var dias = DateTime.DaysInMonth(anio, mes);
        var id = await Etl<int>($"""
            INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, DiasPago) VALUES ({empresaId}, {periodo}, {cond}, 30);
            DECLARE @id int = CAST(SCOPE_IDENTITY() AS int);
            INSERT fac.CuentaCobroLinea (CuentaCobroId, EmpresaId, Orden, Tipo, Concepto, PeriodoServicioKey, CondicionId, FechaDesde, FechaHasta, DiasFacturados, DiasPeriodo, CanonMensualCop, ValorCop)
            VALUES (@id, {empresaId}, 1, 'CANON', N'Canon', {periodo}, {cond}, '{anio}-{mes:D2}-01', '{anio}-{mes:D2}-{dias:D2}', {dias}, {dias}, {valor}, {valor});
            SELECT @id
            """);
        if (emitir) Assert.True((await SuperAsync(new EmitirCuentaCobroCommand(id, null))).EsExitoso);
        return id;
    }

    [Fact]
    public async Task El_pago_aplicado_en_automatico_cubre_primero_la_cuenta_que_vence_antes_y_el_excedente_queda_a_favor()
    {
        var emp = await NuevaEmpresaAsync();
        var primera = await CuentaAsync(emp);
        var segunda = await CuentaAsync(emp);
        // Vencimientos distintos: la primera vence antes.
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"UPDATE fac.CuentaCobro SET FechaVencimiento = '2026-01-10' WHERE CuentaCobroId = {primera}; UPDATE fac.CuentaCobro SET FechaVencimiento = '2026-02-10' WHERE CuentaCobroId = {segunda};");

        var pago1 = await SuperAsync(new RegistrarPagoCommand(emp, "RECAUDO", "TRANSFERENCIA", new DateOnly(2026, 3, 1), 800_000m, "COMP-001", null, AplicarAutomatico: true));
        Assert.True(pago1.EsExitoso);
        Assert.Equal("PAGADA", await Etl<string>($"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {primera}"));
        Assert.Equal(400_000m, await Etl<decimal>($"SELECT SaldoCop FROM fac.CuentaCobro WHERE CuentaCobroId = {segunda}"));

        // Un pago mayor al saldo: la cuenta queda pagada y el resto es saldo a favor.
        var pago2 = await SuperAsync(new RegistrarPagoCommand(emp, "RECAUDO", "PSE", new DateOnly(2026, 3, 2), 500_000m, "COMP-002", null, AplicarAutomatico: true));
        Assert.True(pago2.EsExitoso);
        Assert.Equal("PAGADA", await Etl<string>($"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {segunda}"));
        var estado = (await ClienteAsync(emp, new EstadoCuentaEmpresaQuery())).Valor;
        Assert.Equal(0m, estado.SaldoCop);
        Assert.Equal(100_000m, estado.SaldoAFavorCop);
    }

    [Fact]
    public async Task Una_retencion_y_un_recaudo_completan_la_cuenta_y_la_referencia_repetida_se_rechaza()
    {
        var emp = await NuevaEmpresaAsync();
        var cuenta = await CuentaAsync(emp);

        var recaudo = await SuperAsync(new RegistrarPagoCommand(emp, "RECAUDO", "CONSIGNACION", new DateOnly(2026, 3, 1), 570_000m, "R-77", null, false));
        var retencion = await SuperAsync(new RegistrarPagoCommand(emp, "RETENCION", null, new DateOnly(2026, 3, 1), 30_000m, "RET-77", "Retención practicada", false));
        Assert.True(recaudo.EsExitoso && retencion.EsExitoso);

        Assert.True((await SuperAsync(new AplicarPagoCommand(recaudo.Valor, [new(cuenta, 570_000m)]))).EsExitoso);
        Assert.Equal("EMITIDA", await Etl<string>($"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));
        Assert.True((await SuperAsync(new AplicarPagoCommand(retencion.Valor, [new(cuenta, 30_000m)]))).EsExitoso);
        Assert.Equal("PAGADA", await Etl<string>($"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));

        var duplicado = await SuperAsync(new RegistrarPagoCommand(emp, "RECAUDO", "CONSIGNACION", new DateOnly(2026, 3, 1), 1m, "R-77", null, false));
        Assert.False(duplicado.EsExitoso);
        Assert.Equal("DUPLICADO", duplicado.Errores[0].Codigo);

        // Reverso: la cuenta se reabre.
        var apl = await Etl<int>($"SELECT TOP (1) AplicacionId FROM fac.AplicacionPago WHERE PagoId = {retencion.Valor} AND ReversadaEn IS NULL");
        Assert.True((await SuperAsync(new ReversarAplicacionCommand(apl, "La retención no aplica este mes"))).EsExitoso);
        Assert.Equal("EMITIDA", await Etl<string>($"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));
    }

    [Fact]
    public async Task El_cliente_ve_sus_cuentas_emitidas_sin_borradores_y_nunca_las_de_otra_empresa()
    {
        var mia = await NuevaEmpresaAsync();
        var otra = await NuevaEmpresaAsync();
        var emitida = await CuentaAsync(mia);
        var borrador = await CuentaAsync(mia, emitir: false);
        var ajena = await CuentaAsync(otra);

        var lista = (await ClienteAsync(mia, new ListarCuentasCobroQuery(otra, null, null, false))).Valor;   // pide la ajena: la sesión manda
        Assert.Equal([emitida], lista.Select(c => c.CuentaCobroId).ToArray());

        Assert.True((await ClienteAsync(mia, new ObtenerCuentaCobroQuery(emitida))).EsExitoso);
        Assert.Equal("NO_ENCONTRADO", (await ClienteAsync(mia, new ObtenerCuentaCobroQuery(borrador))).Errores[0].Codigo);   // borrador: interno
        Assert.Equal("NO_ENCONTRADO", (await ClienteAsync(mia, new ObtenerCuentaCobroQuery(ajena))).Errores[0].Codigo);     // otra empresa: la RLS la oculta
    }

    [Fact]
    public async Task El_cliente_no_puede_ejecutar_acciones_de_administracion()
    {
        var emp = await NuevaEmpresaAsync();
        var borrador = await CuentaAsync(emp, emitir: false);

        Assert.False((await ClienteAsync(emp, new EmitirCuentaCobroCommand(borrador, null))).EsExitoso);
        Assert.False((await ClienteAsync(emp, new RegistrarPagoCommand(emp, "RECAUDO", "PSE", new DateOnly(2026, 3, 1), 1m, null, null, false))).EsExitoso);
        Assert.False((await ClienteAsync(emp, new AnularCuentaCobroCommand(borrador, "Intento de anulación del cliente", false))).EsExitoso);
        Assert.False((await ClienteAsync(emp, new ListarAlertasQuery())).EsExitoso);
        Assert.Equal("BORRADOR", await Etl<string>($"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {borrador}"));
    }

    [Fact]
    public async Task Anular_con_reemplazo_avisa_al_cliente_y_la_nueva_cuenta_referencia_a_la_anulada()
    {
        var emp = await NuevaEmpresaAsync();
        var cuenta = await CuentaAsync(emp);
        var periodo = await Etl<int>($"SELECT PeriodoKey FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}");

        var r = await SuperAsync(new AnularCuentaCobroCommand(cuenta, "El valor del canon estaba mal calculado", EmitirReemplazo: true));
        Assert.True(r.EsExitoso);
        Assert.Equal("ANULADA", await Etl<string>($"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'ENCOLAR_ENVIO' AND ClaveExclusion = 'FAC:ANU|CC:{cuenta}'"));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'GENERAR_CUENTA' AND ClaveExclusion = 'FAC:REEMP|CC:{cuenta}'"));

        // El worker genera la que la reemplaza: periodo libre, número nuevo y ReemplazaA.
        var s = new ServiceCollection();
        s.AddLogging(); s.AddProcesamiento(bd.CadenaEtl); s.AddProcesamientoFacturacion();
        await using var sp = s.BuildServiceProvider();
        var generador = sp.GetRequiredService<GeneradorCuentas>();
        var anterior = await Etl<string>($"SELECT Numero FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}");
        _ = await generador.GenerarAsync(emp, periodo, default, reemplazaA: cuenta);
        var nueva = await Etl<int>($"SELECT CuentaCobroId FROM fac.CuentaCobro WHERE EmpresaId = {emp} AND PeriodoKey = {periodo} AND Estado <> 'ANULADA'");
        Assert.NotEqual(cuenta, nueva);
        Assert.Equal(cuenta, await Etl<int>($"SELECT ReemplazaA FROM fac.CuentaCobro WHERE CuentaCobroId = {nueva}"));
        Assert.Equal(anterior, await Etl<string>($"SELECT Numero FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));   // la anulada conserva su número
    }

    [Fact]
    public async Task Descartar_un_borrador_libera_el_periodo_y_regenerar_encola_el_trabajo()
    {
        var emp = await NuevaEmpresaAsync();
        var borrador = await CuentaAsync(emp, emitir: false);
        var otro = await CuentaAsync(emp, emitir: false);

        Assert.True((await SuperAsync(new RegenerarBorradorCommand(otro))).EsExitoso);
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'REGENERAR_BORRADOR' AND ClaveExclusion = 'FAC:REG|CC:{otro}'"));

        Assert.True((await SuperAsync(new DescartarBorradorCommand(borrador, "Se creó por error de prueba"))).EsExitoso);
        Assert.Equal("DESCARTADA", await Etl<string>($"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {borrador}"));
        Assert.False((await SuperAsync(new DescartarBorradorCommand(borrador, "Segundo descarte del mismo"))).EsExitoso);   // ya no es borrador
    }

    [Fact]
    public async Task Reintentar_reenviar_y_marcar_rebotado()
    {
        var emp = await NuevaEmpresaAsync();
        var cuenta = await CuentaAsync(emp);
        var envio = await Etl<Guid>($"""
            DECLARE @e TABLE (Id uniqueidentifier);
            INSERT fac.Envio (EmpresaId, CuentaCobroId, Tipo, EsReenvio, ConAdjunto, MessageId, Estado, CodigoError)
            OUTPUT inserted.EnvioId INTO @e VALUES ({emp}, {cuenta}, 'CUENTA_COBRO', 0, 1, 'fac-test-{emp}@ianalitica.test', 'FALLIDO', 'TIMEOUT');
            DECLARE @id uniqueidentifier = (SELECT Id FROM @e);
            INSERT fac.EnvioDestinatario (EnvioId, EmpresaId, Orden, Clase, Email) VALUES (@id, {emp}, 1, 'TO', N'x{emp}@cliente.test');
            SELECT @id
            """);

        Assert.True((await SuperAsync(new ReintentarEnvioCommand(envio))).EsExitoso);
        Assert.Equal("PENDIENTE", await Etl<string>($"SELECT Estado FROM fac.Envio WHERE EnvioId = '{envio}'"));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'ENVIAR_CORREO' AND ClaveExclusion = 'CORREO:{envio}'"));
        Assert.False((await SuperAsync(new ReintentarEnvioCommand(envio))).EsExitoso);   // ya no está fallido

        Assert.True((await SuperAsync(new ReenviarCuentaCobroCommand(cuenta))).EsExitoso);
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'ENCOLAR_ENVIO' AND Payload LIKE '%\"cuentaCobroId\":{cuenta},%'"));

        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"UPDATE fac.Envio SET Estado = 'ENVIADO' WHERE EnvioId = '{envio}'");
        Assert.True((await SuperAsync(new MarcarEnvioRebotadoCommand(envio, "BUZON_LLENO"))).EsExitoso);
        Assert.Equal("REBOTADO", await Etl<string>($"SELECT Estado FROM fac.Envio WHERE EnvioId = '{envio}'"));
    }

    // ------------------------------------------------------------------ mora (paso 9)
    [Fact]
    public async Task La_mora_envia_recordatorio_previo_y_avisos_una_sola_vez_por_hito_y_sin_adjunto()
    {
        var emp = await NuevaEmpresaAsync();
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"INSERT seg.EmpresaContacto (EmpresaId, Tipo, Nombre, Email) VALUES ({emp}, 'FACTURACION', N'Tesorería', N't{emp}@cliente.test')");
        var cuenta = await CuentaAsync(emp);
        var pagada = await CuentaAsync(emp);
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"""
            UPDATE fac.CuentaCobro SET FechaVencimiento = '2026-12-01' WHERE CuentaCobroId IN ({cuenta}, {pagada});
            UPDATE fac.CuentaCobro SET Estado = 'PAGADA', PagadoCop = TotalCop WHERE CuentaCobroId = {pagada};
            """);
        var reloj = new FakeTimeProvider(new DateTimeOffset(2026, 11, 28, 13, 0, 0, TimeSpan.Zero));   // 08:00 en Bogotá
        var s = new ServiceCollection();
        s.AddLogging(); s.AddSingleton<TimeProvider>(reloj); s.AddProcesamiento(bd.CadenaEtl); s.AddProcesamientoFacturacion();
        s.AddCorreoSaliente(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), esDesarrollo: true);
        await using var sp = s.BuildServiceProvider();
        var mora = sp.GetRequiredService<RevisorMora>();

        // Hay otras empresas de otras pruebas: se verifica lo de esta cuenta.
        await mora.EjecutarAsync(default);
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.Envio WHERE CuentaCobroId = {cuenta} AND Tipo = 'RECORDATORIO' AND Hito = 'PREVIO_3' AND ConAdjunto = 0"));
        await mora.EjecutarAsync(default);   // el mismo día otra vez: nada nuevo
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.Envio WHERE CuentaCobroId = {cuenta}"));
        Assert.Equal(0, await Etl<int>($"SELECT COUNT(*) FROM fac.Envio WHERE CuentaCobroId = {pagada}"));   // pagada: no se cobra

        reloj.SetUtcNow(new DateTimeOffset(2026, 12, 2, 13, 0, 0, TimeSpan.Zero));   // 1 día de mora (desde el día siguiente al vencimiento)
        await mora.EjecutarAsync(default);
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.Envio WHERE CuentaCobroId = {cuenta} AND Tipo = 'AVISO_MORA' AND Hito = 'MORA_1'"));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM seg.Notificacion WHERE EmpresaId = {emp} AND Tipo = 'FACTURACION_MORA'"));

        reloj.SetUtcNow(new DateTimeOffset(2026, 12, 20, 13, 0, 0, TimeSpan.Zero));   // 19 días: solo el aviso de 15
        await mora.EjecutarAsync(default);
        await mora.EjecutarAsync(default);
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.Envio WHERE CuentaCobroId = {cuenta} AND Hito = 'MORA_15'"));
        Assert.Equal(0, await Etl<int>($"SELECT COUNT(*) FROM fac.Envio WHERE CuentaCobroId = {cuenta} AND Hito = 'MORA_30'"));
        Assert.Equal(3, await Etl<int>($"SELECT COUNT(*) FROM fac.Envio WHERE CuentaCobroId = {cuenta}"));
    }

    [Fact]
    public async Task El_dia_del_vencimiento_la_cuenta_aun_no_esta_en_mora()
    {
        var emp = await NuevaEmpresaAsync();
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"INSERT seg.EmpresaContacto (EmpresaId, Tipo, Nombre, Email) VALUES ({emp}, 'FACTURACION', N'Tesorería', N't{emp}@cliente.test')");
        var cuenta = await CuentaAsync(emp);
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"UPDATE fac.CuentaCobro SET FechaVencimiento = '2027-02-10' WHERE CuentaCobroId = {cuenta}");
        var reloj = new FakeTimeProvider(new DateTimeOffset(2027, 2, 10, 13, 0, 0, TimeSpan.Zero));
        var s = new ServiceCollection();
        s.AddLogging(); s.AddSingleton<TimeProvider>(reloj); s.AddProcesamiento(bd.CadenaEtl); s.AddProcesamientoFacturacion();
        s.AddCorreoSaliente(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), esDesarrollo: true);
        await using var sp = s.BuildServiceProvider();

        await sp.GetRequiredService<RevisorMora>().EjecutarAsync(default);

        Assert.Equal(0, await Etl<int>($"SELECT COUNT(*) FROM fac.Envio WHERE CuentaCobroId = {cuenta} AND Tipo = 'AVISO_MORA'"));
        var estado = (await ClienteAsync(emp, new EstadoCuentaEmpresaQuery())).Valor;   // el reloj real (2026) es anterior al vencimiento: al día
        Assert.Equal(0m, estado.VencidoCop);
    }
}
