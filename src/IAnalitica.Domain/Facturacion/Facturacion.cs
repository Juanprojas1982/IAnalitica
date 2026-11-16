namespace IAnalitica.Domain.Facturacion;

/// <summary>Estados de la cuenta de cobro (08 §30.5). «Vencida» y «en mora» no son estados: se derivan (<see cref="EdadCartera"/>).</summary>
public static class EstadosCuentaCobro
{
    public const string Borrador = "BORRADOR", Descartada = "DESCARTADA", Emitida = "EMITIDA", Pagada = "PAGADA", Anulada = "ANULADA";

    private static readonly Dictionary<string, string[]> Transiciones = new()
    {
        [Borrador] = new[] { Emitida, Descartada },
        [Emitida] = new[] { Pagada, Anulada },
        [Pagada] = new[] { Emitida, Anulada },   // PAGADA→EMITIDA por reverso de una aplicación; PAGADA→ANULADA reversa antes
        [Descartada] = Array.Empty<string>(),
        [Anulada] = Array.Empty<string>(),
    };

    public static bool PuedePasar(string de, string a) => Transiciones.TryGetValue(de, out var destinos) && destinos.Contains(a);

    /// <summary>Estados que el Admin{NIT} de la empresa puede ver (los borradores y descartes son internos).</summary>
    public static bool VisibleParaCliente(string estado) => estado is Emitida or Pagada or Anulada;
}

public static class Alertas
{
    public const string SinCondicion = "SIN_CONDICION", SinCanon = "SIN_CANON", SinDestinatario = "SIN_DESTINATARIO",
        VariacionAnomala = "VARIACION_ANOMALA", CuadreIa = "CUADRE_IA", CambioConfigIa = "CAMBIO_CONFIG_IA",
        AprobacionRequerida = "APROBACION_REQUERIDA", TotalCero = "TOTAL_CERO", EnvioFallido = "ENVIO_FALLIDO",
        SmtpNoConfigurado = "SMTP_NO_CONFIGURADO", TrabajoVeneno = "TRABAJO_VENENO";
}

/// <summary>Canon del periodo: el pactado por empresa gana sobre el del plan; sin ninguno no hay canon (SIN_CANON).</summary>
public static class Canon
{
    public static decimal? DelPeriodo(decimal? pactadoCop, decimal? canonPlanCop) => pactadoCop ?? canonPlanCop;
}

/// <summary>Un tramo del mes con una misma condición comercial (08 D48).</summary>
public sealed record TramoCanon(DateOnly Desde, DateOnly Hasta, decimal CanonMensualCop);

/// <summary>Resultado del prorrateo de un tramo: días facturados, días del mes y valor en pesos enteros.</summary>
public sealed record LineaProrrateada(DateOnly Desde, DateOnly Hasta, int DiasFacturados, int DiasPeriodo, decimal CanonMensualCop, decimal ValorCop);

/// <summary>
/// Prorrateo por días calendario del mes, alta y baja inclusive (D48, [S]): <c>ROUND(canon × días / días del mes, 0)</c>, <c>AwayFromZero</c>.
/// Mes completo = canon sin redondeo adicional. Los tramos se recortan al mes de servicio.
/// </summary>
public static class Prorrateo
{
    public static IReadOnlyList<LineaProrrateada> Calcular(int periodoKey, IEnumerable<TramoCanon> tramos)
    {
        var (primero, ultimo) = LimitesDelMes(periodoKey);
        var diasPeriodo = ultimo.DayNumber - primero.DayNumber + 1;
        var resultado = new List<LineaProrrateada>();
        foreach (var t in tramos.OrderBy(x => x.Desde))
        {
            var desde = t.Desde < primero ? primero : t.Desde;
            var hasta = t.Hasta > ultimo ? ultimo : t.Hasta;
            if (hasta < desde) continue;   // el tramo no toca este mes
            var dias = hasta.DayNumber - desde.DayNumber + 1;
            var valor = dias == diasPeriodo
                ? Math.Round(t.CanonMensualCop, 0, MidpointRounding.AwayFromZero)
                : Math.Round(t.CanonMensualCop * dias / diasPeriodo, 0, MidpointRounding.AwayFromZero);
            resultado.Add(new LineaProrrateada(desde, hasta, dias, diasPeriodo, t.CanonMensualCop, valor));
        }
        return resultado;
    }

    public static (DateOnly Primero, DateOnly Ultimo) LimitesDelMes(int periodoKey)
    {
        var anio = periodoKey / 100;
        var mes = periodoKey % 100;
        if (anio is < 2026 or > 2099 || mes is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(periodoKey), "PeriodoKey inválido (AAAAMM).");
        var primero = new DateOnly(anio, mes, 1);
        return (primero, primero.AddMonths(1).AddDays(-1));
    }

    public static int PeriodoAnterior(int periodoKey) => periodoKey % 100 == 1 ? (periodoKey / 100 - 1) * 100 + 12 : periodoKey - 1;
    public static int PeriodoSiguiente(int periodoKey) => periodoKey % 100 == 12 ? (periodoKey / 100 + 1) * 100 + 1 : periodoKey + 1;
}

/// <param name="Canon">Canon indefinido (sin pactado ni de plan) = <c>null</c>.</param>
/// <param name="Destinatarios">Contactos FACTURACION registrados (la cuenta sin destinatario no se emite).</param>
/// <param name="AdvertenciasIa">Códigos CUADRE_IA / CAMBIO_CONFIG_IA de la línea de excedente (vacío si está apagada).</param>
public sealed record EntradaReglasEmision(decimal? Canon, int Destinatarios, decimal Total, decimal? TotalMesAnterior,
    decimal VariacionMaxPct, bool RequiereAprobacion, IReadOnlyList<string> AdvertenciasIa);

/// <summary>Reglas de retención (08 §33). Una lista vacía significa «emitir y enviar». La empresa suspendida NO retiene (D51).</summary>
public static class ReglasEmision
{
    public static IReadOnlyList<string> MotivosRetencion(EntradaReglasEmision e)
    {
        var motivos = new List<string>();
        if (e.Canon is null) motivos.Add(Alertas.SinCanon);
        if (e.Destinatarios <= 0) motivos.Add(Alertas.SinDestinatario);
        if (e.TotalMesAnterior is > 0 && e.Total > 0
            && Math.Abs(e.Total - e.TotalMesAnterior.Value) / e.TotalMesAnterior.Value * 100m > e.VariacionMaxPct)
            motivos.Add(Alertas.VariacionAnomala);
        foreach (var a in e.AdvertenciasIa) if (!motivos.Contains(a)) motivos.Add(a);
        if (e.RequiereAprobacion) motivos.Add(Alertas.AprobacionRequerida);
        return motivos;
    }

    /// <summary>Resuelve el interruptor: el valor de la empresa (1/0) gana sobre el global.</summary>
    public static bool RequiereAprobacion(bool? porEmpresa, bool global) => porEmpresa ?? global;
}

public sealed record EntradaExcedenteIa(decimal ConsumoCop, decimal CupoCop, decimal BrutoCop, bool HuboConsumoEnExcedente, decimal? TopeCop,
    bool CambioConfigEnPeriodo, decimal SumaUsoLlamadasCop, decimal UmbralCuadreCop);

public sealed record ResultadoExcedenteIa(decimal FacturableCop, decimal AbsorbidoCop, decimal? TopeAplicadoCop, decimal DiferenciaCuadreCop, IReadOnlyList<string> Advertencias);

/// <summary>Excedente de IA (08 §31.3). Solo se cobra con consumo autorizado en excedente, hasta el tope; el resto lo absorbe el prestador.</summary>
public static class LiquidadorExcedenteIa
{
    public static ResultadoExcedenteIa Calcular(EntradaExcedenteIa e)
    {
        var bruto = Math.Max(0m, e.BrutoCop);
        var facturable = e.HuboConsumoEnExcedente ? Math.Min(bruto, e.TopeCop ?? bruto) : 0m;
        var tope = e.HuboConsumoEnExcedente ? e.TopeCop : null;
        var diferencia = Math.Abs(e.SumaUsoLlamadasCop - e.ConsumoCop);
        var advertencias = new List<string>();
        if (e.CambioConfigEnPeriodo) advertencias.Add(Alertas.CambioConfigIa);
        if (diferencia > e.UmbralCuadreCop) advertencias.Add(Alertas.CuadreIa);
        return new ResultadoExcedenteIa(Math.Round(facturable, 0, MidpointRounding.AwayFromZero), Math.Round(bruto - facturable, 0, MidpointRounding.AwayFromZero), tope, diferencia, advertencias);
    }
}

public static class Vencimiento
{
    /// <summary>Días calendario: no importa si el día de vencimiento es festivo.</summary>
    public static DateOnly Calcular(DateOnly emision, short dias) => emision.AddDays(dias);
}

public static class EdadCartera
{
    public const string AlDia = "AL_DIA", De1a30 = "1_30", De31a60 = "31_60", De61a90 = "61_90", MasDe90 = "MAS_90";

    /// <summary>La mora corre desde el día siguiente al vencimiento: el día del vencimiento aún está al día.</summary>
    public static string Rango(DateOnly vence, DateOnly hoy)
    {
        var dias = hoy.DayNumber - vence.DayNumber;
        return dias <= 0 ? AlDia : dias <= 30 ? De1a30 : dias <= 60 ? De31a60 : dias <= 90 ? De61a90 : MasDe90;
    }

    public static bool EnMora(DateOnly vence, DateOnly hoy, decimal saldoCop) => saldoCop > 0 && hoy > vence;
}
