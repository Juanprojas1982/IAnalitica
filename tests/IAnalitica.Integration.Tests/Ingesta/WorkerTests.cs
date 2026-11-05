using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Ingesta;
using IAnalitica.Integration.Tests.Infra;
using IAnalitica.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>El worker real (ServicioTrabajos) consumiendo etl.Trabajo con el login de rol_etl.</summary>
[Collection(ColeccionBd.Nombre)]
public class WorkerTests(BaseDeDatosFixture bd)
{
    [Fact]
    public async Task El_worker_procesa_cargas_en_segundo_plano_y_encadena_la_cartera()
    {
        await using var p = await Procesamiento.NuevaEmpresaAsync(bd);
        await p.CargarCatalogosBaseAsync();

        var servicios = new ServiceCollection();
        servicios.AddLogging(l => l.AddFilter("Microsoft", LogLevel.Warning));
        servicios.AddSingleton(TimeProvider.System);
        servicios.AddProcesamiento(bd.CadenaEtl);
        servicios.AddSingleton(new OpcionesWorker { MaxConcurrentes = 2, SondeoSegundos = 1 });
        servicios.AddSingleton<ServicioTrabajos>();   // el expirador de reportes lo trae AddProcesamiento
        await using var sp = servicios.BuildServiceProvider();

        var filas = new[]
        {
            Procesamiento.Linea("W-1", "WM1", "2026-01-10", "130505", 250, 0, "P1", numDoc: "FW"),
            Procesamiento.Linea("W-2", "WM1", "2026-01-10", "413501", 0, 250),
        };
        var carga = await p.CrearCargaAsync("asientos", "VENTANA", filas, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        await p.Cola.EncolarAsync(new TrabajoPendiente(TiposTrabajo.ProcesarCarga, p.EmpresaId, $"EMP:{p.EmpresaId}|CARGA:{carga}", $"{{\"cargaId\":{carga}}}"), CancellationToken.None);

        var worker = sp.GetRequiredService<ServicioTrabajos>();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await EsperarAsync(async () => (await p.ResumenAsync(carga)).Estado == "OK", "la carga debía quedar OK");
            await EsperarAsync(async () => await p.ContarAsync("fact.CarteraDocumento") == 1, "la cartera debía calcularse");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
        Assert.Equal("OK", await p.EscalarAsync<string>("SELECT Estado FROM etl.Trabajo WHERE ClaveExclusion = @c", new { c = $"EMP:{p.EmpresaId}|CARGA:{carga}" }));
    }

    private static async Task EsperarAsync(Func<Task<bool>> condicion, string mensaje)
    {
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            if (await condicion()) return;
            await Task.Delay(300);
        }
        Assert.Fail("Tiempo agotado: " + mensaje);
    }
}
