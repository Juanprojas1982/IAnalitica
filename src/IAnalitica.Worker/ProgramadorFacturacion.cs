using IAnalitica.Infrastructure.Facturacion;

namespace IAnalitica.Worker;

/// <summary>Cada 5 minutos pide al planificador que encole lo vencido de la facturación (08 §32.4). La lógica y las pruebas están en <see cref="PlanificadorFacturacion"/>.</summary>
public sealed class ProgramadorFacturacion(PlanificadorFacturacion planificador, ILogger<ProgramadorFacturacion> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken parada)
    {
        while (!parada.IsCancellationRequested)
        {
            try { await planificador.EjecutarAsync(parada).ConfigureAwait(false); }
            catch (OperationCanceledException) when (parada.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogError(e, "Error en el programador de facturación"); }   // la BD puede no responder: se reintenta en el siguiente ciclo
            try { await Task.Delay(TimeSpan.FromMinutes(5), parada).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
