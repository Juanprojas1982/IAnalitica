using IAnalitica.Integration.Tests.Infra;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>Validador de precios máximos (spec 15 §2): R1, R2, R5 y R6 con catálogo regulatorio sintético, unidades y ciclo de vida.</summary>
[Collection(ColeccionBd.Nombre)]
public class ValidadorPreciosTests(BaseDeDatosFixture bd)
{
    private static readonly DateTimeOffset Hoy = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
    private static object F(string tabla, string clave, object datos) => new { tabla, clave, datos };

    /// <summary>El catálogo regulatorio es global: cada prueba usa CUM propios (prefijo único) para no mezclarse con las demás.</summary>
    private static int _serie = 100000;

    private sealed record Cums(string Alto, string ConFactor, string FactorSinValidar, string SinPrecio);

    private async Task<(Procesamiento P, Cums C)> EscenarioAsync()
    {
        var p = await Procesamiento.NuevaEmpresaAsync(bd);
        var n = Interlocked.Add(ref _serie, 10);
        var c = new Cums($"{n}-1", $"{n + 1}-1", $"{n + 2}-1", $"{n + 3}-1");
        var vigente = await p.EscalarAsync<int>("INSERT reg.Circular (Entidad, Numero, Anio, Tema, FechaExpedicion, VigenteDesde, UrlFuente) OUTPUT inserted.CircularId VALUES ('CNPMDM', @num, 2026, 'PRECIO_MAXIMO', '2026-08-01', '2026-08-19', 'https://ejemplo.invalido/vigente')", new { num = "T" + n });
        var futura = await p.EscalarAsync<int>("INSERT reg.Circular (Entidad, Numero, Anio, Tema, FechaExpedicion, VigenteDesde, UrlFuente) OUTPUT inserted.CircularId VALUES ('CNPMDM', @num, 2026, 'PRECIO_MAXIMO', '2026-10-01', '2026-12-01', 'https://ejemplo.invalido/futura')", new { num = "F" + n });
        // Alto: máximo por unidad 100 (dato de la fuente). ConFactor: 1000 por presentación de 10 unidades = 100/unidad; la circular futura baja a 80/unidad.
        // FactorSinValidar: 400 por presentación de 10 (factor MANUAL sin validar) = 40/unidad. SinPrecio: no está en la lista.
        await p.EjecutarAsync("INSERT reg.PrecioMaximo (CircularId, Cum, PrecioMaximoPresentacion, PrecioMaximoUnidad, VigenteDesde) VALUES (@v, @c, 1000, 100, '2026-08-19')", new { v = vigente, c = c.Alto });
        await p.EjecutarAsync("INSERT reg.PrecioMaximo (CircularId, Cum, PrecioMaximoPresentacion, VigenteDesde) VALUES (@v, @c, 1000, '2026-08-19'), (@f, @c, 800, '2026-12-01')", new { v = vigente, f = futura, c = c.ConFactor });
        await p.EjecutarAsync("INSERT reg.PrecioMaximo (CircularId, Cum, PrecioMaximoPresentacion, VigenteDesde) VALUES (@v, @c, 400, '2026-08-19')", new { v = vigente, c = c.FactorSinValidar });
        await p.EjecutarAsync("INSERT reg.FactorPresentacion (Cum, UnidadesPorPresentacion, Fuente, ValidadoPor, ValidadoEn) VALUES (@a, 10, 'MANUAL', N'QF Prueba', SYSUTCDATETIME()), (@b, 10, 'MANUAL', NULL, NULL)", new { a = c.ConFactor, b = c.FactorSinValidar });
        return (p, c);
    }

    private static object[] Foto(Cums c, bool tarifaAltaCorregida = false) => new object[]
    {
        F("dbo.mEmpresas", "1", new { IdEmpresa = 1, NomEmpresa = "EPS Uno", CodManual = 5, EstadoEntidad = 1 }),
        F("dbo.mEmpresas", "2", new { IdEmpresa = 2, NomEmpresa = "EPS Otra", CodManual = 9, EstadoEntidad = 1 }),
        F("dbo.pSuministros", "S1", new { CodSuministro = "S1", NomSuministro = "Alto", CodCum = c.Alto }),
        F("dbo.pSuministros", "S2", new { CodSuministro = "S2", NomSuministro = "Con factor", CodCum = c.ConFactor }),
        F("dbo.pSuministros", "S3", new { CodSuministro = "S3", NomSuministro = "Factor sin validar", CodCum = c.FactorSinValidar }),
        F("dbo.pSuministros", "S4", new { CodSuministro = "S4", NomSuministro = "Sin precio", CodCum = c.SinPrecio }),
        F("dbo.pSuministros", "S5", new { CodSuministro = "S5", NomSuministro = "Pérdida", Costo = 80 }),
        F("dbo.pSuministros", "S6", new { CodSuministro = "S6", NomSuministro = "CUM mal formado", CodCum = "ABC" }),
        F("dbo.pTarifasSuministros", "S1|5", new { CodSuministro = "S1", CodManual = 5, Valor = tarifaAltaCorregida ? 90 : 120 }),     // R1 crítica (120 > 100)
        F("dbo.pTarifasSuministros", "S2|5", new { CodSuministro = "S2", CodManual = 5, Valor = 100 }),                                // hoy OK; con la circular futura (80) R2
        F("dbo.pTarifasSuministros", "S3|5", new { CodSuministro = "S3", CodManual = 5, Valor = 50 }),                                 // R1 por verificar (50 > 40, factor sin validar)
        F("dbo.pTarifasSuministros", "S4|5", new { CodSuministro = "S4", CodManual = 5, Valor = 999 }),                                // sin límite comparable
        F("dbo.pTarifasSuministros", "S5|5", new { CodSuministro = "S5", CodManual = 5, Valor = 50 }),                                 // R5 (50 < costo 80)
        F("dbo.pTarifasSuministros", "S1|9", new { CodSuministro = "S1", CodManual = 9, Valor = 100 }),                                // EPS Otra: justo en el máximo, no es exceso
    };

    private static Task<List<(string Regla, string Clave, string Severidad, string Estado, bool FactorValidado, decimal? Exceso)>> HallazgosAsync(Procesamiento p)
        => p.ConsultarAsync<(string, string, string, string, bool, decimal?)>("SELECT Regla, ClaveEntidad, Severidad, Estado, FactorValidado, ExcesoUnitario FROM fact.HallazgoTarifario WHERE EmpresaId = @e ORDER BY Regla, ClaveEntidad");

    [Fact]
    public async Task Detecta_R1_R2_R5_y_R6_y_respeta_las_unidades()
    {
        var (p, c) = await EscenarioAsync();
        await using var _ = p;
        await p.ProcesarAsync("configuracion", "FOTO", Foto(c));
        var r = await p.ValidadorEn(Hoy).EjecutarAsync(p.EmpresaId, CancellationToken.None);
        var h = await HallazgosAsync(p);

        Assert.Contains(("R1", "S1|1", "CRITICA", "NUEVO", true, 20m), h);                // 120 − 100, con el máximo por unidad de la fuente
        Assert.DoesNotContain(h, x => x.Regla == "R1" && x.Clave == "S1|2");              // 100 no excede 100
        Assert.DoesNotContain(h, x => x.Regla == "R1" && x.Clave == "S2|1");              // 100 = 1000/10: no excede
        Assert.Contains(("R1", "S3|1", "MEDIA", "NUEVO", false, 10m), h);                 // 50 − 40, pero el factor no está validado: no es crítica
        Assert.Contains(("R2", "S2|1", "ALTA", "NUEVO", true, 20m), h);                   // quedará 80
        Assert.DoesNotContain(h, x => x.Clave.StartsWith("S4|"));                        // sin precio máximo no se compara
        Assert.Contains(("R5", "S5|1", "ALTA", "NUEVO", true, 30m), h);                   // pierde 30 por unidad
        Assert.Contains(h, x => x.Regla == "R6" && x.Clave == "S6" && x.Severidad == "ALTA");
        Assert.Equal(1, r.SinLimiteComparable);                                           // S4
    }

    [Fact]
    public async Task Cierra_lo_corregido_y_no_recrea_lo_marcado_como_falso_positivo()
    {
        var (p, c) = await EscenarioAsync();
        await using var _ = p;
        var validador = p.ValidadorEn(Hoy);
        await p.ProcesarAsync("configuracion", "FOTO", Foto(c));
        await validador.EjecutarAsync(p.EmpresaId, CancellationToken.None);
        var r2 = await validador.EjecutarAsync(p.EmpresaId, CancellationToken.None);
        Assert.Equal(0, r2.Nuevos);
        Assert.Equal(1, await p.EscalarAsync<int>("SELECT COUNT(*) FROM fact.HallazgoTarifario WHERE EmpresaId = @e AND Regla = 'R1' AND ClaveEntidad = 'S1|1' AND Estado = 'ABIERTO'", new { e = p.EmpresaId }));

        await p.EjecutarAsync("UPDATE fact.HallazgoTarifario SET Estado = 'FALSO_POSITIVO' WHERE EmpresaId = @e AND Regla = 'R5'", new { e = p.EmpresaId });
        await p.ProcesarAsync("configuracion", "FOTO", Foto(c, tarifaAltaCorregida: true));
        var r3 = await validador.EjecutarAsync(p.EmpresaId, CancellationToken.None);
        Assert.True(r3.Corregidos >= 1);
        Assert.Equal("CORREGIDO", await p.EscalarAsync<string>("SELECT Estado FROM fact.HallazgoTarifario WHERE EmpresaId = @e AND Regla = 'R1' AND ClaveEntidad = 'S1|1'", new { e = p.EmpresaId }));
        Assert.Equal(1, await p.EscalarAsync<int>("SELECT COUNT(*) FROM fact.HallazgoTarifario WHERE EmpresaId = @e AND Regla = 'R5'", new { e = p.EmpresaId }));
    }

    [Fact]
    public async Task Sin_catalogo_regulatorio_solo_quedan_R5_y_R6()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        await p.ProcesarAsync("configuracion", "FOTO", Foto(new Cums("900001-1", "900002-1", "900003-1", "900004-1")));
        await p.ValidadorEn(Hoy).EjecutarAsync(p.EmpresaId, CancellationToken.None);
        var reglas = (await HallazgosAsync(p)).Select(x => x.Regla).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(new[] { "R5", "R6" }, reglas);
    }

    [Fact]
    public async Task Cargar_la_foto_encola_tambien_el_validador()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        await p.ProcesarAsync("configuracion", "FOTO", Foto(new Cums("900011-1", "900012-1", "900013-1", "900014-1")));
        Assert.Equal(1, await p.EscalarAsync<int>("SELECT COUNT(*) FROM etl.Trabajo WHERE EmpresaId = @e AND Tipo = 'VALIDAR_PRECIOS'", new { e = p.EmpresaId }));
        while (await p.Despachador.EjecutarUnoAsync(IAnalitica.Application.Abstracciones.TiposTrabajo.ValidarPrecios, CancellationToken.None)) { }
        Assert.True(await p.ContarAsync("fact.HallazgoTarifario") > 0);
    }
}
