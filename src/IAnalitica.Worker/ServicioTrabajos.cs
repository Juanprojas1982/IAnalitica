using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure.Ingesta;
using IAnalitica.Infrastructure.Reportes;

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
public sealed class ServicioTrabajos(DespachadorTrabajos despachador, ExpiradorReportes expirador, OpcionesWorker opciones, ILogger<ServicioTrabajos> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken parada)
    {
        var n = Math.Max(1, opciones.MaxConcurrentes);
        log.LogInformation("Worker de procesamiento iniciado ({N} ciclos).", n);
        var ciclos = Enumerable.Range(0, n).Select(_ => Task.Run(() => CicloAsync(parada), CancellationToken.None)).Append(Task.Run(() => MantenimientoAsync(parada), CancellationToken.None));
        await Task.WhenAll(ciclos).ConfigureAwait(false);
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

    /// <summary>Cada hora vence los reportes con más de 7 días (borra el archivo y marca EXPIRADO).</summary>
    private async Task MantenimientoAsync(CancellationToken parada)
    {
        while (!parada.IsCancellationRequested)
        {
            try { var n = await expirador.ExpirarAsync(parada).ConfigureAwait(false); if (n > 0) log.LogInformation("{N} reportes vencidos", n); }
            catch (OperationCanceledException) when (parada.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogError(e, "Error al vencer reportes"); }
            try { await Task.Delay(TimeSpan.FromHours(1), parada).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
