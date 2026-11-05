using IAnalitica.Application.Empresas;
using IAnalitica.Infrastructure.Identity;
using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static IAnalitica.Integration.Tests.Infra.Escenarios;

namespace IAnalitica.Integration.Tests;

/// <summary>Política de contraseñas, historial, bloqueo y doble factor de Identity contra SQL Server real (24 §4.1).</summary>
[Collection(ColeccionBd.Nombre)]
public class IdentidadTests(BaseDeDatosFixture bd)
{
    private async Task<T> ConGestor<T>(Func<UserManager<UsuarioAplicacion>, Task<T>> accion)
    {
        await using var sp = bd.Construir(bd.CadenaApp, sesion: null);
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.ParaAutenticar);
        return await accion(alcance.ServiceProvider.GetRequiredService<UserManager<UsuarioAplicacion>>());
    }

    private async Task<(KitEntrega Kit, string Usuario)> NuevaEmpresaAsync(string nit, string razon)
    {
        var kit = await CrearComoSuperadminAsync(bd, ComandoValido(nit, razon));
        return (kit, kit.UsuarioAdministrador);
    }

    private static string Describir(IdentityResult r) => string.Join(", ", r.Errors.Select(e => e.Code));

    [Fact]
    public async Task La_politica_exige_10_caracteres_mayuscula_minuscula_numero_y_especial()
    {
        var (kit, usuario) = await NuevaEmpresaAsync("900200001", "IPS Politica Uno SAS");
        var casos = new (string Clave, string Codigo)[]
        {
            ("Corta1!a", "PasswordTooShort"),
            ("sinmayuscula1!", "PasswordRequiresUpper"),
            ("SINMINUSCULA1!", "PasswordRequiresLower"),
            ("SinNumeroAqui!", "PasswordRequiresDigit"),
            ("SinEspecial123", "PasswordRequiresNonAlphanumeric"),
        };
        foreach (var (clave, codigo) in casos)
        {
            var r = await ConGestor(async g => await g.ChangePasswordAsync((await g.FindByNameAsync(usuario))!, kit.ClaveInicial, clave));
            Assert.False(r.Succeeded, clave);
            Assert.Contains(codigo, Describir(r));
        }
    }

    [Fact]
    public async Task El_cambio_de_clave_exige_conocer_la_actual()
    {
        var (_, usuario) = await NuevaEmpresaAsync("900200002", "IPS Politica Dos SAS");
        var r = await ConGestor(async g => await g.ChangePasswordAsync((await g.FindByNameAsync(usuario))!, "No-Es-La-Actual1!", "Nueva-Clave-Segura1!"));
        Assert.False(r.Succeeded);
        Assert.Contains("PasswordMismatch", Describir(r));
    }

    [Fact]
    public async Task No_se_puede_repetir_ninguna_de_las_ultimas_5_contrasenas()
    {
        var (kit, usuario) = await NuevaEmpresaAsync("900200003", "IPS Historial SAS");
        var claves = Enumerable.Range(1, 6).Select(i => $"Clave-Numero-{i}-Segura!").ToArray();

        var actual = kit.ClaveInicial;                     // historial: [inicial]
        foreach (var nueva in claves.Take(4))              // historial: [inicial, c1, c2, c3, c4]
        {
            var ok = await ConGestor(async g => await g.ChangePasswordAsync((await g.FindByNameAsync(usuario))!, actual, nueva));
            Assert.True(ok.Succeeded, Describir(ok));
            actual = nueva;
        }

        // La inicial sigue entre las últimas 5 → rechazada. Y la actual también.
        var repiteInicial = await ConGestor(async g => await g.ChangePasswordAsync((await g.FindByNameAsync(usuario))!, actual, kit.ClaveInicial));
        Assert.Contains("CLAVE_REPETIDA", Describir(repiteInicial));
        var repiteC2 = await ConGestor(async g => await g.ChangePasswordAsync((await g.FindByNameAsync(usuario))!, actual, claves[1]));
        Assert.Contains("CLAVE_REPETIDA", Describir(repiteC2));

        // Una sexta contraseña empuja a la inicial fuera de la ventana de 5: ya se puede reutilizar.
        var sexta = await ConGestor(async g => await g.ChangePasswordAsync((await g.FindByNameAsync(usuario))!, actual, claves[4]));
        Assert.True(sexta.Succeeded, Describir(sexta));
        var reusa = await ConGestor(async g => await g.ChangePasswordAsync((await g.FindByNameAsync(usuario))!, claves[4], kit.ClaveInicial));
        Assert.True(reusa.Succeeded, Describir(reusa));
    }

    [Fact]
    public async Task Cinco_intentos_fallidos_bloquean_la_cuenta_por_15_minutos()
    {
        var (_, usuario) = await NuevaEmpresaAsync("900200004", "IPS Bloqueo SAS");
        var (bloqueado, fin) = await ConGestor(async g =>
        {
            var u = (await g.FindByNameAsync(usuario))!;
            for (var i = 0; i < 4; i++) await g.AccessFailedAsync(u);
            Assert.False(await g.IsLockedOutAsync(u));
            await g.AccessFailedAsync(u);
            return (await g.IsLockedOutAsync(u), (await g.GetLockoutEndDateAsync(u))!.Value);
        });
        Assert.True(bloqueado);
        Assert.InRange((fin - DateTimeOffset.UtcNow).TotalMinutes, 14.5, 15.1);
    }

    [Fact]
    public async Task El_doble_factor_con_app_autenticadora_funciona_de_punta_a_punta()
    {
        var (_, usuario) = await NuevaEmpresaAsync("900200005", "IPS Doble Factor SAS");
        await ConGestor(async g =>
        {
            var u = (await g.FindByNameAsync(usuario))!;
            await g.ResetAuthenticatorKeyAsync(u);
            var clave = (await g.GetAuthenticatorKeyAsync(u))!;

            // Un código incorrecto no activa el doble factor.
            Assert.False(await g.VerifyTwoFactorTokenAsync(u, g.Options.Tokens.AuthenticatorTokenProvider, "000000"));
            Assert.False(await g.GetTwoFactorEnabledAsync(u));

            var codigo = Totp.Codigo(clave, DateTimeOffset.UtcNow);
            Assert.True(await g.VerifyTwoFactorTokenAsync(u, g.Options.Tokens.AuthenticatorTokenProvider, codigo));
            Assert.True((await g.SetTwoFactorEnabledAsync(u, true)).Succeeded);
            Assert.True(await g.GetTwoFactorEnabledAsync(u));
            return 0;
        });

        // Persistió: otra sesión de base de datos lo ve activo.
        var activo = await ConGestor(async g => await g.GetTwoFactorEnabledAsync((await g.FindByNameAsync(usuario))!));
        Assert.True(activo);
    }

    [Fact]
    public async Task Los_reclamos_del_cookie_traen_empresa_banderas_y_permisos_del_rol()
    {
        var (kit, usuario) = await NuevaEmpresaAsync("900200006", "IPS Reclamos SAS");
        await using var sp = bd.Construir(bd.CadenaApp, sesion: null);
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.ParaAutenticar);

        var gestor = alcance.ServiceProvider.GetRequiredService<UserManager<UsuarioAplicacion>>();
        var fabrica = alcance.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<UsuarioAplicacion>>();
        var principal = await fabrica.CreateAsync((await gestor.FindByNameAsync(usuario))!);

        Assert.Equal(kit.EmpresaId.ToString(), principal.FindFirst(Reclamos.EmpresaId)!.Value);
        Assert.NotNull(principal.FindFirst(Reclamos.EsAdminPrincipal));
        Assert.Null(principal.FindFirst(Reclamos.EsSuperAdmin));
        var permisos = principal.FindAll(Reclamos.Permiso).Select(c => c.Value).ToHashSet();
        Assert.Contains("admin.usuarios", permisos);
        Assert.Contains("dash.financiero", permisos);
        Assert.DoesNotContain("admin.empresas", permisos);   // solo el superadmin crea empresas
        Assert.True(principal.IsInRole(Roles.AdminEmpresa));
    }

    [Fact]
    public async Task El_superadmin_tiene_permiso_para_crear_empresas_y_no_pertenece_a_ninguna()
    {
        await using var sp = bd.Construir(bd.CadenaApp, sesion: null);
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.ParaAutenticar);
        var gestor = alcance.ServiceProvider.GetRequiredService<UserManager<UsuarioAplicacion>>();
        var principal = await alcance.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<UsuarioAplicacion>>().CreateAsync((await gestor.FindByNameAsync("superadmin"))!);

        Assert.NotNull(principal.FindFirst(Reclamos.EsSuperAdmin));
        Assert.Null(principal.FindFirst(Reclamos.EmpresaId));
        Assert.Contains(principal.FindAll(Reclamos.Permiso), c => c.Value == "admin.empresas");
    }

    [Fact]
    public async Task Solo_puede_existir_un_administrador_principal_por_empresa_lo_garantiza_la_base_de_datos()
    {
        var (kit, _) = await NuevaEmpresaAsync("900200007", "IPS Unico Admin SAS");
        var ex = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => BaseDeDatosFixture.EjecutarAsync(bd.CadenaPropietario, $"""
            EXEC sp_set_session_context N'EsSuperAdmin', 1;
            INSERT seg.Usuario (UserName, NormalizedUserName, EmpresaId, EsAdminPrincipal, EsSuperAdmin, NombreCompleto, DebeCambiarClave, Activo, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
            VALUES ('Otro', 'OTRO', {kit.EmpresaId}, 1, 0, 'x', 0, 1, 0, 0, 0, 0, 0)
            """));
        Assert.Equal(2601, ex.Number);   // UX_Usuario_AdminPrincipal (D27)
    }

    [Fact]
    public async Task Un_usuario_es_de_una_sola_empresa_o_es_superadmin_nunca_ambos()
    {
        var ex = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => BaseDeDatosFixture.EjecutarAsync(bd.CadenaPropietario, """
            EXEC sp_set_session_context N'EsSuperAdmin', 1;
            INSERT seg.Usuario (UserName, NormalizedUserName, EmpresaId, EsAdminPrincipal, EsSuperAdmin, NombreCompleto, DebeCambiarClave, Activo, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
            VALUES ('SinEmpresa', 'SINEMPRESA', NULL, 0, 0, 'x', 0, 1, 0, 0, 0, 0, 0)
            """));
        Assert.Contains("CK_Usuario_Empresa", ex.Message);
    }
}
