using System.Net;
using IAnalitica.Integration.Tests.Infra;
using IAnalitica.Integration.Tests.Ingesta;
using static IAnalitica.Integration.Tests.Web.Flujos;

namespace IAnalitica.Integration.Tests.Web;

/// <summary>«Cumplimiento de precios» por HTTP: hallazgos del validador, «por verificar», aceptar con justificación y módulo inactivo.</summary>
[Collection(ColeccionBd.Nombre)]
public class PreciosWebTests(BaseDeDatosFixture bd) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static Credenciales? _admin;
    private static Credenciales? _sinModulo;
    private static Procesamiento? _datos;

    public async Task InitializeAsync()
    {
        await Candado.WaitAsync();
        try
        {
            if (_admin is not null) return;
            var kit = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido("815" + Random.Shared.Next(100000, 999999), "Farmacia de prueba", modulos: new[] { "FARMACIA" }));
            _datos = Procesamiento.ParaEmpresa(bd, kit.EmpresaId);
            var circular = await _datos.EscalarAsync<int>("INSERT reg.Circular (Entidad, Numero, Anio, Tema, FechaExpedicion, VigenteDesde, UrlFuente) OUTPUT inserted.CircularId VALUES ('CNPMDM', 'W815', 2026, 'PRECIO_MAXIMO', '2026-08-01', '2026-08-19', 'https://ejemplo.invalido')");
            await _datos.EjecutarAsync("INSERT reg.PrecioMaximo (CircularId, Cum, PrecioMaximoPresentacion, PrecioMaximoUnidad, VigenteDesde) VALUES (@c, '815001-1', 100, 100, '2026-08-19'), (@c, '815002-1', 100, NULL, '2026-08-19')", new { c = circular });
            await _datos.EjecutarAsync("INSERT reg.FactorPresentacion (Cum, UnidadesPorPresentacion, Fuente) VALUES ('815002-1', 10, 'MANUAL')");
            await _datos.ProcesarAsync("configuracion", "FOTO", new object[]
            {
                new { tabla = "dbo.mEmpresas", clave = "1", datos = new { IdEmpresa = 1, NomEmpresa = "<b>EPS</b> Uno", CodManual = 5, EstadoEntidad = 1 } },
                new { tabla = "dbo.pSuministros", clave = "S1", datos = new { CodSuministro = "S1", NomSuministro = "Acetaminofén", CodCum = "815001-1" } },
                new { tabla = "dbo.pSuministros", clave = "S2", datos = new { CodSuministro = "S2", NomSuministro = "Ibuprofeno", CodCum = "815002-1" } },
                new { tabla = "dbo.pTarifasSuministros", clave = "S1|5", datos = new { CodSuministro = "S1", CodManual = 5, Valor = 130 } },
                new { tabla = "dbo.pTarifasSuministros", clave = "S2|5", datos = new { CodSuministro = "S2", CodManual = 5, Valor = 20 } },
            });
            await _datos.ValidadorEn(DateTimeOffset.UtcNow).EjecutarAsync(kit.EmpresaId, CancellationToken.None);
            var (clave, totp) = await PrimerIngresoAsync(Fabrica(bd).NuevoNavegador(), kit.UsuarioAdministrador, kit.ClaveInicial, NuevoUsuarioNavegadorClave, requiereDobleFactor: true);
            _admin = new Credenciales(kit.UsuarioAdministrador, clave, totp);

            var kit2 = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido("816" + Random.Shared.Next(100000, 999999), "Sin farmacia", modulos: new[] { "FINANCIERO" }));
            var (clave2, totp2) = await PrimerIngresoAsync(Fabrica(bd).NuevoNavegador(), kit2.UsuarioAdministrador, kit2.ClaveInicial, NuevoUsuarioNavegadorClave, requiereDobleFactor: true);
            _sinModulo = new Credenciales(kit2.UsuarioAdministrador, clave2, totp2);
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
    public async Task Muestra_el_exceso_con_su_circular_y_escapa_el_html()
    {
        var n = await ComoAsync(_admin!);
        var (estado, html, _) = await n.IrAsync("/precios?cerrados=1");   // otra prueba puede haber cerrado uno
        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("Tarifa por encima del precio máximo", html);
        Assert.Contains("Acetaminofén", html);
        Assert.Contains("W815", html);
        Assert.Contains("$ 30,00", html);                    // 130 − 100
        Assert.Contains("por verificar", html);              // Ibuprofeno: factor manual sin validar (20 > 10)
        var crudo = await n.Cliente.GetStringAsync("/precios?cerrados=1");
        Assert.DoesNotContain("<b>EPS</b>", crudo);
        Assert.Contains("&lt;b&gt;EPS&lt;/b&gt;", crudo);
    }

    [Fact]
    public async Task Aceptar_un_hallazgo_de_precios_exige_justificacion_y_lo_saca_de_pendientes()
    {
        var n = await ComoAsync(_admin!);
        var id = await _datos!.EscalarAsync<long>("SELECT HallazgoId FROM fact.HallazgoTarifario WHERE EmpresaId = @e AND Regla = 'R1' AND ClaveEntidad = 'S1|1'", new { e = _datos.EmpresaId });
        var destino = $"/auditor/precios/{id}/estado";
        var (_, _, corta) = await n.PostConTokenAsync("/precios", destino, new Dictionary<string, string> { ["accion"] = "ACEPTAR", ["justificacion"] = "no" });
        Assert.Contains("error=", corta);
        var (_, _, ok) = await n.PostConTokenAsync("/precios", destino, new Dictionary<string, string> { ["accion"] = "FALSO_POSITIVO", ["justificacion"] = "Producto con precio pactado distinto" });
        Assert.Contains("ok=1", ok);
        var (_, html, _) = await n.IrAsync("/precios");
        Assert.DoesNotContain("Acetaminofén", html);
        Assert.Equal(1, await _datos.EscalarAsync<int>("SELECT COUNT(*) FROM aud.CambioAdministrativo WHERE EmpresaId = @e AND Entidad = 'fact.HallazgoTarifario'", new { e = _datos.EmpresaId }));
    }

    [Fact]
    public async Task Sin_el_modulo_de_farmacia_explica_por_que_no_hay_datos()
    {
        var n = await ComoAsync(_sinModulo!);
        var (estado, html, _) = await n.IrAsync("/precios");
        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("no está activo", html);
    }
}
