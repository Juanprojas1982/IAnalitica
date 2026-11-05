using System.Net;
using Dapper;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Facturacion;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static IAnalitica.Integration.Tests.Web.Flujos;

namespace IAnalitica.Integration.Tests.Web;

/// <summary>Facturación en el portal real: vista del cliente, administración del superadmin y descarga del PDF (08 §32.5).</summary>
[Collection(ColeccionBd.Nombre)]
public class FacturacionWebTests(BaseDeDatosFixture bd) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static Credenciales? _cliente;
    private static int _empresa, _emitida, _borrador, _ajena, _periodo = 203600;

    public async Task InitializeAsync()
    {
        await Candado.WaitAsync();
        try
        {
            if (_cliente is not null) return;
            var kit = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido("819" + Random.Shared.Next(100000, 999999), "Clínica facturable"));
            var kitAjeno = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido("820" + Random.Shared.Next(100000, 999999), "Otra clínica"));
            _empresa = kit.EmpresaId;
            await PrepararCondicionAsync(kit.EmpresaId);
            await PrepararCondicionAsync(kitAjeno.EmpresaId);
            _emitida = await CuentaAsync(kit.EmpresaId, emitir: true);
            _borrador = await CuentaAsync(kit.EmpresaId, emitir: false);
            _ajena = await CuentaAsync(kitAjeno.EmpresaId, emitir: true);
            var (clave, totp) = await PrimerIngresoAsync(Fabrica(bd).NuevoNavegador(), kit.UsuarioAdministrador, kit.ClaveInicial, NuevoUsuarioNavegadorClave, requiereDobleFactor: true);
            _cliente = new Credenciales(kit.UsuarioAdministrador, clave, totp);
        }
        finally { Candado.Release(); }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task PrepararCondicionAsync(int empresaId) => BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"""
        INSERT fac.CondicionComercial (EmpresaId, VigenteDesde, PlanId, CanonPactadoCop, CreadoPor)
        VALUES ({empresaId}, '2026-01-01', (SELECT TOP (1) PlanId FROM ia.PlanComercial ORDER BY PlanId), 900000, {bd.SuperadminId})
        """);

    private async Task<int> CuentaAsync(int empresaId, bool emitir)
    {
        var periodo = Interlocked.Increment(ref _periodo);
        if (periodo % 100 > 12) periodo = Interlocked.Add(ref _periodo, 88);
        var anio = periodo / 100; var mes = periodo % 100; var dias = DateTime.DaysInMonth(anio, mes);
        var id = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaEtl, $"""
            INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, DiasPago)
            SELECT {empresaId}, {periodo}, CondicionId, 30 FROM fac.CondicionComercial WHERE EmpresaId = {empresaId} AND VigenteHasta IS NULL;
            DECLARE @id int = CAST(SCOPE_IDENTITY() AS int);
            INSERT fac.CuentaCobroLinea (CuentaCobroId, EmpresaId, Orden, Tipo, Concepto, PeriodoServicioKey, CondicionId, FechaDesde, FechaHasta, DiasFacturados, DiasPeriodo, CanonMensualCop, ValorCop)
            SELECT @id, {empresaId}, 1, 'CANON', N'Canon mensual', {periodo}, CondicionId, '{anio}-{mes:D2}-01', '{anio}-{mes:D2}-{dias:D2}', {dias}, {dias}, 900000, 900000
            FROM fac.CondicionComercial WHERE EmpresaId = {empresaId} AND VigenteHasta IS NULL;
            SELECT @id
            """, superadmin: false);
        if (emitir)
        {
            await using var cn = new SqlConnection(bd.CadenaPropietario);
            await cn.OpenAsync();
            await cn.ExecuteAsync("EXEC sp_set_session_context N'EsSuperAdmin', 1");
            await cn.ExecuteAsync("EXEC fac.usp_EmitirCuentaCobro @CuentaCobroId = @id, @UsuarioId = @u", new { id, u = bd.SuperadminId });
        }
        return id;
    }

    private async Task<Navegador> ClienteAsync()
    {
        var n = Fabrica(bd).NuevoNavegador();
        Assert.True(await IngresarAsync(n, _cliente!));
        return n;
    }

    private async Task<Navegador> SuperadminAsync()
    {
        var n = Fabrica(bd).NuevoNavegador();
        Assert.True(await IngresarAsync(n, await SuperadminListoAsync(bd)));
        return n;
    }

    [Fact]
    public async Task Un_anonimo_es_enviado_a_ingresar()
    {
        foreach (var ruta in new[] { "/facturacion", "/facturacion/1", "/facturacion/1/pdf", "/admin/facturacion", "/admin/facturacion/pagos", "/admin/facturacion/configuracion" })
        {
            var (estado, _, redir) = await Fabrica(bd).NuevoNavegador().IrAsync(ruta);
            Assert.Equal(HttpStatusCode.Redirect, estado);
            Assert.Contains("/ingresar", redir);
        }
    }

    [Fact]
    public async Task El_cliente_ve_su_estado_de_cuenta_pero_no_la_administracion()
    {
        var n = await ClienteAsync();
        var (estado, html, _) = await n.IrAsync("/facturacion");
        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("Facturación del servicio", html);
        Assert.Contains("Saldo pendiente", html);

        foreach (var ruta in new[] { "/admin/facturacion", "/admin/facturacion/pagos", "/admin/facturacion/configuracion" })
        {
            var (e, h, _) = await n.IrAsync(ruta);
            Assert.NotEqual(HttpStatusCode.OK, e);
            Assert.DoesNotContain("Cuentas retenidas", h);
        }
    }

    [Fact]
    public async Task El_cliente_no_puede_ejecutar_acciones_de_administracion_por_HTTP()
    {
        var n = await ClienteAsync();
        var (e, _, _) = await n.PostConTokenAsync("/facturacion", $"/admin/facturacion/cuentas/{_borrador}/emitir");
        Assert.NotEqual(HttpStatusCode.OK, e);
        Assert.Equal("BORRADOR", await BaseDeDatosFixture.EscalarAsync<string>(bd.CadenaEtl, $"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {_borrador}", superadmin: false));
    }

    [Fact]
    public async Task El_detalle_muestra_la_cuenta_propia_y_oculta_borradores_y_cuentas_ajenas()
    {
        var n = await ClienteAsync();
        var (e1, html, _) = await n.IrAsync($"/facturacion/{_emitida}");
        Assert.Equal(HttpStatusCode.OK, e1);
        Assert.Contains("Cuenta de cobro CC-", html);
        Assert.Contains("Canon mensual", html);

        foreach (var id in new[] { _borrador, _ajena })
        {
            var (_, h, _) = await n.IrAsync($"/facturacion/{id}");
            Assert.Contains("La cuenta de cobro no existe", h);
            Assert.DoesNotContain("Canon mensual", h);
        }
    }

    [Fact]
    public async Task El_pdf_se_descarga_sin_cache_queda_auditado_y_no_se_filtra_a_otras_empresas()
    {
        // El worker (aquí, el servicio directo) genera el PDF con la misma carpeta que usa el portal.
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddProcesamiento(bd.CadenaEtl);
        s.AddProcesamientoFacturacion();
        s.AddCorreoSaliente(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Facturacion:Almacen:Ruta"] = FabricaWeb.RutaFacturacion }).Build(), esDesarrollo: true);
        await using var sp = s.BuildServiceProvider();
        await sp.GetRequiredService<EnviosFacturacion>().GenerarPdfAsync(_emitida, default);

        var n = await ClienteAsync();
        var respuesta = await n.Cliente.GetAsync($"/facturacion/{_emitida}/pdf");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("application/pdf", respuesta.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", respuesta.Headers.CacheControl?.ToString());
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await respuesta.Content.ReadAsByteArrayAsync())[..4]));
        Assert.True(await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario,
            $"SELECT COUNT(*) FROM aud.Acceso WHERE Accion = 'DESCARGA_CUENTA_COBRO' AND Detalle = '{_emitida}' AND EmpresaId = {_empresa}", superadmin: true) >= 1);

        Assert.Equal(HttpStatusCode.NotFound, (await n.Cliente.GetAsync($"/facturacion/{_ajena}/pdf")).StatusCode);     // otra empresa
        Assert.Equal(HttpStatusCode.NotFound, (await n.Cliente.GetAsync($"/facturacion/{_borrador}/pdf")).StatusCode);   // borrador
        Assert.Equal(0, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario,
            $"SELECT COUNT(*) FROM aud.Acceso WHERE Accion = 'DESCARGA_CUENTA_COBRO' AND Detalle IN ('{_ajena}', '{_borrador}')", superadmin: true));
    }

    [Fact]
    public async Task El_superadmin_ve_la_administracion_y_puede_emitir_un_borrador()
    {
        var n = await SuperadminAsync();
        foreach (var (ruta, texto) in new[] { ("/admin/facturacion", "Cuentas retenidas"), ("/admin/facturacion/pagos", "Registrar un pago"), ("/admin/facturacion/configuracion", "Canon por plan") })
        {
            var (estado, html, _) = await n.IrAsync(ruta);
            Assert.Equal(HttpStatusCode.OK, estado);
            Assert.Contains(texto, html);
        }

        var (_, _, ok) = await n.PostConTokenAsync("/admin/facturacion", $"/admin/facturacion/cuentas/{_borrador}/emitir");
        Assert.Contains("ok=", ok);
        Assert.Equal("EMITIDA", await BaseDeDatosFixture.EscalarAsync<string>(bd.CadenaEtl, $"SELECT Estado FROM fac.CuentaCobro WHERE CuentaCobroId = {_borrador}", superadmin: false));
        // Ya no es borrador: segundo intento rechazado.
        var (_, _, error) = await n.PostConTokenAsync("/admin/facturacion", $"/admin/facturacion/cuentas/{_borrador}/emitir");
        Assert.Contains("error=", error);
    }

    [Fact]
    public async Task El_administrador_de_la_empresa_mantiene_sus_contactos_de_facturacion()
    {
        var n = await ClienteAsync();
        var (_, _, ok) = await n.PostConTokenAsync("/facturacion/contactos", "/facturacion/contactos", new Dictionary<string, string> { ["tipo"] = "FACTURACION", ["nombre"] = "Tesorería", ["email"] = "tesoreria@clinica-prueba.test" });
        Assert.Contains("ok=", ok);
        var (_, html, _) = await n.IrAsync("/facturacion/contactos");
        Assert.Contains("tesoreria@clinica-prueba.test", html);

        var (_, _, repetido) = await n.PostConTokenAsync("/facturacion/contactos", "/facturacion/contactos", new Dictionary<string, string> { ["tipo"] = "FACTURACION", ["nombre"] = "Otra", ["email"] = "TESORERIA@clinica-prueba.test" });
        Assert.Contains("error=", repetido);
        var (_, _, malo) = await n.PostConTokenAsync("/facturacion/contactos", "/facturacion/contactos", new Dictionary<string, string> { ["tipo"] = "FACTURACION", ["nombre"] = "X", ["email"] = "no-es-un-correo" });
        Assert.Contains("error=", malo);

        // El correo no queda en la auditoría (datos personales).
        Assert.Equal(0, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario,
            "SELECT COUNT(*) FROM aud.CambioAdministrativo WHERE Entidad = 'seg.EmpresaContacto' AND (ValorNuevoJson LIKE '%clinica-prueba%' OR ValorAnteriorJson LIKE '%clinica-prueba%')", superadmin: true));
        var id = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaEtl, $"SELECT ContactoId FROM seg.EmpresaContacto WHERE EmpresaId = {_empresa} AND Email = N'tesoreria@clinica-prueba.test'", superadmin: false);
        var (_, _, quitado) = await n.PostConTokenAsync("/facturacion/contactos", $"/facturacion/contactos/{id}/quitar");
        Assert.Contains("ok=", quitado);
    }
}
