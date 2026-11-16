using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Facturacion;
using NSubstitute;

namespace IAnalitica.Application.Tests.Facturacion;

public class CarteraFacturacionTests
{
    private static ISesionUsuario Sesion(bool superadmin, int? empresa = null)
    {
        var s = Substitute.For<ISesionUsuario>();
        s.EsSuperAdmin.Returns(superadmin);
        s.UsuarioId.Returns(7);
        s.EmpresaId.Returns(empresa);
        return s;
    }

    private static CuentaCobroResumen Cuenta(int id, string estado, DateTime? vence = null, decimal saldo = 100m)
        => new(id, 5, "Empresa", 202610, estado == "BORRADOR" ? null : $"CC-{id:D6}", estado, saldo, 0m, saldo, null, vence, false, null, null);

    private static readonly TimeProvider Hoy = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 12, 10, 13, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task El_pago_valida_clase_medio_valor_y_referencia_sin_numeros_largos()
    {
        var v = new RegistrarPagoValidator();
        RegistrarPagoCommand Pago(string clase = "RECAUDO", string? medio = "PSE", decimal valor = 10m, string? referencia = "COMP-1")
            => new(5, clase, medio, new DateOnly(2026, 3, 1), valor, referencia, null, false);

        Assert.True(v.Validate(Pago()).IsValid);
        Assert.True(v.Validate(Pago(clase: "RETENCION", medio: null)).IsValid);
        Assert.False(v.Validate(Pago(medio: null)).IsValid);                      // el recaudo exige medio
        Assert.False(v.Validate(Pago(clase: "OTRA")).IsValid);
        Assert.False(v.Validate(Pago(valor: 0m)).IsValid);
        Assert.False(v.Validate(Pago(referencia: "cuenta 1234567890")).IsValid);  // nunca números de cuenta ni de tarjeta
        Assert.True(v.Validate(Pago(referencia: "COMP-123456789")).IsValid);      // 9 dígitos seguidos: aceptado
    }

    [Fact]
    public void Aplicar_exige_cuentas_distintas_y_valores_positivos()
    {
        var v = new AplicarPagoValidator();
        Assert.True(v.Validate(new AplicarPagoCommand(1, [new(10, 5m), new(11, 5m)])).IsValid);
        Assert.False(v.Validate(new AplicarPagoCommand(1, [])).IsValid);
        Assert.False(v.Validate(new AplicarPagoCommand(1, [new(10, 5m), new(10, 5m)])).IsValid);
        Assert.False(v.Validate(new AplicarPagoCommand(1, [new(10, 0m)])).IsValid);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("corto", false)]
    [InlineData("Motivo suficientemente largo", true)]
    public void Anular_descartar_y_reversar_exigen_un_motivo(string? motivo, bool valido)
    {
        Assert.Equal(valido, new AnularCuentaCobroValidator().Validate(new AnularCuentaCobroCommand(1, motivo, false)).IsValid);
        Assert.Equal(valido, new DescartarBorradorValidator().Validate(new DescartarBorradorCommand(1, motivo)).IsValid);
        Assert.Equal(valido, new ReversarAplicacionValidator().Validate(new ReversarAplicacionCommand(1, motivo)).IsValid);
        Assert.Equal(valido, new AnularPagoValidator().Validate(new AnularPagoCommand(1, motivo)).IsValid);
    }

    [Fact]
    public async Task Las_acciones_de_administracion_no_llegan_al_repositorio_sin_superadmin()
    {
        var repo = Substitute.For<IRepositorioFacturacion>();
        var cliente = Sesion(superadmin: false, empresa: 5);

        Assert.False((await new EmitirCuentaCobroHandler(cliente, repo).Handle(new(1, null), default)).EsExitoso);
        Assert.False((await new AnularCuentaCobroHandler(cliente, repo).Handle(new(1, "Motivo suficientemente largo", true), default)).EsExitoso);
        Assert.False((await new RegistrarPagoHandler(cliente, repo).Handle(new(5, "RECAUDO", "PSE", new DateOnly(2026, 3, 1), 1m, null, null, true), default)).EsExitoso);
        Assert.False((await new AplicarPagoHandler(cliente, repo).Handle(new(1, [new(2, 1m)]), default)).EsExitoso);
        Assert.False((await new ReenviarCuentaCobroHandler(cliente, repo).Handle(new(1), default)).EsExitoso);
        Assert.False((await new CarteraFacturacionHandler(cliente, repo, Hoy).Handle(new(null), default)).EsExitoso);
        Assert.Empty(repo.ReceivedCalls());
    }

    [Fact]
    public async Task El_pago_con_aplicacion_automatica_aplica_despues_de_registrar_y_un_error_se_propaga()
    {
        var repo = Substitute.For<IRepositorioFacturacion>();
        var pago = new RegistrarPagoCommand(5, "RECAUDO", "PSE", new DateOnly(2026, 3, 1), 100m, "C-1", null, AplicarAutomatico: true);
        repo.RegistrarPagoAsync(pago, 7, Arg.Any<CancellationToken>()).Returns(Result<int>.Ok(42));
        repo.AplicarAutomaticoAsync(42, 7, Arg.Any<CancellationToken>()).Returns(Result.Ok());

        var r = await new RegistrarPagoHandler(Sesion(true), repo).Handle(pago, default);
        Assert.Equal(42, r.Valor);
        await repo.Received(1).AplicarAutomaticoAsync(42, 7, Arg.Any<CancellationToken>());

        // Sin aplicación automática no se aplica nada.
        var repo2 = Substitute.For<IRepositorioFacturacion>();
        repo2.RegistrarPagoAsync(Arg.Any<RegistrarPagoCommand>(), 7, Arg.Any<CancellationToken>()).Returns(Result<int>.Ok(43));
        await new RegistrarPagoHandler(Sesion(true), repo2).Handle(pago with { AplicarAutomatico = false }, default);
        await repo2.DidNotReceiveWithAnyArgs().AplicarAutomaticoAsync(default, default, default);
    }

    [Fact]
    public async Task El_cliente_solo_ve_emitidas_pagadas_y_anuladas_de_su_empresa_con_la_edad_derivada()
    {
        var repo = Substitute.For<IRepositorioFacturacion>();
        repo.ListarCuentasAsync(Arg.Any<FiltroCuentas>(), Arg.Any<CancellationToken>()).Returns(
        [
            Cuenta(1, "EMITIDA", vence: new DateTime(2026, 12, 20)),           // al día
            Cuenta(2, "EMITIDA", vence: new DateTime(2026, 12, 1)),            // 9 días de mora
            Cuenta(3, "BORRADOR"),                                             // interno
            Cuenta(4, "DESCARTADA"),                                           // interno
            Cuenta(5, "PAGADA", vence: new DateTime(2026, 11, 1), saldo: 0m),
        ]);
        var h = new ListarCuentasCobroHandler(Sesion(false, empresa: 5), repo, Hoy);

        var r = await h.Handle(new ListarCuentasCobroQuery(EmpresaId: 99, null, null, false), default);   // intenta ver otra empresa

        Assert.Equal([1, 2, 5], r.Valor.Select(c => c.CuentaCobroId).ToArray());
        await repo.Received().ListarCuentasAsync(Arg.Is<FiltroCuentas>(f => f.EmpresaId == 5 && f.SoloEmitidasYPagadas), Arg.Any<CancellationToken>());   // la sesión manda
        Assert.Equal("AL_DIA", r.Valor.Single(c => c.CuentaCobroId == 1).Edad);
        Assert.Equal("1_30", r.Valor.Single(c => c.CuentaCobroId == 2).Edad);
        Assert.True(r.Valor.Single(c => c.CuentaCobroId == 2).EnMora);

        var soloMora = await h.Handle(new ListarCuentasCobroQuery(null, null, null, SoloMora: true), default);
        Assert.Equal([2], soloMora.Valor.Select(c => c.CuentaCobroId).ToArray());
    }

    [Fact]
    public async Task El_estado_de_cuenta_suma_saldo_y_vencido_solo_de_las_emitidas()
    {
        var repo = Substitute.For<IRepositorioFacturacion>();
        repo.ListarCuentasAsync(Arg.Any<FiltroCuentas>(), Arg.Any<CancellationToken>()).Returns(
        [
            Cuenta(1, "EMITIDA", vence: new DateTime(2026, 12, 20), saldo: 300m),
            Cuenta(2, "EMITIDA", vence: new DateTime(2026, 12, 1), saldo: 200m),
            Cuenta(3, "PAGADA", vence: new DateTime(2026, 11, 1), saldo: 0m),
            Cuenta(4, "ANULADA", vence: new DateTime(2026, 10, 1), saldo: 999m),
        ]);
        repo.SaldoAFavorAsync(5, Arg.Any<CancellationToken>()).Returns(50m);

        var e = (await new EstadoCuentaEmpresaHandler(Sesion(false, empresa: 5), repo, Hoy).Handle(new(), default)).Valor;

        Assert.Equal(500m, e.SaldoCop);
        Assert.Equal(200m, e.VencidoCop);
        Assert.Equal(50m, e.SaldoAFavorCop);
    }

    [Fact]
    public async Task Una_cuenta_en_borrador_no_se_le_muestra_al_cliente()
    {
        var repo = Substitute.For<IRepositorioFacturacion>();
        var borrador = new CuentaCobroDetalle(Cuenta(3, "BORRADOR"), [], [], [], null, false, null);
        repo.ObtenerCuentaAsync(3, Arg.Any<CancellationToken>()).Returns(borrador);

        Assert.Equal("NO_ENCONTRADO", (await new ObtenerCuentaCobroHandler(Sesion(false, 5), repo, Hoy).Handle(new(3), default)).Errores[0].Codigo);
        Assert.True((await new ObtenerCuentaCobroHandler(Sesion(true), repo, Hoy).Handle(new(3), default)).EsExitoso);
    }
}
