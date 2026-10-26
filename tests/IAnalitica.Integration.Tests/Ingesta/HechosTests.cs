using IAnalitica.Integration.Tests.Infra;
using static IAnalitica.Integration.Tests.Ingesta.Procesamiento;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>Asientos y facturas (contrato 05 §6): reemplazo de ventana, validaciones y atomicidad.</summary>
[Collection(ColeccionBd.Nombre)]
public class HechosTests(BaseDeDatosFixture bd)
{
    private static readonly DateOnly Ene1 = new(2026, 1, 1), Ene31 = new(2026, 1, 31), Feb1 = new(2026, 2, 1), Feb28 = new(2026, 2, 28);

    private async Task<Procesamiento> ConBaseAsync()
    {
        var p = await NuevaEmpresaAsync(bd);
        await p.CargarCatalogosBaseAsync();
        return p;
    }

    private static object[] Comprobante(string mov, string fecha, decimal valor, string n = "") => new[]
    {
        Linea(mov + "-1" + n, mov, fecha, "130505", valor, 0, "P1"),
        Linea(mov + "-2" + n, mov, fecha, "413501", 0, valor),
    };

    [Fact]
    public async Task Asientos_insertan_y_reprocesar_lo_mismo_actualiza()
    {
        await using var p = await ConBaseAsync();
        var filas = Comprobante("M1", "2026-01-10", 1000).Concat(Comprobante("M2", "2026-01-20", 500));

        var r1 = await p.ProcesarAsync("asientos", "VENTANA", filas, Ene1, Ene31);
        Assert.Equal(("OK", 4, 0, 0, 0), (r1.Estado, r1.Insertadas, r1.Actualizadas, r1.Eliminadas, r1.Rechazadas));
        Assert.Equal(1500m, await p.EscalarAsync<decimal>("SELECT SUM(Debito) FROM fact.AsientoLinea WHERE EmpresaId = @e", new { e = p.EmpresaId }));
        Assert.Equal(202601, await p.EscalarAsync<int>("SELECT TOP 1 PeriodoKey FROM fact.AsientoLinea WHERE EmpresaId = @e", new { e = p.EmpresaId }));

        var r2 = await p.ProcesarAsync("asientos", "VENTANA", filas, Ene1, Ene31);
        Assert.Equal(("OK", 0, 4, 0, 0), (r2.Estado, r2.Insertadas, r2.Actualizadas, r2.Eliminadas, r2.Rechazadas));
        Assert.Equal(4, await p.ContarAsync("fact.AsientoLinea"));
    }

    [Fact]
    public async Task La_ventana_elimina_lo_que_no_llego_y_respeta_lo_de_fuera_de_la_ventana()
    {
        await using var p = await ConBaseAsync();
        await p.ProcesarAsync("asientos", "VENTANA", Comprobante("E1", "2026-02-10", 300), Feb1, Feb28);      // febrero: fuera de la próxima ventana
        await p.ProcesarAsync("asientos", "VENTANA", Comprobante("M1", "2026-01-10", 1000).Concat(Comprobante("M2", "2026-01-20", 500)), Ene1, Ene31);

        // enero se vuelve a enviar sin M2 (anulado/borrado en el origen) y con M3 nuevo
        var r = await p.ProcesarAsync("asientos", "VENTANA", Comprobante("M1", "2026-01-10", 1000).Concat(Comprobante("M3", "2026-01-25", 70)), Ene1, Ene31);
        Assert.Equal(("OK", 2, 2, 2, 0), (r.Estado, r.Insertadas, r.Actualizadas, r.Eliminadas, r.Rechazadas));
        Assert.Equal(0, await p.ContarAsync("fact.AsientoLinea", "MovimientoSourceKey = 'M2'"));
        Assert.Equal(2, await p.ContarAsync("fact.AsientoLinea", "MovimientoSourceKey = 'E1'"));   // febrero intacto
    }

    [Fact]
    public async Task Un_comprobante_descuadrado_se_rechaza_completo_y_no_afecta_a_los_demas()
    {
        await using var p = await ConBaseAsync();
        var filas = Comprobante("OK1", "2026-01-10", 100).Concat(new[]
        {
            Linea("D-1", "DES", "2026-01-11", "130505", 100, 0, "P1"),
            Linea("D-2", "DES", "2026-01-11", "413501", 0, 99),          // diferencia 1,00 > 0,01
        });
        var r = await p.ProcesarAsync("asientos", "VENTANA", filas, Ene1, Ene31);
        Assert.Equal((2, 2), (r.Insertadas, r.Rechazadas));
        Assert.All(r.Rechazos, x => Assert.Equal("COMPROBANTE_DESCUADRADO", x.Motivo));
        Assert.Equal(0, await p.ContarAsync("fact.AsientoLinea", "MovimientoSourceKey = 'DES'"));
    }

    [Fact]
    public async Task La_diferencia_dentro_de_la_tolerancia_se_acepta_y_las_lineas_anuladas_no_cuentan_en_el_cuadre()
    {
        await using var p = await ConBaseAsync();
        var filas = new[]
        {
            Linea("T-1", "TOL", "2026-01-11", "130505", 100.00m, 0, "P1"),
            Linea("T-2", "TOL", "2026-01-11", "413501", 0, 99.995m),       // 0,005 ≤ 0,01
            Linea("A-1", "ANU", "2026-01-12", "130505", 40, 0, "P1", estado: 2),   // anulado: no se cuadra, pero se guarda
            Linea("A-2", "ANU", "2026-01-12", "413501", 0, 5, estado: 2),
        };
        var r = await p.ProcesarAsync("asientos", "VENTANA", filas, Ene1, Ene31);
        Assert.Equal((4, 0), (r.Insertadas, r.Rechazadas));
        Assert.Equal(2, await p.ContarAsync("fact.AsientoLinea", "Estado = 2"));
    }

    [Fact]
    public async Task Referencia_inexistente_rechaza_la_linea_y_deja_incompleto_al_comprobante()
    {
        await using var p = await ConBaseAsync();
        var filas = new[]
        {
            Linea("R-1", "REF", "2026-01-10", "130505", 100, 0, "P1"),
            Linea("R-2", "REF", "2026-01-10", "999999", 0, 100),           // cuenta que no existe
        }.Concat(Comprobante("BIEN", "2026-01-10", 10));
        var r = await p.ProcesarAsync("asientos", "VENTANA", filas, Ene1, Ene31);
        Assert.Equal((2, 2), (r.Insertadas, r.Rechazadas));
        Assert.Contains(r.Rechazos, x => x.Clave == "R-2" && x.Motivo == "REFERENCIA_INEXISTENTE:cuentaCodigo");
        Assert.Contains(r.Rechazos, x => x.Clave == "R-1" && x.Motivo == "COMPROBANTE_INCOMPLETO");
    }

    [Fact]
    public async Task Una_referencia_opcional_inexistente_queda_en_NULL()
    {
        await using var p = await ConBaseAsync();
        var r = await p.ProcesarAsync("asientos", "VENTANA", new[]
        {
            Linea("O-1", "OPC", "2026-01-10", "130505", 100, 0, "TERCERO-NO-EXISTE"),
            Linea("O-2", "OPC", "2026-01-10", "413501", 0, 100),
        }, Ene1, Ene31);
        Assert.Equal((2, 0), (r.Insertadas, r.Rechazadas));
        Assert.Null(await p.EscalarAsync<int?>("SELECT TerceroId FROM fact.AsientoLinea WHERE EmpresaId = @e AND SourceKey = 'O-1'", new { e = p.EmpresaId }));
    }

    [Fact]
    public async Task Filas_fuera_de_la_ventana_o_con_valores_invalidos_se_rechazan()
    {
        await using var p = await ConBaseAsync();
        var filas = new[]
        {
            Linea("F-1", "FUE", "2026-03-01", "130505", 10, 0),            // fuera de la ventana de enero
            Linea("V-1", "NEG", "2026-01-10", "130505", -5, 0),            // negativo
            Linea("V-2", "AMB", "2026-01-10", "130505", 5, 5),             // débito y crédito a la vez
        };
        var r = await p.ProcesarAsync("asientos", "VENTANA", filas, Ene1, Ene31);
        Assert.Equal((0, 3), (r.Insertadas, r.Rechazadas));
        Assert.Contains(r.Rechazos, x => x.Clave == "F-1" && x.Motivo == "FUERA_DE_VENTANA");
        Assert.Contains(r.Rechazos, x => x.Clave == "V-1" && x.Motivo == "VALORES_INVALIDOS");
        Assert.Contains(r.Rechazos, x => x.Clave == "V-2" && x.Motivo == "VALORES_INVALIDOS");
    }

    [Fact]
    public async Task Una_fila_rechazada_conserva_su_version_anterior_y_no_se_borra_con_la_ventana()
    {
        await using var p = await ConBaseAsync();
        await p.ProcesarAsync("asientos", "VENTANA", Comprobante("M1", "2026-01-10", 100), Ene1, Ene31);
        // reenvío con la cuenta de una línea inexistente: se rechaza el comprobante, pero no se borra lo que ya estaba
        var r = await p.ProcesarAsync("asientos", "VENTANA", new[]
        {
            Linea("M1-1", "M1", "2026-01-10", "130505", 100, 0, "P1"),
            Linea("M1-2", "M1", "2026-01-10", "SIN-CUENTA", 0, 100),
        }, Ene1, Ene31);
        Assert.Equal((0, 0, 0, 2), (r.Insertadas, r.Actualizadas, r.Eliminadas, r.Rechazadas));
        Assert.Equal(2, await p.ContarAsync("fact.AsientoLinea", "MovimientoSourceKey = 'M1'"));
    }

    [Fact]
    public async Task Dos_empresas_con_el_mismo_SourceKey_no_se_pisan()
    {
        await using var a = await ConBaseAsync();
        await using var b = await ConBaseAsync();
        await a.ProcesarAsync("asientos", "VENTANA", Comprobante("M1", "2026-01-10", 100), Ene1, Ene31);
        await b.ProcesarAsync("asientos", "VENTANA", Comprobante("M1", "2026-01-10", 999), Ene1, Ene31);
        Assert.Equal(100m, await a.EscalarAsync<decimal>("SELECT SUM(Debito) FROM fact.AsientoLinea WHERE EmpresaId = @e", new { e = a.EmpresaId }));
        Assert.Equal(999m, await b.EscalarAsync<decimal>("SELECT SUM(Debito) FROM fact.AsientoLinea WHERE EmpresaId = @e", new { e = b.EmpresaId }));
    }

    [Fact]
    public async Task Facturas_ventana_reemplaza_e_incremental_actualiza_sin_eliminar()
    {
        await using var p = await ConBaseAsync();
        var r1 = await p.ProcesarAsync("facturas", "VENTANA", new[]
        {
            Factura("F1|1|10", "F1", "2026-01-10", "E1", 1000),
            Factura("F1|1|11", "F1", "2026-01-10", "E1", 500),
            Factura("F2|1|10", "F2", "2026-01-20", "E2", 700),
        }, Ene1, Ene31);
        Assert.Equal(("OK", 3, 0), (r1.Estado, r1.Insertadas, r1.Rechazadas));
        Assert.Equal(2200m, await p.EscalarAsync<decimal>("SELECT SUM(ValorNeto) FROM fact.FacturaLinea WHERE EmpresaId = @e", new { e = p.EmpresaId }));

        // anulación de F1 fuera de la ventana vigente: INCREMENTAL actualiza y no borra nada
        var inc = await p.ProcesarAsync("facturas", "INCREMENTAL", new[] { Factura("F1|1|10", "F1", "2026-01-10", "E1", 1000, anulada: true) });
        Assert.Equal(("OK", 0, 1, 0), (inc.Estado, inc.Insertadas, inc.Actualizadas, inc.Eliminadas));
        Assert.Equal(1, await p.ContarAsync("fact.FacturaLinea", "Anulada = 1"));
        Assert.Equal(3, await p.ContarAsync("fact.FacturaLinea"));

        // la ventana de enero se reenvía sin F2: se elimina; lo anulado se actualiza
        var v = await p.ProcesarAsync("facturas", "VENTANA", new[]
        {
            Factura("F1|1|10", "F1", "2026-01-10", "E1", 1000, anulada: true),
            Factura("F1|1|11", "F1", "2026-01-10", "E1", 500),
        }, Ene1, Ene31);
        Assert.Equal((0, 2, 1), (v.Insertadas, v.Actualizadas, v.Eliminadas));
    }

    [Fact]
    public async Task Facturas_rechazan_pagador_o_sede_inexistentes()
    {
        await using var p = await ConBaseAsync();
        var r = await p.ProcesarAsync("facturas", "VENTANA", new[]
        {
            Factura("A|1|1", "A", "2026-01-10", "NO-EXISTE", 10),
            Factura("B|1|1", "B", "2026-01-10", "E1", 10, sede: "99"),
            Factura("C|1|1", "C", "2026-01-10", "E1", 10),
        }, Ene1, Ene31);
        Assert.Equal((1, 2), (r.Insertadas, r.Rechazadas));
        Assert.Contains(r.Rechazos, x => x.Clave == "A|1|1" && x.Motivo == "REFERENCIA_INEXISTENTE:pagadorCodigo");
        Assert.Contains(r.Rechazos, x => x.Clave == "B|1|1" && x.Motivo == "REFERENCIA_INEXISTENTE:sedeCodigo");
    }

    [Fact]
    public async Task Una_carga_de_asientos_grande_en_varios_lotes_es_atomica_y_completa()
    {
        await using var p = await ConBaseAsync();
        var filas = Enumerable.Range(1, 6000).SelectMany(i => Comprobante("G" + i, "2026-01-" + (1 + i % 28).ToString("00"), i)).ToList();
        var r = await p.ProcesarAsync("asientos", "VENTANA", filas, Ene1, Ene31, porLote: 5000);
        Assert.Equal(("OK", 12000, 0), (r.Estado, r.Insertadas, r.Rechazadas));
    }
}
