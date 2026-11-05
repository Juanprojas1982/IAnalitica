using System.Reflection;
using IAnalitica.Domain.Modulos;
using static IAnalitica.Domain.Modulos.Declarar;
using static IAnalitica.Domain.Modulos.Unidades;

namespace IAnalitica.Modulos.Ingreso;

/// <summary>Punto de entrada del módulo para la composición de los hosts (mediador, métricas y dashboards se registran desde aquí).</summary>
public static class ModuloIngreso
{
    public static Assembly Ensamblado => typeof(ModuloIngreso).Assembly;

    /// <summary>Módulos analíticos de este proyecto. GLOSAS y TESORERÍA llegan en la fase 2.</summary>
    public static IReadOnlyList<IModuloAnalitico> Modulos { get; } = new IModuloAnalitico[] { new ModuloCicloIngreso(), new ModuloCartera() };
}

/// <summary>
/// CICLO_INGRESO (fase 1): facturación, factura electrónica y radicación, y re-trabajo (anulaciones).
/// Pendiente: «dinero detenido por etapa» y «atendido no facturado» (necesitan admisiones y prestaciones, aún sin ingesta).
/// </summary>
internal sealed class ModuloCicloIngreso : IModuloAnalitico
{
    public string Codigo => "CICLO_INGRESO";
    public string Nombre => "Ciclo del ingreso";

    public void Registrar(IRegistroModulo r)
    {
        var dimsFact = Dims(("periodo", "PeriodoKey"), ("sede", "SedeId"), ("pagador", "PagadorId"), ("centro_costo", "CentroCostoId"), ("linea_servicio", "LineaServicioId"), ("usuario", "UsuarioOpId"));
        var dimsEnvio = Dims(("periodo", "PeriodoKey"), ("sede", "SedeId"), ("pagador", "PagadorId"), ("estado_fe", "EstadoFe"));
        var dimsRetrabajo = Dims(("periodo", "PeriodoKey"), ("sede", "SedeId"), ("usuario", "UsuarioOpId"));

        r.Metrica(new("ciclo_ingreso.facturado", "Valor facturado", Pesos, SentidoMejora.MayorEsMejor, Codigo, "rpt.vw_Facturacion", "SUM(ValorFacturado)", dimsFact));
        r.Metrica(new("ciclo_ingreso.facturas", "Facturas", Unidades_, SentidoMejora.Neutro, Codigo, "rpt.vw_Facturacion", "SUM(Facturas)", dimsFact));
        r.Metrica(new("ciclo_ingreso.descuentos", "Descuentos otorgados", Pesos, SentidoMejora.MenorEsMejor, Codigo, "rpt.vw_Facturacion", "SUM(Descuentos)", dimsFact));

        r.Metrica(new("ciclo_ingreso.facturas_fe", "Facturas", Unidades_, SentidoMejora.Neutro, Codigo, "rpt.vw_EnvioFacturas", "SUM(Facturas)", dimsEnvio));
        r.Metrica(new("ciclo_ingreso.pct_problema_fe", "Facturas con error o sin enviar", Porcentaje, SentidoMejora.MenorEsMejor, Codigo, "rpt.vw_EnvioFacturas",
            "100.0*SUM(FacturasConProblemaFe)/NULLIF(SUM(Facturas),0)", dimsEnvio, KpiMeta: "PCT_FE_PROBLEMA"));
        r.Metrica(new("ciclo_ingreso.sin_radicar", "Facturado sin radicar", Pesos, SentidoMejora.MenorEsMejor, Codigo, "rpt.vw_EnvioFacturas", "SUM(ValorSinRadicar)", dimsEnvio));
        r.Metrica(new("ciclo_ingreso.facturas_sin_radicar", "Facturas sin radicar", Unidades_, SentidoMejora.MenorEsMejor, Codigo, "rpt.vw_EnvioFacturas", "SUM(FacturasSinRadicar)", dimsEnvio));
        r.Metrica(new("ciclo_ingreso.dias_a_radicacion", "Días de factura a radicación", Dias, SentidoMejora.MenorEsMejor, Codigo, "rpt.vw_EnvioFacturas", "AVG(DiasARadicacion)", dimsEnvio, KpiMeta: "DIAS_RADICACION"));

        r.Metrica(new("ciclo_ingreso.facturas_anuladas", "Facturas anuladas", Unidades_, SentidoMejora.MenorEsMejor, Codigo, "rpt.vw_Retrabajo", "SUM(FacturasAnuladas)", dimsRetrabajo));
        r.Metrica(new("ciclo_ingreso.valor_anulado", "Valor anulado", Pesos, SentidoMejora.MenorEsMejor, Codigo, "rpt.vw_Retrabajo", "SUM(ValorAnulado)", dimsRetrabajo));
        r.Metrica(new("ciclo_ingreso.pct_anulado", "Anulado sobre lo emitido", Porcentaje, SentidoMejora.MenorEsMejor, Codigo, "rpt.vw_Retrabajo",
            "100.0*SUM(ValorAnulado)/NULLIF(SUM(ValorEmitido),0)", dimsRetrabajo, KpiMeta: "PCT_ANULADO"));

        r.DashboardDeFabrica(new("ciclo_ingreso.facturacion", "Facturación", Codigo, "dash.ciclo_ingreso", 1, new[]
        {
            Kpi("facturado", "Valor facturado", "ciclo_ingreso.facturado", 0, 0, 4),
            Kpi("facturas", "Facturas", "ciclo_ingreso.facturas", 0, 4, 4),
            Kpi("descuentos", "Descuentos otorgados", "ciclo_ingreso.descuentos", 0, 8, 4),
            Grafico("tendencia", "Facturado por periodo", TipoWidget.Linea, new[] { "ciclo_ingreso.facturado" }, new[] { "periodo" }, 2, 0, 12, 3),
            Grafico("pagadores", "Facturado por pagador", TipoWidget.Barras, new[] { "ciclo_ingreso.facturado" }, new[] { "pagador" }, 5, 0, 6, 4),
            Grafico("lineas", "Facturado por línea de servicio", TipoWidget.Barras, new[] { "ciclo_ingreso.facturado" }, new[] { "linea_servicio" }, 5, 6, 6, 4),
            Grafico("sedes", "Facturación por sede", TipoWidget.Tabla, new[] { "ciclo_ingreso.facturado", "ciclo_ingreso.facturas", "ciclo_ingreso.descuentos" }, new[] { "sede" }, 9, 0, 12, 4),
        }));

        r.DashboardDeFabrica(new("ciclo_ingreso.fe_rips", "Factura electrónica y radicación", Codigo, "dash.ciclo_ingreso", 1, new[]
        {
            Kpi("problemas", "Facturas con error o sin enviar", "ciclo_ingreso.pct_problema_fe", 0, 0, 4),
            Kpi("sin_radicar", "Facturado sin radicar", "ciclo_ingreso.sin_radicar", 0, 4, 4),
            Kpi("dias", "Días de factura a radicación", "ciclo_ingreso.dias_a_radicacion", 0, 8, 4),
            Grafico("estados", "Facturas por estado de la factura electrónica", TipoWidget.Dona, new[] { "ciclo_ingreso.facturas_fe" }, new[] { "estado_fe" }, 2, 0, 5, 4),
            Grafico("pagadores", "Facturado sin radicar por pagador", TipoWidget.Barras, new[] { "ciclo_ingreso.sin_radicar" }, new[] { "pagador" }, 2, 5, 7, 4),
            Grafico("tendencia", "Días a radicación por periodo", TipoWidget.Linea, new[] { "ciclo_ingreso.dias_a_radicacion" }, new[] { "periodo" }, 6, 0, 12, 3),
        }));

        r.DashboardDeFabrica(new("ciclo_ingreso.retrabajo", "Re-trabajo de facturación", Codigo, "dash.ciclo_ingreso", 1, new[]
        {
            Kpi("pct", "Anulado sobre lo emitido", "ciclo_ingreso.pct_anulado", 0, 0, 4),
            Kpi("facturas", "Facturas anuladas", "ciclo_ingreso.facturas_anuladas", 0, 4, 4),
            Kpi("valor", "Valor anulado", "ciclo_ingreso.valor_anulado", 0, 8, 4),
            Grafico("tendencia", "Porcentaje anulado por periodo", TipoWidget.Linea, new[] { "ciclo_ingreso.pct_anulado" }, new[] { "periodo" }, 2, 0, 12, 3),
            Grafico("usuarios", "Anulaciones por usuario", TipoWidget.Tabla,
                new[] { "ciclo_ingreso.facturas_anuladas", "ciclo_ingreso.valor_anulado", "ciclo_ingreso.pct_anulado" }, new[] { "usuario" }, 5, 0, 12, 5),
        }));
    }
}

/// <summary>CARTERA (fase 1): antigüedad de cuentas por cobrar y por pagar, DSO por EPS y conciliación con el libro.</summary>
internal sealed class ModuloCartera : IModuloAnalitico
{
    public string Codigo => "CARTERA";
    public string Nombre => "Cartera";

    public void Registrar(IRegistroModulo r)
    {
        var dimsEdades = Dims(("corte", "CorteFechaKey"), ("tipo_cartera", "TipoCartera"), ("pagador", "PagadorId"), ("sede", "SedeId"), ("rango_edad", "RangoEdad"), ("tercero", "TerceroId"));
        var dimsPagador = Dims(("corte", "CorteFechaKey"), ("pagador", "PagadorId"));
        var dimsConciliacion = Dims(("corte", "CorteFechaKey"), ("tipo_cartera", "TipoCartera"));

        r.Metrica(new("cartera.saldo", "Saldo de cartera", Pesos, SentidoMejora.Neutro, Codigo, "rpt.vw_AntiguedadCartera", "SUM(Saldo)", dimsEdades));
        r.Metrica(new("cartera.saldo_cxc", "Cuentas por cobrar", Pesos, SentidoMejora.Neutro, Codigo, "rpt.vw_AntiguedadCartera", "SUM(Saldo)", dimsEdades, "TipoCartera = 'CXC'"));
        r.Metrica(new("cartera.saldo_cxp", "Cuentas por pagar", Pesos, SentidoMejora.Neutro, Codigo, "rpt.vw_AntiguedadCartera", "SUM(Saldo)", dimsEdades, "TipoCartera = 'CXP'"));
        r.Metrica(new("cartera.vencido_cxc_mas_90", "Por cobrar a más de 90 días", Pesos, SentidoMejora.MenorEsMejor, Codigo, "rpt.vw_AntiguedadCartera", "SUM(SaldoMas90)", dimsEdades, "TipoCartera = 'CXC'"));
        r.Metrica(new("cartera.pct_vencido_cxc", "Por cobrar a más de 90 días (%)", Porcentaje, SentidoMejora.MenorEsMejor, Codigo, "rpt.vw_AntiguedadCartera",
            "100.0*SUM(SaldoMas90)/NULLIF(SUM(Saldo),0)", dimsEdades, "TipoCartera = 'CXC'", KpiMeta: "PCT_CARTERA_90"));
        r.Metrica(new("cartera.documentos", "Documentos con saldo", Unidades_, SentidoMejora.Neutro, Codigo, "rpt.vw_AntiguedadCartera", "SUM(Documentos)", dimsEdades));

        r.Metrica(new("cartera.saldo_cxc_pagador", "Saldo por cobrar", Pesos, SentidoMejora.Neutro, Codigo, "rpt.vw_CarteraPagador", "SUM(SaldoCxC)", dimsPagador));
        r.Metrica(new("cartera.facturado_90", "Facturado en los últimos 90 días", Pesos, SentidoMejora.Neutro, Codigo, "rpt.vw_CarteraPagador", "SUM(Facturado90)", dimsPagador));
        r.Metrica(new("cartera.dso", "DSO (días de cartera)", Dias, SentidoMejora.MenorEsMejor, Codigo, "rpt.vw_CarteraPagador",
            "90.0*SUM(SaldoCxC)/NULLIF(SUM(Facturado90),0)", dimsPagador, KpiMeta: "DSO"));

        r.Metrica(new("cartera.saldo_cartera", "Saldo de la cartera calculada", Pesos, SentidoMejora.Neutro, Codigo, "rpt.vw_ConciliacionCartera", "SUM(SaldoCartera)", dimsConciliacion));
        r.Metrica(new("cartera.saldo_libro", "Saldo contable", Pesos, SentidoMejora.Neutro, Codigo, "rpt.vw_ConciliacionCartera", "SUM(SaldoLibro)", dimsConciliacion));
        r.Metrica(new("cartera.diferencia", "Diferencia", Pesos, SentidoMejora.MenorEsMejor, Codigo, "rpt.vw_ConciliacionCartera", "SUM(Diferencia)", dimsConciliacion));

        r.DashboardDeFabrica(new("cartera.antiguedad", "Antigüedad de cartera", Codigo, "dash.cartera", 1, new[]
        {
            Kpi("cxc", "Cuentas por cobrar", "cartera.saldo_cxc", 0, 0, 4),
            Kpi("vencido", "Por cobrar a más de 90 días", "cartera.pct_vencido_cxc", 0, 4, 4),
            Kpi("cxp", "Cuentas por pagar", "cartera.saldo_cxp", 0, 8, 4),
            Grafico("edades", "Cartera por edad", TipoWidget.BarrasApiladas, new[] { "cartera.saldo" }, new[] { "rango_edad", "tipo_cartera" }, 2, 0, 12, 4),
            Grafico("pagadores", "Por cobrar por pagador", TipoWidget.Barras, new[] { "cartera.saldo_cxc" }, new[] { "pagador" }, 6, 0, 12, 4, "cartera.dso"),
        }));

        r.DashboardDeFabrica(new("cartera.dso", "DSO por EPS", Codigo, "dash.cartera", 1, new[]
        {
            Kpi("dso", "DSO", "cartera.dso", 0, 0, 4),
            Kpi("saldo", "Saldo por cobrar", "cartera.saldo_cxc_pagador", 0, 4, 4),
            Kpi("facturado", "Facturado en 90 días", "cartera.facturado_90", 0, 8, 4),
            Grafico("dso_pagador", "DSO por pagador", TipoWidget.Barras, new[] { "cartera.dso" }, new[] { "pagador" }, 2, 0, 12, 4),
            Grafico("detalle", "Detalle por pagador", TipoWidget.Tabla, new[] { "cartera.saldo_cxc_pagador", "cartera.facturado_90", "cartera.dso" }, new[] { "pagador" }, 6, 0, 12, 5),
        }));

        r.DashboardDeFabrica(new("cartera.conciliacion", "Conciliación de cartera", Codigo, "dash.cartera", 1, new[]
        {
            Kpi("diferencia", "Diferencia contra el libro", "cartera.diferencia", 0, 0, 4),
            Grafico("tabla", "Cartera calculada frente al saldo contable", TipoWidget.Tabla,
                new[] { "cartera.saldo_cartera", "cartera.saldo_libro", "cartera.diferencia" }, new[] { "tipo_cartera" }, 0, 4, 8, 3),
        }));
    }
}
