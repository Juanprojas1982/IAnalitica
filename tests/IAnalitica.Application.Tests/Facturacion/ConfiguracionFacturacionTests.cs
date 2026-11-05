using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Facturacion;
using NSubstitute;

namespace IAnalitica.Application.Tests.Facturacion;

public class ConfiguracionFacturacionTests
{
    private static ISesionUsuario Sesion(bool superadmin, int? usuario = 7)
    {
        var s = Substitute.For<ISesionUsuario>();
        s.EsSuperAdmin.Returns(superadmin);
        s.UsuarioId.Returns(usuario);
        return s;
    }

    private static GuardarCondicionComercialCommand Condicion(DateOnly? desde = null)
        => new(12, desde ?? new DateOnly(2026, 10, 15), 1, 900_000m, null, null, true, null);

    [Fact]
    public async Task Quien_no_es_superadmin_no_puede_guardar_nada()
    {
        var repo = Substitute.For<IRepositorioFacturacionConfig>();
        var sesion = Sesion(superadmin: false);

        var canon = await new GuardarCanonPlanHandler(sesion, repo).Handle(new(1, new DateOnly(2026, 10, 1), 500_000m), default);
        var cond = await new GuardarCondicionComercialHandler(sesion, repo).Handle(Condicion(), default);
        var param = await new GuardarParametroFacturacionHandler(sesion, repo).Handle(new("REQUIERE_APROBACION", "1"), default);

        Assert.Equal("NO_AUTORIZADO", canon.Errores[0].Codigo);
        Assert.Equal("NO_AUTORIZADO", cond.Errores[0].Codigo);
        Assert.Equal("NO_AUTORIZADO", param.Errores[0].Codigo);
        await repo.DidNotReceiveWithAnyArgs().GuardarCanonPlanAsync(default, default, default, default, default);
        await repo.DidNotReceiveWithAnyArgs().GuardarParametroAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task El_superadmin_guarda_con_su_id_y_el_resultado_del_repositorio_se_devuelve_tal_cual()
    {
        var repo = Substitute.For<IRepositorioFacturacionConfig>();
        repo.GuardarCondicionAsync(Arg.Any<GuardarCondicionComercialCommand>(), 7, Arg.Any<CancellationToken>()).Returns(Result<int>.Ok(55));
        repo.GuardarCanonPlanAsync(1, new DateOnly(2026, 10, 1), 500_000m, 7, Arg.Any<CancellationToken>()).Returns(Result.Ok());
        repo.GuardarParametroAsync("REQUIERE_APROBACION", "1", 7, Arg.Any<CancellationToken>())
            .Returns(Result.Falla(Error.Validacion("PARAMETRO_INVALIDO", "x")));
        var sesion = Sesion(superadmin: true);

        Assert.Equal(55, (await new GuardarCondicionComercialHandler(sesion, repo).Handle(Condicion(), default)).Valor);
        Assert.True((await new GuardarCanonPlanHandler(sesion, repo).Handle(new(1, new DateOnly(2026, 10, 1), 500_000m), default)).EsExitoso);
        Assert.Equal("PARAMETRO_INVALIDO", (await new GuardarParametroFacturacionHandler(sesion, repo).Handle(new("REQUIERE_APROBACION", "1"), default)).Errores[0].Codigo);
    }

    [Fact]
    public async Task Una_sesion_sin_usuario_no_se_trata_como_superadmin()
    {
        var repo = Substitute.For<IRepositorioFacturacionConfig>();
        var r = await new GuardarCanonPlanHandler(Sesion(superadmin: true, usuario: null), repo).Handle(new(1, new DateOnly(2026, 10, 1), 1m), default);
        Assert.False(r.EsExitoso);
    }

    [Fact]
    public async Task Las_consultas_de_configuracion_exigen_superadmin()
    {
        var repo = Substitute.For<IRepositorioFacturacionConfig>();
        var cliente = Sesion(superadmin: false);

        Assert.False((await new ListarPlanesFacturacionHandler(cliente, repo).Handle(new(), default)).EsExitoso);
        Assert.False((await new ListarCondicionesComercialesHandler(cliente, repo).Handle(new(12), default)).EsExitoso);
        Assert.False((await new ListarParametrosFacturacionHandler(cliente, repo).Handle(new(), default)).EsExitoso);
        await repo.DidNotReceive().ListarParametrosAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Validaciones_del_canon_la_vigencia_y_los_dias()
    {
        var canon = new GuardarCanonPlanValidator();
        Assert.True(canon.Validate(new GuardarCanonPlanCommand(1, new DateOnly(2026, 10, 1), 0m)).IsValid);
        Assert.False(canon.Validate(new GuardarCanonPlanCommand(1, new DateOnly(2026, 10, 15), 1m)).IsValid);   // el canon de plan rige por mes completo
        Assert.False(canon.Validate(new GuardarCanonPlanCommand(1, new DateOnly(2026, 10, 1), -1m)).IsValid);

        var cond = new GuardarCondicionComercialValidator();
        Assert.True(cond.Validate(Condicion()).IsValid);   // la condición sí puede empezar a mitad de mes (prorrateo)
        Assert.False(cond.Validate(Condicion() with { EmpresaId = 0 }).IsValid);
        Assert.False(cond.Validate(Condicion() with { DiasPago = 181 }).IsValid);
        Assert.False(cond.Validate(Condicion() with { CanonPactadoCop = -5m }).IsValid);
        Assert.False(new GuardarParametroFacturacionValidator().Validate(new GuardarParametroFacturacionCommand("REQUIERE_APROBACION", "")).IsValid);
    }
}
