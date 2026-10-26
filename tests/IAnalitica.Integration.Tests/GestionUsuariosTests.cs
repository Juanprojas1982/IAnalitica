using IAnalitica.Application.Empresas;
using IAnalitica.Application.Seguridad;
using IAnalitica.Application.Usuarios;
using IAnalitica.Infrastructure.Identity;
using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static IAnalitica.Integration.Tests.Infra.Escenarios;

namespace IAnalitica.Integration.Tests;

/// <summary>Gestión de usuarios por el Admin{NIT} (spec 22 §5) y reemplazo del administrador por el superadmin, con intentos cruzados entre empresas.</summary>
[Collection(ColeccionBd.Nombre)]
public class GestionUsuariosTests(BaseDeDatosFixture bd)
{
    private const string Perm = "admin.usuarios";

    private Task<TResp> ComoAdminA<TResp>(DosEmpresas dos, IAnalitica.Application.Mediacion.IRequest<TResp> r)
        => EnviarComoUsuarioAsync(bd, dos.A.EmpresaId, dos.AdminAId, true, r, Perm);

    private Task<TResp> ComoAdminB<TResp>(DosEmpresas dos, IAnalitica.Application.Mediacion.IRequest<TResp> r)
        => EnviarComoUsuarioAsync(bd, dos.B.EmpresaId, dos.AdminBId, true, r, Perm);

    private async Task<T> ConGestor<T>(Func<UserManager<UsuarioAplicacion>, Task<T>> accion)
    {
        await using var sp = bd.Construir(bd.CadenaApp, sesion: null);
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.ParaAutenticar);
        return await accion(alcance.ServiceProvider.GetRequiredService<UserManager<UsuarioAplicacion>>());
    }

    [Fact]
    public async Task El_admin_crea_un_usuario_de_SU_empresa_con_clave_temporal_y_rol()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        var r = await ComoAdminA(dos, new CrearUsuarioCommand("ana.cartera", "ana@a.test", "Ana Cartera", new[] { "CARTERA" }));
        Assert.True(r.EsExitoso, string.Join(",", r.Errores.Select(e => e.Codigo)));

        await ConGestor(async g =>
        {
            var u = (await g.FindByNameAsync("ana.cartera"))!;
            Assert.Equal(dos.A.EmpresaId, u.EmpresaId);          // el servidor lo pone desde la sesión
            Assert.False(u.EsAdminPrincipal);
            Assert.True(u.DebeCambiarClave);
            Assert.Equal(new[] { "CARTERA" }, (await g.GetRolesAsync(u)).ToArray());
            Assert.True(await g.CheckPasswordAsync(u, r.Valor.ClaveInicial));
            return 0;
        });
    }

    [Fact]
    public async Task El_admin_no_puede_crear_otro_administrador_ni_un_superadmin()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        foreach (var rol in new[] { "ADMIN_EMPRESA", "SUPERADMIN", "NOEXISTE" })
        {
            var r = await ComoAdminA(dos, new CrearUsuarioCommand("intento." + rol.ToLower(), "x@a.test", "Intento", new[] { rol }));
            Assert.False(r.EsExitoso);
            Assert.Contains(r.Errores, e => e.Codigo == "ROL_NO_ASIGNABLE");
        }
        var reservado = await ComoAdminA(dos, new CrearUsuarioCommand("Admin999999", "x@a.test", "Falso Admin", new[] { "CONSULTA" }));
        Assert.Contains(reservado.Errores, e => e.Codigo == "USUARIO_RESERVADO");
        Assert.Equal(0, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario,
            "SELECT COUNT(*) FROM seg.Usuario WHERE UserName LIKE 'intento.%' OR UserName = 'Admin999999'", superadmin: true));
    }

    [Fact]
    public async Task El_admin_de_A_no_puede_editar_desactivar_ni_restablecer_usuarios_de_B()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        var creado = await ComoAdminB(dos, new CrearUsuarioCommand("beto.tesoreria", "beto@b.test", "Beto Tesorero", new[] { "TESORERO" }));
        Assert.True(creado.EsExitoso);
        var idBeto = creado.Valor.UsuarioId;

        var editar = await ComoAdminA(dos, new EditarUsuarioCommand(idBeto, "hack@a.test", "Hackeado", new[] { "CONSULTA" }));
        var desactivar = await ComoAdminA(dos, new CambiarEstadoUsuarioCommand(idBeto, false));
        var restablecer = await ComoAdminA(dos, new RestablecerClaveUsuarioCommand(idBeto));
        var sobreAdminB = await ComoAdminA(dos, new RestablecerClaveUsuarioCommand(dos.AdminBId));

        Assert.Equal("NO_ENCONTRADO", editar.Errores[0].Codigo);
        Assert.Equal("NO_ENCONTRADO", desactivar.Errores[0].Codigo);
        Assert.Equal("NO_ENCONTRADO", restablecer.Errores[0].Codigo);
        Assert.Equal("NO_ENCONTRADO", sobreAdminB.Errores[0].Codigo);

        // Intacto.
        await ConGestor(async g =>
        {
            var u = (await g.FindByNameAsync("beto.tesoreria"))!;
            Assert.Equal("beto@b.test", u.Email);
            Assert.True(u.Activo);
            Assert.True(await g.CheckPasswordAsync(u, creado.Valor.ClaveInicial));
            return 0;
        });
    }

    [Fact]
    public async Task El_listado_de_A_solo_trae_usuarios_de_A()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        await ComoAdminB(dos, new CrearUsuarioCommand("lista.b", "lb@b.test", "Lista B", new[] { "CONSULTA" }));
        await ComoAdminA(dos, new CrearUsuarioCommand("lista.a", "la@a.test", "Lista A", new[] { "CONSULTA" }));

        var lista = (await ComoAdminA(dos, new ListarUsuariosQuery())).Valor;
        Assert.Contains(lista, u => u.Usuario == "lista.a");
        Assert.DoesNotContain(lista, u => u.Usuario == "lista.b");
        Assert.DoesNotContain(lista, u => u.Usuario == dos.B.UsuarioAdministrador);
        Assert.Contains(lista, u => u.Usuario == dos.A.UsuarioAdministrador && u.EsAdminPrincipal);
    }

    [Fact]
    public async Task El_admin_principal_no_se_edita_ni_se_desactiva_desde_la_empresa()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        Assert.Equal("ADMIN_PRINCIPAL_PROTEGIDO", (await ComoAdminA(dos, new CambiarEstadoUsuarioCommand(dos.AdminAId, false))).Errores[0].Codigo);
        Assert.Equal("ADMIN_PRINCIPAL_PROTEGIDO", (await ComoAdminA(dos, new EditarUsuarioCommand(dos.AdminAId, "x@a.test", "x", new[] { "CONSULTA" }))).Errores[0].Codigo);
        Assert.Equal("ADMIN_PRINCIPAL_PROTEGIDO", (await ComoAdminA(dos, new RestablecerClaveUsuarioCommand(dos.AdminAId))).Errores[0].Codigo);
    }

    [Fact]
    public async Task Desactivar_cambia_el_sello_de_seguridad_y_la_politica_de_acceso_lo_rechaza()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        var creado = (await ComoAdminA(dos, new CrearUsuarioCommand("carla.baja", "c@a.test", "Carla Baja", new[] { "CONSULTA" }))).Valor;
        var sello = await ConGestor(async g => await g.GetSecurityStampAsync((await g.FindByNameAsync("carla.baja"))!));

        Assert.True((await ComoAdminA(dos, new CambiarEstadoUsuarioCommand(creado.UsuarioId, false))).EsExitoso);

        await ConGestor(async g =>
        {
            var u = (await g.FindByNameAsync("carla.baja"))!;
            Assert.False(u.Activo);
            Assert.NotEqual(sello, await g.GetSecurityStampAsync(u));
            var paso = PoliticaAcceso.Evaluar(new DatosAcceso(u.Activo, u.DebeCambiarClave, u.ClaveTemporalVenceEn, false, false, false), DateTime.UtcNow);
            Assert.Equal(PasoAcceso.Desactivado, paso);
            return 0;
        });

        Assert.True((await ComoAdminA(dos, new CambiarEstadoUsuarioCommand(creado.UsuarioId, true))).EsExitoso);   // se puede reactivar
    }

    [Fact]
    public async Task Restablecer_genera_una_clave_nueva_la_anterior_deja_de_servir_y_se_exige_cambio()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        var creado = (await ComoAdminA(dos, new CrearUsuarioCommand("diego.reset", "d@a.test", "Diego Reset", new[] { "FINANCIERO" }))).Valor;
        var nueva = (await ComoAdminA(dos, new RestablecerClaveUsuarioCommand(creado.UsuarioId))).Valor;

        Assert.NotEqual(creado.ClaveInicial, nueva.ClaveInicial);
        await ConGestor(async g =>
        {
            var u = (await g.FindByNameAsync("diego.reset"))!;
            Assert.False(await g.CheckPasswordAsync(u, creado.ClaveInicial));
            Assert.True(await g.CheckPasswordAsync(u, nueva.ClaveInicial));
            Assert.True(u.DebeCambiarClave);
            return 0;
        });
    }

    [Fact]
    public async Task Editar_cambia_correo_nombre_y_roles()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        var creado = (await ComoAdminA(dos, new CrearUsuarioCommand("eva.edita", "e@a.test", "Eva", new[] { "CARTERA" }))).Valor;
        Assert.True((await ComoAdminA(dos, new EditarUsuarioCommand(creado.UsuarioId, "eva.nueva@a.test", "Eva Editada", new[] { "TESORERO", "CONSULTA" }))).EsExitoso);

        var lista = (await ComoAdminA(dos, new ListarUsuariosQuery())).Valor.Single(u => u.Usuario == "eva.edita");
        Assert.Equal("eva.nueva@a.test", lista.Email);
        Assert.Equal("Eva Editada", lista.NombreCompleto);
        Assert.Equal(new[] { "CONSULTA", "TESORERO" }, lista.Roles.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Un_usuario_sin_el_permiso_admin_usuarios_no_gestiona_usuarios()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        var r = await EnviarComoUsuarioAsync(bd, dos.A.EmpresaId, 424242, false,
            new CrearUsuarioCommand("sin.permiso", "s@a.test", "Sin Permiso", new[] { "CONSULTA" }), "dash.financiero");
        Assert.Equal("NO_AUTORIZADO", r.Errores[0].Codigo);
        Assert.Equal(0, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT COUNT(*) FROM seg.Usuario WHERE UserName = 'sin.permiso'", superadmin: true));
    }

    [Fact]
    public async Task Cada_cambio_de_usuarios_queda_en_la_auditoria_con_antes_y_despues()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        var creado = (await ComoAdminA(dos, new CrearUsuarioCommand("fabio.audita", "f@a.test", "Fabio", new[] { "CARTERA" }))).Valor;
        await ComoAdminA(dos, new EditarUsuarioCommand(creado.UsuarioId, "f2@a.test", "Fabio Dos", new[] { "CARTERA" }));
        await ComoAdminA(dos, new CambiarEstadoUsuarioCommand(creado.UsuarioId, false));
        await ComoAdminA(dos, new RestablecerClaveUsuarioCommand(creado.UsuarioId));

        var acciones = await BaseDeDatosFixture.EscalarAsync<string>(bd.CadenaPropietario,
            "SELECT STRING_AGG(Accion, ',') WITHIN GROUP (ORDER BY CambioId) FROM aud.CambioAdministrativo WHERE ClaveEntidad = 'fabio.audita'", superadmin: true);
        Assert.Equal("CREAR,MODIFICAR,DESACTIVAR,RESTABLECER_CLAVE", acciones);

        var modificar = await BaseDeDatosFixture.EscalarAsync<string>(bd.CadenaPropietario,
            "SELECT ValorAnteriorJson + '|' + ValorNuevoJson FROM aud.CambioAdministrativo WHERE ClaveEntidad = 'fabio.audita' AND Accion = 'MODIFICAR'", superadmin: true);
        Assert.Contains("f@a.test", modificar);
        Assert.Contains("f2@a.test", modificar);
        var todo = await BaseDeDatosFixture.EscalarAsync<string>(bd.CadenaPropietario,
            "SELECT STRING_AGG(CONCAT(ValorAnteriorJson, ValorNuevoJson), '|') FROM aud.CambioAdministrativo WHERE ClaveEntidad = 'fabio.audita'", superadmin: true);
        Assert.DoesNotContain(creado.ClaveInicial, todo);
    }

    // ----- Reemplazo del Admin{NIT}: solo el superadmin -----

    [Fact]
    public async Task El_superadmin_restablece_al_admin_NIT_quita_su_doble_factor_y_deja_motivo_en_la_auditoria()
    {
        var kit = await CrearComoSuperadminAsync(bd, ComandoValido("900300001", "IPS Reemplazo SAS"));
        // El admin activó su doble factor.
        await ConGestor(async g =>
        {
            var u = (await g.FindByNameAsync(kit.UsuarioAdministrador))!;
            await g.ResetAuthenticatorKeyAsync(u);
            await g.SetTwoFactorEnabledAsync(u, true);
            return 0;
        });

        var r = await EnviarComoSuperadminAsync(bd, new RestablecerAdminEmpresaCommand(kit.EmpresaId, "La persona salió de la empresa"));
        Assert.True(r.EsExitoso);

        await ConGestor(async g =>
        {
            var u = (await g.FindByNameAsync(kit.UsuarioAdministrador))!;
            Assert.False(u.TwoFactorEnabled);
            Assert.Null(await g.GetAuthenticatorKeyAsync(u));
            Assert.False(await g.CheckPasswordAsync(u, kit.ClaveInicial));
            Assert.True(await g.CheckPasswordAsync(u, r.Valor.ClaveInicial));
            Assert.True(u.DebeCambiarClave);
            Assert.True(u.EsAdminPrincipal);
            return 0;
        });
        var motivo = await BaseDeDatosFixture.EscalarAsync<string>(bd.CadenaPropietario,
            $"SELECT Motivo FROM aud.CambioAdministrativo WHERE EmpresaId = {kit.EmpresaId} AND Accion = 'RESTABLECER_CLAVE'", superadmin: true);
        Assert.Equal("La persona salió de la empresa", motivo);
    }

    [Fact]
    public async Task El_motivo_es_obligatorio_y_el_admin_de_una_empresa_no_puede_reemplazar_administradores()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        var sinMotivo = await EnviarComoSuperadminAsync(bd, new RestablecerAdminEmpresaCommand(dos.A.EmpresaId, "corto"));
        Assert.Contains(sinMotivo.Errores, e => e.Codigo == "MOTIVO_REQUERIDO");

        var comoAdminB = await ComoAdminB(dos, new RestablecerAdminEmpresaCommand(dos.A.EmpresaId, "Intento de un administrador ajeno"));
        Assert.Equal("NO_AUTORIZADO", comoAdminB.Errores[0].Codigo);
    }
}
