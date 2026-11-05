using IAnalitica.Domain.Auditoria;
using IAnalitica.Integration.Tests.Infra;
using static IAnalitica.Integration.Tests.Ingesta.Procesamiento;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>Auditor de configuración (spec 16) contra SQL Server real: reglas de la fase 1, ciclo de vida del hallazgo e índice de salud.</summary>
[Collection(ColeccionBd.Nombre)]
public class AuditorTests(BaseDeDatosFixture bd)
{
    private static readonly DateTimeOffset Nov15 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static object F(string tabla, string clave, object datos) => new { tabla, clave, datos };
    private static string D(int y, int m, int d) => new DateTime(y, m, d).ToString("yyyy-MM-ddT00:00:00");

    private async Task<Procesamiento> EmpresaConFacturacionAsync()
    {
        var p = await NuevaEmpresaAsync(bd);
        await p.CargarCatalogosBaseAsync();
        await p.ProcesarAsync("pagadores", "COMPLETA", new object[]
        {
            new { codigoOrigen = "1", nombre = "EPS Uno", terceroCodigo = "P1", esParticular = false, activo = true },
            new { codigoOrigen = "2", nombre = "EPS Dos", terceroCodigo = "P2", esParticular = false, activo = true },
        });
        await p.ProcesarAsync("facturas", "VENTANA", new[]
        {
            Factura("F1|1|1", "F1", "2026-11-05", "2", 1000),
            Factura("F2|1|1", "F2", "2026-11-06", "2", 500),
        }, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
        return p;
    }

    private static object[] FotoConProblemas() => new object[]
    {
        F("dbo.mEmpresas", "1", new { IdEmpresa = 1, NomEmpresa = "EPS Uno", CodManual = 5, CodPlan = 1, EstadoEntidad = 1 }),
        F("dbo.mEmpresas", "2", new { IdEmpresa = 2, NomEmpresa = "EPS Dos", EstadoEntidad = 1 }),                 // sin manual (TAR-01) y sin cuentas (CON-01)
        F("dbo.mEmpresas", "3", new { IdEmpresa = 3, NomEmpresa = "EPS Inactiva", EstadoEntidad = 0 }),            // inactiva: no se audita
        F("SIIFNet.Cuentas", "100", new { Id = 100, Codigo = "130505", Nombre = "Clientes", DetalleCartera = 2 }),
        F("SIIFNet.Cuentas", "101", new { Id = 101, Codigo = "130510", Nombre = "Clientes radicada", DetalleCartera = 1 }),   // CON-04
        F("dbo.mEmpresasCuentasContables", "1", new { Id = 1, IdEmpresa = 1, Vigencia = 2026, CuentaPorCobrarId = 100, CuentaPorCobrarRadicadaId = 101, EstadoEntidad = 1 }),
        // FE: la vigente por vencer y casi agotada (FE-01 ALTA, FE-02), una vencida sin reemplazo (FE-01 CRITICA) y una vencida reemplazada (no cuenta)
        F("dbo.AutorizacionesDIAN", "1", new { Id = 1, Modalidad = 1, Prefijo = "FE", NumeroResolucion = 111, FechaFinVigencia = D(2026, 11, 20), NumeracionDesde = 1, NumeracionHasta = 1000, NumeracionActual = 950, EstadoEntidad = 1 }),
        F("dbo.AutorizacionesDIAN", "2", new { Id = 2, Modalidad = 1, Prefijo = "OLD", NumeroResolucion = 222, FechaFinVigencia = D(2025, 12, 31), NumeracionDesde = 1, NumeracionHasta = 100, NumeracionActual = 10, EstadoEntidad = 1 }),
        F("dbo.AutorizacionesDIAN", "3", new { Id = 3, Modalidad = 1, Prefijo = "FE", NumeroResolucion = 3, FechaFinVigencia = D(2025, 1, 1), NumeracionDesde = 1, NumeracionHasta = 10, NumeracionActual = 10, EstadoEntidad = 1 }),
        // TAR-02: EPS Uno puede usar el tipo de servicio 10, con dos procedimientos; solo uno tiene tarifa
        F("dbo.TipoServiciosXEmpresa", "1|10", new { IdEmpresa = 1, CodTipoServicio = 10 }),
        F("dbo.pPortafolio", "A|10", new { CodProcedimiento = "A", CodTipoServicio = 10 }),
        F("dbo.pPortafolio", "B|10", new { CodProcedimiento = "B", CodTipoServicio = 10 }),
        F("dbo.pTarifasProcedimientos", "A|5", new { CodProcedimiento = "A", CodManual = 5, CodPlan = 1, Valor = 100 }),
        // TAR-06
        F("dbo.pProcedimientos", "A", new { CodProcedimiento = "A", NomProcedimiento = "Consulta", CodCups = "890201" }),
        F("dbo.pProcedimientos", "B", new { CodProcedimiento = "B", NomProcedimiento = "Sin cups" }),
        F("dbo.pSuministros", "S1", new { CodSuministro = "S1", NomSuministro = "Sin cum" }),
        // USU-01: empleado con contrato terminado y usuario activo; otro con contrato vigente
        F("dbo.AspNetUsers", "u1", new { Id = "u1", TerceroId = 500, UserName = "retirado", Bloqueado = 0 }),
        F("dbo.AspNetUsers", "u2", new { Id = "u2", TerceroId = 501, UserName = "vigente", Bloqueado = 0 }),
        F("Nomina.Empleados", "1", new { Id = 1, TerceroId = 500 }),
        F("Nomina.Empleados", "2", new { Id = 2, TerceroId = 501 }),
        F("Nomina.Contratos", "1", new { Id = 1, EmpleadoId = 1, FechaSalida = D(2026, 6, 30) }),
        F("Nomina.Contratos", "2", new { Id = 2, EmpleadoId = 2 }),
    };

    private static Task<List<(string Codigo, string Clave, string Severidad, string Estado, int? Cant)>> HallazgosAsync(Procesamiento p)
        => p.ConsultarAsync<(string Codigo, string Clave, string Severidad, string Estado, int? Cant)>(
            "SELECT Codigo, ClaveEntidad, Severidad, Estado, ImpactoCantidad FROM fact.HallazgoConfiguracion WHERE EmpresaId = @e ORDER BY Codigo, ClaveEntidad");

    [Fact]
    public async Task Detecta_los_problemas_de_la_foto_y_no_marca_lo_correcto()
    {
        await using var p = await EmpresaConFacturacionAsync();
        await p.ProcesarAsync("configuracion", "FOTO", FotoConProblemas());
        var r = await p.AuditorEn(Nov15).EjecutarAsync(p.EmpresaId, CancellationToken.None);
        var h = await HallazgosAsync(p);

        Assert.True(r.ReglasFallidas == 0, string.Join(" | ", r.Errores));
        Assert.Equal(10, r.ReglasEvaluadas);
        Assert.Contains(("CON-01", "2", "CRITICA", "NUEVO", 2), h);                    // EPS Dos: 2 facturas de los últimos 30 días
        Assert.DoesNotContain(h, x => x.Codigo == "CON-01" && x.Clave is "1" or "3");  // la 1 tiene cuentas; la 3 está inactiva
        Assert.Contains(("CON-04", "Cuentas:130510", "ALTA", "NUEVO", 1), h);
        Assert.DoesNotContain(h, x => x.Clave == "Cuentas:130505");
        Assert.Contains(h, x => x.Codigo == "FE-01" && x.Clave == "AutorizacionesDIAN:1" && x.Severidad == "ALTA");
        Assert.Contains(h, x => x.Codigo == "FE-01" && x.Clave == "AutorizacionesDIAN:2" && x.Severidad == "CRITICA");
        Assert.DoesNotContain(h, x => x.Codigo == "FE-01" && x.Clave == "AutorizacionesDIAN:3");   // reemplazada por la 1
        Assert.Contains(h, x => x.Codigo == "FE-02" && x.Clave == "AutorizacionesDIAN:1" && x.Cant == 50);
        Assert.Contains(h, x => x.Codigo == "FE-03" && x.Clave == "Sede:1");                // factura sin resolución asignada a la sede
        Assert.Contains(h, x => x.Codigo == "TAR-01" && x.Clave == "2");
        Assert.DoesNotContain(h, x => x.Codigo == "TAR-01" && x.Clave == "1");
        Assert.Contains(("TAR-02", "1", "ALTA", "NUEVO", 1), h);                           // solo falta el procedimiento B
        Assert.Contains(("TAR-06", "pProcedimientos.CodCups", "ALTA", "NUEVO", 1), h);
        Assert.Contains(("TAR-06", "pSuministros.CodCum", "ALTA", "NUEVO", 1), h);
        Assert.Contains(h, x => x.Codigo == "USU-01" && x.Clave == "AspNetUsers:u1" && x.Severidad == "CRITICA");
        Assert.DoesNotContain(h, x => x.Clave == "AspNetUsers:u2");
        // CON-02 (noviembre): EPS Uno tiene cuentas 2026 y ninguna 2027
        Assert.Contains(h, x => x.Codigo == "CON-02" && x.Clave == "mEmpresas:1" && x.Severidad == "ALTA");
    }

    [Fact]
    public async Task Cargar_la_foto_encola_la_auditoria_y_el_worker_la_ejecuta()
    {
        await using var p = await EmpresaConFacturacionAsync();
        await p.ProcesarAsync("configuracion", "FOTO", FotoConProblemas());
        Assert.Equal(1, await p.EscalarAsync<int>("SELECT COUNT(*) FROM etl.Trabajo WHERE EmpresaId = @e AND Tipo = 'AUDITAR_CONFIG'", new { e = p.EmpresaId }));
        while (await p.Despachador.EjecutarUnoAsync(IAnalitica.Application.Abstracciones.TiposTrabajo.AuditarConfig, CancellationToken.None)) { }
        Assert.True(await p.ContarAsync("fact.HallazgoConfiguracion") > 0);
        Assert.Equal(1, await p.ContarAsync("fact.IndiceSaludConfiguracion", "Area = 'TOTAL'"));
    }

    [Fact]
    public async Task Con02_no_aplica_antes_de_noviembre_y_es_critica_en_diciembre()
    {
        await using var p = await EmpresaConFacturacionAsync();
        await p.ProcesarAsync("configuracion", "FOTO", FotoConProblemas());
        await p.AuditorEn(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero)).EjecutarAsync(p.EmpresaId, CancellationToken.None);
        Assert.DoesNotContain(await HallazgosAsync(p), x => x.Codigo == "CON-02");

        await p.AuditorEn(new DateTimeOffset(2026, 12, 10, 0, 0, 0, TimeSpan.Zero)).EjecutarAsync(p.EmpresaId, CancellationToken.None);
        Assert.Contains(await HallazgosAsync(p), x => x.Codigo == "CON-02" && x.Severidad == "CRITICA");
    }

    [Fact]
    public async Task Ciclo_de_vida_nuevo_abierto_corregido_y_supresion_por_falso_positivo()
    {
        await using var p = await EmpresaConFacturacionAsync();
        await p.ProcesarAsync("configuracion", "FOTO", FotoConProblemas());
        var motor = p.AuditorEn(Nov15);
        var r1 = await motor.EjecutarAsync(p.EmpresaId, CancellationToken.None);
        Assert.True(r1.Nuevos > 0);
        Assert.Equal(1, await p.EscalarAsync<int>("SELECT COUNT(*) FROM fact.HallazgoConfiguracion WHERE EmpresaId = @e AND Codigo = 'TAR-01' AND Estado = 'NUEVO'", new { e = p.EmpresaId }));

        // Segunda corrida: sigue ahí → ABIERTO, sin duplicar
        var r2 = await motor.EjecutarAsync(p.EmpresaId, CancellationToken.None);
        Assert.Equal(0, r2.Nuevos);
        Assert.Equal(0, r2.Corregidos);
        Assert.Equal(1, await p.EscalarAsync<int>("SELECT COUNT(*) FROM fact.HallazgoConfiguracion WHERE EmpresaId = @e AND Codigo = 'TAR-01' AND Estado = 'ABIERTO'", new { e = p.EmpresaId }));

        // Una persona marca USU-01 como falso positivo
        await p.EjecutarAsync("UPDATE fact.HallazgoConfiguracion SET Estado = 'FALSO_POSITIVO', Justificacion = N'Contratista', CerradoEn = SYSUTCDATETIME() WHERE EmpresaId = @e AND Codigo = 'USU-01'", new { e = p.EmpresaId });

        // Se corrige TAR-01 (EPS Dos recibe manual): la foto nueva reemplaza la anterior
        var foto = FotoConProblemas().Select(f => f).ToList();
        foto[1] = F("dbo.mEmpresas", "2", new { IdEmpresa = 2, NomEmpresa = "EPS Dos", CodManual = 5, EstadoEntidad = 1 });
        await p.ProcesarAsync("configuracion", "FOTO", foto);
        var r3 = await motor.EjecutarAsync(p.EmpresaId, CancellationToken.None);
        Assert.True(r3.Corregidos >= 1);
        Assert.Equal("CORREGIDO", await p.EscalarAsync<string>("SELECT Estado FROM fact.HallazgoConfiguracion WHERE EmpresaId = @e AND Codigo = 'TAR-01'", new { e = p.EmpresaId }));
        Assert.Equal(1, await p.EscalarAsync<int>("SELECT COUNT(*) FROM fact.HallazgoConfiguracion WHERE EmpresaId = @e AND Codigo = 'USU-01'", new { e = p.EmpresaId }));   // no se vuelve a crear
        Assert.Equal("FALSO_POSITIVO", await p.EscalarAsync<string>("SELECT Estado FROM fact.HallazgoConfiguracion WHERE EmpresaId = @e AND Codigo = 'USU-01'", new { e = p.EmpresaId }));

        // Reincidencia: si el problema vuelve se crea un hallazgo nuevo (el CORREGIDO queda en el historial)
        await p.ProcesarAsync("configuracion", "FOTO", FotoConProblemas());
        await motor.EjecutarAsync(p.EmpresaId, CancellationToken.None);
        Assert.Equal(2, await p.EscalarAsync<int>("SELECT COUNT(*) FROM fact.HallazgoConfiguracion WHERE EmpresaId = @e AND Codigo = 'TAR-01'", new { e = p.EmpresaId }));
    }

    [Fact]
    public async Task Una_regla_que_falla_no_borra_sus_hallazgos_y_las_demas_siguen()
    {
        await using var p = await EmpresaConFacturacionAsync();
        await p.ProcesarAsync("configuracion", "FOTO", FotoConProblemas());
        await p.AuditorEn(Nov15).EjecutarAsync(p.EmpresaId, CancellationToken.None);
        var antes = await p.ContarAsync("fact.HallazgoConfiguracion", "Codigo = 'CON-01' AND Estado IN ('NUEVO','ABIERTO')");
        Assert.True(antes > 0);

        var r = await p.AuditorEn(Nov15, new ReglaQueFalla(), new ReglaFija()).EjecutarAsync(p.EmpresaId, CancellationToken.None);
        Assert.Equal((1, 1), (r.ReglasEvaluadas, r.ReglasFallidas));
        Assert.Equal(antes, await p.ContarAsync("fact.HallazgoConfiguracion", "Codigo = 'CON-01' AND Estado IN ('NUEVO','ABIERTO')"));
    }

    [Fact]
    public async Task Calcula_el_indice_de_salud_por_area_y_total()
    {
        await using var p = await EmpresaConFacturacionAsync();
        await p.ProcesarAsync("configuracion", "FOTO", FotoConProblemas());
        var r = await p.AuditorEn(Nov15).EjecutarAsync(p.EmpresaId, CancellationToken.None);
        Assert.InRange(r.IndiceTotal, 0, 99);
        var seguridad = await p.EscalarAsync<int>("SELECT Indice FROM fact.IndiceSaludConfiguracion WHERE EmpresaId = @e AND Area = 'Seguridad'", new { e = p.EmpresaId });
        Assert.Equal(80, seguridad);                                                    // un hallazgo CRITICA (USU-01) = -20
        var total = await p.EscalarAsync<int>("SELECT Indice FROM fact.IndiceSaludConfiguracion WHERE EmpresaId = @e AND Area = 'TOTAL'", new { e = p.EmpresaId });
        Assert.Equal(r.IndiceTotal, total);

        // Empresa sin foto: índice 100
        await using var vacia = await NuevaEmpresaAsync(bd);
        var rv = await vacia.AuditorEn(Nov15).EjecutarAsync(vacia.EmpresaId, CancellationToken.None);
        Assert.Equal(100, rv.IndiceTotal);
    }

    [Fact]
    public async Task Las_reglas_respetan_los_ajustes_de_la_empresa()
    {
        await using var p = await EmpresaConFacturacionAsync();
        await p.ProcesarAsync("configuracion", "FOTO", FotoConProblemas());
        await using (var cn = new Microsoft.Data.SqlClient.SqlConnection(bd.CadenaPropietario))
        {
            await cn.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(cn, "EXEC sp_set_session_context N'EsSuperAdmin', 1");
            await Dapper.SqlMapper.ExecuteAsync(cn, "INSERT cfg.ReglaConfiguracionEmpresa (EmpresaId, Codigo, Activa) VALUES (@e, 'TAR-06', 0)", new { e = p.EmpresaId });
            await Dapper.SqlMapper.ExecuteAsync(cn, "INSERT cfg.ReglaConfiguracionEmpresa (EmpresaId, Codigo, Activa, SeveridadAjustada) VALUES (@e, 'TAR-01', 1, 'MEDIA')", new { e = p.EmpresaId });
        }
        await p.AuditorEn(Nov15).EjecutarAsync(p.EmpresaId, CancellationToken.None);
        var h = await HallazgosAsync(p);
        Assert.DoesNotContain(h, x => x.Codigo == "TAR-06");
        Assert.Contains(h, x => x.Codigo == "TAR-01" && x.Severidad == "MEDIA");
    }

    private sealed class ReglaQueFalla : IReglaConfiguracion
    {
        public string Codigo => "CON-01"; public string Area => "Contabilidad"; public Severidad SeveridadBase => Severidad.Critica;
        public Task<IReadOnlyList<HallazgoDetectado>> EvaluarAsync(ContextoAuditoria ctx, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private sealed class ReglaFija : IReglaConfiguracion
    {
        public string Codigo => "CON-04"; public string Area => "Contabilidad"; public Severidad SeveridadBase => Severidad.Alta;
        public Task<IReadOnlyList<HallazgoDetectado>> EvaluarAsync(ContextoAuditoria ctx, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<HallazgoDetectado>>(new[] { new HallazgoDetectado("X", "Fija", null, null, null, null, null) });
    }
}
