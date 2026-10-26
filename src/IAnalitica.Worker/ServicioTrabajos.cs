using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure.Ingesta;

namespace IAnalitica.Worker;

public sealed class OpcionesWorker
{
    /// <summary>Trabajos simultáneos (24 §8: 2 en pruebas, configurable).</summary>
    public int MaxConcurrentes { get; set; } = 2;
    public int SondeoSegundos { get; set; } = 5;
}

/// <summary>
/// Consume <c>etl.Trabajo</c> con <see cref="OpcionesWorker.MaxConcurrentes"/> ciclos en paralelo. Cada ciclo busca primero
/// PROCESAR_CARGA y después CALCULAR_CARTERA; si no hay nada, duerme. Un apagado ordenado no pierde trabajo: el lease lo devuelve a la cola.
/// </summary>
public sealed class ServicioTrabajos(DespachadorTrabajos despachador, OpcionesWorker opciones, ILogger<ServicioTrabajos> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken parada)
    {
        var n = Math.Max(1, opciones.MaxConcurrentes);
        log.LogInformation("Worker de procesamiento iniciado ({N} ciclos).", n);
        await Task.WhenAll(Enumerable.Range(0, n).Select(_ => Task.Run(() => CicloAsync(parada), CancellationToken.None))).ConfigureAwait(false);
        log.LogInformation("Worker de procesamiento detenido.");
    }

    private async Task CicloAsync(CancellationToken parada)
    {
        var tipos = despachador.Tipos.OrderBy(t => t == TiposTrabajo.ProcesarCarga ? 0 : 1).ToArray();
        while (!parada.IsCancellationRequested)
        {
            var hizoAlgo = false;
            try
            {
                foreach (var tipo in tipos)
                    if (await despachador.EjecutarUnoAsync(tipo, parada).ConfigureAwait(false)) { hizoAlgo = true; break; }
            }
            catch (OperationCanceledException) when (parada.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                log.LogError(e, "Error inesperado en el ciclo del worker");   // p. ej. la BD no responde: se reintenta tras la pausa
            }

            if (hizoAlgo) continue;
            try { await Task.Delay(TimeSpan.FromSeconds(opciones.SondeoSegundos), parada).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
