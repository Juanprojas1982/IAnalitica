using FluentValidation;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace IAnalitica.Application.Tests.Mediacion;

[RequierePermiso("dash.cartera")]
[RequiereModulo("CARTERA")]
public sealed record ConsultaCartera(int Mes) : IRequest<Result<int>>;
public sealed class ConsultaCarteraHandler : IRequestHandler<ConsultaCartera, Result<int>>
{
    public Task<Result<int>> Handle(ConsultaCartera r, CancellationToken ct) => Task.FromResult(Result<int>.Ok(r.Mes));
}
public sealed class ConsultaCarteraValidator : AbstractValidator<ConsultaCartera>
{
    public ConsultaCarteraValidator() => RuleFor(x => x.Mes).InclusiveBetween(1, 12).WithErrorCode("MES_INVALIDO").WithMessage("El mes debe estar entre 1 y 12.");
}

[Collection("Mediacion")]
public class BehaviorsTests
{
    private static (ServiceProvider Sp, ISesionUsuario Sesion, IModulosEmpresa Modulos) Armar(bool permiso, bool moduloActivo, int? empresa = 7)
    {
        var sesion = Substitute.For<ISesionUsuario>();
        sesion.UsuarioId.Returns(1);
        sesion.EmpresaId.Returns(empresa);
        sesion.TienePermiso("dash.cartera").Returns(permiso);
        var modulos = Substitute.For<IModulosEmpresa>();
        modulos.EstaActivoAsync(Arg.Any<int>(), "CARTERA", Arg.Any<CancellationToken>()).Returns(moduloActivo);

        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton(sesion);
        s.AddSingleton(modulos);
        s.AddSingleton(Substitute.For<IVersionCarga>());
        s.AddSingleton(Substitute.For<ICacheConsultas>());
        s.AddSingleton(Substitute.For<ITransaccion>());
        s.AddApplication(new[] { typeof(ConsultaCartera), typeof(ConsultaCarteraHandler), typeof(ConsultaCarteraValidator) });
        return (s.BuildServiceProvider(), sesion, modulos);
    }

    private static Task<Result<int>> Enviar(ServiceProvider sp, int mes)
    {
        var scope = sp.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ISender>().Send(new ConsultaCartera(mes));
    }

    [Fact]
    public async Task Sin_permiso_devuelve_falla_NO_AUTORIZADO_sin_excepcion()
    {
        var (sp, _, _) = Armar(permiso: false, moduloActivo: true);
        var r = await Enviar(sp, 3);
        Assert.False(r.EsExitoso);
        Assert.Equal("NO_AUTORIZADO", Assert.Single(r.Errores).Codigo);
    }

    [Fact]
    public async Task Con_modulo_inactivo_devuelve_MODULO_INACTIVO()
    {
        var (sp, _, _) = Armar(permiso: true, moduloActivo: false);
        var r = await Enviar(sp, 3);
        Assert.Equal("MODULO_INACTIVO", Assert.Single(r.Errores).Codigo);
    }

    [Fact]
    public async Task Sin_empresa_en_el_contexto_no_ejecuta_requests_por_modulo()
    {
        var (sp, _, _) = Armar(permiso: true, moduloActivo: true, empresa: null);
        Assert.Equal("NO_AUTORIZADO", Assert.Single((await Enviar(sp, 3)).Errores).Codigo);
    }

    [Fact]
    public async Task La_validacion_devuelve_los_errores_como_Result()
    {
        var (sp, _, _) = Armar(permiso: true, moduloActivo: true);
        var r = await Enviar(sp, 13);
        Assert.Equal("MES_INVALIDO", Assert.Single(r.Errores).Codigo);
    }

    [Fact]
    public async Task Con_permiso_modulo_activo_y_datos_validos_llega_al_handler()
    {
        var (sp, _, _) = Armar(permiso: true, moduloActivo: true);
        var r = await Enviar(sp, 5);
        Assert.True(r.EsExitoso);
        Assert.Equal(5, r.Valor);
    }

    [Fact]
    public void Leer_el_valor_de_un_resultado_fallido_lanza()
        => Assert.Throws<InvalidOperationException>(() => Result<int>.Falla(Error.NoEncontrado("x")).Valor);
}
