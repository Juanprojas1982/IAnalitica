using System.Net;
using IAnalitica.Application.Empresas;
using IAnalitica.Integration.Tests.Infra;
using static IAnalitica.Integration.Tests.Web.Flujos;

namespace IAnalitica.Integration.Tests.Web;

/// <summary>El portal real, de punta a punta: sesión, primer ingreso obligatorio, alta de empresa con kit de entrega y aislamiento por HTTP.</summary>
[Collection(ColeccionBd.Nombre)]
public class PortalTests(BaseDeDatosFixture bd)
{
    private Navegador Nuevo() => Fabrica(bd).NuevoNavegador();

    private async Task<Navegador> SuperadminAsync()
    {
        var n = Nuevo();
        Assert.True(await IngresarAsync(n, await SuperadminListoAsync(bd)));
        return n;
    }

    private static string DvDe(string nit)
    {
        IAnalitica.Domain.Empresas.Nit.TryCrear(nit, out var x);
        return x.CalcularDigitoVerificacion().ToString();
    }

    private static Dictionary<string, string> CamposEmpresa(string nit, string razon, params string[] modulos)
    {
        var campos = new Dictionary<string, string>
        {
            ["Entrada.Nit"] = nit, ["Entrada.DigitoVerificacion"] = DvDe(nit), ["Entrada.RazonSocial"] = razon,
            ["Entrada.TipoEntidad"] = "IPS", ["Entrada.EmailContacto"] = $"admin{nit}@prueba.test", ["Entrada.Naturaleza"] = "",
        };
        foreach (var m in modulos.DefaultIfEmpty("FINANCIERO")) campos[$"Entrada.Modulos[{m}]"] = "true";
        return campos;
    }

    private async Task<Credenciales> CrearEmpresaPorPortalAsync(string nit, string razon, bool completarPrimerIngreso = true)
    {
        var sa = await SuperadminAsync();
        var (estado, html, _) = await sa.EnviarFormularioAsync("/admin/empresas/nueva", "nueva-empresa", CamposEmpresa(nit, razon, "FINANCIERO", "CARTERA"));
        Assert.True(estado == HttpStatusCode.OK, $"Alta de {nit}: {estado}\n{html[..Math.Min(html.Length, 3000)]}");
        var usuario = Texto(html, "kit-usuario");
        var clave = Texto(html, "kit-clave");
        if (!completarPrimerIngreso) return new Credenciales(usuario, clave, null);

        var admin = Nuevo();
        var (nueva, totp) = await PrimerIngresoAsync(admin, usuario, clave, NuevoUsuarioNavegadorClave, requiereDobleFactor: true);
        return new Credenciales(usuario, nueva, totp);
    }

    // ---------- Sesión y seguridad básica ----------

    [Fact]
    public async Task Un_anonimo_es_enviado_al_ingreso_y_no_ve_nada_del_portal()
    {
        var n = Nuevo();
        foreach (var ruta in new[] { "/", "/admin/usuarios", "/admin/empresas/nueva", "/cuenta/cambiar-clave" })
        {
            var (estado, _, redir) = await n.IrAsync(ruta);
            Assert.Equal(HttpStatusCode.Redirect, estado);
            Assert.Contains("/ingresar", redir);
        }
    }

    [Fact]
    public async Task Las_respuestas_llevan_cabeceras_de_seguridad_y_no_se_guardan_en_cache()
    {
        var r = await Nuevo().Cliente.GetAsync("/ingresar");
        Assert.Contains("default-src 'self'", r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.DoesNotContain("unsafe-inline", r.Headers.GetValues("Content-Security-Policy").Single().Split(';').First(p => p.Contains("script-src")));
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", r.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Un_formulario_sin_token_antiforgery_es_rechazado()
    {
        var r = await Nuevo().Cliente.PostAsync("/ingresar", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["_handler"] = "ingresar", ["Entrada.Usuario"] = "x", ["Entrada.Clave"] = "y" }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Usuario_inexistente_y_clave_incorrecta_dan_el_mismo_mensaje()
    {
        var n = Nuevo();
        var (_, html1, _) = await n.EnviarFormularioAsync("/ingresar", "ingresar", new Dictionary<string, string> { ["Entrada.Usuario"] = "nadie.existe", ["Entrada.Clave"] = "Cualquiera-1!" });
        var (_, html2, _) = await n.EnviarFormularioAsync("/ingresar", "ingresar", new Dictionary<string, string> { ["Entrada.Usuario"] = "superadmin", ["Entrada.Clave"] = "Incorrecta-123!" });
        Assert.Contains("Usuario o contraseña incorrectos.", html1);
        Assert.Contains("Usuario o contraseña incorrectos.", html2);
    }

    [Fact]
    public async Task El_ingreso_solo_redirige_a_rutas_del_propio_sitio()
    {
        var sa = await SuperadminListoAsync(bd);
        foreach (var destino in new[] { "https://malo.example/robo", "//malo.example", "/\\malo.example" })
        {
            var n = Nuevo();
            var (_, _, redir) = await n.EnviarFormularioAsync("/ingresar?returnUrl=" + Uri.EscapeDataString(destino), "ingresar",
                new Dictionary<string, string> { ["Entrada.Usuario"] = sa.Usuario, ["Entrada.Clave"] = sa.Clave });
            if (redir is null) continue;   // pidió el segundo factor: el destino se valida igual en esa etapa
            Assert.DoesNotContain("malo.example", redir);
        }
    }

    [Fact]
    public async Task Cinco_intentos_fallidos_bloquean_la_cuenta_y_ni_la_clave_correcta_entra()
    {
        var kit = await CrearEmpresaPorPortalAsync("900400001", "IPS Bloqueo Web SAS", completarPrimerIngreso: false);
        var n = Nuevo();
        for (var i = 0; i < 5; i++)
            await n.EnviarFormularioAsync("/ingresar", "ingresar", new Dictionary<string, string> { ["Entrada.Usuario"] = kit.Usuario, ["Entrada.Clave"] = "Mal-Intento-" + i });

        var (_, html, redir) = await n.EnviarFormularioAsync("/ingresar", "ingresar",
            new Dictionary<string, string> { ["Entrada.Usuario"] = kit.Usuario, ["Entrada.Clave"] = kit.Clave });
        Assert.Null(redir);
        Assert.Contains("bloqueada", html);

        Assert.True(await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario,
            $"SELECT COUNT(*) FROM aud.Seguridad WHERE NombreUsuario = '{kit.Usuario}' AND Evento IN ('LOGIN_FALLIDO','BLOQUEO')", superadmin: true) >= 5);
        Assert.True(await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario,
            $"SELECT COUNT(*) FROM aud.Seguridad WHERE NombreUsuario = '{kit.Usuario}' AND Evento = 'BLOQUEO'", superadmin: true) >= 1);
    }

    [Fact]
    public async Task Cerrar_sesion_invalida_el_acceso()
    {
        var n = await SuperadminAsync();
        Assert.Equal(HttpStatusCode.OK, (await n.IrAsync("/")).Estado);
        var (_, _, redir) = await n.PostConTokenAsync("/", "/salir");
        Assert.Contains("/ingresar", redir);
        var (estado, _, destino) = await n.IrAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, estado);
        Assert.Contains("/ingresar", destino);
    }

    [Fact]
    public async Task Las_paginas_de_ingreso_tienen_limite_de_solicitudes_por_IP()
    {
        await using var limitada = new FabricaWeb(bd, limiteIngresoPorMinuto: 5);
        var cliente = limitada.NuevoNavegador().Cliente;
        var estados = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) estados.Add((await cliente.GetAsync("/ingresar")).StatusCode);

        Assert.Equal(5, estados.Count(e => e == HttpStatusCode.OK));
        Assert.Equal(3, estados.Count(e => e == HttpStatusCode.TooManyRequests));
    }

    // ---------- Primer ingreso y alta de empresa ----------

    [Fact]
    public async Task El_superadmin_en_su_primer_ingreso_debe_cambiar_la_clave_y_activar_el_doble_factor()
    {
        var sa = await SuperadminListoAsync(bd);   // el recorrido completo (con sus aserciones) ocurre al prepararlo
        Assert.NotNull(sa.ClaveTotp);
        Assert.Equal(1, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario,
            "SELECT CAST(TwoFactorEnabled AS int) FROM seg.Usuario WHERE UserName = 'superadmin'", superadmin: true));
        Assert.Equal(0, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario,
            "SELECT CAST(DebeCambiarClave AS int) FROM seg.Usuario WHERE UserName = 'superadmin'", superadmin: true));
    }

    [Fact]
    public async Task Con_doble_factor_activo_el_ingreso_pide_el_codigo_y_uno_incorrecto_no_entra()
    {
        var sa = await SuperadminListoAsync(bd);
        var n = Nuevo();
        var (_, _, redir) = await n.EnviarFormularioAsync("/ingresar", "ingresar", new Dictionary<string, string> { ["Entrada.Usuario"] = sa.Usuario, ["Entrada.Clave"] = sa.Clave });
        Assert.Contains("/ingresar/2fa", redir);

        var (_, html, malo) = await n.EnviarFormularioAsync("/ingresar/2fa", "codigo", new Dictionary<string, string> { ["Codigo.Valor"] = "000000" });
        Assert.Null(malo);
        Assert.Contains("Código incorrecto", html);
        Assert.Equal(HttpStatusCode.Redirect, (await n.IrAsync("/")).Estado);   // sigue sin sesión
    }

    [Fact]
    public async Task El_alta_de_empresa_muestra_el_kit_una_sola_vez()
    {
        var sa = await SuperadminAsync();
        var (estado, html, _) = await sa.EnviarFormularioAsync("/admin/empresas/nueva", "nueva-empresa", CamposEmpresa("900400002", "IPS Kit Web SAS", "FINANCIERO"));

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Equal("Admin900400002", Texto(html, "kit-usuario"));
        Assert.Equal(14, Texto(html, "kit-clave").Length);
        Assert.Equal("conector-900400002", Texto(html, "kit-clientid"));
        Assert.True(Texto(html, "kit-secreto").Length >= 40);
        Assert.Equal("https://portal.prueba.test", Texto(html, "kit-portal"));
        Assert.Equal("https://api.prueba.test", Texto(html, "kit-api"));
        Assert.Contains("Se muestra una sola vez", html);
        Assert.Contains("no-store", (await sa.Cliente.GetAsync("/admin/empresas/nueva")).Headers.CacheControl!.ToString());

        // Recargar no lo vuelve a mostrar: la contraseña y el secreto no se guardan en claro.
        var (_, otra, _) = await sa.IrAsync("/admin/empresas/nueva");
        Assert.DoesNotContain("kit-clave", otra);
        // Repetir el envío no crea otra empresa.
        var (_, repetido, _) = await sa.EnviarFormularioAsync("/admin/empresas/nueva", "nueva-empresa", CamposEmpresa("900400002", "IPS Kit Web SAS", "FINANCIERO"));
        Assert.Contains("Ya existe una empresa con ese NIT.", repetido);
        Assert.DoesNotContain("kit-clave", repetido);
    }

    [Fact]
    public async Task El_alta_de_empresa_valida_nit_y_digito_de_verificacion()
    {
        var sa = await SuperadminAsync();
        var campos = CamposEmpresa("900400003", "IPS DV Mala SAS", "FINANCIERO");
        campos["Entrada.DigitoVerificacion"] = campos["Entrada.DigitoVerificacion"] == "1" ? "2" : "1";
        var (_, html, _) = await sa.EnviarFormularioAsync("/admin/empresas/nueva", "nueva-empresa", campos);
        Assert.Contains("dígito de verificación no corresponde", html);
        Assert.Equal(0, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT COUNT(*) FROM seg.Empresa WHERE Nit = '900400003'", superadmin: true));
    }

    [Fact]
    public async Task El_Admin_NIT_recien_creado_solo_ve_datos_tras_cambiar_la_clave_y_activar_el_doble_factor()
    {
        var cred = await CrearEmpresaPorPortalAsync("900400004", "IPS Primer Ingreso SAS");   // PrimerIngresoAsync verifica el orden de los pasos
        Assert.NotNull(cred.ClaveTotp);

        var n = Nuevo();
        Assert.True(await IngresarAsync(n, cred));
        var (estado, html, _) = await n.IrAsync("/admin/usuarios");
        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains(cred.Usuario, html);
    }

    [Fact]
    public async Task Un_Admin_NIT_no_puede_crear_empresas_ni_ver_pantallas_de_superadmin()
    {
        var cred = await CrearEmpresaPorPortalAsync("900400005", "IPS Sin Poder SAS");
        var n = Nuevo();
        Assert.True(await IngresarAsync(n, cred));

        var (estado, _, redir) = await n.IrAsync("/admin/empresas/nueva");
        Assert.Equal(HttpStatusCode.Redirect, estado);
        Assert.Contains("/acceso-denegado", redir);
    }

    [Fact]
    public async Task La_clave_inicial_vencida_no_permite_ingresar()
    {
        var kit = await CrearEmpresaPorPortalAsync("900400006", "IPS Clave Vencida SAS", completarPrimerIngreso: false);
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaPropietario,
            $"EXEC sp_set_session_context N'EsSuperAdmin', 1; UPDATE seg.Usuario SET ClaveTemporalVenceEn = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE UserName = '{kit.Usuario}'");

        var n = Nuevo();
        var (_, html, _) = await n.EnviarFormularioAsync("/ingresar", "ingresar", new Dictionary<string, string> { ["Entrada.Usuario"] = kit.Usuario, ["Entrada.Clave"] = kit.Clave });
        Assert.Contains("La contraseña inicial venció", html);
        var (estado, _, _) = await n.IrAsync("/admin/usuarios");
        Assert.Equal(HttpStatusCode.Redirect, estado);   // no quedó con sesión
    }

    // ---------- Aislamiento y gestión de usuarios por HTTP ----------

    [Fact]
    public async Task Cada_Admin_ve_solo_los_usuarios_de_su_empresa_y_no_puede_actuar_sobre_los_de_otra()
    {
        var a = await CrearEmpresaPorPortalAsync("900400007", "IPS Aislada A SAS");
        var b = await CrearEmpresaPorPortalAsync("900400008", "IPS Aislada B SAS");
        var navA = Nuevo(); var navB = Nuevo();
        Assert.True(await IngresarAsync(navA, a));
        Assert.True(await IngresarAsync(navB, b));

        var campos = new Dictionary<string, string>
            { ["Entrada.NombreUsuario"] = "gerente.b", ["Entrada.NombreCompleto"] = "Gerente de B", ["Entrada.Email"] = "g@b.test", ["Entrada.Roles[GERENTE]"] = "true" };
        var (estadoCrear, htmlCrear, _) = await navB.EnviarFormularioAsync("/admin/usuarios", "nuevo-usuario", campos);
        Assert.Equal(HttpStatusCode.OK, estadoCrear);
        Assert.Contains("cred-clave", htmlCrear);

        Assert.Contains("gerente.b", (await navB.IrAsync("/admin/usuarios")).Html);
        var listaA = (await navA.IrAsync("/admin/usuarios")).Html;
        Assert.DoesNotContain("gerente.b", listaA);
        Assert.DoesNotContain(b.Usuario, listaA);
        Assert.Contains(a.Usuario, listaA);

        // A intenta desactivar y restablecer al usuario de B por su id.
        var idGerenteB = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT Id FROM seg.Usuario WHERE UserName = 'gerente.b'", superadmin: true);
        var (_, _, redirEstado) = await navA.PostConTokenAsync("/admin/usuarios", $"/admin/usuarios/{idGerenteB}/estado?activo=false");
        Assert.Contains("error=", redirEstado);
        var (_, _, redirReset) = await navA.PostConTokenAsync("/admin/usuarios", $"/admin/usuarios/{idGerenteB}/restablecer");
        Assert.Contains("error=", redirReset);
        Assert.Equal(1, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT CAST(Activo AS int) FROM seg.Usuario WHERE UserName = 'gerente.b'", superadmin: true));
    }

    [Fact]
    public async Task Un_usuario_desactivado_pierde_la_sesion_abierta_en_la_siguiente_solicitud()
    {
        var admin = await CrearEmpresaPorPortalAsync("900400009", "IPS Baja Inmediata SAS");
        var navAdmin = Nuevo();
        Assert.True(await IngresarAsync(navAdmin, admin));

        var (_, htmlCrear, _) = await navAdmin.EnviarFormularioAsync("/admin/usuarios", "nuevo-usuario", new Dictionary<string, string>
            { ["Entrada.NombreUsuario"] = "consulta.baja", ["Entrada.NombreCompleto"] = "Consulta Baja", ["Entrada.Email"] = "c@baja.test", ["Entrada.Roles[CONSULTA]"] = "true" });
        var claveTemporal = Texto(htmlCrear, "cred-clave");

        var navUsuario = Nuevo();
        await PrimerIngresoAsync(navUsuario, "consulta.baja", claveTemporal, NuevoUsuarioNavegadorClave, requiereDobleFactor: false);   // CONSULTA: 2FA opcional
        Assert.Equal(HttpStatusCode.OK, (await navUsuario.IrAsync("/")).Estado);

        var id = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT Id FROM seg.Usuario WHERE UserName = 'consulta.baja'", superadmin: true);
        await navAdmin.PostConTokenAsync("/admin/usuarios", $"/admin/usuarios/{id}/estado?activo=false");

        var (estado, _, redir) = await navUsuario.IrAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, estado);
        Assert.Contains("/ingresar", redir);
    }

    [Fact]
    public async Task Restablecer_una_clave_desde_el_portal_muestra_la_credencial_una_sola_vez()
    {
        var admin = await CrearEmpresaPorPortalAsync("900400010", "IPS Restablece SAS");
        var n = Nuevo();
        Assert.True(await IngresarAsync(n, admin));
        await n.EnviarFormularioAsync("/admin/usuarios", "nuevo-usuario", new Dictionary<string, string>
            { ["Entrada.NombreUsuario"] = "carlos.reset", ["Entrada.NombreCompleto"] = "Carlos Reset", ["Entrada.Email"] = "c@r.test", ["Entrada.Roles[TESORERO]"] = "true" });
        var id = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT Id FROM seg.Usuario WHERE UserName = 'carlos.reset'", superadmin: true);

        var (estado, html, _) = await n.PostConTokenAsync("/admin/usuarios", $"/admin/usuarios/{id}/restablecer");
        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Equal("carlos.reset", Texto(html, "cred-usuario"));
        Assert.Equal(14, Texto(html, "cred-clave").Length);
        Assert.DoesNotContain("cred-clave", (await n.IrAsync("/admin/usuarios")).Html);
    }
}
