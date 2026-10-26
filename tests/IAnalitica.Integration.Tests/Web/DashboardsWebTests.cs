using System.Net;
using System.Text.Json;
using IAnalitica.Integration.Tests.Infra;
using IAnalitica.Integration.Tests.Ingesta;
using Microsoft.Extensions.DependencyInjection;
using static IAnalitica.Integration.Tests.Web.Flujos;

namespace IAnalitica.Integration.Tests.Web;

/// <summary>Los dashboards por HTTP, con el portal real: sesión del Admin{NIT}, filtros en la URL, módulos y seguridad del HTML.</summary>
[Collection(ColeccionBd.Nombre)]
public class DashboardsWebTests(BaseDeDatosFixture bd) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static Credenciales? _admin;
    private static Procesamiento? _datos;
    private static Credenciales? _adminSoloFinanciero;

    public async Task InitializeAsync()
    {
        await Candado.WaitAsync();
        try
        {
            if (_admin is not null) return;
            // Empresa con los 3 módulos y cifras conocidas
            var nit = "811" + Random.Shared.Next(100000, 999999);
            var kit = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido(nit, "Clínica con datos", modulos: new[] { "FINANCIERO", "CICLO_INGRESO", "CARTERA" }));
            _datos = Procesamiento.ParaEmpresa(bd, kit.EmpresaId);
            await DatosDashboards.CargarAsync(_datos);
            await DatosDashboards.CalcularCarteraAsync(_datos);
            // un pagador con nombre hostil: el HTML debe escaparlo
            await _datos.ProcesarAsync("pagadores", "COMPLETA", new object[]
            {
                new { codigoOrigen = "E1", nombre = "EPS Uno", terceroCodigo = "P1", activo = true },
                new { codigoOrigen = "E2", nombre = "<img src=x onerror=alert(1)>", terceroCodigo = "P2", activo = true },
            });
            var (clave, totp) = await PrimerIngresoAsync(Fabrica(bd).NuevoNavegador(), kit.UsuarioAdministrador, kit.ClaveInicial, NuevoUsuarioNavegadorClave, requiereDobleFactor: true);
            _admin = new Credenciales(kit.UsuarioAdministrador, clave, totp);

            // Empresa con un solo módulo
            var nit2 = "812" + Random.Shared.Next(100000, 999999);
            var kit2 = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido(nit2, "Clínica solo financiera", modulos: new[] { "FINANCIERO" }));
            var (clave2, totp2) = await PrimerIngresoAsync(Fabrica(bd).NuevoNavegador(), kit2.UsuarioAdministrador, kit2.ClaveInicial, NuevoUsuarioNavegadorClave, requiereDobleFactor: true);
            _adminSoloFinanciero = new Credenciales(kit2.UsuarioAdministrador, clave2, totp2);
        }
        finally { Candado.Release(); }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Navegador> ComoAsync(Credenciales c)
    {
        var n = Fabrica(bd).NuevoNavegador();
        Assert.True(await IngresarAsync(n, c));
        return n;
    }

    [Fact]
    public async Task Un_anonimo_no_ve_dashboards_ni_consultas()
    {
        var n = Fabrica(bd).NuevoNavegador();
        foreach (var ruta in new[] { "/dashboards", "/d/financiero.gerencial", "/q/dashboards", "/q/dashboards/financiero.gerencial", "/q/widgets/financiero.gerencial/ingresos/datos" })
        {
            var (estado, _, redir) = await n.IrAsync(ruta);
            Assert.Equal(HttpStatusCode.Redirect, estado);
            Assert.Contains("/ingresar", redir);
        }
    }

    [Fact]
    public async Task El_indice_lista_los_dashboards_de_los_modulos_activos()
    {
        var n = await ComoAsync(_admin!);
        var (_, html, _) = await n.IrAsync("/dashboards");
        Assert.Contains("Gerencial", html);
        Assert.Contains("Estado de resultados", html);
        Assert.Contains("Antigüedad de cartera", html);
        Assert.Contains("DSO por EPS", html);
        Assert.Contains("Re-trabajo de facturación", html);
        Assert.Contains("href=\"/d/cartera.dso\"", html);

        var solo = await ComoAsync(_adminSoloFinanciero!);
        var (_, htmlSolo, _) = await solo.IrAsync("/dashboards");
        Assert.Contains("Balance y razones", htmlSolo);
        Assert.DoesNotContain("Antigüedad de cartera", htmlSolo);
        Assert.DoesNotContain("Re-trabajo de facturación", htmlSolo);
    }

    [Fact]
    public async Task El_gerencial_muestra_las_cifras_con_formato_es_CO_y_conserva_los_filtros()
    {
        var n = await ComoAsync(_admin!);
        var (estado, html, _) = await n.IrAsync("/d/financiero.gerencial?desde=2026-01&hasta=2026-02");
        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("$ 3.000", html);          // ingresos (el cierre no cuenta)
        Assert.Contains("$ 500", html);             // costos y gastos
        Assert.Contains("$ 2.500", html);           // utilidad
        Assert.Contains("83,3 %", html);            // margen
        Assert.Contains("$ 3.300", html);           // cartera (widget de otro módulo activo)
        Assert.Contains("name=\"desde\"", html);
        Assert.Contains("value=\"2026-01\"", html);
        Assert.Contains("value=\"2026-02\"", html);
        // gráfico de líneas accesible
        Assert.Contains("role=\"img\"", html);
        Assert.Contains("<details class=\"datos\">", html);
        Assert.Contains("<th scope=\"row\">2026-01</th>", html);
    }

    [Fact]
    public async Task Un_filtro_de_periodo_cambia_las_cifras_y_muestra_la_variacion()
    {
        var n = await ComoAsync(_admin!);
        var (_, html, _) = await n.IrAsync("/d/financiero.gerencial?desde=2026-02&hasta=2026-02");
        Assert.Contains("$ 2.000", html);
        Assert.Contains("+100 %", html);            // frente a enero
        Assert.Contains("frente al periodo anterior", html);
    }

    [Fact]
    public async Task Los_filtros_invalidos_no_rompen_la_pagina()
    {
        var n = await ComoAsync(_admin!);
        foreach (var ruta in new[] { "/d/financiero.gerencial?desde=basura&hasta=2026-13", "/d/financiero.gerencial?sede=abc&pagador=-1", "/d/cartera.antiguedad?corte=ayer", "/d/financiero.gerencial?desde=2026-05&hasta=2026-01" })
        {
            var (estado, html, _) = await n.IrAsync(ruta);
            Assert.Equal(HttpStatusCode.OK, estado);
            Assert.Contains("<h1>", html);
        }
    }

    [Fact]
    public async Task La_cartera_muestra_edades_dso_y_conciliacion()
    {
        var n = await ComoAsync(_admin!);
        var (_, edades, _) = await n.IrAsync("/d/cartera.antiguedad");
        Assert.Contains("$ 3.300", edades);
        Assert.Contains("24,2 %", edades);                      // más de 90 días
        Assert.Contains("151-180", edades);
        var (_, dso, _) = await n.IrAsync("/d/cartera.dso");
        Assert.Contains("style=\"width:100%\"", dso);            // la barra más larga (EPS Dos, 126 d)
        Assert.Contains("style=\"width:35.7%\"", dso);           // EPS Uno: 45 / 126
        Assert.Contains("99 d", dso);
        Assert.Contains("45 d", dso);
        Assert.Contains("126 d", dso);
        var (_, conc, _) = await n.IrAsync("/d/cartera.conciliacion");
        Assert.Contains("$ 0", conc);
    }

    [Fact]
    public async Task El_drill_lleva_el_filtro_a_otro_dashboard()
    {
        var n = await ComoAsync(_admin!);
        var (_, html, _) = await n.IrAsync("/d/cartera.antiguedad");
        var enlace = System.Text.RegularExpressions.Regex.Match(html, "href=\"(/d/cartera\\.dso\\?pagador=\\d+)\"");
        Assert.True(enlace.Success, "Las barras por pagador deben enlazar al DSO con el pagador elegido.");
        var (estado, destino, _) = await n.IrAsync(enlace.Groups[1].Value);
        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("DSO", destino);
    }

    [Fact]
    public async Task Un_modulo_inactivo_responde_409_y_un_dashboard_inexistente_404()
    {
        var solo = await ComoAsync(_adminSoloFinanciero!);
        var (estado, html, _) = await solo.IrAsync("/d/cartera.antiguedad");
        Assert.Equal(HttpStatusCode.Conflict, estado);
        Assert.Contains("no está activo", html);

        var (e404, h404, _) = await solo.IrAsync("/d/no.existe");
        Assert.Equal(HttpStatusCode.NotFound, e404);
        Assert.Contains("no existe", h404);

        // la empresa solo financiera no ve los widgets de otros módulos en el gerencial
        var (_, gerencial, _) = await solo.IrAsync("/d/financiero.gerencial");
        Assert.DoesNotContain("Cartera por cobrar", gerencial);
        Assert.DoesNotContain("Facturado en el periodo", gerencial);
    }

    [Fact]
    public async Task El_HTML_escapa_nombres_hostiles_y_no_deja_pasar_scripts()
    {
        var n = await ComoAsync(_admin!);
        var crudo = await n.Cliente.GetStringAsync("/d/cartera.dso");
        Assert.DoesNotContain("<img src=x", crudo);                      // nunca como etiqueta
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", crudo);    // sí como texto
        Assert.DoesNotContain("<script>", crudo.Replace("<script src=", ""));
    }

    [Fact]
    public async Task Los_endpoints_q_devuelven_la_definicion_y_los_datos_en_JSON()
    {
        var n = await ComoAsync(_admin!);
        var lista = JsonDocument.Parse(await n.Cliente.GetStringAsync("/q/dashboards")).RootElement;
        Assert.True(lista.GetArrayLength() >= 9);

        var def = JsonDocument.Parse(await n.Cliente.GetStringAsync("/q/dashboards/financiero.gerencial")).RootElement;
        Assert.Equal("Gerencial", def.GetProperty("titulo").GetString());
        Assert.Contains(def.GetProperty("widgets").EnumerateArray(), w => w.GetProperty("codigo").GetString() == "ingresos");

        var datos = JsonDocument.Parse(await n.Cliente.GetStringAsync("/q/widgets/financiero.gerencial/ingresos/datos?desde=2026-01&hasta=2026-02")).RootElement;
        Assert.Equal("Kpi", datos.GetProperty("tipo").GetString());
        Assert.Equal(3000m, datos.GetProperty("series")[0].GetProperty("resultado").GetProperty("filas")[0].GetProperty("valor").GetDecimal());

        var r404 = await n.Cliente.GetAsync("/q/widgets/financiero.gerencial/no_existe/datos");
        Assert.Equal(HttpStatusCode.NotFound, r404.StatusCode);
        var solo = await ComoAsync(_adminSoloFinanciero!);
        var r409 = await solo.Cliente.GetAsync("/q/dashboards/cartera.antiguedad");
        Assert.Equal(HttpStatusCode.Conflict, r409.StatusCode);
        Assert.Equal("MODULO_INACTIVO", JsonDocument.Parse(await r409.Content.ReadAsStringAsync()).RootElement.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task Otra_empresa_no_ve_las_cifras_de_esta_ni_por_las_consultas_q()
    {
        var solo = await ComoAsync(_adminSoloFinanciero!);
        var datos = JsonDocument.Parse(await solo.Cliente.GetStringAsync("/q/widgets/financiero.gerencial/ingresos/datos?desde=2026-01&hasta=2026-02")).RootElement;
        Assert.Equal(JsonValueKind.Null, datos.GetProperty("series")[0].GetProperty("resultado").GetProperty("filas")[0].GetProperty("valor").ValueKind);
        var (_, html, _) = await solo.IrAsync("/d/financiero.gerencial?desde=2026-01&hasta=2026-02");
        Assert.DoesNotContain("$ 3.000", html);
    }

    [Fact]
    public async Task Pedir_un_reporte_por_el_portal_esperar_y_descargarlo()
    {
        var n = await ComoAsync(_admin!);
        var (_, tablero, _) = await n.IrAsync("/d/financiero.gerencial?desde=2026-01&hasta=2026-02");
        Assert.Contains("action=\"/reportes/solicitar\"", tablero);
        Assert.Contains("name=\"formato\"", tablero);

        var (estado, _, redir) = await n.PostConTokenAsync("/d/financiero.gerencial?desde=2026-01&hasta=2026-02", "/reportes/solicitar");
        // PostConTokenAsync solo manda el token: sin dashboard ni formato → se rechaza con un mensaje, no con un error del servidor
        Assert.Equal(HttpStatusCode.Redirect, estado);
        Assert.Contains("/reportes?error=", redir);

        // Pedido real: el formulario de la página, tal como lo enviaría el navegador
        var respuesta = await EnviarExportacionAsync(n, "financiero.gerencial", "XLSX", "2026-01", "2026-02");
        Assert.Equal(HttpStatusCode.Redirect, respuesta.Estado);
        Assert.Contains("/reportes?nuevo=1", respuesta.Redireccion);

        var (_, lista, _) = await n.IrAsync("/reportes");
        Assert.Contains("En cola", lista);
        Assert.Contains("http-equiv=\"refresh\"", lista);               // mientras hay reportes en curso, la página se actualiza sola

        await using var worker = WorkerDePrueba.Construir(bd, FabricaWeb.RutaReportes);
        var despachador = worker.GetRequiredService<IAnalitica.Infrastructure.Ingesta.DespachadorTrabajos>();
        while (await despachador.EjecutarUnoAsync("GENERAR_REPORTE", CancellationToken.None)) { }

        var (_, listo, _) = await n.IrAsync("/reportes");
        Assert.Contains("Listo (", listo);
        var enlace = System.Text.RegularExpressions.Regex.Match(listo, "href=\"(/reportes/[0-9a-f-]{36}/descargar)\"");
        Assert.True(enlace.Success);

        var descarga = await n.Cliente.GetAsync(enlace.Groups[1].Value);
        Assert.Equal(HttpStatusCode.OK, descarga.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", descarga.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", descarga.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("financiero-gerencial-", descarga.Content.Headers.ContentDisposition.FileName);
        Assert.Contains("no-store", descarga.Headers.CacheControl!.ToString());
        var bytes = await descarga.Content.ReadAsByteArrayAsync();
        Assert.Equal((byte)'P', bytes[0]);                              // un .xlsx es un ZIP («PK»)
        Assert.Equal((byte)'K', bytes[1]);

        // Otra empresa ni siquiera lo ve; un anónimo va al ingreso
        var solo = await ComoAsync(_adminSoloFinanciero!);
        Assert.Equal(HttpStatusCode.NotFound, (await solo.Cliente.GetAsync(enlace.Groups[1].Value)).StatusCode);
        var (eAnon, _, rAnon) = await Fabrica(bd).NuevoNavegador().IrAsync(enlace.Groups[1].Value);
        Assert.Equal(HttpStatusCode.Redirect, eAnon);
        Assert.Contains("/ingresar", rAnon);
    }

    private static async Task<(HttpStatusCode Estado, string? Redireccion)> EnviarExportacionAsync(Navegador n, string dashboard, string formato, string desde, string hasta)
    {
        var (_, html, _) = await n.IrAsync($"/d/{dashboard}?desde={desde}&hasta={hasta}");
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var r = await n.Cliente.PostAsync("/reportes/solicitar", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = System.Net.WebUtility.HtmlDecode(token), ["dashboard"] = dashboard, ["formato"] = formato, ["desde"] = desde, ["hasta"] = hasta,
        }));
        return (r.StatusCode, r.Headers.Location?.OriginalString);
    }
}
