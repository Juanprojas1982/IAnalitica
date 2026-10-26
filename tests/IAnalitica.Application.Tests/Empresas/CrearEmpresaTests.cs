using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Empresas;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace IAnalitica.Application.Tests.Empresas;

public class CrearEmpresaTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static CrearEmpresaCommand Comando(string nit = "800197268", string dv = "4") => new(
        nit, dv, "Clínica Prueba SAS", null, "IPS", null, "gerencia@prueba.co", "Cali", "Valle", null, null, null, null, null, null,
        new[] { "FINANCIERO", "CARTERA" }, null);

    private sealed class Entorno
    {
        public readonly ISesionUsuario Sesion = Substitute.For<ISesionUsuario>();
        public readonly IRepositorioEmpresas Empresas = Substitute.For<IRepositorioEmpresas>();
        public readonly IGestorUsuarios Usuarios = Substitute.For<IGestorUsuarios>();
        public readonly IGestorCredencialesApi Api = Substitute.For<IGestorCredencialesApi>();
        public readonly IAuditoriaAdministrativa Auditoria = Substitute.For<IAuditoriaAdministrativa>();
        public readonly IGeneradorSecretos Secretos = Substitute.For<IGeneradorSecretos>();
        public readonly IConfiguracionPortal Portal = Substitute.For<IConfiguracionPortal>();
        public readonly List<CambioAdministrativo> Cambios = new();

        public Entorno()
        {
            Sesion.UsuarioId.Returns(1);
            Empresas.CrearAsync(Arg.Any<EmpresaNueva>(), Arg.Any<CancellationToken>()).Returns(42);
            Usuarios.CrearAdministradorPrincipalAsync(Arg.Any<AdministradorNuevo>(), Arg.Any<CancellationToken>()).Returns(Result<int>.Ok(7));
            Secretos.ClaveTemporal().Returns("Kq7#vR2m@Lp9xT");
            Secretos.SecretoCliente().Returns("SECRETO-CONECTOR-UNICO");
            Portal.UrlPortal.Returns("https://portal.local");
            Portal.UrlApi.Returns("https://api.local");
            Auditoria.When(a => a.RegistrarAsync(Arg.Any<CambioAdministrativo>(), Arg.Any<CancellationToken>()))
                .Do(ci => Cambios.Add(ci.Arg<CambioAdministrativo>()));
        }

        public CrearEmpresaCommandHandler Handler() =>
            new(Sesion, Empresas, Usuarios, Api, Auditoria, Secretos, Portal, new FakeTimeProvider(Ahora));
    }

    [Fact]
    public async Task Crea_la_empresa_el_admin_y_la_credencial_y_devuelve_el_kit()
    {
        var e = new Entorno();
        var r = await e.Handler().Handle(Comando(), CancellationToken.None);

        Assert.True(r.EsExitoso);
        var kit = r.Valor;
        Assert.Equal(42, kit.EmpresaId);
        Assert.Equal("Admin800197268", kit.UsuarioAdministrador);
        Assert.Equal("Kq7#vR2m@Lp9xT", kit.ClaveInicial);
        Assert.Equal(Ahora.UtcDateTime.AddHours(72), kit.ClaveVenceEnUtc);
        Assert.Equal("conector-800197268", kit.ClientId);
        Assert.Equal("SECRETO-CONECTOR-UNICO", kit.ClientSecret);

        await e.Usuarios.Received(1).CrearAdministradorPrincipalAsync(
            Arg.Is<AdministradorNuevo>(a => a.EmpresaId == 42 && a.NombreUsuario == "Admin800197268" && a.CreadoPor == 1 && a.ClaveVenceEnUtc == kit.ClaveVenceEnUtc),
            Arg.Any<CancellationToken>());
        await e.Api.Received(1).CrearAsync(42, "conector-800197268", "SECRETO-CONECTOR-UNICO", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task La_auditoria_registra_tres_cambios_y_nunca_contrasenas_ni_secretos()
    {
        var e = new Entorno();
        await e.Handler().Handle(Comando(), CancellationToken.None);

        Assert.Equal(new[] { "seg.Empresa", "seg.Usuario", "seg.ApiClienteEmpresa" }, e.Cambios.Select(c => c.Entidad).ToArray());
        var todo = string.Join("|", e.Cambios.Select(c => c.ValorNuevoJson + c.ValorAnteriorJson + c.ClaveEntidad));
        Assert.DoesNotContain("Kq7#vR2m@Lp9xT", todo);
        Assert.DoesNotContain("SECRETO-CONECTOR-UNICO", todo);
        Assert.All(e.Cambios, c => Assert.Equal(1, c.UsuarioId));
    }

    [Fact]
    public async Task Un_nit_existente_no_crea_nada()
    {
        var e = new Entorno();
        e.Empresas.ExisteNitAsync("800197268", Arg.Any<CancellationToken>()).Returns(true);
        var r = await e.Handler().Handle(Comando(), CancellationToken.None);

        Assert.Equal("NIT_DUPLICADO", Assert.Single(r.Errores).Codigo);
        await e.Empresas.DidNotReceive().CrearAsync(Arg.Any<EmpresaNueva>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Un_usuario_Admin_existente_no_crea_nada()
    {
        var e = new Entorno();
        e.Usuarios.ExisteNombreAsync("Admin800197268", Arg.Any<CancellationToken>()).Returns(true);
        var r = await e.Handler().Handle(Comando(), CancellationToken.None);

        Assert.Equal("USUARIO_DUPLICADO", Assert.Single(r.Errores).Codigo);
        await e.Empresas.DidNotReceive().CrearAsync(Arg.Any<EmpresaNueva>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Una_falla_al_crear_el_admin_se_propaga_sin_crear_la_credencial()
    {
        var e = new Entorno();
        e.Usuarios.CrearAdministradorPrincipalAsync(Arg.Any<AdministradorNuevo>(), Arg.Any<CancellationToken>())
            .Returns(Result<int>.Falla(Error.Conflicto("CLAVE_DEBIL", "x")));
        var r = await e.Handler().Handle(Comando(), CancellationToken.None);

        Assert.Equal("CLAVE_DEBIL", Assert.Single(r.Errores).Codigo);
        await e.Api.DidNotReceive().CrearAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Una_ESE_nace_con_naturaleza_publica()
    {
        var e = new Entorno();
        await e.Handler().Handle(Comando() with { TipoEntidad = "ESE" }, CancellationToken.None);
        await e.Empresas.Received(1).CrearAsync(Arg.Is<EmpresaNueva>(n => n.Naturaleza == "PUBLICA" && n.Codigo == "800197268"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("800197268", "5", "DV_INVALIDO")]
    [InlineData("800197268-4", "4", "NIT_INVALIDO")]
    [InlineData("12", "1", "NIT_INVALIDO")]
    public void El_validador_exige_nit_y_digito_correctos(string nit, string dv, string codigo)
    {
        var r = new CrearEmpresaCommandValidator().Validate(Comando(nit, dv));
        Assert.Contains(r.Errors, x => x.ErrorCode == codigo);
    }

    [Fact]
    public void El_validador_rechaza_modulos_vacios_tipos_y_colores_invalidos()
    {
        var malo = Comando() with { Modulos = new[] { "NOEXISTE" }, TipoEntidad = "OTRO", ColorPrimario = "azul" };
        var codigos = new CrearEmpresaCommandValidator().Validate(malo).Errors.Select(x => x.ErrorCode).ToArray();
        Assert.Contains("MODULO_INVALIDO", codigos);
        Assert.Contains("TIPO_ENTIDAD_INVALIDO", codigos);
        Assert.Contains("COLOR_INVALIDO", codigos);
        Assert.Contains("MODULOS_REQUERIDOS", new CrearEmpresaCommandValidator().Validate(Comando() with { Modulos = Array.Empty<string>() }).Errors.Select(x => x.ErrorCode));
    }

    [Fact]
    public void Un_comando_valido_pasa_el_validador()
        => Assert.True(new CrearEmpresaCommandValidator().Validate(Comando()).IsValid);
}
