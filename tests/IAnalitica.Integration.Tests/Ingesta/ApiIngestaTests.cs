using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>
/// API de ingesta de punta a punta (contrato 05): token real de OpenIddict, lotes gzip, cierre, cola, worker y consulta,
/// contra SQL Server real y con la RLS activa (la API corre con rol_app).
/// </summary>
[Collection(ColeccionBd.Nombre)]
public class ApiIngestaTests(BaseDeDatosFixture bd) : IAsyncLifetime
{
    private FabricaApi _api = null!;
    private DosEmpresas _dos = null!;
    private Procesamiento _a = null!;

    public async Task InitializeAsync()
    {
        _api = new FabricaApi(bd);
        _dos = await bd.ObtenerDosEmpresasAsync();
        _a = Procesamiento.ParaEmpresa(bd, _dos.A.EmpresaId);
    }

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _api.DisposeAsync();
    }

    /// <summary>DML de preparación con el contexto de superadmin (rol_etl solo lee seg.*).</summary>
    private async Task SuperAsync(string sql)
    {
        await using var c = await Escenarios.AbrirAsync(bd.CadenaPropietario, new IAnalitica.Infrastructure.Persistencia.ContextoBd(null, null, true, false, true));
        await Escenarios.Ejecutar(c, sql);
    }

    private Task<HttpClient> ComoAAsync() => _api.ClienteAutenticadoAsync(_dos.A.ClientId, _dos.A.ClientSecret);
    private Task<HttpClient> ComoBAsync() => _api.ClienteAutenticadoAsync(_dos.B.ClientId, _dos.B.ClientSecret);

    /// <summary>Corre el worker hasta vaciar la cola de cargas (la cola es compartida con las demás pruebas).</summary>
    private async Task ProcesarColaAsync()
    {
        while (await _a.Despachador.EjecutarUnoAsync(TiposTrabajo.ProcesarCarga, CancellationToken.None)) { }
    }

    private static object Sede(string codigo, string nombre) => new { codigoOrigen = codigo, nombre, activa = true };

    // ---------- autenticación ----------

    [Fact]
    public async Task El_token_trae_la_empresa_del_cliente_y_vence_en_60_minutos()
    {
        var http = _api.Cliente();
        var token = await FabricaApi.ObtenerTokenAsync(http, _dos.A.ClientId, _dos.A.ClientSecret);
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(_dos.A.EmpresaId.ToString(), jwt.Claims.Single(c => c.Type == "empresa_id").Value);
        Assert.InRange((jwt.ValidTo - DateTime.UtcNow).TotalMinutes, 59, 60.1);
    }

    [Fact]
    public async Task Un_secreto_incorrecto_no_obtiene_token()
    {
        var r = await _api.Cliente().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = _dos.A.ClientId, ["client_secret"] = "incorrecto", ["scope"] = "ingesta",
        }));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);   // OpenIddict responde invalid_client con 401
        Assert.Equal("invalid_client", (await r.JsonAsync()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Sin_token_o_con_token_basura_la_API_responde_401()
    {
        var http = _api.Cliente();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/v1/estado")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "no-es-un-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/v1/estado")).StatusCode);
    }

    [Fact]
    public async Task Un_cliente_desactivado_no_obtiene_token_nuevo()
    {
        var kit = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido("900500500", "Empresa con cliente apagado"));
        await SuperAsync($"UPDATE seg.ApiClienteEmpresa SET Activo = 0 WHERE ClientId = '{kit.ClientId}'");
        var r = await _api.Cliente().PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = kit.ClientId, ["client_secret"] = kit.ClientSecret, ["scope"] = "ingesta",
        }));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("invalid_client", (await r.JsonAsync()).GetProperty("error").GetString());
    }

    // ---------- flujo completo ----------

    [Fact]
    public async Task Flujo_completo_de_un_catalogo_hasta_la_marca_de_agua()
    {
        var http = await ComoAAsync();
        var id = await http.IniciarAsync("sedes", "COMPLETA");
        var lote = await http.PostLoteAsync(id, 1, new[] { Sede("S1", "Principal"), Sede("S2", "Norte") });
        Assert.Equal(HttpStatusCode.Accepted, lote.StatusCode);
        Assert.Equal(2, (await lote.JsonAsync()).GetProperty("filasRecibidas").GetInt32());

        Assert.Equal(HttpStatusCode.Accepted, (await http.CerrarAsync(id, 1, 2)).StatusCode);
        Assert.Equal("CERRADA", (await (await http.GetAsync($"/v1/cargas/{id}")).JsonAsync()).GetProperty("estado").GetString());

        await ProcesarColaAsync();
        var estado = await (await http.GetAsync($"/v1/cargas/{id}")).JsonAsync();
        Assert.Equal("OK", estado.GetProperty("estado").GetString());
        Assert.Equal(2, estado.GetProperty("filasInsertadas").GetInt32());
        Assert.Equal(2, await _a.ContarAsync("dim.Sede", "CodigoOrigen IN ('S1','S2')"));

        var marca = (await (await http.GetAsync("/v1/estado")).JsonAsync()).GetProperty("entidades").EnumerateArray().Single(e => e.GetProperty("entidad").GetString() == "sedes");
        Assert.Equal(id, marca.GetProperty("cargaId").GetInt64());
    }

    [Fact]
    public async Task Un_lote_sin_comprimir_tambien_se_acepta_y_reenviar_el_mismo_numero_es_idempotente()
    {
        var http = await ComoAAsync();
        var id = await http.IniciarAsync("lineas-servicio", "COMPLETA");
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostLoteAsync(id, 1, new[] { new { codigoOrigen = "L1", nombre = "A" } }, gzip: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostLoteAsync(id, 1, new[] { new { codigoOrigen = "L1", nombre = "A" } })).StatusCode);   // reintento
        Assert.Equal(1, await _a.EscalarAsync<int>("SELECT COUNT(*) FROM stg.Payload WHERE CargaId = @id", new { id }));
        Assert.Equal(HttpStatusCode.Accepted, (await http.CerrarAsync(id, 1, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await http.CerrarAsync(id, 1, 1)).StatusCode);   // cerrar también es idempotente
        Assert.Equal(1, await _a.EscalarAsync<int>("SELECT COUNT(*) FROM etl.Trabajo WHERE ClaveExclusion = @c", new { c = $"EMP:{_dos.A.EmpresaId}|CARGA:{id}" }));
        await ProcesarColaAsync();
    }

    [Fact]
    public async Task Una_carga_identica_a_otra_ya_procesada_responde_409_y_cuenta_como_marca_de_agua()
    {
        var http = await ComoAAsync();
        var filas = new[] { new { codigoOrigen = "ESP-D1", nombre = "Pediatría" } };
        var id1 = await http.IniciarAsync("especialidades", "COMPLETA");
        await http.PostLoteAsync(id1, 1, filas);
        await http.CerrarAsync(id1, 1, 1);
        await ProcesarColaAsync();

        var id2 = await http.IniciarAsync("especialidades", "COMPLETA");
        await http.PostLoteAsync(id2, 1, filas);
        var cierre = await http.CerrarAsync(id2, 1, 1);
        Assert.Equal(HttpStatusCode.Conflict, cierre.StatusCode);
        Assert.Equal("CARGA_DUPLICADA", await cierre.CodigoErrorAsync());
        Assert.Equal("RECHAZADA", (await (await http.GetAsync($"/v1/cargas/{id2}")).JsonAsync()).GetProperty("estado").GetString());
        Assert.Equal(0, await _a.EscalarAsync<int>("SELECT COUNT(*) FROM stg.Payload WHERE CargaId = @id", new { id = id2 }));

        // cerrar de nuevo repite el mismo resultado
        Assert.Equal(HttpStatusCode.Conflict, (await http.CerrarAsync(id2, 1, 1)).StatusCode);

        var marca = (await (await http.GetAsync("/v1/estado")).JsonAsync()).GetProperty("entidades").EnumerateArray().Single(e => e.GetProperty("entidad").GetString() == "especialidades");
        Assert.Equal(id2, marca.GetProperty("cargaId").GetInt64());
    }

    [Fact]
    public async Task La_suma_de_control_o_los_totales_que_no_cuadran_rechazan_la_carga_completa()
    {
        var http = await ComoAAsync();
        var filas = new object[]
        {
            new { sourceKey = "SC-1", movimientoSourceKey = "SCM", fecha = "2026-01-10", tipoComprobanteCodigo = "T1", numeroComprobante = "1", cuentaCodigo = "130505", debito = 100m, credito = 0m, estado = 1 },
            new { sourceKey = "SC-2", movimientoSourceKey = "SCM", fecha = "2026-01-10", tipoComprobanteCodigo = "T1", numeroComprobante = "1", cuentaCodigo = "413501", debito = 0m, credito = 100m, estado = 1 },
        };
        var desde = new DateOnly(2026, 1, 1); var hasta = new DateOnly(2026, 1, 31);

        var id = await http.IniciarAsync("asientos", "VENTANA", desde, hasta);
        await http.PostLoteAsync(id, 1, filas);
        var mal = await http.CerrarAsync(id, 1, 2, suma: 199m);          // la correcta es 200
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mal.StatusCode);
        Assert.Equal("CONTROL_NO_CUADRA", await mal.CodigoErrorAsync());
        Assert.Equal("RECHAZADA", (await (await http.GetAsync($"/v1/cargas/{id}")).JsonAsync()).GetProperty("estado").GetString());
        Assert.Equal(0, await _a.EscalarAsync<int>("SELECT COUNT(*) FROM etl.Trabajo WHERE ClaveExclusion = @c", new { c = $"EMP:{_dos.A.EmpresaId}|CARGA:{id}" }));

        var id2 = await http.IniciarAsync("asientos", "VENTANA", desde, hasta);
        await http.PostLoteAsync(id2, 1, filas);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await http.CerrarAsync(id2, 2, 2, suma: 200m)).StatusCode);   // declara 2 lotes y llegó 1

        var id3 = await http.IniciarAsync("asientos", "VENTANA", desde, hasta);
        await http.PostLoteAsync(id3, 1, filas);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await http.CerrarAsync(id3, 1, 5, suma: 200m)).StatusCode);   // declara 5 filas y llegaron 2

        var id4 = await http.IniciarAsync("asientos", "VENTANA", desde, hasta);
        await http.PostLoteAsync(id4, 2, filas);                                                                           // hueco: falta el lote 1
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await http.CerrarAsync(id4, 1, 2, suma: 200m)).StatusCode);

        var id5 = await http.IniciarAsync("asientos", "VENTANA", desde, hasta);
        await http.PostLoteAsync(id5, 1, filas);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await http.CerrarAsync(id5, 1, 2, suma: null)).StatusCode);      // la entidad exige sumaControl
    }

    [Fact]
    public async Task Asientos_de_punta_a_punta_encadenan_el_calculo_de_cartera()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        await p.CargarCatalogosBaseAsync();
        var kit = await ObtenerKitDeAsync(p.EmpresaId);
        var http = await _api.ClienteAutenticadoAsync(kit.ClientId, kit.ClientSecret);

        var filas = new[]
        {
            Procesamiento.Linea("API-1", "AM1", "2026-01-10", "130505", 1000, 0, "P1", numDoc: "F-API"),
            Procesamiento.Linea("API-2", "AM1", "2026-01-10", "413501", 0, 1000),
        };
        var id = await http.IniciarAsync("asientos", "VENTANA", new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await http.PostLoteAsync(id, 1, filas);
        Assert.Equal(HttpStatusCode.Accepted, (await http.CerrarAsync(id, 1, 2, suma: 2000m)).StatusCode);

        while (await p.Despachador.EjecutarUnoAsync(TiposTrabajo.ProcesarCarga, CancellationToken.None)) { }
        Assert.Equal("OK", (await p.ResumenAsync(id)).Estado);
        Assert.Equal(1, await p.EscalarAsync<int>("SELECT COUNT(*) FROM etl.Trabajo WHERE ClaveExclusion = @c", new { c = $"EMP:{p.EmpresaId}|CARTERA" }));   // cartera encolada

        while (await p.Despachador.EjecutarUnoAsync(TiposTrabajo.CalcularCartera, CancellationToken.None)) { }
        Assert.Equal(1000m, await p.EscalarAsync<decimal>("SELECT SUM(Saldo) FROM fact.CarteraDocumento WHERE EmpresaId = @e AND TipoCartera = 'CXC'", new { e = p.EmpresaId }));
    }

    [Fact]
    public async Task Los_rechazos_por_fila_se_consultan_en_el_endpoint_de_rechazos()
    {
        var http = await ComoAAsync();
        var id = await http.IniciarAsync("cuentas", "COMPLETA");
        await http.PostLoteAsync(id, 1, new object[]
        {
            new { codigo = "RJ1", nombre = "Buena", nivel = 4, naturaleza = "D" },
            new { codigo = "RJ2", nombre = "Mala", nivel = 4, naturaleza = "Z" },
        });
        await http.CerrarAsync(id, 1, 2);
        await ProcesarColaAsync();

        var estado = await (await http.GetAsync($"/v1/cargas/{id}")).JsonAsync();
        Assert.Equal(("OK", 1, 1), (estado.GetProperty("estado").GetString(), estado.GetProperty("filasInsertadas").GetInt32(), estado.GetProperty("filasRechazadas").GetInt32()));
        var rechazos = (await (await http.GetAsync($"/v1/cargas/{id}/rechazos")).JsonAsync()).EnumerateArray().ToList();
        var rechazo = Assert.Single(rechazos);
        Assert.Equal(("RJ2", "VALOR_INVALIDO:naturaleza"), (rechazo.GetProperty("sourceKey").GetString(), rechazo.GetProperty("motivo").GetString()));
    }

    // ---------- validaciones del contrato ----------

    [Fact]
    public async Task Rechaza_entidades_modos_y_ventanas_invalidos_con_400_y_codigo_estable()
    {
        var http = await ComoAAsync();
        var casos = new (object Cuerpo, string Codigo)[]
        {
            (new { entidad = "recibos", modo = "VENTANA", ventanaDesde = "2026-01-01", ventanaHasta = "2026-01-31" }, "ENTIDAD_NO_SOPORTADA"),
            (new { entidad = "configuracion", modo = "VENTANA", ventanaDesde = "2026-01-01", ventanaHasta = "2026-01-31" }, "CONTRATO_INVALIDO"),
            (new { entidad = "asientos", modo = "COMPLETA" }, "CONTRATO_INVALIDO"),
            (new { entidad = "asientos", modo = "VENTANA" }, "CONTRATO_INVALIDO"),
            (new { entidad = "asientos", modo = "VENTANA", ventanaDesde = "2026-02-01", ventanaHasta = "2026-01-01" }, "CONTRATO_INVALIDO"),
        };
        foreach (var (cuerpo, codigo) in casos)
        {
            var r = await http.PostAsJsonAsync("/v1/cargas", cuerpo);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal(codigo, await r.CodigoErrorAsync());
        }
    }

    [Fact]
    public async Task Un_lote_de_mas_de_5000_filas_responde_413_y_un_cuerpo_invalido_400()
    {
        var http = await ComoAAsync();
        var id = await http.IniciarAsync("lineas-servicio", "COMPLETA");
        var grande = await http.PostLoteAsync(id, 1, Enumerable.Range(1, 5001).Select(i => (object)new { codigoOrigen = "G" + i, nombre = "x" }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, grande.StatusCode);
        Assert.Equal("LOTE_GRANDE", await grande.CodigoErrorAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync($"/v1/cargas/{id}/lotes/1", new StringContent("{no es json", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync($"/v1/cargas/{id}/lotes/1", new StringContent("{\"otra\":[]}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync($"/v1/cargas/{id}/lotes/1", new StringContent("{\"filas\":[1,2]}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostLoteAsync(id, 0, new[] { new { codigoOrigen = "G", nombre = "x" } })).StatusCode);   // lote 0
    }

    [Fact]
    public async Task Un_tercero_paciente_con_nombre_rechaza_toda_la_carga_y_el_nombre_nunca_se_guarda()
    {
        var http = await ComoAAsync();
        var id = await http.IniciarAsync("terceros", "INCREMENTAL");
        var r = await http.PostLoteAsync(id, 1, new object[]
        {
            new { codigoOrigen = "OK1", identificacion = "900", esPaciente = false, nombre = "EPS" },
            new { codigoOrigen = "PAC1", identificacion = "HASH123", esPaciente = true, nombre = "María Gómez" },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("VALIDACION_NEGOCIO", await r.CodigoErrorAsync());
        Assert.DoesNotContain("María", await r.Content.ReadAsStringAsync());   // ni siquiera se devuelve en la respuesta
        Assert.Equal("RECHAZADA", (await (await http.GetAsync($"/v1/cargas/{id}")).JsonAsync()).GetProperty("estado").GetString());
        Assert.Equal(0, await _a.EscalarAsync<int>("SELECT COUNT(*) FROM stg.Payload WHERE CargaId = @id", new { id }));
        // y la carga rechazada ya no recibe más lotes
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostLoteAsync(id, 2, new[] { new { codigoOrigen = "X", identificacion = "1", esPaciente = false } })).StatusCode);
    }

    [Fact]
    public async Task Una_ventana_sobre_un_periodo_cerrado_en_la_plataforma_responde_409()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        var kit = await ObtenerKitDeAsync(p.EmpresaId);
        await p.EjecutarAsync("INSERT etl.PeriodoCierre (EmpresaId, PeriodoKey, Estado, CerradoEn) VALUES (@e, 202601, 'CERRADO', SYSUTCDATETIME())", new { e = p.EmpresaId });
        var http = await _api.ClienteAutenticadoAsync(kit.ClientId, kit.ClientSecret);

        var r = await http.PostAsJsonAsync("/v1/cargas", new { entidad = "asientos", modo = "VENTANA", ventanaDesde = "2025-12-15", ventanaHasta = "2026-01-05" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("PERIODO_CERRADO", await r.CodigoErrorAsync());

        var ok = await http.PostAsJsonAsync("/v1/cargas", new { entidad = "asientos", modo = "VENTANA", ventanaDesde = "2026-02-01", ventanaHasta = "2026-02-28" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    // ---------- aislamiento entre empresas ----------

    [Fact]
    public async Task El_EmpresaId_del_cuerpo_se_ignora_y_una_empresa_no_ve_ni_toca_las_cargas_de_otra()
    {
        var a = await ComoAAsync();
        var b = await ComoBAsync();

        // B intenta hacerse pasar por A en el cuerpo: la carga queda en B igual
        var r = await b.PostAsJsonAsync("/v1/cargas", new { entidad = "sedes", modo = "COMPLETA", empresaId = _dos.A.EmpresaId });
        var idB = (await r.JsonAsync()).GetProperty("cargaId").GetInt64();
        Assert.Equal(_dos.B.EmpresaId, await _a.EscalarAsync<int>("SELECT EmpresaId FROM etl.Carga WHERE CargaId = @idB", new { idB }));

        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/v1/cargas/{idB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/v1/cargas/{idB}/rechazos")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.PostLoteAsync(idB, 1, new[] { Sede("X", "x") })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.CerrarAsync(idB, 1, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync($"/v1/cargas/{idB}")).StatusCode);
    }

    // ---------- límites ----------

    [Fact]
    public async Task El_limite_por_minuto_del_cliente_responde_429_con_Retry_After()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        var kit = await ObtenerKitDeAsync(p.EmpresaId);
        await SuperAsync($"UPDATE seg.ApiClienteEmpresa SET LimitePorMinuto = 3 WHERE ClientId = '{kit.ClientId}'");
        var http = await _api.ClienteAutenticadoAsync(kit.ClientId, kit.ClientSecret);

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/v1/estado")).StatusCode);
        var limitado = await http.GetAsync("/v1/estado");
        Assert.Equal(HttpStatusCode.TooManyRequests, limitado.StatusCode);
        Assert.Equal("60", limitado.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
        Assert.Equal("LIMITE_EXCEDIDO", await limitado.CodigoErrorAsync());

        // el límite es por cliente: otro cliente sigue pasando
        var otro = await ComoAAsync();
        Assert.Equal(HttpStatusCode.OK, (await otro.GetAsync("/v1/estado")).StatusCode);
    }

    private async Task<(string ClientId, string ClientSecret)> ObtenerKitDeAsync(int empresaId)
    {
        // El secreto solo se muestra al crear la empresa; para empresas de prueba se crea un cliente propio con el caso de uso real.
        var secreto = "Secreto-" + Guid.NewGuid().ToString("N");
        var clientId = "prueba-" + Guid.NewGuid().ToString("N")[..12];
        await using var sp = bd.Construir(bd.CadenaApp, new SesionPrueba(bd.SuperadminId, null, true, "admin.empresas"));
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IAnalitica.Infrastructure.Persistencia.IContextoBdAccesor>()
            .Establecer(IAnalitica.Infrastructure.Persistencia.ContextoBd.DeSuperadmin(bd.SuperadminId));
        await alcance.ServiceProvider.GetRequiredService<IGestorCredencialesApi>().CrearAsync(empresaId, clientId, secreto, "cliente de prueba", CancellationToken.None);
        return (clientId, secreto);
    }
}
