using IAnalitica.Application.Abstracciones;
using IAnalitica.Integration.Tests.Infra;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>Cola SQL (spec 20 §2): exclusión por clave, concurrencia entre workers, lease, reintentos 1/2/4/8/16 min y VENENO.</summary>
[Collection(ColeccionBd.Nombre)]
public class ColaTrabajosTests(BaseDeDatosFixture bd)
{
    private static string Tipo() => "PRUEBA_" + Guid.NewGuid().ToString("N")[..8];

    private static TrabajoPendiente Nuevo(string tipo, string clave, byte prioridad = 5, int? empresa = null)
        => new(tipo, empresa, clave, $"{{\"x\":\"{clave}\"}}", Prioridad: prioridad);

    private async Task<Procesamiento> ColaAsync() => await Procesamiento.NuevaEmpresaAsync(bd);

    [Fact]
    public async Task Solo_un_trabajo_activo_por_clave_y_al_terminar_se_puede_volver_a_encolar()
    {
        await using var p = await ColaAsync();
        var tipo = Tipo(); var clave = "K:" + tipo;
        Assert.True(await p.Cola.EncolarAsync(Nuevo(tipo, clave), CancellationToken.None));
        Assert.False(await p.Cola.EncolarAsync(Nuevo(tipo, clave), CancellationToken.None));   // consolidado

        var t = await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None);
        Assert.NotNull(t);
        Assert.False(await p.Cola.EncolarAsync(Nuevo(tipo, clave), CancellationToken.None));   // EN_PROCESO también bloquea
        await p.Cola.CompletarAsync(t!.TrabajoId, CancellationToken.None);
        Assert.True(await p.Cola.EncolarAsync(Nuevo(tipo, clave), CancellationToken.None));
    }

    [Fact]
    public async Task Toma_por_prioridad_y_devuelve_null_si_no_hay()
    {
        await using var p = await ColaAsync();
        var tipo = Tipo();
        await p.Cola.EncolarAsync(Nuevo(tipo, "b:" + tipo, prioridad: 7), CancellationToken.None);
        await p.Cola.EncolarAsync(Nuevo(tipo, "a:" + tipo, prioridad: 2), CancellationToken.None);
        Assert.Equal("{\"x\":\"a:" + tipo + "\"}", (await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None))!.Payload);
        Assert.Equal("{\"x\":\"b:" + tipo + "\"}", (await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None))!.Payload);
        Assert.Null(await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None));
    }

    [Fact]
    public async Task Varios_workers_a_la_vez_nunca_toman_el_mismo_trabajo()
    {
        await using var p = await ColaAsync();
        var tipo = Tipo();
        for (var i = 0; i < 40; i++) await p.Cola.EncolarAsync(Nuevo(tipo, $"{tipo}:{i}"), CancellationToken.None);

        var tomados = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var workers = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            while (await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None) is { } t) tomados.Add(t.TrabajoId);
        }));
        await Task.WhenAll(workers);

        Assert.Equal(40, tomados.Count);
        Assert.Equal(40, tomados.Distinct().Count());
    }

    [Fact]
    public async Task Un_fallo_reintenta_con_espera_exponencial_y_al_agotarse_queda_en_VENENO()
    {
        await using var p = await ColaAsync();
        var tipo = Tipo(); var clave = "F:" + tipo;
        await p.Cola.EncolarAsync(Nuevo(tipo, clave), CancellationToken.None);
        var esperados = new[] { 1, 2, 4, 8 };
        for (var i = 0; i < esperados.Length; i++)
        {
            await p.EjecutarAsync("UPDATE etl.Trabajo SET DisponibleEn = SYSUTCDATETIME() WHERE ClaveExclusion = @clave", new { clave });
            var t = await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None);
            Assert.Equal(i + 1, t!.Intentos);
            await p.Cola.FallarAsync(t.TrabajoId, "boom", CancellationToken.None);
            var espera = await p.EscalarAsync<int>("SELECT DATEDIFF(SECOND, SYSUTCDATETIME(), DisponibleEn) FROM etl.Trabajo WHERE ClaveExclusion = @clave", new { clave });
            Assert.InRange(espera, esperados[i] * 60 - 10, esperados[i] * 60 + 2);
            Assert.Null(await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None));   // todavía no está disponible
        }

        await p.EjecutarAsync("UPDATE etl.Trabajo SET DisponibleEn = SYSUTCDATETIME() WHERE ClaveExclusion = @clave", new { clave });
        var ultimo = await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None);
        Assert.True(ultimo!.EsUltimoIntento);
        await p.Cola.FallarAsync(ultimo.TrabajoId, "boom final", CancellationToken.None);
        Assert.Equal("VENENO", await p.EscalarAsync<string>("SELECT Estado FROM etl.Trabajo WHERE ClaveExclusion = @clave", new { clave }));
        Assert.Null(await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None));          // nunca se reintenta indefinidamente
        Assert.True(await p.Cola.EncolarAsync(Nuevo(tipo, clave), CancellationToken.None));    // VENENO libera la clave
    }

    [Fact]
    public async Task Un_worker_que_muere_libera_el_trabajo_cuando_vence_el_lease()
    {
        await using var p = await ColaAsync();
        var tipo = Tipo(); var clave = "L:" + tipo;
        await p.Cola.EncolarAsync(Nuevo(tipo, clave), CancellationToken.None);
        var t1 = await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None);
        Assert.Null(await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None));          // en proceso con lease vigente

        await p.EjecutarAsync("UPDATE etl.Trabajo SET LeaseHasta = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE ClaveExclusion = @clave", new { clave });
        var t2 = await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None);
        Assert.Equal(t1!.TrabajoId, t2!.TrabajoId);
        Assert.Equal(2, t2.Intentos);
    }

    [Fact]
    public async Task Postergar_no_consume_un_intento()
    {
        await using var p = await ColaAsync();
        var tipo = Tipo(); var clave = "P:" + tipo;
        await p.Cola.EncolarAsync(Nuevo(tipo, clave), CancellationToken.None);
        var t = await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None);
        await p.Cola.PostergarAsync(t!.TrabajoId, TimeSpan.Zero, CancellationToken.None);
        var otra = await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None);
        Assert.Equal(1, otra!.Intentos);
    }

    [Fact]
    public async Task El_lease_vencido_sin_intentos_restantes_pasa_a_VENENO()
    {
        await using var p = await ColaAsync();
        var tipo = Tipo(); var clave = "V:" + tipo;
        await p.Cola.EncolarAsync(Nuevo(tipo, clave), CancellationToken.None);
        await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None);
        await p.EjecutarAsync("UPDATE etl.Trabajo SET Intentos = MaxIntentos, LeaseHasta = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE ClaveExclusion = @clave", new { clave });
        Assert.Null(await p.Cola.TomarSiguienteAsync(tipo, CancellationToken.None));
        Assert.Equal("VENENO", await p.EscalarAsync<string>("SELECT Estado FROM etl.Trabajo WHERE ClaveExclusion = @clave", new { clave }));
    }

    [Fact]
    public async Task El_despachador_falla_el_trabajo_y_deja_la_carga_en_ERROR_solo_en_el_ultimo_intento()
    {
        await using var p = await ColaAsync();
        // Carga de una entidad sin procesador: siempre falla al aplicar
        var cargaId = await p.CrearCargaAsync("sedes", "COMPLETA", new object[] { new { codigoOrigen = "1", nombre = "A" } });
        await p.EjecutarAsync("UPDATE etl.Carga SET Entidad = 'inexistente' WHERE CargaId = @cargaId", new { cargaId });
        await p.Cola.EncolarAsync(new TrabajoPendiente(TiposTrabajo.ProcesarCarga, p.EmpresaId, $"EMP:{p.EmpresaId}|CARGA:{cargaId}", $"{{\"cargaId\":{cargaId}}}"), CancellationToken.None);

        Assert.True(await p.Despachador.EjecutarUnoAsync(TiposTrabajo.ProcesarCarga, CancellationToken.None));
        Assert.Equal("CERRADA", (await p.ResumenAsync(cargaId)).Estado);               // aún reintentable
        Assert.Equal("PENDIENTE", await p.EscalarAsync<string>("SELECT Estado FROM etl.Trabajo WHERE ClaveExclusion = @c", new { c = $"EMP:{p.EmpresaId}|CARGA:{cargaId}" }));

        await p.EjecutarAsync("UPDATE etl.Trabajo SET DisponibleEn = SYSUTCDATETIME(), Intentos = MaxIntentos - 1 WHERE ClaveExclusion = @c", new { c = $"EMP:{p.EmpresaId}|CARGA:{cargaId}" });
        await p.Despachador.EjecutarUnoAsync(TiposTrabajo.ProcesarCarga, CancellationToken.None);
        var final = await p.ResumenAsync(cargaId);
        Assert.Equal("ERROR", final.Estado);
        Assert.DoesNotContain("A", final.Error!.Replace("Exception", "").Replace("Operation", ""), StringComparison.Ordinal);   // sin contenido de filas
    }

    [Fact]
    public async Task Dos_cargas_de_la_misma_empresa_y_entidad_no_se_procesan_a_la_vez()
    {
        await using var p = await ColaAsync();
        var id1 = await p.CrearCargaAsync("sedes", "COMPLETA", new object[] { new { codigoOrigen = "1", nombre = "A" } });
        var id2 = await p.CrearCargaAsync("sedes", "COMPLETA", new object[] { new { codigoOrigen = "2", nombre = "B" } });

        // Una conexión externa toma el applock de la entidad (simula una carga en curso en otro worker)
        await using var otra = await p.EtlAsync();
        await using (var cmd = otra.CreateCommand())
        {
            cmd.CommandText = $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = 'carga:{p.EmpresaId}:sedes', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;";
            Assert.True((int)(await cmd.ExecuteScalarAsync())! >= 0);
        }

        Assert.False(await p.Procesador.ProcesarAsync(id2, false, CancellationToken.None));     // ocupada: se pospone
        Assert.Equal("CERRADA", (await p.ResumenAsync(id2)).Estado);

        await using (var cmd = otra.CreateCommand())
        {
            cmd.CommandText = $"EXEC sp_releaseapplock @Resource = 'carga:{p.EmpresaId}:sedes', @LockOwner = 'Session';";
            await cmd.ExecuteNonQueryAsync();
        }
        Assert.True(await p.Procesador.ProcesarAsync(id2, false, CancellationToken.None));
        Assert.True(await p.Procesador.ProcesarAsync(id1, false, CancellationToken.None));
        Assert.Equal("OK", (await p.ResumenAsync(id2)).Estado);
    }
}
