using System.IO.Compression;
using System.Text;
using Dapper;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using IAnalitica.Application;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using IAnalitica.Application.Reportes;
using IAnalitica.Application.Semantica;
using IAnalitica.Domain.Modulos;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Ingesta;
using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Infrastructure.Reportes;
using IAnalitica.Integration.Tests.Infra;
using IAnalitica.Integration.Tests.Web;
using IAnalitica.Modulos.Financiero;
using IAnalitica.Modulos.Ingreso;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>
/// Reportes en segundo plano de punta a punta (spec 21): se piden con la identidad del usuario, los genera el worker con esa misma
/// identidad (RLS y permisos) y se descargan después. Los archivos se abren y se validan de verdad (ZIP/OpenXML, BOM, %PDF).
/// </summary>
[Collection(ColeccionBd.Nombre)]
public class ReportesTests(BaseDeDatosFixture bd) : IAsyncLifetime
{
    private static readonly string[] PermisosAdmin = { "dash.financiero", "dash.ciclo_ingreso", "dash.cartera", "reportes.exportar", "admin.usuarios" };
    private static readonly FiltrosDashboard EneFeb = new(202601, 202602);
    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static Procesamiento? _datos;
    private static int _usuarioAdmin;

    private ServiceProvider _worker = null!;

    public async Task InitializeAsync()
    {
        await Candado.WaitAsync();
        try
        {
            if (_datos is null)
            {
                var nit = "821" + Random.Shared.Next(100000, 999999);
                var kit = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido(nit, "Clínica de reportes", modulos: new[] { "FINANCIERO", "CICLO_INGRESO", "CARTERA" }));
                var p = Procesamiento.ParaEmpresa(bd, kit.EmpresaId);
                await DatosDashboards.CargarAsync(p);
                await DatosDashboards.CalcularCarteraAsync(p);
                _usuarioAdmin = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, $"SELECT Id FROM seg.Usuario WHERE UserName = '{kit.UsuarioAdministrador}'", superadmin: true);
                _datos = p;
            }
        }
        finally { Candado.Release(); }

        // El «worker» de la prueba: mismo cableado que el host real
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton(Substitute.For<IConfiguracionPortal>());
        s.AddInfrastructure(bd.CadenaApp);
        s.AddApplication(typeof(IAnalitica.Application.Empresas.CrearEmpresaCommand).Assembly);
        s.AddCapaSemantica(new[] { ModuloFinanciero.Modulo }.Concat(ModuloIngreso.Modulos));
        s.AddProcesamiento(bd.CadenaEtl);
        s.AddGeneracionReportes();
        s.Configure<OpcionesAlmacenReportes>(o => o.Ruta = FabricaWeb.RutaReportes);
        _worker = s.BuildServiceProvider();
    }

    public async Task DisposeAsync() => await _worker.DisposeAsync();

    private int EmpresaId => _datos!.EmpresaId;
    private DespachadorTrabajos Despachador => _worker.GetRequiredService<DespachadorTrabajos>();

    private Task<Result> ComoUsuarioAsync(IRequest<Result> request, int usuarioId, int? empresaId = null, string[]? permisos = null)
        => EnviarAsync(request, usuarioId, empresaId, permisos);

    private Task<Result<T>> ComoUsuarioAsync<T>(IRequest<Result<T>> request, int usuarioId, int? empresaId = null, string[]? permisos = null)
        => EnviarAsync(request, usuarioId, empresaId, permisos);

    private async Task<TResp> EnviarAsync<TResp>(IRequest<TResp> request, int usuarioId, int? empresaId, string[]? permisos)
    {
        var empresa = empresaId ?? EmpresaId;
        var sesion = new SesionPrueba(usuarioId, empresa, false, permisos ?? PermisosAdmin);
        var s = new ServiceCollection();
        s.AddLogging(); s.AddSingleton(TimeProvider.System); s.AddSingleton(Substitute.For<IConfiguracionPortal>());
        s.AddInfrastructure(bd.CadenaApp);
        s.AddSingleton<ISesionUsuario>(sesion);
        s.AddApplication(typeof(IAnalitica.Application.Empresas.CrearEmpresaCommand).Assembly);
        s.AddCapaSemantica(new[] { ModuloFinanciero.Modulo }.Concat(ModuloIngreso.Modulos));
        s.Configure<OpcionesAlmacenReportes>(o => o.Ruta = FabricaWeb.RutaReportes);
        await using var sp = s.BuildServiceProvider();
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeEmpresa(empresa, usuarioId));
        return await alcance.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private async Task<Guid> PedirAsync(string dashboard, string formato, FiltrosDashboard? filtros = null, int? usuario = null)
    {
        var r = await ComoUsuarioAsync(new SolicitarReporteCommand(dashboard, formato, filtros ?? EneFeb), usuario ?? _usuarioAdmin);
        Assert.True(r.EsExitoso, string.Join("; ", r.Errores.Select(e => e.Codigo + ": " + e.Mensaje)));
        return r.Valor.SolicitudId;
    }

    /// <summary>Corre el worker hasta que no quede nada de reportes por hacer.</summary>
    private async Task ProcesarAsync()
    {
        while (await Despachador.EjecutarUnoAsync(TiposTrabajo.GenerarReporte, CancellationToken.None)) { }
    }

    private async Task<(string Estado, string? Ruta, string? Error)> EstadoAsync(Guid id)
    {
        var r = await _datos!.EscalarAsync<string>("SELECT CONCAT(Estado, '|', ISNULL(BlobRuta, ''), '|', ISNULL(Error, '')) FROM rpt.SolicitudReporte WHERE SolicitudId = @id", new { id });
        var p = r.Split('|');
        return (p[0], p[1] == "" ? null : p[1], p[2] == "" ? null : p[2]);
    }

    private async Task<byte[]> ArchivoAsync(Guid id, int? usuario = null, string[]? permisos = null)
    {
        var r = await ComoUsuarioAsync(new DescargarReporteQuery(id, "10.0.0.1"), usuario ?? _usuarioAdmin, permisos: permisos);
        Assert.True(r.EsExitoso, string.Join("; ", r.Errores.Select(e => e.Codigo + ": " + e.Mensaje)));
        await using var flujo = r.Valor.Contenido;
        using var ms = new MemoryStream();
        await flujo.CopyToAsync(ms);
        return ms.ToArray();
    }

    // ---------- Formatos ----------

    [Fact]
    public async Task El_Excel_generado_es_valido_tiene_numeros_como_numeros_y_los_parametros()
    {
        var id = await PedirAsync("financiero.gerencial", "XLSX");
        Assert.Equal("PENDIENTE", (await EstadoAsync(id)).Estado);   // nada se genera dentro de la solicitud
        await ProcesarAsync();
        Assert.Equal("LISTO", (await EstadoAsync(id)).Estado);

        var bytes = await ArchivoAsync(id);
        using var ms = new MemoryStream(bytes);
        using (var doc = SpreadsheetDocument.Open(new MemoryStream(bytes), false))
        {
            var errores = new OpenXmlValidator().Validate(doc).Select(e => e.Description).ToList();
            Assert.True(errores.Count == 0, "El .xlsx no valida: " + string.Join(" | ", errores));
            var hojas = doc.WorkbookPart!.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Select(h => h.Name!.Value!).ToList();
            Assert.Equal("Resumen", hojas[0]);
            Assert.Contains("Ingresos", hojas);
            Assert.Contains("Ingresos, costos y utilidad por", hojas);   // nombre de hoja recortado a 31 caracteres
            Assert.All(hojas, h => Assert.True(h.Length <= 31));
            Assert.Equal(hojas.Count, hojas.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        using var zip = new ZipArchive(ms);
        var resumen = Leer(zip, "xl/worksheets/sheet1.xml");
        Assert.Contains("Clínica de reportes", resumen);
        Assert.Contains("2026-01 a 2026-02", resumen);
        Assert.Contains("Datos a la carga", resumen);
        var hojaIngresos = Enumerable.Range(2, 3).Select(i => Leer(zip, $"xl/worksheets/sheet{i}.xml")).First(x => x.Contains("<v>3000</v>"));
        Assert.DoesNotContain("t=\"inlineStr\"><is><t xml:space=\"preserve\">3000", hojaIngresos);   // el 3000 es número, no texto
    }

    [Fact]
    public async Task El_CSV_lleva_BOM_numeros_invariantes_y_neutraliza_formulas()
    {
        // un pagador con nombre de fórmula: no debe ejecutarse al abrir en Excel
        await _datos!.ProcesarAsync("pagadores", "COMPLETA", new object[]
        {
            new { codigoOrigen = "E1", nombre = "EPS Uno", terceroCodigo = "P1", activo = true },
            new { codigoOrigen = "E2", nombre = "=HYPERLINK(\"http://malo\",\"x\")", terceroCodigo = "P2", activo = true },
        });
        var id = await PedirAsync("ciclo_ingreso.facturacion", "CSV");
        await ProcesarAsync();
        var bytes = await ArchivoAsync(id);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var texto = Encoding.UTF8.GetString(bytes);
        Assert.Contains("Facturación", texto);
        Assert.Contains("Valor facturado (COP)", texto);
        Assert.Contains(",3000", texto);                       // punto como separador decimal, sin símbolo ni miles
        Assert.Contains("'=HYPERLINK", texto);                 // neutralizada
        Assert.DoesNotContain(",=HYPERLINK", texto);

        await _datos.ProcesarAsync("pagadores", "COMPLETA", new object[]
        {
            new { codigoOrigen = "E1", nombre = "EPS Uno", terceroCodigo = "P1", activo = true },
            new { codigoOrigen = "E2", nombre = "EPS Dos", terceroCodigo = "P2", activo = true },
        });
    }

    [Fact]
    public async Task El_PDF_es_un_PDF_con_contenido()
    {
        var id = await PedirAsync("cartera.antiguedad", "PDF", FiltrosDashboard.Ninguno);
        await ProcesarAsync();
        var bytes = await ArchivoAsync(id);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes[..5]));
        Assert.True(bytes.Length > 3000, $"El PDF parece vacío ({bytes.Length} bytes).");
        Assert.Contains("%%EOF", Encoding.ASCII.GetString(bytes[^32..]));
    }

    // ---------- Seguridad y límites ----------

    [Fact]
    public async Task Sin_permiso_de_exportar_o_del_modulo_no_se_puede_pedir()
    {
        var sinExportar = await ComoUsuarioAsync(new SolicitarReporteCommand("financiero.gerencial", "PDF", EneFeb), 777, permisos: new[] { "dash.financiero" });
        Assert.Equal("NO_AUTORIZADO", sinExportar.Errores[0].Codigo);
        var sinModulo = await ComoUsuarioAsync(new SolicitarReporteCommand("cartera.antiguedad", "PDF", EneFeb), 777, permisos: new[] { "dash.financiero", "reportes.exportar" });
        Assert.Equal("NO_AUTORIZADO", sinModulo.Errores[0].Codigo);
        var inexistente = await ComoUsuarioAsync(new SolicitarReporteCommand("no.existe", "PDF", EneFeb), _usuarioAdmin);
        Assert.False(inexistente.EsExitoso);
        var formato = await ComoUsuarioAsync(new SolicitarReporteCommand("financiero.gerencial", "DOCX", EneFeb), _usuarioAdmin);
        Assert.Equal(CodigosReporte.FormatoInvalido, formato.Errores[0].Codigo);
    }

    [Fact]
    public async Task Maximo_5_reportes_en_cola_por_usuario_y_se_pueden_cancelar()
    {
        const int usuario = 880001;   // usuario sintético: sus reportes no los toma nadie hasta procesar
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add(await PedirAsync("financiero.gerencial", "CSV", new FiltrosDashboard(202601 + i, 202602 + i), usuario));
        var sexto = await ComoUsuarioAsync(new SolicitarReporteCommand("financiero.gerencial", "CSV", new FiltrosDashboard(202610, 202611)), usuario);
        Assert.Equal(CodigosReporte.LimiteEnCola, sexto.Errores[0].Codigo);

        Assert.True((await ComoUsuarioAsync(new CancelarReporteCommand(ids[0]), usuario)).EsExitoso);
        Assert.Equal("CANCELADO", (await EstadoAsync(ids[0])).Estado);
        Assert.False((await ComoUsuarioAsync(new CancelarReporteCommand(ids[0]), usuario)).EsExitoso);   // ya no está en cola
        Assert.False((await ComoUsuarioAsync(new CancelarReporteCommand(ids[1]), 880002)).EsExitoso);     // ni de otro usuario
        await PedirAsync("financiero.gerencial", "CSV", new FiltrosDashboard(202610, 202611), usuario);    // ya cabe uno más

        foreach (var id in ids.Skip(1)) await _datos!.EjecutarAsync("UPDATE rpt.SolicitudReporte SET Estado = 'CANCELADO' WHERE SolicitudId = @id", new { id });
        await _datos!.EjecutarAsync("UPDATE rpt.SolicitudReporte SET Estado = 'CANCELADO' WHERE UsuarioId = @usuario AND Estado = 'PENDIENTE'", new { usuario });
    }

    [Fact]
    public async Task Un_reporte_cancelado_no_se_genera()
    {
        var id = await PedirAsync("financiero.pyg", "CSV", new FiltrosDashboard(202601, 202601), usuario: 880010);
        Assert.True((await ComoUsuarioAsync(new CancelarReporteCommand(id), 880010)).EsExitoso);
        await ProcesarAsync();
        var (estado, ruta, _) = await EstadoAsync(id);
        Assert.Equal(("CANCELADO", null), (estado, ruta));
    }

    [Fact]
    public async Task Un_reporte_identico_y_reciente_se_reutiliza_hasta_que_cambian_los_datos()
    {
        var f = new FiltrosDashboard(202601, 202601);
        var id1 = await PedirAsync("financiero.pyg", "CSV", f);
        await ProcesarAsync();
        var segundo = await ComoUsuarioAsync(new SolicitarReporteCommand("financiero.pyg", "CSV", f), _usuarioAdmin);
        Assert.True(segundo.Valor.Reutilizado);
        Assert.Equal(id1, segundo.Valor.SolicitudId);

        var otroFormato = await ComoUsuarioAsync(new SolicitarReporteCommand("financiero.pyg", "XLSX", f), _usuarioAdmin);
        Assert.False(otroFormato.Valor.Reutilizado);
        var otroUsuario = await ComoUsuarioAsync(new SolicitarReporteCommand("financiero.pyg", "CSV", f), 880020);
        Assert.False(otroUsuario.Valor.Reutilizado);
        await ProcesarAsync();
    }

    [Fact]
    public async Task Solo_el_dueno_o_el_administrador_descargan_y_cada_descarga_queda_auditada()
    {
        var id = await PedirAsync("financiero.gerencial", "CSV", new FiltrosDashboard(202601, 202601));
        await ProcesarAsync();

        var otro = await ComoUsuarioAsync(new DescargarReporteQuery(id, null), 880030, permisos: new[] { "reportes.exportar", "dash.financiero" });
        Assert.Equal("NO_ENCONTRADO", otro.Errores[0].Codigo);                                                           // ni existe para él
        var admin = await ComoUsuarioAsync(new DescargarReporteQuery(id, null), 880031, permisos: PermisosAdmin);        // administrador de la empresa
        Assert.True(admin.EsExitoso);
        await admin.Valor.Contenido.DisposeAsync();
        var otraEmpresa = await ComoUsuarioAsync(new DescargarReporteQuery(id, null), _usuarioAdmin, empresaId: (await Procesamiento.NuevaEmpresaAsync(bd)).EmpresaId);
        Assert.False(otraEmpresa.EsExitoso);

        await ArchivoAsync(id);
        var descargas = await _datos!.EscalarAsync<int>("SELECT COUNT(*) FROM aud.Acceso WHERE Accion = 'DESCARGA_REPORTE' AND Detalle = @d", new { d = id.ToString() });
        Assert.Equal(2, descargas);   // admin + dueño
    }

    [Fact]
    public async Task Los_listados_son_por_usuario_y_el_administrador_ve_toda_la_empresa()
    {
        var id = await PedirAsync("financiero.pyg", "CSV", new FiltrosDashboard(202602, 202602), usuario: 880040);
        var propios = (await ComoUsuarioAsync(new ListarMisReportesQuery(), 880040)).Valor;
        Assert.Contains(propios, s => s.SolicitudId == id);
        var deOtro = (await ComoUsuarioAsync(new ListarMisReportesQuery(), 880041)).Valor;
        Assert.DoesNotContain(deOtro, s => s.SolicitudId == id);
        var todos = (await ComoUsuarioAsync(new ListarMisReportesQuery(TodosLosDeLaEmpresa: true), 880041, permisos: PermisosAdmin)).Valor;
        Assert.Contains(todos, s => s.SolicitudId == id);
        var sinAdmin = (await ComoUsuarioAsync(new ListarMisReportesQuery(TodosLosDeLaEmpresa: true), 880041, permisos: new[] { "reportes.exportar" })).Valor;
        Assert.DoesNotContain(sinAdmin, s => s.SolicitudId == id);                                                     // pedir «todos» sin ser administrador no sirve
        await ComoUsuarioAsync(new CancelarReporteCommand(id), 880040);
    }

    // ---------- Worker ----------

    [Fact]
    public async Task Un_usuario_a_la_vez_el_segundo_reporte_espera_sin_gastar_intento()
    {
        var usuario = _usuarioAdmin;   // el worker consulta los permisos del usuario en la base: tiene que existir
        var id = await PedirAsync("financiero.pyg", "CSV", new FiltrosDashboard(202601, 202601), usuario);

        // Otro reporte del mismo usuario «en curso»: un applock en otra conexión
        await using var otra = await _datos!.EtlAsync();
        await otra.ExecuteAsync($"DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'reporte:{usuario}', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;");
        await ProcesarUnoDeEstaSolicitudAsync(id);
        Assert.Equal("PENDIENTE", (await EstadoAsync(id)).Estado);
        Assert.Equal(0, await _datos.EscalarAsync<int>("SELECT Intentos FROM etl.Trabajo WHERE ClaveExclusion = @c", new { c = $"REP:{id}" }));   // esperar no gasta intentos

        await otra.ExecuteAsync($"EXEC sp_releaseapplock @Resource = 'reporte:{usuario}', @LockOwner = 'Session';");
        await _datos.EjecutarAsync("UPDATE etl.Trabajo SET DisponibleEn = SYSUTCDATETIME() WHERE ClaveExclusion = @c", new { c = $"REP:{id}" });
        await ProcesarAsync();
        Assert.Equal("LISTO", (await EstadoAsync(id)).Estado);
    }

    private async Task ProcesarUnoDeEstaSolicitudAsync(Guid id)
    {
        // pone este trabajo de primero y lo toma
        await _datos!.EjecutarAsync("UPDATE etl.Trabajo SET Prioridad = 0, DisponibleEn = SYSUTCDATETIME() WHERE ClaveExclusion = @c", new { c = $"REP:{id}" });
        await Despachador.EjecutarUnoAsync(TiposTrabajo.GenerarReporte, CancellationToken.None);
    }

    [Fact]
    public async Task Un_fallo_en_el_ultimo_intento_deja_ERROR_con_mensaje_entendible_y_avisa()
    {
        var usuario = _usuarioAdmin;
        var antes = await _datos!.EscalarAsync<int>("SELECT COUNT(*) FROM seg.Notificacion WHERE UsuarioId = @usuario AND Tipo = 'REPORTE_ERROR'", new { usuario });
        var id = await PedirAsync("financiero.pyg", "CSV", new FiltrosDashboard(202512, 202512), usuario);   // filtros propios: nada que reutilizar
        // se rompe a propósito: el dashboard de los parámetros ya no existe
        await _datos.EjecutarAsync("UPDATE rpt.SolicitudReporte SET ParametrosJson = REPLACE(ParametrosJson, 'financiero.pyg', 'no.existe') WHERE SolicitudId = @id", new { id });
        await _datos.EjecutarAsync("UPDATE etl.Trabajo SET Intentos = MaxIntentos - 1, Prioridad = 0, DisponibleEn = SYSUTCDATETIME() WHERE ClaveExclusion = @c", new { c = $"REP:{id}" });
        await Despachador.EjecutarUnoAsync(TiposTrabajo.GenerarReporte, CancellationToken.None);

        var (estado, _, error) = await EstadoAsync(id);
        Assert.Equal("ERROR", estado);
        Assert.Contains("No fue posible generar el reporte", error);
        Assert.DoesNotContain("no.existe", error);   // sin detalles internos
        Assert.Equal(antes + 1, await _datos.EscalarAsync<int>("SELECT COUNT(*) FROM seg.Notificacion WHERE UsuarioId = @usuario AND Tipo = 'REPORTE_ERROR'", new { usuario }));
    }

    [Fact]
    public async Task Al_quedar_listo_avisa_con_la_campana_y_ver_la_lista_la_apaga()
    {
        var usuario = _usuarioAdmin;
        await using var limpiar = await Escenarios.AbrirAsync(bd.CadenaPropietario, new ContextoBd(null, null, true, false, true));
        await Escenarios.Ejecutar(limpiar, $"UPDATE seg.Notificacion SET Leida = 1 WHERE UsuarioId = {usuario}");
        await PedirAsync("financiero.pyg", "CSV", new FiltrosDashboard(202601, 202601), usuario);
        await ProcesarAsync();
        Assert.Equal(1, await _datos!.EscalarAsync<int>("SELECT COUNT(*) FROM seg.Notificacion WHERE UsuarioId = @usuario AND Tipo = 'REPORTE_LISTO' AND Leida = 0", new { usuario }));

        var sesion = new SesionPrueba(usuario, EmpresaId, false, PermisosAdmin);
        await using var sp = bd.Construir(bd.CadenaApp, sesion);
        await using var al = sp.CreateAsyncScope();
        al.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeEmpresa(EmpresaId, usuario));
        var n = al.ServiceProvider.GetRequiredService<INotificaciones>();
        Assert.Equal(1, await n.ContarNoLeidasAsync(default));
        await n.MarcarLeidasAsync("REPORTE", default);
        Assert.Equal(0, await n.ContarNoLeidasAsync(default));
    }

    [Fact]
    public async Task Los_reportes_vencidos_se_expiran_y_su_archivo_se_borra()
    {
        var id = await PedirAsync("financiero.pyg", "CSV", new FiltrosDashboard(202601, 202602));
        await ProcesarAsync();
        var (_, ruta, _) = await EstadoAsync(id);
        Assert.NotNull(ruta);
        var archivo = Path.Combine(FabricaWeb.RutaReportes, ruta!);
        Assert.True(File.Exists(archivo));

        await _datos!.EjecutarAsync("UPDATE rpt.SolicitudReporte SET ExpiraEn = DATEADD(DAY, -1, SYSUTCDATETIME()) WHERE SolicitudId = @id", new { id });
        var vencidos = await _worker.GetRequiredService<ExpiradorReportes>().ExpirarAsync(default);
        Assert.True(vencidos >= 1);
        Assert.Equal("EXPIRADO", (await EstadoAsync(id)).Estado);
        Assert.False(File.Exists(archivo));
        var descarga = await ComoUsuarioAsync(new DescargarReporteQuery(id, null), _usuarioAdmin);
        Assert.False(descarga.EsExitoso);
    }

    [Fact]
    public async Task El_reporte_lo_calcula_con_la_identidad_del_solicitante_y_trae_todas_las_filas()
    {
        // Mismos números que en pantalla, pero completos: el CSV de la facturación por sede trae todas las filas (sin el tope de 50 de la pantalla)
        var id = await PedirAsync("ciclo_ingreso.facturacion", "CSV");
        await ProcesarAsync();
        var texto = Encoding.UTF8.GetString(await ArchivoAsync(id));
        Assert.Contains("Principal", texto);
        Assert.Contains("EPS Dos", texto);
    }

    private static string Leer(ZipArchive zip, string ruta)
    {
        using var lector = new StreamReader(zip.GetEntry(ruta)!.Open(), Encoding.UTF8);
        return lector.ReadToEnd();
    }
}
