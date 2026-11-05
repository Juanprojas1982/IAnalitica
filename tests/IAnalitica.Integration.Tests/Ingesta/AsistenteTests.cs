using System.Text.Json;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Asistente;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Dapper;
using Microsoft.Data.SqlClient;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>
/// Asistente IA interno (spec 07; 24 §5.7) con un modelo simulado: sin proveedor real ni costo. Verifica el flujo completo contra SQL Server:
/// reserva y liquidación de cuota, herramientas sobre el catálogo con RLS, seudonimización, sanitización y los topes.
/// </summary>
[Collection(ColeccionBd.Nombre)]
public class AsistenteTests(BaseDeDatosFixture bd)
{
    private const string Permisos = "ia.usar";

    private sealed class FabricaGuionada(IChatClient? cliente) : IAsistenteChatClientFactory
    {
        public string? ProveedorPedido { get; private set; }
        public string? ModeloPedido { get; private set; }
        public IChatClient? Crear(string proveedorCodigo, string modeloCodigo) { ProveedorPedido = proveedorCodigo; ModeloPedido = modeloCodigo; return cliente; }
    }

    private static ChatResponse Texto(string texto, long entrada = 1000, long salida = 100, long cache = 0)
        => new(new ChatMessage(ChatRole.Assistant, texto)) { Usage = new UsageDetails { InputTokenCount = entrada, OutputTokenCount = salida, CachedInputTokenCount = cache } };

    private static ChatResponse Llamar(string herramienta, object argumentos, long entrada = 1000, long salida = 50)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(argumentos))!;
        // Los argumentos llegan como JsonElement, igual que desde un proveedor real
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c" + Guid.NewGuid().ToString("N")[..6], herramienta, args)]))
        { Usage = new UsageDetails { InputTokenCount = entrada, OutputTokenCount = salida }, FinishReason = ChatFinishReason.ToolCalls };
    }

    /// <summary>Empresa con IA y FINANCIERO activos, datos del escenario de dashboards y la configuración del asistente (plan, tarifa, cupo).</summary>
    private async Task<Procesamiento> EmpresaAsync(decimal cupoCop = 100_000m, Action<Dictionary<string, object?>>? ajustar = null, params string[] modulosExtra)
    {
        var p = await Procesamiento.NuevaEmpresaAsync(bd, new[] { "FINANCIERO", "IA" }.Concat(modulosExtra).ToArray());
        await DatosDashboards.CargarAsync(p);
        var cfg = new Dictionary<string, object?> { ["e"] = p.EmpresaId, ["habilitado"] = 1, ["guardar"] = 1, ["cupo"] = cupoCop, ["maxIter"] = 5, ["maxSalida"] = 2000, ["topeUsd"] = 100m, ["topeUsuario"] = null };
        ajustar?.Invoke(cfg);
        await using var cn = new SqlConnection(bd.CadenaPropietario);
        await cn.OpenAsync();
        await cn.ExecuteAsync("EXEC sp_set_session_context N'EsSuperAdmin', 1");
        // El alta de la empresa ya crea su configuración (deshabilitada, con el modelo por defecto): aquí se habilita y se le pone cupo
        await cn.ExecuteAsync("""
            UPDATE ia.EmpresaConfig SET Habilitado = @habilitado, CupoIncluidoCop = @cupo, MaxTokensSalidaPorLlamada = @maxSalida, MaxIteracionesHerramientas = @maxIter,
                   TopeCostoProveedorUsd = @topeUsd, TopeUsuarioDiarioCop = @topeUsuario, GuardarTextoConversacion = @guardar
            WHERE EmpresaId = @e
            """, new { e = cfg["e"], habilitado = cfg["habilitado"], cupo = cfg["cupo"], maxSalida = cfg["maxSalida"], maxIter = cfg["maxIter"], topeUsd = cfg["topeUsd"], topeUsuario = cfg["topeUsuario"], guardar = cfg["guardar"] });
        // Tarifa de venta propia de la empresa (la global es decisión comercial pendiente): 1.000 COP por 1M de entrada, 5.000 de salida, 100 de caché
        await cn.ExecuteAsync("INSERT ia.Tarifa (EmpresaId, VigenteDesde, ValorMTokEntradaCop, ValorMTokSalidaCop, ValorMTokCacheLecturaCop) VALUES (@e, '2026-01-01', 1000, 5000, 100)", new { e = p.EmpresaId });
        return p;
    }

    private async Task<(Result<RespuestaAsistente> R, ClienteGuionado Modelo)> PreguntarAsync(Procesamiento p, ClienteGuionado modelo, string pregunta = "¿Cuánto facturamos?", Guid? conversacion = null, int? usuario = null, params string[] permisos)
    {
        var usuarioId = usuario ?? bd.SuperadminId;
        var sesion = new SesionPrueba(usuarioId, p.EmpresaId, false, permisos.Length == 0 ? new[] { Permisos, "dash.financiero" } : permisos);
        await using var sp = bd.Construir(bd.CadenaApp, sesion, s => s.AddSingleton<IAsistenteChatClientFactory>(new FabricaGuionada(modelo)));
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeEmpresa(p.EmpresaId, usuarioId));
        var r = await alcance.ServiceProvider.GetRequiredService<ISender>().Send(new EnviarMensajeAsistenteCommand(conversacion, pregunta));
        return (r, modelo);
    }

    private async Task<int> OtroUsuarioAsync()
    {
        await using var cn = new SqlConnection(bd.CadenaPropietario);
        await cn.OpenAsync();
        await cn.ExecuteAsync("EXEC sp_set_session_context N'EsSuperAdmin', 1");
        return await cn.ExecuteScalarAsync<int>("SELECT MIN(Id) FROM seg.Usuario WHERE Id <> @sa", new { sa = bd.SuperadminId });
    }

    [Fact]
    public async Task Responde_con_una_herramienta_valora_el_consumo_y_deja_la_cuota_liquidada()
    {
        var p = await EmpresaAsync();
        var modelo = new ClienteGuionado(
            _ => Llamar("consultar_metrica", new { metrica = "financiero.ingresos", periodo_desde = 202601, periodo_hasta = 202602 }, entrada: 2000, salida: 40),
            mensajes => Texto("Entre 2026-01 y 2026-02 los ingresos fueron de $ 3.000. [Ver](https://malo.example/x) <script>alert(1)</script>", entrada: 3000, salida: 200));
        var (r, _) = await PreguntarAsync(p, modelo);

        Assert.True(r.EsExitoso, string.Join("; ", r.Errores.Select(e => e.Mensaje)));
        Assert.Contains("$ 3.000", r.Valor.Texto);
        Assert.DoesNotContain("<script>", r.Valor.Texto);
        Assert.DoesNotContain("malo.example", r.Valor.Texto);                           // el enlace externo se quita y queda su texto
        Assert.Single(r.Valor.Fuentes);
        Assert.Equal("financiero.ingresos", r.Valor.Fuentes[0].Metrica);
        Assert.Contains("2026-01", r.Valor.Fuentes[0].Parametros);

        // La herramienta devolvió los ingresos de las dos llamadas (datos, no instrucciones) al modelo
        var resultado = modelo.Recibidos[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result!.ToString()!;
        Assert.Contains("\"valor\":3000", resultado);

        // Valor: (2.000+3.000) × 1.000/1M + (40+200) × 5.000/1M = 5 + 1,2 = 6,2 COP
        Assert.Equal(6.2m, r.Valor.ValorCop);
        Assert.False(r.Valor.EnExcedente);
        var consumo = await p.ConsultarAsync<(decimal Valor, decimal Reservado, long Entrada, long Salida)>("SELECT ValorConsumoCop, ReservadoCop, TokensEntrada, TokensSalida FROM ia.ConsumoPeriodo WHERE EmpresaId = @e");
        Assert.Equal((6.2m, 0m, 5000L, 240L), consumo.Single());                         // la reserva se liberó y el consumo real quedó registrado
        Assert.Equal(2, await p.ContarAsync("ia.UsoLlamada"));
        Assert.Equal(0, await p.ContarAsync("ia.Reserva"));
    }

    [Fact]
    public async Task No_llama_al_proveedor_si_la_cuota_no_alcanza()
    {
        var p = await EmpresaAsync(cupoCop: 0.01m);                                      // la reserva máxima de una pregunta cuesta más que esto
        var modelo = new ClienteGuionado(_ => Texto("no debería llegar aquí"));
        var (r, _) = await PreguntarAsync(p, modelo);
        Assert.False(r.EsExitoso);
        Assert.Equal("CUPO_AGOTADO", r.Errores[0].Codigo);
        Assert.Equal(0, modelo.Llamadas);
        Assert.Equal(0, await p.ContarAsync("ia.Reserva"));
    }

    [Fact]
    public async Task Sin_modulo_o_sin_configuracion_o_sin_proveedor_explica_por_que()
    {
        var sinModulo = await Procesamiento.NuevaEmpresaAsync(bd, "FINANCIERO");
        var (a, _) = await PreguntarAsync(sinModulo, new ClienteGuionado());
        Assert.Equal("MODULO_INACTIVO", a.Errores[0].Codigo);

        var sinConfig = await Procesamiento.NuevaEmpresaAsync(bd, "FINANCIERO", "IA");
        var (b, _) = await PreguntarAsync(sinConfig, new ClienteGuionado());
        Assert.Equal("DESHABILITADO", b.Errores[0].Codigo);

        var p = await EmpresaAsync();
        var sesion = new SesionPrueba(bd.SuperadminId, p.EmpresaId, false, Permisos);               // la fábrica por defecto no tiene proveedor
        await using var sp = bd.Construir(bd.CadenaApp, sesion);
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeEmpresa(p.EmpresaId, bd.SuperadminId));
        var c = await alcance.ServiceProvider.GetRequiredService<ISender>().Send(new EnviarMensajeAsistenteCommand(null, "hola"));
        Assert.Equal("PROVEEDOR_NO_CONFIGURADO", c.Errores[0].Codigo);
    }

    [Fact]
    public async Task Exige_el_permiso_ia_usar()
    {
        var p = await EmpresaAsync();
        var (r, modelo) = await PreguntarAsync(p, new ClienteGuionado(), permisos: "dash.financiero");
        Assert.False(r.EsExitoso);
        Assert.Equal("NO_AUTORIZADO", r.Errores[0].Codigo);
        Assert.Equal(0, modelo.Llamadas);
    }

    [Fact]
    public async Task Las_herramientas_solo_ofrecen_las_metricas_que_el_usuario_puede_ver()
    {
        var p = await EmpresaAsync();
        var modelo = new ClienteGuionado(
            _ => Llamar("listar_metricas", new { }),
            _ => Llamar("consultar_metrica", new { metrica = "cartera.saldo_cxc" }),            // módulo CARTERA inactivo para esta empresa
            _ => Texto("No dispongo de cartera."));
        var (r, _) = await PreguntarAsync(p, modelo);
        Assert.True(r.EsExitoso);
        var lista = modelo.Recibidos[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result!.ToString()!;
        Assert.Contains("financiero.ingresos", lista);
        Assert.DoesNotContain("cartera.saldo_cxc", lista);
        var rechazo = modelo.Recibidos[2].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last().Result!.ToString()!;
        Assert.Contains("no existe o no está disponible", rechazo);
        Assert.Empty(r.Valor.Fuentes);                                                          // lo rechazado no cuenta como fuente
    }

    [Fact]
    public async Task Sin_el_permiso_del_modulo_no_hay_metricas()
    {
        var p = await EmpresaAsync();
        var modelo = new ClienteGuionado(_ => Llamar("listar_metricas", new { }), _ => Texto("Sin datos."));
        await PreguntarAsync(p, modelo, permisos: Permisos);                                     // tiene ia.usar pero no dash.financiero
        var lista = modelo.Recibidos[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result!.ToString()!;
        Assert.Equal("[]", lista);
    }

    [Fact]
    public async Task Respeta_el_tope_de_iteraciones_y_liquida_lo_consumido()
    {
        var p = await EmpresaAsync(ajustar: c => c["maxIter"] = 2);
        var modelo = new ClienteGuionado(_ => Llamar("listar_metricas", new { }), _ => Llamar("listar_metricas", new { }), _ => Texto("nunca"));
        var (r, _) = await PreguntarAsync(p, modelo);
        Assert.True(r.EsExitoso);
        Assert.True(r.Valor.Parcial);
        Assert.Equal(2, modelo.Llamadas);
        Assert.Equal(0, await p.ContarAsync("ia.Reserva"));
        Assert.Equal(2, await p.ContarAsync("ia.UsoLlamada"));
    }

    [Fact]
    public async Task Si_el_proveedor_falla_registra_el_error_y_libera_la_reserva()
    {
        var p = await EmpresaAsync();
        var modelo = new ClienteGuionado(_ => throw new HttpRequestException("503"));
        var (r, _) = await PreguntarAsync(p, modelo);
        Assert.False(r.EsExitoso);
        Assert.Equal("ASISTENTE_ERROR", r.Errores[0].Codigo);
        Assert.DoesNotContain("503", r.Errores[0].Mensaje);                                      // no se filtran detalles internos al usuario
        Assert.Equal(0, await p.ContarAsync("ia.Reserva"));
        Assert.Equal("ERROR", await p.EscalarAsync<string>("SELECT Resultado FROM ia.UsoLlamada WHERE EmpresaId = @e", new { e = p.EmpresaId }));
        Assert.Equal(0m, await p.EscalarAsync<decimal>("SELECT ISNULL(SUM(ReservadoCop), 0) FROM ia.ConsumoPeriodo WHERE EmpresaId = @e", new { e = p.EmpresaId }));
    }

    [Fact]
    public async Task Una_negativa_del_modelo_se_reporta_sin_cobrar_de_mas()
    {
        var p = await EmpresaAsync();
        var modelo = new ClienteGuionado(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "")) { FinishReason = ChatFinishReason.ContentFilter, Usage = new UsageDetails { InputTokenCount = 500, OutputTokenCount = 0 } });
        var (r, _) = await PreguntarAsync(p, modelo);
        Assert.True(r.EsExitoso);
        Assert.True(r.Valor.Parcial);
        Assert.Contains("no pudo responder", r.Valor.Texto);
        Assert.Equal("RECHAZO", await p.EscalarAsync<string>("SELECT Resultado FROM ia.UsoLlamada WHERE EmpresaId = @e", new { e = p.EmpresaId }));
    }

    [Fact]
    public async Task Las_personas_viajan_como_alias_al_modelo_y_la_respuesta_vuelve_con_sus_nombres()
    {
        var p = await EmpresaAsync(modulosExtra: "CICLO_INGRESO");
        var modelo = new ClienteGuionado(
            _ => Llamar("consultar_metrica", new { metrica = "ciclo_ingreso.facturas_anuladas", agrupar_por = new[] { "usuario" } }),
            mensajes =>
            {
                var datos = mensajes.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last().Result!.ToString()!;
                var alias = System.Text.RegularExpressions.Regex.Match(datos, @"Usuario #\d+").Value;
                return Texto($"{alias} anuló una factura.");
            });
        var (r, _) = await PreguntarAsync(p, modelo, "¿Quién anuló facturas?", permisos: new[] { Permisos, "dash.ciclo_ingreso" });
        Assert.True(r.EsExitoso, string.Join("; ", r.Errores.Select(e => e.Mensaje)));

        var enviado = string.Join("\n", modelo.Recibidos.SelectMany(l => l).SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.Result?.ToString()));
        Assert.Contains("Usuario #", enviado);
        Assert.DoesNotContain("Facturador Uno", enviado);                  // el nombre real nunca salió hacia el proveedor
        Assert.Equal("Facturador Uno anuló una factura.", r.Valor.Texto);  // y la empresa lo ve con su nombre

        // El historial que vuelve al modelo en el turno siguiente conserva el alias, no el nombre
        var siguiente = new ClienteGuionado(_ => Texto("Entendido."));
        var (r2, _) = await PreguntarAsync(p, siguiente, "Gracias", r.Valor.ConversacionId, permisos: new[] { Permisos, "dash.ciclo_ingreso" });
        Assert.True(r2.EsExitoso);
        var historial = string.Join("\n", siguiente.Recibidos[0].Select(m => m.Text));
        Assert.Contains("Usuario #", historial);
        Assert.DoesNotContain("Facturador Uno", historial);
    }

    [Fact]
    public async Task El_seudonimizador_no_confunde_alias_que_comparten_prefijo()
    {
        var p = await EmpresaAsync();
        // «usuario» es una dimensión de personas (usuarios operativos); la métrica de facturación se puede agrupar por ella si el módulo la declara.
        // Se ejercita directamente el seudonimizador con el flujo de la herramienta, porque FINANCIERO no agrupa por usuario.
        var s = new Seudonimizador();
        var alias = s.Alias("usuario", "12", "María Pérez");
        var alias2 = s.Alias("usuario", "123", "Juan Gómez");
        Assert.Equal("Usuario #12", alias);
        Assert.DoesNotContain("María", alias);
        Assert.Equal("María Pérez y Juan Gómez facturaron.", s.Rehidratar("Usuario #12 y Usuario #123 facturaron."));   // «#12» no pisa a «#123»
        Assert.Equal("Usuario #4", s.Rehidratar("Usuario #4"));                                                         // alias desconocido: queda igual
        Assert.True(Seudonimizador.EsPersona("tercero"));
        Assert.False(Seudonimizador.EsPersona("pagador"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Guarda_el_texto_solo_si_la_empresa_lo_autoriza_y_la_conversacion_es_del_usuario()
    {
        var con = await EmpresaAsync();
        var (r1, _) = await PreguntarAsync(con, new ClienteGuionado(_ => Texto("Respuesta uno.")), "Primera pregunta");
        Assert.Equal(2, await con.ContarAsync("ia.Mensaje"));
        Assert.Equal("Primera pregunta", await con.EscalarAsync<string>("SELECT Texto FROM ia.Mensaje WHERE EmpresaId = @e AND Rol = 'USUARIO'", new { e = con.EmpresaId }));

        // La segunda pregunta recibe el historial
        var segundo = new ClienteGuionado(mensajes => Texto("Respuesta dos."));
        var (r2, _) = await PreguntarAsync(con, segundo, "Segunda pregunta", r1.Valor.ConversacionId);
        Assert.True(r2.EsExitoso);
        Assert.Contains(segundo.Recibidos[0], m => m.Text == "Primera pregunta");

        // Otro usuario no puede entrar a la conversación
        var (ajena, _) = await PreguntarAsync(con, new ClienteGuionado(_ => Texto("x")), "Intruso", r1.Valor.ConversacionId, usuario: await OtroUsuarioAsync());
        Assert.False(ajena.EsExitoso);
        Assert.Equal("NO_ENCONTRADO", ajena.Errores[0].Codigo);

        var sin = await EmpresaAsync(ajustar: c => c["guardar"] = 0);
        await PreguntarAsync(sin, new ClienteGuionado(_ => Texto("Respuesta.")), "Pregunta reservada");
        Assert.Equal(0, await sin.ContarAsync("ia.Mensaje", "Texto IS NOT NULL"));
        Assert.Equal(2, await sin.ContarAsync("ia.Mensaje"));                                    // se registra el intercambio, no el texto
    }

    [Fact]
    public async Task El_modelo_de_ahorro_se_usa_al_pasar_el_porcentaje_del_cupo()
    {
        var p = await EmpresaAsync(cupoCop: 1000m);
        await using (var cn = new SqlConnection(bd.CadenaPropietario))
        {
            await cn.OpenAsync();
            await cn.ExecuteAsync("EXEC sp_set_session_context N'EsSuperAdmin', 1");
            var sonnet = await cn.ExecuteScalarAsync<short>("SELECT ModeloId FROM ia.Modelo WHERE Codigo = 'claude-sonnet-5-5'");
            await cn.ExecuteAsync("UPDATE ia.EmpresaConfig SET ModeloPrincipalId = @sonnet, ModeloAhorroId = (SELECT ModeloId FROM ia.Modelo WHERE Codigo = 'claude-haiku-5-5'), AhorroDesdePct = 50 WHERE EmpresaId = @e",
                new { sonnet, e = p.EmpresaId });
        }
        async Task<string?> PreguntarConAsync()
        {
            var fabrica = new FabricaGuionada(new ClienteGuionado(_ => Texto("ok")));
            var sesion = new SesionPrueba(bd.SuperadminId, p.EmpresaId, false, Permisos);
            await using var sp = bd.Construir(bd.CadenaApp, sesion, s => s.AddSingleton<IAsistenteChatClientFactory>(fabrica));
            await using var alcance = sp.CreateAsyncScope();
            alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeEmpresa(p.EmpresaId, bd.SuperadminId));
            var r = await alcance.ServiceProvider.GetRequiredService<ISender>().Send(new EnviarMensajeAsistenteCommand(null, "hola"));
            Assert.True(r.EsExitoso, string.Join("; ", r.Errores.Select(e => e.Mensaje)));
            return fabrica.ModeloPedido;
        }

        Assert.Equal("claude-sonnet-5-5", await PreguntarConAsync());                              // casi 0 % del cupo: modelo principal
        await using (var cn = new SqlConnection(bd.CadenaPropietario))
        {
            await cn.OpenAsync();
            await cn.ExecuteAsync("EXEC sp_set_session_context N'EsSuperAdmin', 1");
            await cn.ExecuteAsync("UPDATE ia.ConsumoPeriodo SET ValorConsumoCop = 600 WHERE EmpresaId = @e", new { e = p.EmpresaId });   // 60 % del cupo
        }
        Assert.Equal("claude-haiku-5-5", await PreguntarConAsync());                               // pasó el 50 %: modelo de ahorro
    }
}
