using System.Text;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Mediacion;
using IAnalitica.Application.Regulacion;
using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>Importación de listas de precios máximos y factores de presentación (catálogo global reg.*).</summary>
[Collection(ColeccionBd.Nombre)]
public class RegulacionTests(BaseDeDatosFixture bd)
{
    private static int _serie = 700000;

    private static MapeoPreciosMaximos Mapeo() => new()
    {
        CampoExpediente = "expediente", CampoConsecutivo = "consecutivo", CampoPrincipio = "principio", CampoPresentacion = "presentacion",
        CampoPrecioPresentacion = "precio", CampoPrecioUnidad = "precio_unidad", CampoUnidadesPorPresentacion = "umd",
    };

    private static DatosCircular Circular(string numero) => new("CNPMDM", numero, 2026, "PRECIO_MAXIMO", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 19), null, "https://ejemplo.invalido/" + numero, null);

    private static Stream Json(string texto) => new MemoryStream(Encoding.UTF8.GetBytes(texto));

    private async Task<(AsyncServiceScope Alcance, ISender Enviar)> SuperadminAsync(MapeoPreciosMaximos? mapeo, params string[] permisos)
    {
        var sesion = new SesionPrueba(bd.SuperadminId, null, true, permisos.Length == 0 ? new[] { "admin.catalogos" } : permisos);
        var sp = bd.Construir(bd.CadenaApp, sesion, s => { if (mapeo is not null) s.AddSingleton<IOptions<MapeoPreciosMaximos>>(Options.Create(mapeo)); else s.AddSingleton<IOptions<MapeoPreciosMaximos>>(Options.Create(new MapeoPreciosMaximos())); });
        var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeSuperadmin(bd.SuperadminId));
        return (alcance, alcance.ServiceProvider.GetRequiredService<ISender>());
    }

    [Fact]
    public async Task Importa_precios_y_factores_oficiales_rechaza_filas_malas_y_reimportar_reemplaza()
    {
        var n = Interlocked.Add(ref _serie, 10);
        var (alcance, enviar) = await SuperadminAsync(Mapeo());
        await using var _ = alcance;
        var json = $$"""
            [
              {"expediente":"{{n}}","consecutivo":"1","principio":"Acetaminofén","presentacion":"Caja x 100","precio":"1500.50","precio_unidad":"15.005","umd":"100"},
              {"expediente":"{{n}}","consecutivo":"2","principio":"Ibuprofeno","presentacion":"Caja x 30","precio":"900"},
              {"expediente":"{{n}}","consecutivo":"3","precio":"no es número"},
              {"expediente":"","consecutivo":"1","precio":"10"},
              {"expediente":"{{n}}","consecutivo":"1","principio":"Acetaminofén (repetido)","precio":"1600"}
            ]
            """;
        var r = await enviar.Send(new ImportarPreciosMaximosCommand(Circular("I" + n), Json(json)));
        Assert.True(r.EsExitoso, string.Join("; ", r.Errores.Select(e => e.Mensaje)));
        Assert.Equal((2, 2), (r.Valor.Importados, r.Valor.Rechazados));

        var cum1 = $"{n}-1";
        Assert.Equal(1600m, await Escalar<decimal>("SELECT PrecioMaximoPresentacion FROM reg.PrecioMaximo WHERE Cum = @c", new { c = cum1 }));     // gana la última fila
        Assert.Equal(2, await Escalar<int>("SELECT COUNT(*) FROM reg.PrecioMaximo WHERE CircularId = @id", new { id = r.Valor.CircularId }));
        Assert.Equal("2026-08-19", await Escalar<string>("SELECT CONVERT(varchar(10), VigenteDesde, 23) FROM reg.PrecioMaximo WHERE Cum = @c", new { c = cum1 }));
        Assert.Equal(0, await Escalar<int>("SELECT COUNT(*) FROM reg.FactorPresentacion WHERE Cum = @c", new { c = cum1 }));                    // la fila ganadora no trae factor

        // Reimportar la misma circular reemplaza sus precios, sin duplicar
        var r2 = await enviar.Send(new ImportarPreciosMaximosCommand(Circular("I" + n), Json($$"""[{"expediente":"{{n}}","consecutivo":"1","precio":"1700","umd":"50"}]""")));
        Assert.Equal(r.Valor.CircularId, r2.Valor.CircularId);
        Assert.Equal(1, await Escalar<int>("SELECT COUNT(*) FROM reg.PrecioMaximo WHERE CircularId = @id", new { id = r.Valor.CircularId }));
        Assert.Equal(50m, await Escalar<decimal>("SELECT UnidadesPorPresentacion FROM reg.FactorPresentacion WHERE Cum = @c AND Fuente = 'OFICIAL'", new { c = cum1 }));
        Assert.Equal(2, await Escalar<int>("SELECT COUNT(*) FROM aud.CambioAdministrativo WHERE Entidad = 'reg.PrecioMaximo' AND ClaveEntidad = @k AND Accion = 'IMPORTAR'", new { k = $"CNPMDM-I{n}-2026" }));
    }

    [Fact]
    public async Task Sin_mapeo_configurado_no_importa_ni_adivina_los_campos()
    {
        var (alcance, enviar) = await SuperadminAsync(mapeo: null);
        await using var _ = alcance;
        var r = await enviar.Send(new ImportarPreciosMaximosCommand(Circular("SM"), Json("[]")));
        Assert.False(r.EsExitoso);
        Assert.Equal(CodigosRegulacion.MapeoNoConfigurado, r.Errores[0].Codigo);
    }

    [Fact]
    public async Task Un_archivo_que_no_es_un_arreglo_JSON_se_rechaza_con_mensaje()
    {
        var (alcance, enviar) = await SuperadminAsync(Mapeo());
        await using var _ = alcance;
        var r = await enviar.Send(new ImportarPreciosMaximosCommand(Circular("AI"), Json("esto no es json")));
        Assert.False(r.EsExitoso);
        Assert.Equal(CodigosRegulacion.ArchivoInvalido, r.Errores[0].Codigo);
    }

    [Fact]
    public async Task Importar_exige_el_permiso_de_catalogos()
    {
        var (alcance, enviar) = await SuperadminAsync(Mapeo(), "dash.financiero");
        await using var _ = alcance;
        var r = await enviar.Send(new ImportarPreciosMaximosCommand(Circular("NP"), Json("[]")));
        Assert.False(r.EsExitoso);
        Assert.Equal("NO_AUTORIZADO", r.Errores[0].Codigo);
    }

    [Fact]
    public async Task El_factor_validado_pisa_al_oficial_y_un_factor_manual_no_lo_pisa_otra_importacion()
    {
        var n = Interlocked.Add(ref _serie, 10);
        var cum = $"{n}-1";
        var (alcance, enviar) = await SuperadminAsync(Mapeo());
        await using var _ = alcance;
        var invalido = await enviar.Send(new RegistrarFactorPresentacionCommand("sin-formato", 10, "QF Prueba"));
        Assert.False(invalido.EsExitoso);

        Assert.True((await enviar.Send(new RegistrarFactorPresentacionCommand(cum, 12, "QF Prueba"))).EsExitoso);
        Assert.Equal("MANUAL", await Escalar<string>("SELECT Fuente FROM reg.FactorPresentacion WHERE Cum = @c", new { c = cum }));
        Assert.NotNull(await Escalar<string?>("SELECT CONVERT(varchar(30), ValidadoEn) FROM reg.FactorPresentacion WHERE Cum = @c", new { c = cum }));

        await enviar.Send(new ImportarPreciosMaximosCommand(Circular("FM" + n), Json($$"""[{"expediente":"{{n}}","consecutivo":"1","precio":"100","umd":"99"}]""")));
        Assert.Equal(12m, await Escalar<decimal>("SELECT UnidadesPorPresentacion FROM reg.FactorPresentacion WHERE Cum = @c", new { c = cum }));   // el manual validado sigue
    }

    private async Task<T> Escalar<T>(string sql, object? p = null)
    {
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(bd.CadenaPropietario);
        await c.OpenAsync();
        await Dapper.SqlMapper.ExecuteAsync(c, "EXEC sp_set_session_context N'EsSuperAdmin', 1");
        return (await Dapper.SqlMapper.ExecuteScalarAsync<T>(c, sql, p))!;
    }
}
