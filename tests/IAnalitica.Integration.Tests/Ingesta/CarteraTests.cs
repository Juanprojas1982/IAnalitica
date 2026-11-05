using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Integration.Tests.Infra;
using static IAnalitica.Integration.Tests.Ingesta.Procesamiento;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>
/// Cartera (24 §13 F2): réplica de CARTERAESTADOCUENTAEDADES sobre asientos cargados por la API.
/// El cuadre con datos reales del piloto (≤ 0,5 %) no se puede probar aquí: ver docs/26_NOTAS_IMPLEMENTACION_F2.md.
/// </summary>
[Collection(ColeccionBd.Nombre)]
public class CarteraTests(BaseDeDatosFixture bd)
{
    private static readonly DateOnly Ene1 = new(2026, 1, 1), Mar31 = new(2026, 3, 31);

    private async Task<List<(string Origen, decimal Original, decimal Abonos, decimal Saldo, int Dias, string Rango, string Tipo)>> CalcularAsync(Procesamiento p, string corte)
    {
        // Procesar asientos ya encoló CALCULAR_CARTERA (con el corte de hoy): se fija el corte de la prueba en ese mismo trabajo.
        await p.EjecutarAsync("UPDATE etl.Trabajo SET Payload = @payload, DisponibleEn = SYSUTCDATETIME() WHERE ClaveExclusion = @clave AND Estado = 'PENDIENTE'",
            new { clave = $"EMP:{p.EmpresaId}|CARTERA", payload = $"{{\"corte\":\"{corte}\"}}" });
        // La cola es compartida con las demás pruebas: se vacía (los trabajos de otras empresas también se ejecutan, sin efecto aquí).
        while (await p.Despachador.EjecutarUnoAsync(TiposTrabajo.CalcularCartera, CancellationToken.None)) { }
        var job = await p.EscalarAsync<string>("SELECT Estado + ' ' + ISNULL(Error, '') FROM etl.Trabajo WHERE ClaveExclusion = @c", new { c = $"EMP:{p.EmpresaId}|CARTERA" });
        Assert.True(job.StartsWith("OK"), job);
        await using var c = await p.EtlAsync();
        return (await c.QueryAsync<(string, decimal, decimal, decimal, int, string, string)>(
            "SELECT LineaOrigenSourceKey, ValorOriginal, Abonos, Saldo, Dias, RangoEdad, TipoCartera FROM fact.CarteraDocumento WHERE EmpresaId = @e ORDER BY LineaOrigenSourceKey",
            new { e = p.EmpresaId })).ToList();
    }

    [Fact]
    public async Task Saldo_edad_abonos_y_reversiones_de_cuentas_por_cobrar()
    {
        await using var p = await NuevaEmpresaAsync(bd);
        await p.CargarCatalogosBaseAsync();
        await p.ProcesarAsync("asientos", "VENTANA", new[]
        {
            // F1: 1000, abono parcial de 400 → saldo 600 (50 días al 2026-03-01)
            Linea("F1-CXC", "FAC1", "2026-01-10", "130505", 1000, 0, "P1", numDoc: "F1"),
            Linea("F1-ING", "FAC1", "2026-01-10", "413501", 0, 1000),
            Linea("F1-ABO", "REC1", "2026-02-01", "130505", 0, 400, "P1", rel: "F1-CXC"),
            Linea("F1-BAN", "REC1", "2026-02-01", "111005", 400, 0),
            // F2: 800 pagada completa → no aparece
            Linea("F2-CXC", "FAC2", "2026-01-15", "130505", 800, 0, "P2", numDoc: "F2"),
            Linea("F2-ING", "FAC2", "2026-01-15", "413501", 0, 800),
            Linea("F2-ABO", "REC2", "2026-01-30", "130505", 0, 800, "P2", rel: "F2-CXC"),
            Linea("F2-BAN", "REC2", "2026-01-30", "111005", 800, 0),
            // F3: 300 con abono de 300 que luego se reversa → saldo vuelve a 300
            Linea("F3-CXC", "FAC3", "2026-01-20", "130505", 300, 0, "P1", numDoc: "F3", fechaCartera: "2025-12-01"),
            Linea("F3-ING", "FAC3", "2026-01-20", "413501", 0, 300),
            Linea("F3-ABO", "REC3", "2026-02-05", "130505", 0, 300, "P1", rel: "F3-CXC"),
            Linea("F3-BAN", "REC3", "2026-02-05", "111005", 300, 0),
            Linea("F3-REV", "REV3", "2026-02-10", "130505", 300, 0, "P1", rel: "F3-ABO"),
            Linea("F3-RBA", "REV3", "2026-02-10", "111005", 0, 300),
        }, Ene1, Mar31);

        var cartera = await CalcularAsync(p, "2026-03-01");

        Assert.Equal(2, cartera.Count);
        var f1 = cartera.Single(x => x.Origen == "F1-CXC");
        Assert.Equal((1000m, 400m, 600m, 50, "31-60", "CXC"), (f1.Original, f1.Abonos, f1.Saldo, f1.Dias, f1.Rango, f1.Tipo));
        var f3 = cartera.Single(x => x.Origen == "F3-CXC");
        Assert.Equal((300m, 0m, 300m), (f3.Original, f3.Abonos, f3.Saldo));
        Assert.Equal(90, f3.Dias);                 // edad desde FechaCartera (2025-12-01), no desde el movimiento
        Assert.Equal("61-90", f3.Rango);
    }

    [Fact]
    public async Task Cuentas_por_pagar_y_cuadre_contra_el_libro()
    {
        await using var p = await NuevaEmpresaAsync(bd);
        await p.CargarCatalogosBaseAsync();
        await p.ProcesarAsync("asientos", "VENTANA", new[]
        {
            Linea("C1-GAS", "COM1", "2026-01-05", "111005", 0, 0, null),            // sin efecto (línea vacía para comprobar que no estorba)
            Linea("C1-CXP", "COM1", "2026-01-05", "233500", 0, 900, "P1", numDoc: "C1"),
            Linea("C1-BAN", "COM1", "2026-01-05", "111005", 900, 0),
            Linea("C1-PAG", "PAG1", "2026-01-25", "233500", 200, 0, "P1", rel: "C1-CXP"),
            Linea("C1-BA2", "PAG1", "2026-01-25", "111005", 0, 200),
        }, Ene1, Mar31);

        var cartera = await CalcularAsync(p, "2026-02-01");

        var cxp = Assert.Single(cartera);
        Assert.Equal(("CXP", 900m, 200m, 700m, 27, "0-30"), (cxp.Tipo, cxp.Original, cxp.Abonos, cxp.Saldo, cxp.Dias, cxp.Rango));
        // la cartera cuadra con el saldo contable de la cuenta
        var saldoLibro = await p.EscalarAsync<decimal>(
            "SELECT SUM(a.Credito - a.Debito) FROM fact.AsientoLinea a JOIN dim.CuentaContable c ON c.CuentaId = a.CuentaId WHERE a.EmpresaId = @e AND c.Codigo = '233500'", new { e = p.EmpresaId });
        Assert.Equal(saldoLibro, cartera.Sum(x => x.Saldo));
    }

    [Fact]
    public async Task Recalcular_el_mismo_corte_reemplaza_sin_duplicar()
    {
        await using var p = await NuevaEmpresaAsync(bd);
        await p.CargarCatalogosBaseAsync();
        await p.ProcesarAsync("asientos", "VENTANA", new[]
        {
            Linea("X-CXC", "FACX", "2026-01-10", "130505", 100, 0, "P1", numDoc: "X"), Linea("X-ING", "FACX", "2026-01-10", "413501", 0, 100),
        }, Ene1, Mar31);
        await CalcularAsync(p, "2026-03-01");
        var otra = await CalcularAsync(p, "2026-03-01");
        Assert.Single(otra);
    }
}
