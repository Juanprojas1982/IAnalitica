using System.Reflection;
using IAnalitica.Domain.Modulos;
using static IAnalitica.Domain.Modulos.Declarar;
using static IAnalitica.Domain.Modulos.Unidades;

namespace IAnalitica.Modulos.Financiero;

/// <summary>Punto de entrada del módulo para la composición de los hosts (mediador, métricas y dashboards se registran desde aquí).</summary>
public static class ModuloFinanciero
{
    public static Assembly Ensamblado => typeof(ModuloFinanciero).Assembly;
    public static IModuloAnalitico Modulo { get; } = new ModuloFinancieroAnalitico();
}

/// <summary>
/// FINANCIERO (fase 1, 24 §5.3): gerencial, P&amp;G sin distribución y balance con razones, calculados desde los asientos cargados.
/// Pendiente: P&amp;G con distribución (F5), flujo de caja (necesita tesorería) y ejecución presupuestal (necesita presupuesto).
/// </summary>
internal sealed class ModuloFinancieroAnalitico : IModuloAnalitico
{
    public string Codigo => "FINANCIERO";
    public string Nombre => "Financiero";

    private const string Resultados = "rpt.vw_EstadoResultados";
    private const string Balance = "rpt.vw_SaldoBalance";
    private const string Rentabilidad = "rpt.vw_RentabilidadCentroCosto";

    public void Registrar(IRegistroModulo r)
    {
        var dimsResultados = Dims(("periodo", "PeriodoKey"), ("sede", "SedeId"), ("centro_costo", "CentroCostoId"), ("clase_puc", "ClasePuc"),
            ("grupo_resultados", "GrupoEstadoResultados"), ("grupo_puc", "Grupo"), ("cuenta", "CuentaId"));
        var dimsBalance = Dims(("periodo", "PeriodoKey"), ("sede", "SedeId"), ("clase_puc", "ClasePuc"), ("grupo_balance", "GrupoBalance"), ("grupo_puc", "Grupo"), ("cuenta", "CuentaId"));

        // ---- Estado de resultados (vw_EstadoResultados) ----
        r.Metrica(new("financiero.ingresos", "Ingresos", Pesos, SentidoMejora.MayorEsMejor, Codigo, Resultados, "SUM(Saldo)", dimsResultados, "ClasePuc = '4'"));
        r.Metrica(new("financiero.costos_gastos", "Costos y gastos", Pesos, SentidoMejora.MenorEsMejor, Codigo, Resultados, "SUM(Saldo)", dimsResultados, "ClasePuc IN ('5','6','7')"));
        r.Metrica(new("financiero.utilidad", "Utilidad del periodo", Pesos, SentidoMejora.MayorEsMejor, Codigo, Resultados, "SUM(Utilidad)", dimsResultados));
        r.Metrica(new("financiero.margen", "Margen", Porcentaje, SentidoMejora.MayorEsMejor, Codigo, Resultados, "100.0*SUM(Utilidad)/NULLIF(SUM(Ingreso),0)", dimsResultados, KpiMeta: "MARGEN_OPERACIONAL"));

        // ---- Balance y razones (vw_SaldoBalance): saldos acumulados hasta el periodo final ----
        r.Metrica(new("financiero.activo_total", "Activo total", Pesos, SentidoMejora.Neutro, Codigo, Balance, "SUM(ActivoTotal)", dimsBalance, Acumulado: true));
        r.Metrica(new("financiero.pasivo_total", "Pasivo total", Pesos, SentidoMejora.Neutro, Codigo, Balance, "SUM(PasivoTotal)", dimsBalance, Acumulado: true));
        r.Metrica(new("financiero.patrimonio", "Patrimonio", Pesos, SentidoMejora.Neutro, Codigo, Balance, "SUM(Patrimonio)", dimsBalance, Acumulado: true));
        r.Metrica(new("financiero.saldo_balance", "Saldo", Pesos, SentidoMejora.Neutro, Codigo, Balance, "SUM(Saldo)", dimsBalance, Acumulado: true));
        r.Metrica(new("financiero.capital_trabajo", "Capital de trabajo", Pesos, SentidoMejora.MayorEsMejor, Codigo, Balance, "SUM(CapitalTrabajo)", dimsBalance, Acumulado: true));
        r.Metrica(new("financiero.razon_corriente", "Razón corriente", Veces, SentidoMejora.MayorEsMejor, Codigo, Balance,
            "SUM(ActivoCorriente)/NULLIF(SUM(PasivoCorriente),0)", dimsBalance, KpiMeta: "RAZON_CORRIENTE", Acumulado: true));
        r.Metrica(new("financiero.prueba_acida", "Prueba ácida", Veces, SentidoMejora.MayorEsMejor, Codigo, Balance,
            "SUM(ActivoCorrienteSinInv)/NULLIF(SUM(PasivoCorriente),0)", dimsBalance, Acumulado: true));
        r.Metrica(new("financiero.endeudamiento", "Endeudamiento", Porcentaje, SentidoMejora.MenorEsMejor, Codigo, Balance,
            "100.0*SUM(PasivoTotal)/NULLIF(SUM(ActivoTotal),0)", dimsBalance, KpiMeta: "ENDEUDAMIENTO", Acumulado: true));

        // ---- Rentabilidad por centro de costo con distribución (vw_RentabilidadCentroCosto; la llena el motor de costos, F5) ----
        var dimsRentabilidad = Dims(("periodo", "PeriodoKey"), ("centro_costo", "CentroCostoId"));
        r.Metrica(new("financiero.ingreso_centro", "Ingreso del centro de costo", Pesos, SentidoMejora.MayorEsMejor, Codigo, Rentabilidad, "SUM(Ingreso)", dimsRentabilidad));
        r.Metrica(new("financiero.costo_directo_centro", "Costo directo", Pesos, SentidoMejora.MenorEsMejor, Codigo, Rentabilidad, "SUM(CostoDirecto)", dimsRentabilidad));
        r.Metrica(new("financiero.costo_recibido_centro", "Costo recibido por distribución", Pesos, SentidoMejora.MenorEsMejor, Codigo, Rentabilidad, "SUM(CostoRecibido)", dimsRentabilidad));
        r.Metrica(new("financiero.costo_total_centro", "Costo total (directo + recibido − entregado)", Pesos, SentidoMejora.MenorEsMejor, Codigo, Rentabilidad, "SUM(CostoTotal)", dimsRentabilidad));
        r.Metrica(new("financiero.margen_centro", "Margen con distribución", Pesos, SentidoMejora.MayorEsMejor, Codigo, Rentabilidad, "SUM(Margen)", dimsRentabilidad));
        r.Metrica(new("financiero.margen_centro_pct", "Margen con distribución (%)", Porcentaje, SentidoMejora.MayorEsMejor, Codigo, Rentabilidad,
            "100.0*SUM(Margen)/NULLIF(SUM(Ingreso),0)", dimsRentabilidad));

        // ---- Dashboards ----
        r.DashboardDeFabrica(new("financiero.gerencial", "Gerencial", Codigo, "dash.financiero", 1, new[]
        {
            Kpi("ingresos", "Ingresos", "financiero.ingresos", 0, 0, drill: "financiero.pyg"),
            Kpi("costos", "Costos y gastos", "financiero.costos_gastos", 0, 3, drill: "financiero.pyg"),
            Kpi("utilidad", "Utilidad", "financiero.utilidad", 0, 6, drill: "financiero.pyg"),
            Kpi("margen", "Margen", "financiero.margen", 0, 9, drill: "financiero.pyg"),
            Grafico("tendencia", "Ingresos, costos y utilidad por periodo", TipoWidget.Linea,
                new[] { "financiero.ingresos", "financiero.costos_gastos", "financiero.utilidad" }, new[] { "periodo" }, 2, 0, 8, 4),
            Kpi("facturado", "Facturado en el periodo", "ciclo_ingreso.facturado", 2, 8, 4, 2, "ciclo_ingreso.facturacion", "CICLO_INGRESO"),
            Kpi("cartera", "Cartera por cobrar", "cartera.saldo_cxc", 4, 8, 4, 2, "cartera.antiguedad", "CARTERA"),
            Grafico("pagadores", "Facturado por pagador", TipoWidget.Barras, new[] { "ciclo_ingreso.facturado" }, new[] { "pagador" }, 6, 0, 12, 4,
                "ciclo_ingreso.facturacion", "CICLO_INGRESO"),
        }));

        r.DashboardDeFabrica(new("financiero.pyg", "Estado de resultados", Codigo, "dash.financiero", 1, new[]
        {
            Grafico("cascada", "De los ingresos a la utilidad", TipoWidget.Cascada, new[] { "financiero.utilidad" }, new[] { "clase_puc" }, 0, 0, 6, 4),
            Grafico("grupos", "Resultado por grupo del PUC", TipoWidget.Tabla, new[] { "financiero.utilidad" }, new[] { "grupo_puc" }, 0, 6, 6, 4),
            Grafico("tendencia", "Utilidad por periodo", TipoWidget.Linea, new[] { "financiero.utilidad" }, new[] { "periodo" }, 4, 0, 12, 3),
            Grafico("centros", "Resultado por centro de costo (sin distribución de costos indirectos)", TipoWidget.Tabla,
                new[] { "financiero.ingresos", "financiero.costos_gastos", "financiero.utilidad", "financiero.margen" }, new[] { "centro_costo" }, 7, 0, 12, 5),
        }));

        r.DashboardDeFabrica(new("financiero.balance", "Balance y razones", Codigo, "dash.financiero", 1, new[]
        {
            Kpi("razon_corriente", "Razón corriente", "financiero.razon_corriente", 0, 0),
            Kpi("prueba_acida", "Prueba ácida", "financiero.prueba_acida", 0, 3),
            Kpi("capital_trabajo", "Capital de trabajo", "financiero.capital_trabajo", 0, 6),
            Kpi("endeudamiento", "Endeudamiento", "financiero.endeudamiento", 0, 9),
            Grafico("grupos", "Saldo por grupo del balance", TipoWidget.Barras, new[] { "financiero.saldo_balance" }, new[] { "grupo_balance" }, 2, 0, 6, 4),
            Grafico("composicion", "Activo, pasivo y patrimonio", TipoWidget.Tabla,
                new[] { "financiero.activo_total", "financiero.pasivo_total", "financiero.patrimonio" }, Array.Empty<string>(), 2, 6, 6, 4),
            Grafico("detalle", "Saldo por grupo del PUC", TipoWidget.Tabla, new[] { "financiero.saldo_balance" }, new[] { "grupo_puc" }, 6, 0, 12, 5),
        }));

        r.DashboardDeFabrica(new("financiero.pyg_distribuido", "Estado de resultados con distribución de costos", Codigo, "dash.financiero", 1, new[]
        {
            Kpi("ingresos", "Ingresos de los centros", "financiero.ingreso_centro", 0, 0),
            Kpi("costo_total", "Costo total distribuido", "financiero.costo_total_centro", 0, 3),
            Kpi("margen", "Margen con distribución", "financiero.margen_centro", 0, 6),
            Kpi("margen_pct", "Margen con distribución (%)", "financiero.margen_centro_pct", 0, 9),
            Grafico("margen_centros", "Margen por centro de costo", TipoWidget.Barras, new[] { "financiero.margen_centro" }, new[] { "centro_costo" }, 2, 0, 6, 4),
            Grafico("tendencia", "Ingresos y costo total por periodo", TipoWidget.Linea,
                new[] { "financiero.ingreso_centro", "financiero.costo_total_centro" }, new[] { "periodo" }, 2, 6, 6, 4),
            Grafico("detalle", "Del costo directo al costo total, por centro de costo", TipoWidget.Tabla,
                new[] { "financiero.ingreso_centro", "financiero.costo_directo_centro", "financiero.costo_recibido_centro", "financiero.costo_total_centro", "financiero.margen_centro", "financiero.margen_centro_pct" },
                new[] { "centro_costo" }, 6, 0, 12, 6),
        }));
    }
}
