using IAnalitica.Application.Empresas;
using IAnalitica.Infrastructure.Identity;
using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static IAnalitica.Integration.Tests.Infra.Escenarios;

namespace IAnalitica.Integration.Tests;

[Collection(ColeccionBd.Nombre)]
public class AltaEmpresaTests(BaseDeDatosFixture bd)
{
    private async Task<T> ComoAutenticacion<T>(Func<IServiceProvider, Task<T>> accion)
    {
        await using var sp = bd.Construir(bd.CadenaApp, sesion: null);
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.ParaAutenticar);
        return await accion(alcance.ServiceProvider);
    }

    [Fact]
    public async Task El_kit_trae_usuario_clave_de_14_caracteres_vencimiento_de_72_horas_y_credencial_del_conector()
    {
        var kit = await CrearComoSuperadminAsync(bd, ComandoValido("900123456", "Centro Médico Kit SAS"));

        Assert.Equal("Admin900123456", kit.UsuarioAdministrador);
        Assert.Equal(14, kit.ClaveInicial.Length);
        Assert.Contains(kit.ClaveInicial, char.IsUpper);
        Assert.Contains(kit.ClaveInicial, char.IsLower);
        Assert.Contains(kit.ClaveInicial, char.IsDigit);
        Assert.Contains(kit.ClaveInicial, c => !char.IsLetterOrDigit(c));
        Assert.InRange((kit.ClaveVenceEnUtc - DateTime.UtcNow).TotalHours, 71.9, 72.1);
        Assert.Equal("conector-900123456", kit.ClientId);
        Assert.True(kit.ClientSecret.Length >= 40);
        Assert.NotEmpty(kit.InstruccionesPrimerIngreso);
    }

    [Fact]
    public async Task El_Admin_NIT_nace_con_rol_ADMIN_EMPRESA_clave_temporal_y_cambio_obligatorio()
    {
        var kit = await CrearComoSuperadminAsync(bd, ComandoValido("900123457", "Clínica Rol SAS"));

        await ComoAutenticacion(async sp =>
        {
            var gestor = sp.GetRequiredService<UserManager<UsuarioAplicacion>>();
            var admin = await gestor.FindByNameAsync(kit.UsuarioAdministrador);
            Assert.NotNull(admin);
            Assert.Equal(kit.EmpresaId, admin!.EmpresaId);
            Assert.True(admin.EsAdminPrincipal);
            Assert.False(admin.EsSuperAdmin);
            Assert.True(admin.DebeCambiarClave);
            Assert.True(admin.Activo);
            Assert.False(admin.TwoFactorEnabled);   // lo configura en el primer ingreso
            Assert.Equal(kit.ClaveVenceEnUtc, admin.ClaveTemporalVenceEn!.Value, TimeSpan.FromSeconds(1));
            Assert.True(await gestor.CheckPasswordAsync(admin, kit.ClaveInicial));
            Assert.False(await gestor.CheckPasswordAsync(admin, kit.ClaveInicial + "x"));
            Assert.Equal(new[] { Roles.AdminEmpresa }, (await gestor.GetRolesAsync(admin)).ToArray());
            return 0;
        });
    }

    [Fact]
    public async Task La_credencial_del_conector_funciona_y_el_secreto_no_queda_en_claro()
    {
        var kit = await CrearComoSuperadminAsync(bd, ComandoValido("900123458", "IPS Credencial SAS"));

        await ComoAutenticacion(async sp =>
        {
            var aplicaciones = sp.GetRequiredService<IOpenIddictApplicationManager>();
            var app = await aplicaciones.FindByClientIdAsync(kit.ClientId);
            Assert.NotNull(app);
            Assert.True(await aplicaciones.ValidateClientSecretAsync(app!, kit.ClientSecret));
            Assert.False(await aplicaciones.ValidateClientSecretAsync(app!, "otro-secreto"));
            return 0;
        });

        var guardado = await BaseDeDatosFixture.EscalarAsync<string>(bd.CadenaPropietario,
            $"SELECT ClientSecret FROM seg.OidcAplicacion WHERE ClientId = '{kit.ClientId}'", superadmin: false);
        Assert.NotEqual(kit.ClientSecret, guardado);
        Assert.DoesNotContain(kit.ClientSecret, guardado);
        Assert.Equal(1, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario,
            $"SELECT COUNT(*) FROM seg.ApiClienteEmpresa WHERE ClientId = '{kit.ClientId}' AND EmpresaId = {kit.EmpresaId}", superadmin: true));
    }

    [Fact]
    public async Task La_empresa_queda_con_sus_modulos_y_la_IA_deshabilitada()
    {
        var kit = await CrearComoSuperadminAsync(bd, ComandoValido("900123459", "IPS Modulos SAS"));
        Assert.Equal(2, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, $"SELECT COUNT(*) FROM seg.EmpresaModulo WHERE EmpresaId = {kit.EmpresaId}", superadmin: true));
        Assert.Equal(0, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, $"SELECT CAST(Habilitado AS int) FROM ia.EmpresaConfig WHERE EmpresaId = {kit.EmpresaId}", superadmin: true));
        Assert.Equal("PRIVADA", await BaseDeDatosFixture.EscalarAsync<string>(bd.CadenaPropietario, $"SELECT Naturaleza FROM seg.Empresa WHERE EmpresaId = {kit.EmpresaId}", superadmin: true));
    }

    [Fact]
    public async Task La_auditoria_registra_el_alta_sin_contrasenas_ni_secretos()
    {
        var kit = await CrearComoSuperadminAsync(bd, ComandoValido("900123460", "IPS Auditada SAS"));
        var filas = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario,
            $"SELECT COUNT(*) FROM aud.CambioAdministrativo WHERE EmpresaId = {kit.EmpresaId} AND Accion = 'CREAR' AND UsuarioId = {bd.SuperadminId}", superadmin: true);
        Assert.Equal(3, filas);

        var texto = await BaseDeDatosFixture.EscalarAsync<string>(bd.CadenaPropietario,
            $"SELECT STRING_AGG(CONCAT(ClaveEntidad, ValorNuevoJson, ValorAnteriorJson), '|') FROM aud.CambioAdministrativo WHERE EmpresaId = {kit.EmpresaId}", superadmin: true);
        Assert.DoesNotContain(kit.ClaveInicial, texto);
        Assert.DoesNotContain(kit.ClientSecret, texto);
    }

    [Fact]
    public async Task Un_nit_repetido_no_crea_nada_y_la_clave_inicial_cuenta_como_primera_del_historial()
    {
        var primero = await CrearComoSuperadminAsync(bd, ComandoValido("900123461", "IPS Unica SAS"));
        var segundo = await EnviarComoSuperadminAsync(bd, ComandoValido("900123461", "IPS Unica Bis SAS"));

        Assert.False(segundo.EsExitoso);
        Assert.Equal("NIT_DUPLICADO", segundo.Errores[0].Codigo);
        Assert.Equal(1, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT COUNT(*) FROM seg.Empresa WHERE Nit = '900123461'", superadmin: true));
        Assert.Equal(1, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario,
            $"SELECT COUNT(*) FROM seg.HistorialClave h JOIN seg.Usuario u ON u.Id = h.UsuarioId WHERE u.EmpresaId = {primero.EmpresaId}", superadmin: true));
    }

    [Fact]
    public async Task Una_falla_a_mitad_del_alta_deshace_todo_la_transaccion_es_atomica()
    {
        // La credencial del conector ya existe: el alta falla en el último paso, DESPUÉS de crear empresa y administrador.
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaPropietario, """
            INSERT seg.OidcAplicacion (Id, ClientId, ClientType, ApplicationType, ConcurrencyToken, DisplayName)
            VALUES (CONVERT(nvarchar(36), NEWID()), 'conector-900123462', 'confidential', 'web', 'x', 'previa')
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => EnviarComoSuperadminAsync(bd, ComandoValido("900123462", "IPS Atomica SAS")));

        Assert.Equal(0, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT COUNT(*) FROM seg.Empresa WHERE Nit = '900123462'", superadmin: true));
        Assert.Equal(0, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT COUNT(*) FROM seg.Usuario WHERE UserName = 'Admin900123462'", superadmin: true));
        Assert.Equal(0, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario,
            "SELECT COUNT(*) FROM aud.CambioAdministrativo WHERE ClaveEntidad IN ('Admin900123462','conector-900123462')", superadmin: true));
    }

    [Fact]
    public async Task Solo_quien_tiene_el_permiso_admin_empresas_puede_crear_empresas()
    {
        var sesion = new SesionPrueba(123, 1, false, "dash.financiero");
        await using var sp = bd.Construir(bd.CadenaApp, sesion);
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeEmpresa(1, 123));

        var r = await alcance.ServiceProvider.GetRequiredService<IAnalitica.Application.Mediacion.ISender>().Send(ComandoValido("900123463", "Intrusa SAS"));
        Assert.Equal("NO_AUTORIZADO", r.Errores[0].Codigo);
        Assert.Equal(0, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT COUNT(*) FROM seg.Empresa WHERE Nit = '900123463'", superadmin: true));
    }

    [Fact]
    public async Task Datos_invalidos_devuelven_errores_de_validacion_sin_tocar_la_base()
    {
        var r = await EnviarComoSuperadminAsync(bd, Comando("900123464", "9", "DV Malo SAS"));
        Assert.Contains(r.Errores, e => e.Codigo == "DV_INVALIDO");
    }
}
