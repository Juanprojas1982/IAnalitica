using System.Net;
using Dapper;
using IAnalitica.Integration.Tests.Infra;
using IAnalitica.Integration.Tests.Ingesta;
using Microsoft.Data.SqlClient;
using static IAnalitica.Integration.Tests.Web.Flujos;

namespace IAnalitica.Integration.Tests.Web;

/// <summary>Reglas de distribución de costos por el portal: definir, bases manuales rechazadas, reglas por defecto y recálculo.</summary>
[Collection(ColeccionBd.Nombre)]
public class CostosWebTests(BaseDeDatosFixture bd) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static Credenciales? _admin;
    private static Procesamiento? _datos;
    private static int _admId, _apoId, _prodId;

    public async Task InitializeAsync()
    {
        await Candado.WaitAsync();
        try
        {
            if (_admin is not null) return;
            var kit = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido("818" + Random.Shared.Next(100000, 999999), "Clínica con costos", modulos: new[] { "FINANCIERO" }));
            _datos = Procesamiento.ParaEmpresa(bd, kit.EmpresaId);
            await _datos.ProcesarAsync("centros-costo", "COMPLETA", new object[]
            {
                new { codigoOrigen = "ADM", nombre = "Administración" }, new { codigoOrigen = "APO", nombre = "<i>Lavandería</i>" }, new { codigoOrigen = "PRO", nombre = "Consulta" },
            });
            var ids = await _datos.ConsultarAsync<(string Codigo, int Id)>("SELECT CodigoOrigen, CentroCostoId FROM dim.CentroCosto WHERE EmpresaId = @e");
            (_admId, _apoId, _prodId) = (ids.First(x => x.Codigo == "ADM").Id, ids.First(x => x.Codigo == "APO").Id, ids.First(x => x.Codigo == "PRO").Id);
            var (clave, totp) = await PrimerIngresoAsync(Fabrica(bd).NuevoNavegador(), kit.UsuarioAdministrador, kit.ClaveInicial, NuevoUsuarioNavegadorClave, requiereDobleFactor: true);
            _admin = new Credenciales(kit.UsuarioAdministrador, clave, totp);
        }
        finally { Candado.Release(); }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Navegador> ComoAsync()
    {
        var n = Fabrica(bd).NuevoNavegador();
        Assert.True(await IngresarAsync(n, _admin!));
        return n;
    }

    private async Task ComoPropietarioAsync(string sql, object? p = null)
    {
        await using var cn = new SqlConnection(bd.CadenaPropietario);
        await cn.OpenAsync();
        await cn.ExecuteAsync("EXEC sp_set_session_context N'EsSuperAdmin', 1");
        await cn.ExecuteAsync(sql, p);
    }

    [Fact]
    public async Task Un_anonimo_es_enviado_a_ingresar_y_el_HTML_escapa_los_nombres()
    {
        var (estado, _, redir) = await Fabrica(bd).NuevoNavegador().IrAsync("/costos");
        Assert.Equal(HttpStatusCode.Redirect, estado);
        Assert.Contains("/ingresar", redir);

        var n = await ComoAsync();
        var crudo = await n.Cliente.GetStringAsync("/costos");
        Assert.DoesNotContain("<i>Lavandería</i>", crudo);
        Assert.Contains("&lt;i&gt;", crudo);
    }

    [Fact]
    public async Task Define_cambia_y_quita_una_regla_con_rastro_de_auditoria()
    {
        var n = await ComoAsync();
        var (_, _, ok) = await n.PostConTokenAsync("/costos", "/costos/regla", new Dictionary<string, string> { ["centro"] = _admId.ToString(), ["orden"] = "1", ["base"] = "COSTO_PERSONAL", ["desde"] = "202601" });
        Assert.Contains("ok=", ok);
        Assert.Equal("COSTO_PERSONAL", await _datos!.EscalarAsync<string>("SELECT BaseCodigo FROM cfg.CostoRegla WHERE EmpresaId = @e AND CentroCostoId = @c", new { e = _datos.EmpresaId, c = _admId }));
        var (_, html, _) = await n.IrAsync("/costos");
        Assert.Contains("Costo de personal del CC (nómina)", html);

        await n.PostConTokenAsync("/costos", "/costos/regla", new Dictionary<string, string> { ["centro"] = _admId.ToString(), ["orden"] = "2", ["base"] = "ACTIVIDADES", ["desde"] = "202601" });
        Assert.Equal(1, await _datos.ContarAsync("cfg.CostoRegla", $"CentroCostoId = {_admId}"));           // misma vigencia: se reemplaza

        var (_, _, quitado) = await n.PostConTokenAsync("/costos", "/costos/regla/quitar", new Dictionary<string, string> { ["centro"] = _admId.ToString(), ["desde"] = "202601" });
        Assert.Contains("ok=", quitado);
        Assert.Equal(0, await _datos.ContarAsync("cfg.CostoRegla", $"CentroCostoId = {_admId}"));
        Assert.True(await _datos.EscalarAsync<int>("SELECT COUNT(*) FROM aud.CambioAdministrativo WHERE EmpresaId = @e AND Entidad = 'cfg.CostoRegla'", new { e = _datos.EmpresaId }) >= 3);
    }

    [Fact]
    public async Task Rechaza_bases_manuales_bases_inexistentes_y_entradas_invalidas()
    {
        var n = await ComoAsync();
        var antes = await _datos!.ContarAsync("cfg.CostoRegla", $"CentroCostoId = {_apoId}");
        var (_, _, manual) = await n.PostConTokenAsync("/costos", "/costos/regla", new Dictionary<string, string> { ["centro"] = _apoId.ToString(), ["orden"] = "1", ["base"] = "M2", ["desde"] = "202601" });
        Assert.Contains("error=", manual);
        var (_, _, falsa) = await n.PostConTokenAsync("/costos", "/costos/regla", new Dictionary<string, string> { ["centro"] = _apoId.ToString(), ["orden"] = "1", ["base"] = "NO_EXISTE", ["desde"] = "202601" });
        Assert.Contains("error=", falsa);
        var (_, _, periodo) = await n.PostConTokenAsync("/costos", "/costos/regla", new Dictionary<string, string> { ["centro"] = _apoId.ToString(), ["orden"] = "1", ["base"] = "ACTIVIDADES", ["desde"] = "202613" });
        Assert.Contains("error=", periodo);
        var (_, _, ajeno) = await n.PostConTokenAsync("/costos", "/costos/regla", new Dictionary<string, string> { ["centro"] = "999999999", ["orden"] = "1", ["base"] = "ACTIVIDADES", ["desde"] = "202601" });
        Assert.Contains("error=", ajeno);
        Assert.Equal(antes, await _datos.ContarAsync("cfg.CostoRegla", $"CentroCostoId = {_apoId}"));      // ninguna entrada inválida cambió nada
    }

    [Fact]
    public async Task Las_reglas_por_defecto_exigen_centros_clasificados_y_no_pisan_lo_existente()
    {
        var n = await ComoAsync();
        var (_, _, sin) = await n.PostConTokenAsync("/costos", "/costos/defecto", new Dictionary<string, string> { ["desde"] = "202601" });
        Assert.Contains("error=", sin);                                         // todos son PRODUCTIVO hasta que se homologue el tipo de origen

        await ComoPropietarioAsync("UPDATE dim.CentroCosto SET TipoCC = CASE CodigoOrigen WHEN 'ADM' THEN 'ADMINISTRATIVO' WHEN 'APO' THEN 'APOYO' ELSE TipoCC END WHERE EmpresaId = @e", new { e = _datos!.EmpresaId });
        await ComoPropietarioAsync("INSERT cfg.CostoRegla (EmpresaId, CentroCostoId, Orden, BaseCodigo, VigenteDesde) VALUES (@e, @c, 7, 'INGRESO', 202512)", new { e = _datos.EmpresaId, c = _apoId });
        var (_, _, ok) = await n.PostConTokenAsync("/costos", "/costos/defecto", new Dictionary<string, string> { ["desde"] = "202601" });
        Assert.Contains("ok=", ok);
        Assert.Equal("COSTO_PERSONAL", await _datos.EscalarAsync<string>("SELECT BaseCodigo FROM cfg.CostoRegla WHERE EmpresaId = @e AND CentroCostoId = @c", new { e = _datos.EmpresaId, c = _admId }));
        Assert.Equal(1, await _datos.EscalarAsync<int>("SELECT CAST(Orden AS int) FROM cfg.CostoRegla WHERE EmpresaId = @e AND CentroCostoId = @c", new { e = _datos.EmpresaId, c = _admId }));
        Assert.Equal("INGRESO", await _datos.EscalarAsync<string>("SELECT BaseCodigo FROM cfg.CostoRegla WHERE EmpresaId = @e AND CentroCostoId = @c", new { e = _datos.EmpresaId, c = _apoId }));   // la existente se respeta
        Assert.Equal(0, await _datos.ContarAsync("cfg.CostoRegla", $"CentroCostoId = {_prodId}"));
    }

    [Fact]
    public async Task Recalcular_encola_un_trabajo_por_periodo_y_limita_el_rango()
    {
        var n = await ComoAsync();
        var (_, _, ok) = await n.PostConTokenAsync("/costos", "/costos/recalcular", new Dictionary<string, string> { ["desde"] = "202511", ["hasta"] = "202602" });
        Assert.Contains("ok=", ok);
        var periodos = await _datos!.ConsultarAsync<int>("SELECT CAST(JSON_VALUE(Payload, '$.periodo') AS int) FROM etl.Trabajo WHERE EmpresaId = @e AND Tipo = 'CALCULAR_COSTOS' AND CAST(JSON_VALUE(Payload, '$.periodo') AS int) BETWEEN 202511 AND 202602 ORDER BY 1");
        Assert.Equal(new[] { 202511, 202512, 202601, 202602 }, periodos.Distinct().ToArray());

        var (_, _, largo) = await n.PostConTokenAsync("/costos", "/costos/recalcular", new Dictionary<string, string> { ["desde"] = "202001", ["hasta"] = "202612" });
        Assert.Contains("error=", largo);
        var (_, _, invertido) = await n.PostConTokenAsync("/costos", "/costos/recalcular", new Dictionary<string, string> { ["desde"] = "202606", ["hasta"] = "202601" });
        Assert.Contains("error=", invertido);
    }
}
