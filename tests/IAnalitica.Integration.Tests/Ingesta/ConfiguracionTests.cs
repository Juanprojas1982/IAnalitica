using IAnalitica.Integration.Tests.Infra;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>Entidad <c>configuracion</c> (FOTO): tablas tipadas saludips.* por empresa.</summary>
[Collection(ColeccionBd.Nombre)]
public class ConfiguracionTests(BaseDeDatosFixture bd)
{
    private static object Fila(string tabla, string clave, object datos) => new { tabla, clave, datos };

    [Fact]
    public async Task Carga_una_foto_en_las_tablas_tipadas_y_rechaza_tablas_desconocidas()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        var r = await p.ProcesarAsync("configuracion", "FOTO", new object[]
        {
            Fila("dbo.pTarifasSuministros", "1|2", new { CodSuministro = "S1", CodManual = 2, Valor = 1500.5, CodPlan = 1 }),
            Fila("dbo.pTarifasSuministros", "2|2", new { CodSuministro = "S2", CodManual = 2, Valor = 0 }),
            Fila("dbo.pTarifasSuministros", "x", new { CodManual = 2, Valor = 7 }),                       // sin llave: se rechaza
            Fila("dbo.pTarifasSuministros", "dup", new { CodSuministro = "S1", CodManual = 2, Valor = 1600 }),   // repetida: gana la última
            Fila("dbo.TablaInexistente", "1", new { Id = 1 }),
            Fila("SIIFNet.CierresContables", "1", new { Id = 1, Vigencia = 2026, Mes = 1 }),
        });
        Assert.Equal("OK", r.Estado);
        Assert.Equal(3, r.Insertadas);
        Assert.Equal(3, r.Rechazadas);
        Assert.Contains(r.Rechazos, x => x.Motivo == "TABLA_NO_PERMITIDA");
        Assert.Equal(1600m, await p.EscalarAsync<decimal>("SELECT Valor FROM saludips.dbo_pTarifasSuministros WHERE EmpresaId = @e AND CodSuministro = 'S1'", new { e = p.EmpresaId }));
        Assert.Equal(1, await p.ContarAsync("saludips.SIIFNet_CierresContables"));
    }

    [Fact]
    public async Task La_nueva_foto_reemplaza_la_anterior_y_no_toca_otras_empresas()
    {
        await using var a = await Procesamiento.NuevaEmpresaAsync(bd);
        await using var b = await Procesamiento.NuevaEmpresaAsync(bd);
        await a.ProcesarAsync("configuracion", "FOTO", new[] { Fila("SIIFNet.CierresContables", "1", new { Id = 1, Vigencia = 2026, Mes = 1 }), Fila("SIIFNet.CierresContables", "2", new { Id = 2, Vigencia = 2026, Mes = 2 }) });
        await b.ProcesarAsync("configuracion", "FOTO", new[] { Fila("SIIFNet.CierresContables", "9", new { Id = 9, Vigencia = 2026, Mes = 9 }) });
        await a.ProcesarAsync("configuracion", "FOTO", new[] { Fila("SIIFNet.CierresContables", "3", new { Id = 3, Vigencia = 2026, Mes = 3 }) });
        Assert.Equal(1, await a.ContarAsync("saludips.SIIFNet_CierresContables"));
        Assert.Equal(3, await a.EscalarAsync<int>("SELECT Mes FROM saludips.SIIFNet_CierresContables WHERE EmpresaId = @e", new { e = a.EmpresaId }));
        Assert.Equal(1, await b.ContarAsync("saludips.SIIFNet_CierresContables"));
    }
}
