using System.Net;
using Dapper;
using IAnalitica.Integration.Tests.Infra;
using IAnalitica.Integration.Tests.Ingesta;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using static IAnalitica.Integration.Tests.Web.Flujos;

namespace IAnalitica.Integration.Tests.Web;

/// <summary>El asistente por el portal con un modelo simulado: formulario, respuesta con fuentes, errores legibles y escape del HTML.</summary>
[Collection(ColeccionBd.Nombre)]
public class AsistenteWebTests(BaseDeDatosFixture bd) : IAsyncLifetime
{
    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static Credenciales? _admin;
    private static Credenciales? _sinIa;
    private static Procesamiento? _datos;

    public async Task InitializeAsync()
    {
        await Candado.WaitAsync();
        try
        {
            if (_admin is not null) return;
            var kit = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido("819" + Random.Shared.Next(100000, 999999), "Clínica con asistente", modulos: new[] { "FINANCIERO", "IA" }));
            _datos = Procesamiento.ParaEmpresa(bd, kit.EmpresaId);
            await DatosDashboards.CargarAsync(_datos);
            await using (var cn = new SqlConnection(bd.CadenaPropietario))
            {
                await cn.OpenAsync();
                await cn.ExecuteAsync("EXEC sp_set_session_context N'EsSuperAdmin', 1");
                await cn.ExecuteAsync("UPDATE ia.EmpresaConfig SET Habilitado = 1, CupoIncluidoCop = 100000 WHERE EmpresaId = @e", new { e = kit.EmpresaId });
                await cn.ExecuteAsync("INSERT ia.Tarifa (EmpresaId, VigenteDesde, ValorMTokEntradaCop, ValorMTokSalidaCop, ValorMTokCacheLecturaCop) VALUES (@e, '2026-01-01', 1000, 5000, 100)", new { e = kit.EmpresaId });
            }
            var (clave, totp) = await PrimerIngresoAsync(Fabrica(bd).NuevoNavegador(), kit.UsuarioAdministrador, kit.ClaveInicial, NuevoUsuarioNavegadorClave, requiereDobleFactor: true);
            _admin = new Credenciales(kit.UsuarioAdministrador, clave, totp);

            var kit2 = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido("820" + Random.Shared.Next(100000, 999999), "Clínica sin asistente", modulos: new[] { "FINANCIERO" }));
            var (clave2, totp2) = await PrimerIngresoAsync(Fabrica(bd).NuevoNavegador(), kit2.UsuarioAdministrador, kit2.ClaveInicial, NuevoUsuarioNavegadorClave, requiereDobleFactor: true);
            _sinIa = new Credenciales(kit2.UsuarioAdministrador, clave2, totp2);
        }
        finally { Candado.Release(); }
    }

    public Task DisposeAsync() { AsistenteDePrueba.Cliente = null; return Task.CompletedTask; }

    private async Task<Navegador> ComoAsync(Credenciales c)
    {
        var n = Fabrica(bd).NuevoNavegador();
        Assert.True(await IngresarAsync(n, c));
        return n;
    }

    private static ChatResponse Texto(string texto) => new(new ChatMessage(ChatRole.Assistant, texto)) { Usage = new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 100 } };

    [Fact]
    public async Task Un_anonimo_es_enviado_a_ingresar()
    {
        var (estado, _, redir) = await Fabrica(bd).NuevoNavegador().IrAsync("/asistente");
        Assert.Equal(HttpStatusCode.Redirect, estado);
        Assert.Contains("/ingresar", redir);
    }

    [Fact]
    public async Task Pregunta_y_respuesta_por_el_formulario_con_valor_para_el_administrador_y_html_escapado()
    {
        AsistenteDePrueba.Cliente = new ClienteGuionado(
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "consultar_metrica",
                new Dictionary<string, object?> { ["metrica"] = "financiero.ingresos", ["periodo_desde"] = 202601, ["periodo_hasta"] = 202602 })])) { FinishReason = ChatFinishReason.ToolCalls, Usage = new UsageDetails { InputTokenCount = 800, OutputTokenCount = 40 } },
            _ => Texto("Los ingresos fueron $ 3.000. <img src=x onerror=alert(1)>"));
        var n = await ComoAsync(_admin!);
        var (estado, html, _) = await n.EnviarFormularioAsync("/asistente", "asistente", new Dictionary<string, string> { ["Entrada.Pregunta"] = "¿Cuánto ingresó en enero y febrero?" });
        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("$ 3.000", html);
        Assert.Contains("Fuentes (1)", html);
        Assert.Contains("Ingresos", html);
        Assert.Contains("Valor de esta respuesta", html);                    // el administrador ve el valor
        Assert.DoesNotContain("<img src=x", html);                           // la etiqueta se quita antes de llegar a la página
    }

    [Fact]
    public async Task Un_error_del_proveedor_se_muestra_sin_detalles_internos()
    {
        AsistenteDePrueba.Cliente = new ClienteGuionado(_ => throw new HttpRequestException("secret-detail-503"));
        var n = await ComoAsync(_admin!);
        var (_, html, _) = await n.EnviarFormularioAsync("/asistente", "asistente", new Dictionary<string, string> { ["Entrada.Pregunta"] = "Hola" });
        Assert.Contains("no pudo responder", html);
        Assert.DoesNotContain("secret-detail", html);
    }

    [Fact]
    public async Task Sin_el_modulo_de_IA_explica_por_que_no_esta_disponible()
    {
        AsistenteDePrueba.Cliente = new ClienteGuionado(_ => Texto("no debería llegar"));
        var n = await ComoAsync(_sinIa!);
        var (_, html, _) = await n.EnviarFormularioAsync("/asistente", "asistente", new Dictionary<string, string> { ["Entrada.Pregunta"] = "Hola" });
        Assert.Contains("no está activo", html);
    }

    [Fact]
    public async Task Una_pregunta_vacia_no_llama_al_modelo()
    {
        var cliente = new ClienteGuionado(_ => Texto("no debería llegar"));
        AsistenteDePrueba.Cliente = cliente;
        var n = await ComoAsync(_admin!);
        var (estado, html, _) = await n.EnviarFormularioAsync("/asistente", "asistente", new Dictionary<string, string> { ["Entrada.Pregunta"] = "" });
        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("Escriba su pregunta", html);
        Assert.Equal(0, cliente.Llamadas);
    }
}
