using Dapper;
using IAnalitica.Integration.Tests.Infra;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Mediacion;
using IAnalitica.Application.Semantica;
using IAnalitica.Domain.Modulos;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>
/// Motor de costos (spec 24 §5.4) contra SQL Server real. Escenario a mano, periodo 202601:
/// costo directo ADM 600 · APO 300 · P1 100 · P2 100 (total 1.100) e ingresos P1 1.000 · P2 3.000.
/// ADM reparte por COSTO_PERSONAL (P1 400, P2 200, APO 400 → 240 / 120 / 240); APO (300 + 240 = 540) reparte por ACTIVIDADES (P1 10, P2 30 → 135 / 405).
/// Final: P1 = 100 + 240 + 135 = 475 · P2 = 100 + 120 + 405 = 625.
/// </summary>
[Collection(ColeccionBd.Nombre)]
public class CostosTests(BaseDeDatosFixture bd)
{
    private const int Periodo = 202601;

    private static Dictionary<string, object?> Mov(string key, string mov, string fecha, string cuenta, decimal debito, decimal credito, string? cc = null)
        => new Dictionary<string, object?>
        {
            ["sourceKey"] = key, ["movimientoSourceKey"] = mov, ["fecha"] = fecha, ["tipoComprobanteCodigo"] = "T1", ["numeroComprobante"] = "N-" + mov,
            ["cuentaCodigo"] = cuenta, ["debito"] = debito, ["credito"] = credito, ["estado"] = 1, ["sedeCodigo"] = "1", ["centroCostoCodigo"] = cc,
        }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>Las reglas (cfg) las escribe la aplicación, no el worker: aquí se preparan como propietario con identidad de superadmin.</summary>
    private static async Task ComoPropietarioAsync(BaseDeDatosFixture bd, string sql, object p)
    {
        await using var cn = new SqlConnection(bd.CadenaPropietario);
        await cn.OpenAsync();
        await cn.ExecuteAsync("EXEC sp_set_session_context N'EsSuperAdmin', 1");
        await cn.ExecuteAsync(sql, p);
    }

    private static async Task<Procesamiento> EscenarioAsync(BaseDeDatosFixture bd, params string[] modulos)
    {
        var p = await Procesamiento.NuevaEmpresaAsync(bd, modulos);
        await p.CargarCatalogosBaseAsync();
        await p.ProcesarAsync("cuentas", "COMPLETA", new object[]
        {
            new { codigo = "111005", nombre = "Bancos", nivel = 6, naturaleza = "D", esBanco = true, aceptaMovimiento = true },
            new { codigo = "413501", nombre = "Servicios de salud", nivel = 6, naturaleza = "C", aceptaMovimiento = true },
            new { codigo = "510506", nombre = "Sueldos", nivel = 6, naturaleza = "D", aceptaMovimiento = true },
        });
        await p.ProcesarAsync("centros-costo", "COMPLETA", new object[]
        {
            new { codigoOrigen = "ADM", nombre = "Administración" }, new { codigoOrigen = "APO", nombre = "Lavandería" },
            new { codigoOrigen = "P1", nombre = "Consulta externa" }, new { codigoOrigen = "P2", nombre = "Hospitalización" },
        });
        var asientos = new List<object>();
        var n = 0;
        foreach (var (cc, costo) in new[] { ("ADM", 600m), ("APO", 300m), ("P1", 100m), ("P2", 100m) })
        {
            n++;
            asientos.Add(Mov($"G{n}a", $"G{n}", "2026-01-15", "510506", costo, 0, cc));
            asientos.Add(Mov($"G{n}b", $"G{n}", "2026-01-15", "111005", 0, costo));
        }
        foreach (var (cc, ingreso) in new[] { ("P1", 1000m), ("P2", 3000m) })
        {
            n++;
            asientos.Add(Mov($"I{n}a", $"I{n}", "2026-01-20", "111005", ingreso, 0));
            asientos.Add(Mov($"I{n}b", $"I{n}", "2026-01-20", "413501", 0, ingreso, cc));
        }
        var r = await p.ProcesarAsync("asientos", "VENTANA", asientos, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Assert.Equal("OK", r.Estado);

        var carga = await p.CrearCargaAsync("sedes", "COMPLETA", Array.Empty<object>());
        var cc_ = await p.ConsultarAsync<(string Codigo, int Id)>("SELECT CodigoOrigen, CentroCostoId FROM dim.CentroCosto WHERE EmpresaId = @e");
        int Id(string c) => cc_.First(x => x.Codigo == c).Id;
        foreach (var (clave, centro, valor) in new[] { ("N1", "P1", 400m), ("N2", "P2", 200m), ("N3", "APO", 400m) })
            await p.EjecutarAsync("INSERT fact.NominaCosto (EmpresaId, SourceKey, PeriodoKey, ContratoCodigo, EmpleadoTerceroCodigo, ConceptoCodigo, Concepto, EsCosto, CentroCostoId, Valor, CargaId) VALUES (@e, @clave, @per, 'C', 'T', '1', N'Sueldo', 1, @centro, @valor, @carga)",
                new { e = p.EmpresaId, clave, per = Periodo, centro = Id(centro), valor, carga });
        await p.EjecutarAsync("INSERT fact.NominaCosto (EmpresaId, SourceKey, PeriodoKey, ContratoCodigo, EmpleadoTerceroCodigo, ConceptoCodigo, Concepto, EsCosto, CentroCostoId, Valor, CargaId) VALUES (@e, 'N4', @per, 'C', 'T', '2', N'Deducción', 0, @centro, 999, @carga)",
            new { e = p.EmpresaId, per = Periodo, centro = Id("P1"), carga });   // no es costo: no cuenta
        foreach (var (clave, centro, cantidad) in new[] { ("A1", "P1", 10m), ("A2", "P2", 30m) })
            await p.EjecutarAsync("INSERT fact.Prestacion (EmpresaId, SourceKey, AdmisionSourceKey, FechaRealizacionKey, Anulado, CentroCostoId, Cantidad, ValorUnitario, ValorTotal, CargaId) VALUES (@e, @clave, @clave, 20260110, 0, @centro, @cantidad, 1, @cantidad, @carga)",
                new { e = p.EmpresaId, clave, centro = Id(centro), cantidad, carga });
        await ComoPropietarioAsync(bd, "INSERT cfg.CostoRegla (EmpresaId, CentroCostoId, Orden, BaseCodigo, VigenteDesde) VALUES (@e, @adm, 1, 'COSTO_PERSONAL', 202601), (@e, @apo, 2, 'ACTIVIDADES', 202601)",
            new { e = p.EmpresaId, adm = Id("ADM"), apo = Id("APO") });
        return p;
    }

    private static async Task CalcularAsync(Procesamiento p, int periodo = Periodo)
    {
        await using var cn = await p.EtlAsync();
        await cn.ExecuteAsync("etl.usp_CalcularBasesCosto", new { EmpresaId = p.EmpresaId, PeriodoKey = periodo }, commandType: System.Data.CommandType.StoredProcedure);
        await cn.ExecuteAsync("etl.usp_DistribuirCostos", new { EmpresaId = p.EmpresaId, PeriodoKey = periodo }, commandType: System.Data.CommandType.StoredProcedure);
    }

    private static Task<Dictionary<string, decimal>> TotalesAsync(Procesamiento p)
        => p.ConsultarAsync<(string Codigo, decimal Costo)>("""
            SELECT cc.CodigoOrigen, r.CostoTotal FROM rpt.vw_RentabilidadCentroCosto r JOIN dim.CentroCosto cc ON cc.CentroCostoId = r.CentroCostoId
            WHERE r.EmpresaId = @e AND r.PeriodoKey = 202601
            """).ContinueWith(t => t.Result.ToDictionary(x => x.Codigo, x => x.Costo));

    [Fact]
    public async Task Calcula_las_bases_desde_los_hechos_y_excluye_lo_que_no_es_costo()
    {
        await using var p = await EscenarioAsync(bd);
        await CalcularAsync(p);
        var bases = (await p.ConsultarAsync<(string Base, string Cc, decimal Valor)>("""
            SELECT b.BaseCodigo, cc.CodigoOrigen, b.Valor FROM fact.CostoBaseValor b JOIN dim.CentroCosto cc ON cc.CentroCostoId = b.CentroCostoId WHERE b.EmpresaId = @e AND b.PeriodoKey = 202601
            """)).ToDictionary(x => (x.Base, x.Cc), x => x.Valor);
        Assert.Equal(400m, bases[("COSTO_PERSONAL", "P1")]);                // la deducción de 999 no es costo
        Assert.Equal(200m, bases[("COSTO_PERSONAL", "P2")]);
        Assert.Equal(400m, bases[("COSTO_PERSONAL", "APO")]);
        Assert.Equal(10m, bases[("ACTIVIDADES", "P1")]);
        Assert.Equal(30m, bases[("ACTIVIDADES", "P2")]);
        // INGRESO sale de las facturas, que este escenario no carga
        Assert.False(bases.ContainsKey(("INGRESO", "P1")));
    }

    [Fact]
    public async Task Distribuye_en_cascada_conserva_el_total_y_deja_la_rentabilidad_correcta()
    {
        await using var p = await EscenarioAsync(bd);
        await CalcularAsync(p);
        var pasos = (await p.ConsultarAsync<(short Paso, string Origen, string Destino, decimal Valor)>("""
            SELECT d.Paso, o.CodigoOrigen, t.CodigoOrigen, d.Valor FROM fact.CostoDistribuido d
            JOIN dim.CentroCosto o ON o.CentroCostoId = d.CentroCostoOrigenId JOIN dim.CentroCosto t ON t.CentroCostoId = d.CentroCostoDestinoId WHERE d.EmpresaId = @e ORDER BY d.Paso, t.CodigoOrigen
            """));
        Assert.Equal(new (short, string, string, decimal)[]
        {
            (1, "ADM", "APO", 240m), (1, "ADM", "P1", 240m), (1, "ADM", "P2", 120m),
            (2, "APO", "P1", 135m), (2, "APO", "P2", 405m),
        }, pasos);

        var costo = await TotalesAsync(p);
        Assert.Equal(475m, costo["P1"]);
        Assert.Equal(625m, costo["P2"]);
        Assert.Equal(0m, costo["ADM"]);
        Assert.Equal(0m, costo["APO"]);
        Assert.Equal(1100m, costo.Values.Sum());                              // conservación del costo total
        var margen = await p.ConsultarAsync<(string Cc, decimal Margen)>("SELECT cc.CodigoOrigen, r.Margen FROM rpt.vw_RentabilidadCentroCosto r JOIN dim.CentroCosto cc ON cc.CentroCostoId = r.CentroCostoId WHERE r.EmpresaId = @e AND r.PeriodoKey = 202601");
        Assert.Equal(525m, margen.First(x => x.Cc == "P1").Margen);            // 1.000 − 475
        Assert.Equal(2375m, margen.First(x => x.Cc == "P2").Margen);           // 3.000 − 625
    }

    [Fact]
    public async Task Recalcular_es_idempotente_y_reemplaza_el_resultado()
    {
        await using var p = await EscenarioAsync(bd);
        await CalcularAsync(p);
        await CalcularAsync(p);
        Assert.Equal(5, await p.ContarAsync("fact.CostoDistribuido"));
        await ComoPropietarioAsync(bd, "UPDATE cfg.CostoRegla SET BaseCodigo = 'COSTO_PERSONAL' WHERE EmpresaId = @e AND Orden = 2", new { e = p.EmpresaId });
        await CalcularAsync(p);
        var costo = await TotalesAsync(p);
        Assert.Equal(1100m, costo.Values.Sum());
        Assert.NotEqual(475m, costo["P1"]);                                    // otra base, otro reparto
    }

    [Fact]
    public async Task Un_centro_sin_base_conserva_su_costo_y_se_reporta_en_el_origen()
    {
        await using var p = await EscenarioAsync(bd);
        await p.EjecutarAsync("DELETE fact.Prestacion WHERE EmpresaId = @e", new { e = p.EmpresaId });          // APO no tiene actividades que repartir
        await CalcularAsync(p);
        var costo = await TotalesAsync(p);
        Assert.Equal(540m, costo["APO"]);                                      // 300 propios + 240 de ADM, sin destino
        Assert.Equal(1100m, costo.Values.Sum());
    }

    [Fact]
    public async Task Una_empresa_no_toca_los_costos_de_otra()
    {
        await using var a = await EscenarioAsync(bd);
        await using var b = await EscenarioAsync(bd);
        await CalcularAsync(a);
        Assert.Equal(5, await a.ContarAsync("fact.CostoDistribuido"));
        Assert.Equal(0, await b.ContarAsync("fact.CostoDistribuido"));
        await CalcularAsync(b);
        Assert.Equal(5, await a.ContarAsync("fact.CostoDistribuido"));
        Assert.Equal(5, await b.ContarAsync("fact.CostoDistribuido"));
    }

    private static object[] UnMovimiento(string fecha) => new object[]
    {
        Mov("X1", "X", fecha, "510506", 50, 0, "P1"), Mov("X2", "X", fecha, "111005", 0, 50),
    };

    [Fact]
    public async Task Cargar_contabilidad_encola_un_calculo_por_mes_de_la_ventana_si_hay_reglas()
    {
        await using var p = await EscenarioAsync(bd);
        await p.ProcesarAsync("asientos", "VENTANA", UnMovimiento("2026-03-10"), new DateOnly(2026, 2, 15), new DateOnly(2026, 4, 20));
        var periodos = await p.ConsultarAsync<int>("SELECT CAST(JSON_VALUE(Payload, '$.periodo') AS int) FROM etl.Trabajo WHERE EmpresaId = @e AND Tipo = 'CALCULAR_COSTOS' ORDER BY 1");
        Assert.Equal(new[] { 202602, 202603, 202604 }, periodos);
    }

    [Fact]
    public async Task Sin_reglas_de_distribucion_no_se_encola_nada()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        await p.CargarCatalogosBaseAsync();
        await p.ProcesarAsync("asientos", "VENTANA", new object[] { Procesamiento.Linea("L1", "M1", "2026-01-10", "130505", 100, 0), Procesamiento.Linea("L2", "M1", "2026-01-10", "413501", 0, 100) }, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        Assert.Equal(0, await p.EscalarAsync<int>("SELECT COUNT(*) FROM etl.Trabajo WHERE EmpresaId = @e AND Tipo = 'CALCULAR_COSTOS'", new { e = p.EmpresaId }));
    }

    [Fact]
    public async Task El_worker_calcula_el_periodo_encolado()
    {
        await using var p = await EscenarioAsync(bd);
        await p.EncolarCostosPruebaAsync(Periodo);
        while (await p.Despachador.EjecutarUnoAsync(IAnalitica.Application.Abstracciones.TiposTrabajo.CalcularCostos, CancellationToken.None)) { }
        Assert.Equal(5, await p.ContarAsync("fact.CostoDistribuido", "PeriodoKey = 202601"));
    }

    [Fact]
    public async Task El_dashboard_de_resultados_con_distribucion_muestra_el_margen_por_centro()
    {
        await using var p = await EscenarioAsync(bd, "FINANCIERO");
        await CalcularAsync(p);
        var sesion = new SesionPrueba(4242, p.EmpresaId, false, "dash.financiero");
        await using var sp = bd.Construir(bd.CadenaApp, sesion);
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeEmpresa(p.EmpresaId, 4242));
        var enviar = alcance.ServiceProvider.GetRequiredService<ISender>();
        var filtros = new FiltrosDashboard(Periodo, Periodo);

        var margen = await enviar.Send(new ObtenerDatosWidgetQuery("financiero.pyg_distribuido", "margen", filtros));
        Assert.True(margen.EsExitoso, string.Join("; ", margen.Errores.Select(e => e.Mensaje)));
        Assert.Equal(2900m, margen.Valor.Series[0].Resultado.Total);                  // 4.000 de ingresos − 1.100 de costo total

        var centros = await enviar.Send(new ObtenerDatosWidgetQuery("financiero.pyg_distribuido", "margen_centros", filtros));
        var porCentro = centros.Valor.Series[0].Resultado.Filas.ToDictionary(f => f.Dimensiones.Values.First().Etiqueta, f => f.Valor);
        Assert.Equal(525m, porCentro["Consulta externa"]);
        Assert.Equal(2375m, porCentro["Hospitalización"]);
    }
}
