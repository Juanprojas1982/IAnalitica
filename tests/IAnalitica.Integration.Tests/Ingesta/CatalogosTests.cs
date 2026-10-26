using IAnalitica.Integration.Tests.Infra;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>Procesamiento de catálogos (contrato 05 §3–§5) contra SQL Server real.</summary>
[Collection(ColeccionBd.Nombre)]
public class CatalogosTests(BaseDeDatosFixture bd)
{
    [Fact]
    public async Task Completa_inserta_actualiza_y_desactiva_lo_ausente()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        var r1 = await p.ProcesarAsync("sedes", "COMPLETA", new object[]
        {
            new { codigoOrigen = "1", nombre = "Principal", activa = true },
            new { codigoOrigen = "2", nombre = "Norte", nombreCorto = "N", activa = true },
            new { codigoOrigen = "3", nombre = "Sur", activa = true },
        });
        Assert.Equal(("OK", 3, 0, 0, 0), (r1.Estado, r1.Insertadas, r1.Actualizadas, r1.Eliminadas, r1.Rechazadas));

        var r2 = await p.ProcesarAsync("sedes", "COMPLETA", new object[]
        {
            new { codigoOrigen = "1", nombre = "Principal renombrada", activa = true },
            new { codigoOrigen = "2", nombre = "Norte", activa = true },
        });
        Assert.Equal(("OK", 0, 2, 1, 0), (r2.Estado, r2.Insertadas, r2.Actualizadas, r2.Eliminadas, r2.Rechazadas));
        Assert.Equal("Principal renombrada", await p.EscalarAsync<string>("SELECT Nombre FROM dim.Sede WHERE EmpresaId = @e AND CodigoOrigen = '1'", new { e = p.EmpresaId }));
        Assert.Equal(0, await p.EscalarAsync<int>("SELECT CAST(Activa AS int) FROM dim.Sede WHERE EmpresaId = @e AND CodigoOrigen = '3'", new { e = p.EmpresaId }));
        Assert.Equal(3, await p.ContarAsync("dim.Sede"));   // desactivar no borra
        Assert.Null(await p.EscalarAsync<string?>("SELECT NombreCorto FROM dim.Sede WHERE EmpresaId = @e AND CodigoOrigen = '2'", new { e = p.EmpresaId }));
    }

    [Fact]
    public async Task El_staging_se_borra_al_terminar_la_carga()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        var r = await p.ProcesarAsync("sedes", "COMPLETA", new object[] { new { codigoOrigen = "1", nombre = "A" } });
        Assert.Equal(0, await p.EscalarAsync<int>("SELECT COUNT(*) FROM stg.Payload WHERE CargaId = @id", new { id = r.CargaId }));
    }

    [Fact]
    public async Task Rechaza_por_fila_con_motivo_y_sigue_con_el_resto()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        var r = await p.ProcesarAsync("cuentas", "COMPLETA", new object[]
        {
            new { codigo = "1105", nombre = "Caja", nivel = 4, naturaleza = "D" },
            new { codigo = "1110", nombre = "Bancos", nivel = 4, naturaleza = "X" },            // naturaleza inválida
            new { codigo = "1115", nivel = 4, naturaleza = "D" },                               // sin nombre
            new { codigo = "1105", nombre = "Caja repetida", nivel = 4, naturaleza = "D" },     // repetida: gana la última del archivo
            new { codigo = "1120", nombre = "Otra", nivel = 4, naturaleza = "C", detalleCartera = 7 },
        });
        Assert.Equal("OK", r.Estado);
        Assert.Equal(1, r.Insertadas);
        Assert.Equal(4, r.Rechazadas);
        Assert.Contains(r.Rechazos, x => x.Clave == "1110" && x.Motivo == "VALOR_INVALIDO:naturaleza");
        Assert.Contains(r.Rechazos, x => x.Clave == "1115" && x.Motivo == "CAMPO_REQUERIDO:nombre");
        Assert.Contains(r.Rechazos, x => x.Clave == "1120" && x.Motivo == "VALOR_INVALIDO:detalleCartera");
        Assert.Equal("Caja repetida", await p.EscalarAsync<string>("SELECT Nombre FROM dim.CuentaContable WHERE EmpresaId = @e AND Codigo = '1105'", new { e = p.EmpresaId }));
        Assert.Single(r.Rechazos, x => x.Motivo == "DUPLICADO_EN_CARGA");
    }

    [Fact]
    public async Task Referencia_requerida_inexistente_se_rechaza_y_la_opcional_queda_en_NULL()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        await p.ProcesarAsync("articulos", "COMPLETA", new object[] { new { codigoOrigen = "A1", nombre = "Acetaminofén" } });
        await p.ProcesarAsync("pagadores", "COMPLETA", new object[] { new { codigoOrigen = "E1", nombre = "EPS" } });

        var lotes = await p.ProcesarAsync("lotes", "COMPLETA", new object[]
        {
            new { codigoOrigen = "L1", articuloCodigo = "A1", numeroLote = "X1" },
            new { codigoOrigen = "L2", articuloCodigo = "NO-EXISTE", numeroLote = "X2" },
        });
        Assert.Equal(1, lotes.Insertadas);
        Assert.Contains(lotes.Rechazos, x => x.Clave == "L2" && x.Motivo == "REFERENCIA_INEXISTENTE:articuloCodigo");

        var contratos = await p.ProcesarAsync("contratos", "COMPLETA", new object[]
        {
            new { codigoOrigen = "C1", numeroContrato = "001", pagadorCodigo = "E1", modalidad = "EVENTO" },
            new { codigoOrigen = "C2", numeroContrato = "002", pagadorCodigo = "NO-EXISTE", modalidad = "PGP" },
        });
        Assert.Equal((2, 0), (contratos.Insertadas, contratos.Rechazadas));
        Assert.Null(await p.EscalarAsync<int?>("SELECT PagadorId FROM dim.Contrato WHERE EmpresaId = @e AND CodigoOrigen = 'C2'", new { e = p.EmpresaId }));
        Assert.NotNull(await p.EscalarAsync<int?>("SELECT PagadorId FROM dim.Contrato WHERE EmpresaId = @e AND CodigoOrigen = 'C1'", new { e = p.EmpresaId }));
    }

    [Fact]
    public async Task Un_paciente_nunca_guarda_nombre_y_los_pagadores_marcan_su_tercero()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        await p.ProcesarAsync("terceros", "INCREMENTAL", new object[]
        {
            new { codigoOrigen = "T1", identificacion = "ABCDEF0123456789", esPaciente = true, nombre = "Juan Pérez" },   // defensa en profundidad
            new { codigoOrigen = "T2", identificacion = "900123456", esPaciente = false, nombre = "EPS Salud", tipoPersona = "J", digitoVerificacion = "7" },
        });
        Assert.Null(await p.EscalarAsync<string?>("SELECT Nombre FROM dim.Tercero WHERE EmpresaId = @e AND CodigoOrigen = 'T1'", new { e = p.EmpresaId }));
        Assert.Equal(1, await p.EscalarAsync<int>("SELECT CAST(EsPaciente AS int) FROM dim.Tercero WHERE EmpresaId = @e AND CodigoOrigen = 'T1'", new { e = p.EmpresaId }));
        Assert.Equal("J", await p.EscalarAsync<string>("SELECT TipoPersona FROM dim.Tercero WHERE EmpresaId = @e AND CodigoOrigen = 'T1'", new { e = p.EmpresaId }));   // por defecto

        await p.ProcesarAsync("pagadores", "COMPLETA", new object[] { new { codigoOrigen = "E1", nombre = "EPS Salud", terceroCodigo = "T2", activo = true } });
        Assert.Equal(1, await p.EscalarAsync<int>("SELECT CAST(EsPagador AS int) FROM dim.Tercero WHERE EmpresaId = @e AND CodigoOrigen = 'T2'", new { e = p.EmpresaId }));
        Assert.Equal(0, await p.EscalarAsync<int>("SELECT CAST(EsPagador AS int) FROM dim.Tercero WHERE EmpresaId = @e AND CodigoOrigen = 'T1'", new { e = p.EmpresaId }));
    }

    [Fact]
    public async Task Incremental_no_desactiva_nada_y_los_profesionales_toman_el_nombre_de_su_especialidad()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        await p.ProcesarAsync("especialidades", "COMPLETA", new object[] { new { codigoOrigen = "ESP1", nombre = "Cardiología" } });
        var r = await p.ProcesarAsync("profesionales", "COMPLETA", new object[]
        {
            new { codigoOrigen = "M1", nombre = "Dr. Uno", especialidadCodigo = "ESP1", activo = true },
            new { codigoOrigen = "M2", nombre = "Dra. Dos", especialidadCodigo = "ESP-X", activo = true },
        });
        Assert.Equal(2, r.Insertadas);
        Assert.Equal("Cardiología", await p.EscalarAsync<string>("SELECT Especialidad FROM dim.Profesional WHERE EmpresaId = @e AND CodigoOrigen = 'M1'", new { e = p.EmpresaId }));
        Assert.Null(await p.EscalarAsync<string?>("SELECT Especialidad FROM dim.Profesional WHERE EmpresaId = @e AND CodigoOrigen = 'M2'", new { e = p.EmpresaId }));

        // Terceros en INCREMENTAL: lo que no llega se conserva
        await p.ProcesarAsync("terceros", "INCREMENTAL", new object[] { new { codigoOrigen = "A", identificacion = "1", esPaciente = false, nombre = "A" } });
        var inc = await p.ProcesarAsync("terceros", "INCREMENTAL", new object[] { new { codigoOrigen = "B", identificacion = "2", esPaciente = false, nombre = "B" } });
        Assert.Equal(0, inc.Eliminadas);
        Assert.Equal(2, await p.ContarAsync("dim.Tercero"));
    }

    [Fact]
    public async Task Dos_empresas_con_el_mismo_codigo_no_se_mezclan()
    {
        await using var a = await Procesamiento.NuevaEmpresaAsync(bd);
        await using var b = await Procesamiento.NuevaEmpresaAsync(bd);
        await a.ProcesarAsync("sedes", "COMPLETA", new object[] { new { codigoOrigen = "1", nombre = "Sede de A" } });
        await b.ProcesarAsync("sedes", "COMPLETA", new object[] { new { codigoOrigen = "1", nombre = "Sede de B" } });
        // B recarga sin la sede «1»: solo desactiva la suya
        await b.ProcesarAsync("sedes", "COMPLETA", new object[] { new { codigoOrigen = "9", nombre = "Otra de B" } });
        Assert.Equal(1, await a.EscalarAsync<int>("SELECT CAST(Activa AS int) FROM dim.Sede WHERE EmpresaId = @e AND CodigoOrigen = '1'", new { e = a.EmpresaId }));
        Assert.Equal("Sede de A", await a.EscalarAsync<string>("SELECT Nombre FROM dim.Sede WHERE EmpresaId = @e AND CodigoOrigen = '1'", new { e = a.EmpresaId }));
    }

    /// <summary>Humo: el SQL generado de las 16 definiciones compila y aplica una fila mínima válida.</summary>
    [Theory]
    [InlineData("sedes", """{"codigoOrigen":"1","nombre":"X"}""")]
    [InlineData("lineas-servicio", """{"codigoOrigen":"1","nombre":"X"}""")]
    [InlineData("centros-costo", """{"codigoOrigen":"1","nombre":"X","codigoPadre":"0","nivel":2,"tipoOrigen":"1","manejaMovimientos":true,"unidadNegocio":"U","activo":true}""")]
    [InlineData("cuentas", """{"codigo":"1","nombre":"X","nivel":1,"naturaleza":"D"}""")]
    [InlineData("tipos-comprobante", """{"codigoOrigen":"1","prefijo":"P","nombre":"X","claseSugerida":"CIERRE"}""")]
    [InlineData("terceros", """{"codigoOrigen":"1","identificacion":"1","esPaciente":false,"nombre":"X","claseOrigen":"3"}""")]
    [InlineData("pagadores", """{"codigoOrigen":"1","nombre":"X","codigoAdministradora":"EPS01","esParticular":false,"activo":true}""")]
    [InlineData("contratos", """{"codigoOrigen":"1","numeroContrato":"1","modalidad":"CAPITACION","fechaInicio":"2026-01-01","fechaFin":"2026-12-31","valorContrato":1000000.50}""")]
    [InlineData("servicios", """{"codigoOrigen":"P:890201","nombre":"Consulta","codigoEstandar":"890201","tipo":"CONSULTA"}""")]
    [InlineData("articulos", """{"codigoOrigen":"1","nombre":"X","linea":"L","grupo":"G","subgrupo":"S","generico":"g","laboratorio":"l","unidadMedida":"u","codigoCum":"123-1"}""")]
    [InlineData("almacenes", """{"codigoOrigen":"1","nombre":"X","activo":true}""")]
    [InlineData("especialidades", """{"codigoOrigen":"1","nombre":"X"}""")]
    [InlineData("profesionales", """{"codigoOrigen":"1","nombre":"X","terceroCodigo":"T","activo":true}""")]
    [InlineData("usuarios-operativos", """{"codigoOrigen":"C:1","nombre":"X","activo":true}""")]
    [InlineData("cuentas-bancarias", """{"codigoOrigen":"B:1","tipo":"BANCO","entidad":"Banco","numeroEnmascarado":"****1234"}""")]
    public async Task Cada_catalogo_aplica_una_fila_minima(string entidad, string json)
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        var fila = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;
        var r = await p.ProcesarAsync(entidad, entidad == "terceros" ? "INCREMENTAL" : "COMPLETA", new object[] { fila });
        Assert.True(r is { Estado: "OK", Insertadas: 1, Rechazadas: 0 }, $"{entidad}: {r.Estado} ins={r.Insertadas} rech={r.Rechazadas} {string.Join(",", r.Rechazos)}");
    }

    [Fact]
    public async Task Toda_entidad_catalogo_que_acepta_la_API_tiene_procesador()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        foreach (var e in IAnalitica.Application.Ingesta.CatalogoEntidades.Todas.Where(e => e.EsCatalogo))
        {
            var r = await p.ProcesarAsync(e.Nombre, e.Modos[0], Array.Empty<object>());   // carga vacía: debe aplicarse sin errores
            Assert.True(r.Estado == "OK", $"{e.Nombre}: {r.Estado} {r.Error}");
        }
    }

    [Fact]
    public async Task Una_carga_grande_en_varios_lotes_se_procesa_completa()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        var filas = Enumerable.Range(1, 12000).Select(i => (object)new { codigoOrigen = "S" + i, nombre = "Servicio " + i, tipo = "PROCEDIMIENTO" });
        var r = await p.ProcesarAsync("servicios", "COMPLETA", filas, porLote: 5000);
        Assert.Equal(("OK", 12000), (r.Estado, r.Insertadas));
    }
}
