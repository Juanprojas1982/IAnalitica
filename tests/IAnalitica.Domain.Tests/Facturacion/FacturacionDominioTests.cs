using IAnalitica.Domain.Facturacion;

namespace IAnalitica.Domain.Tests.Facturacion;

public class FacturacionDominioTests
{
    [Theory]
    [InlineData("BORRADOR", "EMITIDA", true)]
    [InlineData("BORRADOR", "DESCARTADA", true)]
    [InlineData("BORRADOR", "PAGADA", false)]
    [InlineData("EMITIDA", "PAGADA", true)]
    [InlineData("EMITIDA", "ANULADA", true)]
    [InlineData("PAGADA", "EMITIDA", true)]     // reverso
    [InlineData("PAGADA", "ANULADA", true)]
    [InlineData("ANULADA", "EMITIDA", false)]   // la anulada es final; se corrige con otra CC
    [InlineData("DESCARTADA", "BORRADOR", false)]
    [InlineData("EMITIDA", "BORRADOR", false)]
    public void Transiciones_de_estado(string de, string a, bool esperado) => Assert.Equal(esperado, EstadosCuentaCobro.PuedePasar(de, a));

    [Fact]
    public void El_cliente_no_ve_borradores_ni_descartadas()
    {
        Assert.False(EstadosCuentaCobro.VisibleParaCliente("BORRADOR"));
        Assert.False(EstadosCuentaCobro.VisibleParaCliente("DESCARTADA"));
        Assert.True(EstadosCuentaCobro.VisibleParaCliente("EMITIDA"));
        Assert.True(EstadosCuentaCobro.VisibleParaCliente("ANULADA"));
    }

    [Fact]
    public void El_canon_pactado_gana_sobre_el_del_plan_y_sin_ninguno_es_nulo()
    {
        Assert.Equal(900m, Canon.DelPeriodo(900m, 500m));
        Assert.Equal(500m, Canon.DelPeriodo(null, 500m));
        Assert.Equal(0m, Canon.DelPeriodo(0m, 500m));   // pilotos: canon pactado en cero
        Assert.Null(Canon.DelPeriodo(null, null));
    }

    [Fact]
    public void Mes_completo_no_se_prorratea()
    {
        var l = Assert.Single(Prorrateo.Calcular(202610, new[] { new TramoCanon(new DateOnly(2026, 1, 1), new DateOnly(2099, 12, 31), 1_000_000m) }));
        Assert.Equal(31, l.DiasFacturados);
        Assert.Equal(31, l.DiasPeriodo);
        Assert.Equal(1_000_000m, l.ValorCop);
        Assert.Equal(new DateOnly(2026, 10, 1), l.Desde);
        Assert.Equal(new DateOnly(2026, 10, 31), l.Hasta);
    }

    [Fact]
    public void Alta_a_mitad_de_mes_prorratea_con_dia_de_alta_inclusive()
    {
        // Alta el 18-ene: 14 de 31 días → 1.000.000 × 14 / 31 = 451.612,9 → 451.613
        var l = Assert.Single(Prorrateo.Calcular(202601, new[] { new TramoCanon(new DateOnly(2026, 1, 18), new DateOnly(2099, 12, 31), 1_000_000m) }));
        Assert.Equal(14, l.DiasFacturados);
        Assert.Equal(451_613m, l.ValorCop);
    }

    [Fact]
    public void Baja_a_mitad_de_mes_incluye_el_ultimo_dia()
    {
        // Último día de servicio 10-feb-2026 (28 días): 10/28 → 357.142,857… → 357.143
        var l = Assert.Single(Prorrateo.Calcular(202602, new[] { new TramoCanon(new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 10), 1_000_000m) }));
        Assert.Equal(10, l.DiasFacturados);
        Assert.Equal(28, l.DiasPeriodo);
        Assert.Equal(357_143m, l.ValorCop);
    }

    [Fact]
    public void Cambio_de_condicion_a_mitad_de_mes_genera_una_linea_por_tramo()
    {
        var tramos = new[]
        {
            new TramoCanon(new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 9), 800_000m),     // 9 días de junio
            new TramoCanon(new DateOnly(2026, 6, 10), new DateOnly(2099, 12, 31), 1_200_000m), // 21 días de junio
        };
        var lineas = Prorrateo.Calcular(202606, tramos);
        Assert.Equal(2, lineas.Count);
        Assert.Equal(9, lineas[0].DiasFacturados);
        Assert.Equal(240_000m, lineas[0].ValorCop);   // 800.000 × 9 / 30
        Assert.Equal(21, lineas[1].DiasFacturados);
        Assert.Equal(840_000m, lineas[1].ValorCop);   // 1.200.000 × 21 / 30
        Assert.Equal(30, lineas.Sum(l => l.DiasFacturados));   // cubre el mes sin huecos ni solapes
    }

    [Fact]
    public void Un_tramo_fuera_del_mes_no_genera_linea_y_el_periodo_invalido_falla()
    {
        Assert.Empty(Prorrateo.Calcular(202603, new[] { new TramoCanon(new DateOnly(2026, 4, 1), new DateOnly(2099, 12, 31), 1m) }));
        Assert.Empty(Prorrateo.Calcular(202603, new[] { new TramoCanon(new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 28), 1m) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Prorrateo.Calcular(202613, Array.Empty<TramoCanon>()));
        Assert.Equal(202512, Prorrateo.PeriodoAnterior(202601));
        Assert.Equal(202701, Prorrateo.PeriodoSiguiente(202612));
    }

    private static EntradaReglasEmision Entrada(decimal? canon = 1_000_000m, int destinatarios = 1, decimal total = 1_000_000m, decimal? anterior = 1_000_000m,
        bool aprobacion = false, params string[] ia)
        => new(canon, destinatarios, total, anterior, 50m, aprobacion, ia);

    [Fact]
    public void Sin_motivos_se_emite_y_envia()
        => Assert.Empty(ReglasEmision.MotivosRetencion(Entrada()));

    [Fact]
    public void Sin_canon_o_sin_destinatario_se_retiene()
    {
        Assert.Equal(new[] { "SIN_CANON" }, ReglasEmision.MotivosRetencion(Entrada(canon: null, total: 0, anterior: null)));
        Assert.Equal(new[] { "SIN_DESTINATARIO" }, ReglasEmision.MotivosRetencion(Entrada(destinatarios: 0)));
    }

    [Theory]
    [InlineData(1_500_000, false)]   // +50 %: en el límite, no se retiene
    [InlineData(1_500_001, true)]
    [InlineData(400_000, true)]      // -60 %
    [InlineData(500_000, false)]     // -50 %
    public void Variacion_anomala_frente_al_mes_anterior(int total, bool retiene)
        => Assert.Equal(retiene, ReglasEmision.MotivosRetencion(Entrada(total: total)).Contains("VARIACION_ANOMALA"));

    [Fact]
    public void Sin_mes_anterior_no_hay_variacion_y_la_suspension_no_retiene()
    {
        Assert.Empty(ReglasEmision.MotivosRetencion(Entrada(anterior: null, total: 99_000_000m)));
        Assert.Empty(ReglasEmision.MotivosRetencion(Entrada(anterior: 0m, total: 99_000_000m)));
        // D51: la entrada ni siquiera tiene un indicador de empresa activa.
        Assert.DoesNotContain(typeof(EntradaReglasEmision).GetProperties(), p => p.Name.Contains("Activa", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Aprobacion_requerida_y_advertencias_de_ia_se_acumulan_sin_duplicar()
    {
        var m = ReglasEmision.MotivosRetencion(Entrada(aprobacion: true, ia: new[] { "CUADRE_IA", "CUADRE_IA", "CAMBIO_CONFIG_IA" }));
        Assert.Equal(new[] { "CUADRE_IA", "CAMBIO_CONFIG_IA", "APROBACION_REQUERIDA" }, m);
    }

    [Fact]
    public void El_interruptor_de_la_empresa_gana_sobre_el_global()
    {
        Assert.True(ReglasEmision.RequiereAprobacion(null, true));
        Assert.False(ReglasEmision.RequiereAprobacion(false, true));
        Assert.True(ReglasEmision.RequiereAprobacion(true, false));
        Assert.False(ReglasEmision.RequiereAprobacion(null, false));
    }

    [Fact]
    public void Excedente_ia_cobra_el_menor_entre_bruto_y_tope_solo_con_consumo_autorizado()
    {
        var r = LiquidadorExcedenteIa.Calcular(new(150_000m, 100_000m, 50_000m, true, 30_000m, false, 150_000m, 1000m));
        Assert.Equal(30_000m, r.FacturableCop);
        Assert.Equal(20_000m, r.AbsorbidoCop);
        Assert.Equal(30_000m, r.TopeAplicadoCop);
        Assert.Empty(r.Advertencias);

        var sinTope = LiquidadorExcedenteIa.Calcular(new(150_000m, 100_000m, 50_000m, true, null, false, 150_000m, 1000m));
        Assert.Equal(50_000m, sinTope.FacturableCop);

        var noAutorizado = LiquidadorExcedenteIa.Calcular(new(150_000m, 100_000m, 50_000m, false, 30_000m, false, 150_000m, 1000m));
        Assert.Equal(0m, noAutorizado.FacturableCop);
        Assert.Equal(50_000m, noAutorizado.AbsorbidoCop);
    }

    [Fact]
    public void Excedente_ia_advierte_cambio_de_configuracion_y_descuadre()
    {
        var r = LiquidadorExcedenteIa.Calcular(new(150_000m, 100_000m, 50_000m, true, null, true, 140_000m, 1000m));
        Assert.Equal(10_000m, r.DiferenciaCuadreCop);
        Assert.Equal(new[] { "CAMBIO_CONFIG_IA", "CUADRE_IA" }, r.Advertencias);
        Assert.Empty(LiquidadorExcedenteIa.Calcular(new(150_000m, 100_000m, 50_000m, true, null, false, 150_500m, 1000m)).Advertencias);   // dentro del umbral
    }

    [Fact]
    public void El_vencimiento_cuenta_dias_calendario()
        => Assert.Equal(new DateOnly(2026, 12, 1), Vencimiento.Calcular(new DateOnly(2026, 11, 1), 30));

    [Theory]
    [InlineData("2026-12-01", "2026-12-01", "AL_DIA")]   // el día del vencimiento aún está al día
    [InlineData("2026-12-01", "2026-12-02", "1_30")]
    [InlineData("2026-12-01", "2026-12-31", "1_30")]
    [InlineData("2026-12-01", "2027-01-01", "31_60")]
    [InlineData("2026-12-01", "2027-01-30", "31_60")]
    [InlineData("2026-12-01", "2027-01-31", "61_90")]
    [InlineData("2026-12-01", "2027-03-01", "61_90")]
    [InlineData("2026-12-01", "2027-03-02", "MAS_90")]
    [InlineData("2026-12-10", "2026-12-01", "AL_DIA")]
    public void Edad_de_cartera(string vence, string hoy, string esperado)
        => Assert.Equal(esperado, EdadCartera.Rango(DateOnly.Parse(vence), DateOnly.Parse(hoy)));

    [Fact]
    public void Mora_solo_con_saldo_y_despues_del_vencimiento()
    {
        var vence = new DateOnly(2026, 12, 1);
        Assert.False(EdadCartera.EnMora(vence, vence, 100m));
        Assert.True(EdadCartera.EnMora(vence, vence.AddDays(1), 100m));
        Assert.False(EdadCartera.EnMora(vence, vence.AddDays(1), 0m));
    }
}
