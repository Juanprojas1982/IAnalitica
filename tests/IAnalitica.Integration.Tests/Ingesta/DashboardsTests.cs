using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using IAnalitica.Application.Semantica;
using IAnalitica.Domain.Modulos;
using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>
/// Capa semántica y dashboards de la fase 1 contra SQL Server real, con cifras calculadas a mano (ver <see cref="DatosDashboards"/>).
/// Todo corre como un usuario de rol_app con RLS, igual que el portal.
/// </summary>
[Collection(ColeccionBd.Nombre)]
public class DashboardsTests(BaseDeDatosFixture bd) : IAsyncLifetime
{
    private static readonly string[] TodosLosPermisos = { "dash.financiero", "dash.ciclo_ingreso", "dash.cartera" };
    private static readonly FiltrosDashboard EneFeb = new(202601, 202602);

    private static Procesamiento? _datos;
    private static readonly SemaphoreSlim Candado = new(1, 1);

    /// <summary>Los datos son de solo lectura: se cargan una vez para todas las pruebas de esta clase.</summary>
    public async Task InitializeAsync()
    {
        await Candado.WaitAsync();
        try
        {
            if (_datos is not null) return;
            var p = await Procesamiento.NuevaEmpresaAsync(bd, "FINANCIERO", "CICLO_INGRESO", "CARTERA");
            await DatosDashboards.CargarAsync(p);
            await DatosDashboards.CalcularCarteraAsync(p);
            _datos = p;
        }
        finally { Candado.Release(); }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private int EmpresaId => _datos!.EmpresaId;

    private async Task<Result<T>> EnviarAsync<T>(IRequest<Result<T>> request, int? empresaId = null, params string[] permisos)
    {
        var empresa = empresaId ?? EmpresaId;
        var sesion = new SesionPrueba(4242, empresa, false, permisos.Length == 0 ? TodosLosPermisos : permisos);
        await using var sp = bd.Construir(bd.CadenaApp, sesion);
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeEmpresa(empresa, 4242));
        return await alcance.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private async Task<DatosWidget> DatosAsync(string dashboard, string widget, FiltrosDashboard? filtros = null, int? empresaId = null, params string[] permisos)
    {
        var r = await EnviarAsync(new ObtenerDatosWidgetQuery(dashboard, widget, filtros ?? EneFeb), empresaId, permisos);
        Assert.True(r.EsExitoso, string.Join("; ", r.Errores.Select(e => e.Codigo + ": " + e.Mensaje)));
        return r.Valor;
    }

    private async Task<decimal?> KpiAsync(string dashboard, string widget, FiltrosDashboard? filtros = null)
        => (await DatosAsync(dashboard, widget, filtros)).Series[0].Resultado.Total;

    private static Dictionary<string, decimal?> PorClave(DatosWidget d, int serie = 0)
        => d.Series[serie].Resultado.Filas.ToDictionary(f => f.Dimensiones.Values.First().Clave, f => f.Valor);

    private static Dictionary<string, decimal?> PorDimension(DatosWidget d, int serie = 0)
        => d.Series[serie].Resultado.Filas.ToDictionary(f => f.Dimensiones.Values.First().Etiqueta, f => f.Valor);

    // ---------- Catálogo ----------

    [Fact]
    public async Task Toda_metrica_de_fabrica_ejecuta_su_SQL_sin_errores()
    {
        await using var sp = bd.Construir(bd.CadenaApp, new SesionPrueba(4242, EmpresaId, false, TodosLosPermisos));
        var catalogo = sp.GetRequiredService<CatalogoAnalitico>();
        Assert.True(catalogo.Metricas.Count >= 30);
        Assert.Equal(9, catalogo.Dashboards.Count);

        foreach (var m in catalogo.Metricas.Values)
        {
            await using var alcance = sp.CreateAsyncScope();
            alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeEmpresa(EmpresaId, 4242));
            var motor = alcance.ServiceProvider.GetRequiredService<IMotorMetricas>();
            await motor.EjecutarAsync(new ConsultaMetrica(m.Codigo, Array.Empty<string>(), FiltrosDashboard.Ninguno), default);
            foreach (var dim in m.Dimensiones.Keys.Where(d => d != "corte"))   // cada dimensión también agrupa sin error
                await motor.EjecutarAsync(new ConsultaMetrica(m.Codigo, new[] { dim }, EneFeb, Top: 5), default);
        }
    }

    [Fact]
    public async Task Todo_widget_de_fabrica_devuelve_datos_o_sin_datos_sin_errores()
    {
        await using var sp = bd.Construir(bd.CadenaApp, new SesionPrueba(4242, EmpresaId, false, TodosLosPermisos));
        var catalogo = sp.GetRequiredService<CatalogoAnalitico>();
        foreach (var d in catalogo.Dashboards.Values)
            foreach (var w in d.Widgets)
            {
                var r = await EnviarAsync(new ObtenerDatosWidgetQuery(d.Codigo, w.Codigo, EneFeb));
                Assert.True(r.EsExitoso, $"{d.Codigo}/{w.Codigo}: " + string.Join("; ", r.Errores.Select(e => e.Mensaje)));
            }
    }

    // ---------- FINANCIERO ----------

    [Fact]
    public async Task El_estado_de_resultados_cuadra_a_mano_y_excluye_el_comprobante_de_cierre()
    {
        Assert.Equal(3000m, await KpiAsync("financiero.gerencial", "ingresos"));     // 1.000 + 2.000; el cierre por 3.000 no cuenta
        Assert.Equal(500m, await KpiAsync("financiero.gerencial", "costos"));        // gasto 300 + costo de ventas 200
        Assert.Equal(2500m, await KpiAsync("financiero.gerencial", "utilidad"));
        Assert.Equal(83.33m, Math.Round((await KpiAsync("financiero.gerencial", "margen"))!.Value, 2));
    }

    [Fact]
    public async Task La_tendencia_por_periodo_trae_una_serie_por_metrica()
    {
        var d = await DatosAsync("financiero.gerencial", "tendencia");
        Assert.Equal(3, d.Series.Count);
        Assert.Equal(new decimal?[] { 1000m, 2000m }, PorDimension(d, 0).Values.ToArray());     // ingresos
        Assert.Equal(new[] { "2026-01", "2026-02" }, PorDimension(d, 0).Keys.ToArray());
        Assert.Equal(new[] { 500m }, PorDimension(d, 1).Values.Select(v => v ?? 0m).ToArray());   // costos: febrero no tiene fila (el renderizador alinea las series por periodo)
        Assert.Equal(new decimal?[] { 500m, 2000m }, PorDimension(d, 2).Values.ToArray());      // utilidad
    }

    [Fact]
    public async Task El_kpi_compara_con_el_periodo_anterior_de_igual_longitud()
    {
        var feb = (await DatosAsync("financiero.gerencial", "ingresos", new FiltrosDashboard(202602, 202602))).Series[0];
        Assert.Equal(2000m, feb.Resultado.Total);
        Assert.Equal(1000m, feb.Anterior);          // enero
        Assert.Equal(1m, feb.Variacion);            // +100 %
        var enero = (await DatosAsync("financiero.gerencial", "ingresos", new FiltrosDashboard(202601, 202601))).Series[0];
        Assert.Equal(0m, enero.Anterior ?? 0m);     // diciembre 2025 no tiene ingresos
    }

    [Fact]
    public async Task El_resultado_por_grupo_y_la_cascada_dan_los_signos_correctos()
    {
        var grupos = PorClave(await DatosAsync("financiero.pyg", "grupos"));
        Assert.Equal(3000m, grupos["41"]);
        Assert.Equal(-300m, grupos["51"]);
        Assert.Equal(-200m, grupos["61"]);

        var cascada = PorClave(await DatosAsync("financiero.pyg", "cascada"));
        Assert.Equal(3000m, cascada["4"]);
        Assert.Equal(-500m, cascada["5"] + cascada["6"]);
    }

    [Fact]
    public async Task El_resultado_por_centro_de_costo_agrupa_lo_que_no_tiene_centro()
    {
        var d = await DatosAsync("financiero.pyg", "centros");
        Assert.Equal(4, d.Series.Count);
        var fila = Assert.Single(d.Series[0].Resultado.Filas);                  // todo está sin centro de costo en estos datos
        Assert.Equal("(sin dato)", fila.Dimensiones["centro_costo"].Etiqueta);
        Assert.Equal(3000m, fila.Valor);
    }

    [Fact]
    public async Task El_balance_es_acumulado_y_las_razones_cuadran_a_mano()
    {
        var feb = new FiltrosDashboard(202601, 202602);   // el inicio se ignora: es un saldo
        Assert.Equal(44.5m, await KpiAsync("financiero.balance", "razon_corriente", feb));           // 8.900 / 200
        Assert.Equal(42.5m, await KpiAsync("financiero.balance", "prueba_acida", feb));              // (8.900 − 400) / 200
        Assert.Equal(8700m, await KpiAsync("financiero.balance", "capital_trabajo", feb));
        Assert.Equal(2.25m, Math.Round((await KpiAsync("financiero.balance", "endeudamiento", feb))!.Value, 2));   // 200 / 8.900

        var enero = new FiltrosDashboard(202601, 202601);
        Assert.Equal(34.5m, await KpiAsync("financiero.balance", "razon_corriente", enero));         // 6.900 / 200
        // el saldo de octubre (antes del inicio del filtro) sí entra: es acumulado
        Assert.Equal(6900m, await KpiAsync("financiero.balance", "razon_corriente", new FiltrosDashboard(202601, 202601)) * 200);
    }

    [Fact]
    public async Task El_balance_agrupa_por_grupo_del_balance_con_el_respaldo_del_PUC()
    {
        var d = await DatosAsync("financiero.balance", "grupos", new FiltrosDashboard(null, 202602));
        var grupos = PorClave(d);
        Assert.Equal(8900m, grupos["ACTIVO_CORRIENTE"]);
        Assert.Equal(200m, grupos["PASIVO_CORRIENTE"]);
        Assert.Equal(8400m, grupos["PATRIMONIO"]);          // capital 5.400 + resultado 3.000
    }

    // ---------- CICLO DEL INGRESO ----------

    [Fact]
    public async Task La_facturacion_excluye_anuladas_y_agrupa_por_pagador_con_su_nombre()
    {
        Assert.Equal(3000m, await KpiAsync("ciclo_ingreso.facturacion", "facturado"));        // F1 + F2 (F3 anulada y F0 fuera de periodo)
        Assert.Equal(2m, await KpiAsync("ciclo_ingreso.facturacion", "facturas"));
        var pagadores = PorDimension(await DatosAsync("ciclo_ingreso.facturacion", "pagadores"));
        Assert.Equal(1000m, pagadores["EPS Uno"]);
        Assert.Equal(2000m, pagadores["EPS Dos"]);
    }

    [Fact]
    public async Task Un_filtro_de_pagador_reduce_todo_lo_que_tiene_esa_dimension()
    {
        var e1 = await ConsultarPagadorAsync("E1");
        var f = new FiltrosDashboard(202601, 202602, Pagadores: new[] { e1 });
        Assert.Equal(1000m, await KpiAsync("ciclo_ingreso.facturacion", "facturado", f));
        var d = await DatosAsync("financiero.gerencial", "ingresos", f);                     // la contabilidad no tiene pagador: el filtro no aplica y se avisa
        Assert.Equal(3000m, d.Series[0].Resultado.Total);
        Assert.Contains("pagador", d.FiltrosNoAplicados);
    }

    private async Task<int> ConsultarPagadorAsync(string codigo)
        => await _datos!.EscalarAsync<int>("SELECT PagadorId FROM dim.Pagador WHERE EmpresaId = @e AND CodigoOrigen = @c", new { e = EmpresaId, c = codigo });

    [Fact]
    public async Task Factura_electronica_y_radicacion_cuadran_a_mano()
    {
        var todo = new FiltrosDashboard(202510, 202602);
        Assert.Equal(33.33m, Math.Round((await KpiAsync("ciclo_ingreso.fe_rips", "problemas", todo))!.Value, 2));    // F2 con error de 3 facturas
        Assert.Equal(2000m, await KpiAsync("ciclo_ingreso.fe_rips", "sin_radicar", todo));                             // F2
        Assert.Equal(10m, await KpiAsync("ciclo_ingreso.fe_rips", "dias", todo));                                       // F1 y F0: 10 días

        var estados = PorClave(await DatosAsync("ciclo_ingreso.fe_rips", "estados", todo));
        Assert.Equal(new Dictionary<string, decimal?> { ["ENVIADA"] = 1, ["ERROR"] = 1, ["CARGADA"] = 1 }, estados);
    }

    [Fact]
    public async Task El_retrabajo_mide_lo_anulado_sobre_lo_emitido()
    {
        Assert.Equal(33.33m, Math.Round((await KpiAsync("ciclo_ingreso.retrabajo", "pct", new FiltrosDashboard(202601, 202601)))!.Value, 2));    // 500 / 1.500
        Assert.Equal(14.29m, Math.Round((await KpiAsync("ciclo_ingreso.retrabajo", "pct", EneFeb))!.Value, 2));                                  // 500 / 3.500
        Assert.Equal(1m, await KpiAsync("ciclo_ingreso.retrabajo", "facturas", EneFeb));
        var usuarios = (await DatosAsync("ciclo_ingreso.retrabajo", "usuarios")).Series[0].Resultado.Filas.Single();
        Assert.Equal("Facturador Uno", usuarios.Dimensiones["usuario"].Etiqueta);
    }

    // ---------- CARTERA ----------

    [Fact]
    public async Task La_cartera_toma_el_ultimo_corte_y_calcula_vencidos_y_edades()
    {
        Assert.Equal(3300m, await KpiAsync("cartera.antiguedad", "cxc", FiltrosDashboard.Ninguno));
        Assert.Equal(200m, await KpiAsync("cartera.antiguedad", "cxp", FiltrosDashboard.Ninguno));
        Assert.Equal(24.24m, Math.Round((await KpiAsync("cartera.antiguedad", "vencido", FiltrosDashboard.Ninguno))!.Value, 2));    // 800 / 3.300

        var edades = (await DatosAsync("cartera.antiguedad", "edades", FiltrosDashboard.Ninguno)).Series[0].Resultado.Filas
            .ToDictionary(f => f.Dimensiones["rango_edad"].Clave + "|" + f.Dimensiones["tipo_cartera"].Clave, f => f.Valor);
        Assert.Equal(2000m, edades["0-30|CXC"]);
        Assert.Equal(500m, edades["31-60|CXC"]);
        Assert.Equal(200m, edades["31-60|CXP"]);
        Assert.Equal(800m, edades["151-180|CXC"]);
    }

    [Fact]
    public async Task Un_corte_pedido_que_no_existe_no_trae_datos_y_el_corte_se_muestra_legible()
    {
        var sinCorte = await DatosAsync("cartera.antiguedad", "cxc", new FiltrosDashboard(Corte: 20200101));
        Assert.True(sinCorte.SinDatos);
        var conCorte = await DatosAsync("cartera.antiguedad", "cxc", new FiltrosDashboard(Corte: 20260301));
        Assert.Equal(3300m, conCorte.Series[0].Resultado.Total);
    }

    [Fact]
    public async Task El_DSO_por_pagador_y_global_cuadra_a_mano()
    {
        Assert.Equal(99m, await KpiAsync("cartera.dso", "dso", FiltrosDashboard.Ninguno));     // 3.300 / 3.000 × 90
        var dso = PorDimension(await DatosAsync("cartera.dso", "dso_pagador", FiltrosDashboard.Ninguno));
        Assert.Equal(45m, dso["EPS Uno"]);        // 500 / 1.000 × 90
        Assert.Equal(126m, dso["EPS Dos"]);       // 2.800 / 2.000 × 90
    }

    [Fact]
    public async Task La_conciliacion_de_cartera_con_el_libro_no_tiene_diferencia()
    {
        var tabla = (await DatosAsync("cartera.conciliacion", "tabla", FiltrosDashboard.Ninguno));
        var cxc = tabla.Series[0].Resultado.Filas.Single(f => f.Dimensiones["tipo_cartera"].Clave == "CXC");
        Assert.Equal(3300m, cxc.Valor);                                                       // cartera calculada
        Assert.Equal(3300m, tabla.Series[1].Resultado.Filas.Single(f => f.Dimensiones["tipo_cartera"].Clave == "CXC").Valor);   // libro
        Assert.All(tabla.Series[2].Resultado.Filas, f => Assert.Equal(0m, f.Valor));          // diferencia
        Assert.Equal(0m, await KpiAsync("cartera.conciliacion", "diferencia", FiltrosDashboard.Ninguno));
    }

    // ---------- Permisos, módulos, aislamiento, caché ----------

    [Fact]
    public async Task Sin_el_permiso_del_modulo_no_se_ve_el_dashboard_ni_sus_datos()
    {
        var r = await EnviarAsync(new ObtenerDatosWidgetQuery("cartera.antiguedad", "cxc", FiltrosDashboard.Ninguno), null, "dash.financiero");
        Assert.False(r.EsExitoso);
        Assert.Equal("NO_AUTORIZADO", r.Errores[0].Codigo);

        var menu = (await EnviarAsync(new ListarDashboardsQuery(), null, "dash.financiero")).Valor;
        Assert.All(menu, d => Assert.Equal("FINANCIERO", d.Modulo));
        Assert.Equal(3, menu.Count);
    }

    [Fact]
    public async Task Un_modulo_inactivo_oculta_dashboards_y_los_widgets_que_dependen_de_el()
    {
        await using var solo = await Procesamiento.NuevaEmpresaAsync(bd, "FINANCIERO");       // sin CICLO_INGRESO ni CARTERA
        var gerencial = await EnviarAsync(new ObtenerDashboardQuery("financiero.gerencial"), solo.EmpresaId);
        Assert.True(gerencial.EsExitoso);
        var codigos = gerencial.Valor.Widgets.Select(w => w.Codigo).ToList();
        Assert.Contains("ingresos", codigos);
        Assert.DoesNotContain("facturado", codigos);        // pide CICLO_INGRESO
        Assert.DoesNotContain("cartera", codigos);          // pide CARTERA
        Assert.DoesNotContain("pagadores", codigos);

        var inactivo = await EnviarAsync(new ObtenerDashboardQuery("cartera.antiguedad"), solo.EmpresaId);
        Assert.Equal("MODULO_INACTIVO", inactivo.Errores[0].Codigo);
        var oculto = await EnviarAsync(new ObtenerDatosWidgetQuery("financiero.gerencial", "facturado", EneFeb), solo.EmpresaId);
        Assert.False(oculto.EsExitoso);

        var menu = (await EnviarAsync(new ListarDashboardsQuery(), solo.EmpresaId)).Valor;
        Assert.All(menu, d => Assert.Equal("FINANCIERO", d.Modulo));
    }

    [Fact]
    public async Task Otra_empresa_no_ve_las_cifras_de_esta_ni_con_los_mismos_permisos()
    {
        await using var otra = await Procesamiento.NuevaEmpresaAsync(bd, "FINANCIERO", "CICLO_INGRESO", "CARTERA");
        var d = await DatosAsync("financiero.gerencial", "ingresos", EneFeb, otra.EmpresaId);
        Assert.True(d.SinDatos);
        Assert.True((await DatosAsync("cartera.antiguedad", "cxc", FiltrosDashboard.Ninguno, otra.EmpresaId)).SinDatos);
        // la sesión de otra empresa pero con el contexto de BD de ESTA: la RLS no deja pasar nada de más y el motor filtra por la sesión
        Assert.Equal(3000m, await KpiAsync("financiero.gerencial", "ingresos"));
    }

    [Fact]
    public async Task Las_metas_dan_el_semaforo_del_kpi()
    {
        await using var c = await Escenarios.AbrirAsync(bd.CadenaPropietario, new ContextoBd(null, null, true, false, true));
        await Escenarios.Ejecutar(c, $"""
            DELETE cfg.KpiMeta WHERE EmpresaId = {EmpresaId};
            INSERT cfg.KpiMeta (EmpresaId, KpiCodigo, PeriodoKey, Meta, UmbralAlerta) VALUES ({EmpresaId}, 'MARGEN_OPERACIONAL', 202601, 80, 60);
            """);
        var feb = (await DatosAsync("financiero.gerencial", "margen", new FiltrosDashboard(202602, 202602))).Series[0];
        Assert.Equal(Semaforo.Verde, feb.Semaforo);           // 100 ≥ 80
        Assert.Equal(80m, feb.Meta!.Meta);
        var ene = (await DatosAsync("financiero.gerencial", "margen", new FiltrosDashboard(202601, 202601))).Series[0];
        Assert.Equal(Semaforo.Rojo, ene.Semaforo);            // 50 < 60
        var sinMeta = (await DatosAsync("financiero.gerencial", "ingresos")).Series[0];
        Assert.Equal(Semaforo.SinMeta, sinMeta.Semaforo);
        await Escenarios.Ejecutar(c, $"DELETE cfg.KpiMeta WHERE EmpresaId = {EmpresaId}");
    }

    [Fact]
    public async Task Los_resultados_se_cachean_hasta_que_termina_una_carga_de_la_empresa()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd, "FINANCIERO");
        await DatosDashboards.CargarAsync(p);
        var f = EneFeb;
        Assert.Equal(3000m, (await DatosAsync("financiero.gerencial", "ingresos", f, p.EmpresaId)).Series[0].Resultado.Total);

        await p.EjecutarAsync("DELETE fact.AsientoLinea WHERE EmpresaId = @e", new { e = p.EmpresaId });
        // el caché es por proceso: cada prueba arma su propio proveedor, así que se comprueba con UN proveedor y dos consultas seguidas
        await using var sp = bd.Construir(bd.CadenaApp, new SesionPrueba(4242, p.EmpresaId, false, TodosLosPermisos));
        async Task<decimal?> Consultar()
        {
            await using var al = sp.CreateAsyncScope();
            al.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeEmpresa(p.EmpresaId, 4242));
            var r = await al.ServiceProvider.GetRequiredService<ISender>().Send(new ObtenerDatosWidgetQuery("financiero.gerencial", "ingresos", f));
            return r.Valor.Series[0].Resultado.Total;
        }
        Assert.Null(await Consultar());                      // ya sin asientos
        await p.EjecutarAsync("""
            INSERT fact.AsientoLinea (EmpresaId, SourceKey, MovimientoSourceKey, FechaKey, PeriodoKey, TipoComprobanteId, NumeroComprobante, CuentaId, Debito, Credito, Estado, CargaId)
            SELECT TOP (1) @e, 'X-1', 'XM', 20260110, 202601, tc.TipoComprobanteId, '1', c.CuentaId, 0, 700, 1, ca.CargaId
            FROM dim.TipoComprobante tc, dim.CuentaContable c, etl.Carga ca
            WHERE tc.EmpresaId = @e AND c.EmpresaId = @e AND c.Codigo = '413501' AND ca.EmpresaId = @e
            """, new { e = p.EmpresaId });
        Assert.Null(await Consultar());                      // el caché lo conserva: no ha terminado ninguna carga
        await p.ProcesarAsync("sedes", "COMPLETA", new object[] { new { codigoOrigen = "ZZ", nombre = "Nueva" } });   // termina una carga → nueva versión
        Assert.Equal(700m, await Consultar());
    }
}
