using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace IAnalitica.Infrastructure.Observabilidad;

public sealed class OpcionesObservabilidad
{
    public const string Ninguno = "ninguno";
    public const string Consola = "consola";
    public const string Otlp = "otlp";

    /// <summary>ninguno | consola | otlp. En local nada sale a la nube (D32). El exportador a Azure Monitor se agrega con aprobación.</summary>
    public string Exportador { get; set; } = Consola;

    /// <summary>Fracción de spans exitosos que se exportan: 1.0 en desarrollo; 0.1 en producción (spec 19 §4). Los errores siempre.</summary>
    public double FraccionMuestreo { get; set; } = 1.0;

    public string? PuntoOtlp { get; set; }
    public string VersionServicio { get; set; } = "0.1.0";
}

public static class ObservabilidadExtensions
{
    public const string FuenteApplication = "IAnalitica.Application";
    public const string FuenteWorker = "IAnalitica.Worker";

    /// <summary>Trazas, métricas y logs con OpenTelemetry y el procesador de limpieza obligatorio (spec 19).</summary>
    public static IServiceCollection AddObservabilidad(this IServiceCollection services, string nombreServicio, OpcionesObservabilidad opciones)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(nombreServicio, serviceVersion: opciones.VersionServicio))
            .WithTracing(t =>
            {
                t.AddSource(FuenteApplication, FuenteWorker)
                 .SetSampler(new ParentBasedSampler(new MuestreoConErroresSampler(opciones.FraccionMuestreo)))
                 .AddAspNetCoreInstrumentation(o => o.RecordException = false)
                 .AddHttpClientInstrumentation(o => o.RecordException = false)
                 .AddSqlClientInstrumentation(o => o.RecordException = false)    // sin el texto del comando ni parámetros
                 .AddProcessor(new ExportarErroresProcessor())
                 .AddProcessor(new LimpiezaActividadProcessor());                // siempre antes de los exportadores
                AgregarExportadorTrazas(t, opciones);
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (opciones.Exportador == OpcionesObservabilidad.Consola) m.AddConsoleExporter();
                if (opciones.Exportador == OpcionesObservabilidad.Otlp) m.AddOtlpExporter(o => ConfigurarOtlp(o, opciones));
            })
            .WithLogging(l =>
            {
                l.AddProcessor(new LimpiezaLogProcessor());
                if (opciones.Exportador == OpcionesObservabilidad.Consola) l.AddConsoleExporter();
                if (opciones.Exportador == OpcionesObservabilidad.Otlp) l.AddOtlpExporter(o => ConfigurarOtlp(o, opciones));
            });
        return services;
    }

    private static void AgregarExportadorTrazas(TracerProviderBuilder t, OpcionesObservabilidad opciones)
    {
        if (opciones.Exportador == OpcionesObservabilidad.Consola) t.AddConsoleExporter();
        if (opciones.Exportador == OpcionesObservabilidad.Otlp) t.AddOtlpExporter(o => ConfigurarOtlp(o, opciones));
    }

    private static void ConfigurarOtlp(OpenTelemetry.Exporter.OtlpExporterOptions o, OpcionesObservabilidad opciones)
    {
        if (!string.IsNullOrWhiteSpace(opciones.PuntoOtlp)) o.Endpoint = new Uri(opciones.PuntoOtlp);
    }
}
