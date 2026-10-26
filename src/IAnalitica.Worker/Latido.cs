namespace IAnalitica.Worker;

/// <summary>Marcador de posición de F0: mantiene vivo el host hasta que F2 agregue el consumo de la cola de trabajos.</summary>
public sealed class Latido(ILogger<Latido> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker iniciado (sin trabajos hasta F2).");
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { logger.LogInformation("Worker detenido."); }
    }
}
