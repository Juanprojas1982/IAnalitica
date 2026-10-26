using System.Net;
using IAnalitica.Integration.Tests.Infra;
using IAnalitica.Integration.Tests.Ingesta;
using static IAnalitica.Integration.Tests.Web.Flujos;

namespace IAnalitica.Integration.Tests.Web;

/// <summary>«Salud de la configuración» por HTTP con el portal real: índice, hallazgos, escape del HTML, acciones y módulo inactivo.</summary>
[Collection(ColeccionBd.Nombre)]
public class AuditorWebTests(BaseDeDatosFixture bd) : IAsyncLifetime
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
            var kit = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido("813" + Random.Shared.Next(100000, 999999), "Clínica auditada", modulos: new[] { "AUDITOR_CONFIG" }));
            _datos = Procesamiento.ParaEmpresa(bd, kit.EmpresaId);
            await _datos.ProcesarAsync("configuracion", "FOTO", new object[]
            {
                new { tabla = "dbo.mEmpresas", clave = "1", datos = new { IdEmpresa = 1, NomEmpresa = "<img src=x onerror=alert(1)>", CodManual = 5, EstadoEntidad = 1 } },   // sin cuentas: CON-01
                new { tabla = "dbo.AspNetUsers", clave = "u1", datos = new { Id = "u1", TerceroId = 500, UserName = "retirado", Bloqueado = 0 } },
                new { tabla = "Nomina.Empleados", clave = "1", datos = new { Id = 1, TerceroId = 500 } },
                new { tabla = "Nomina.Contratos", clave = "1", datos = new { Id = 1, EmpleadoId = 1, FechaSalida = "2026-06-30T00:00:00" } },
            });
            await _datos.AuditorEn(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)).EjecutarAsync(kit.EmpresaId, CancellationToken.None);
            var (clave, totp) = await PrimerIngresoAsync(Fabrica(bd).NuevoNavegador(), kit.UsuarioAdministrador, kit.ClaveInicial, NuevoUsuarioNavegadorClave, requiereDobleFactor: true);
            _admin = new Credenciales(kit.UsuarioAdministrador, clave, totp);

            var kit2 = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido("814" + Random.Shared.Next(100000, 999999), "Clínica sin auditor", modulos: new[] { "FINANCIERO" }));
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
    public async Task Un_anonimo_es_enviado_a_ingresar()
    {
        var (estado, _, redir) = await Fabrica(bd).NuevoNavegador().IrAsync("/auditor");
        Assert.Equal(HttpStatusCode.Redirect, estado);
        Assert.Contains("/ingresar", redir);
    }

    [Fact]
    public async Task Muestra_indice_hallazgos_con_su_explicacion_y_escapa_el_html()
    {
        var n = await ComoAsync(_admin!);
        var (estado, html, _) = await n.IrAsync("/auditor");
        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("Salud de la configuración", html);
        Assert.Contains("Pagador activo sin cuentas contables en la vigencia", html);
        Assert.Contains("Usuario activo de un empleado retirado", html);
        Assert.Contains("Cómo corregirlo", html);
        Assert.Contains("Facturación › Empresas › Cuentas contables", html);
        Assert.Contains("por verificar", html);
        Assert.Contains("/100", html);
        var crudo = await n.Cliente.GetStringAsync("/auditor");
        Assert.DoesNotContain("<img src=x", crudo);                      // nunca como etiqueta
        Assert.Contains("&lt;img src=x", crudo);                         // sí como texto
        // la crítica va antes que las de menor severidad
        Assert.True(html.IndexOf("sev-critica", StringComparison.Ordinal) < html.IndexOf("sev-alta", StringComparison.Ordinal) || !html.Contains("sev-alta"));
    }

    [Fact]
    public async Task Aceptar_exige_justificacion_la_guarda_y_se_puede_reabrir()
    {
        var n = await ComoAsync(_admin!);
        var id = await _datos!.EscalarAsync<long>("SELECT HallazgoId FROM fact.HallazgoConfiguracion WHERE EmpresaId = @e AND Codigo = 'USU-01'", new { e = _datos.EmpresaId });
        var destino = $"/auditor/hallazgos/{id}/estado";

        // Sin justificación: no cambia
        var (e1, _, r1) = await n.PostConTokenAsync("/auditor", destino, new Dictionary<string, string> { ["accion"] = "ACEPTAR", ["justificacion"] = "corta" });
        Assert.Equal(HttpStatusCode.Redirect, e1);
        Assert.Contains("error=", r1);
        Assert.Equal("NUEVO", await Estado(id));

        var (_, _, r2) = await n.PostConTokenAsync("/auditor", destino, new Dictionary<string, string> { ["accion"] = "ACEPTAR", ["justificacion"] = "Es un contratista con acceso autorizado" });
        Assert.Contains("ok=1", r2);
        Assert.Equal("ACEPTADO", await Estado(id));
        var (_, html, _) = await n.IrAsync("/auditor");
        Assert.DoesNotContain("Usuario activo de un empleado retirado", html);                 // ya no está pendiente
        var (_, htmlTodos, _) = await n.IrAsync("/auditor?cerrados=1");
        Assert.Contains("Es un contratista con acceso autorizado", htmlTodos);
        Assert.Equal(1, await _datos.EscalarAsync<int>("SELECT COUNT(*) FROM aud.CambioAdministrativo WHERE EmpresaId = @e AND Entidad = 'fact.HallazgoConfiguracion' AND Accion = 'ACEPTAR'", new { e = _datos.EmpresaId }));

        var (_, _, r3) = await n.PostConTokenAsync("/auditor?cerrados=1", destino, new Dictionary<string, string> { ["accion"] = "REABRIR", ["cerrados"] = "1" });
        Assert.Contains("ok=1", r3);
        Assert.Equal("ABIERTO", await Estado(id));
    }

    [Fact]
    public async Task Sin_el_modulo_activo_explica_por_que_no_hay_datos()
    {
        var n = await ComoAsync(_sinModulo!);
        var (estado, html, _) = await n.IrAsync("/auditor");
        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("no está activo", html);
    }

    private Task<string> Estado(long id)
        => _datos!.EscalarAsync<string>("SELECT Estado FROM fact.HallazgoConfiguracion WHERE EmpresaId = @e AND HallazgoId = @id", new { e = _datos.EmpresaId, id });
}
