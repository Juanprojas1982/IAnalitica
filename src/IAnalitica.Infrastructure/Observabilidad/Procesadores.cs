using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace IAnalitica.Infrastructure.Observabilidad;

/// <summary>Limpia las etiquetas de cada span antes de exportarlo. Debe registrarse ANTES que cualquier exportador.</summary>
public sealed class LimpiezaActividadProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity actividad)
    {
        var cambios = new List<(string Clave, object? Valor)>();
        foreach (var etiqueta in actividad.TagObjects)
        {
            var limpio = Limpiador.Limpiar(etiqueta.Key, etiqueta.Value);
            if (!Equals(limpio, etiqueta.Value)) cambios.Add((etiqueta.Key, limpio));
        }
        foreach (var (clave, valor) in cambios) actividad.SetTag(clave, valor);

        if (actividad.StatusDescription is { Length: > 0 } descripcion)
            actividad.SetStatus(actividad.Status, Limpiador.LimpiarTexto(descripcion));
        if (actividad.DisplayName.Length > 0)
            actividad.DisplayName = Limpiador.LimpiarTexto(actividad.DisplayName);
    }
}

/// <summary>Limpia mensaje, atributos y excepción de cada registro de log antes de exportarlo.</summary>
public sealed class LimpiezaLogProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord registro)
    {
        if (registro.FormattedMessage is { } formateado) registro.FormattedMessage = Limpiador.LimpiarTexto(formateado);
        if (registro.Body is { } cuerpo) registro.Body = Limpiador.LimpiarTexto(cuerpo);

        if (registro.Attributes is { Count: > 0 } atributos)
            registro.Attributes = atributos.Select(a => new KeyValuePair<string, object?>(a.Key, Limpiador.Limpiar(a.Key, a.Value))).ToList();

        // El mensaje de una excepción puede traer datos (valores de una validación, SQL, rutas): se conserva solo el tipo.
        if (registro.Exception is { } ex)
        {
            var atributosConTipo = (registro.Attributes ?? Array.Empty<KeyValuePair<string, object?>>()).ToList();
            atributosConTipo.Add(new KeyValuePair<string, object?>("exception.type", ex.GetType().FullName));
            registro.Attributes = atributosConTipo;
            registro.Exception = null;
        }
    }
}

/// <summary>
/// Muestreo (spec 19 §4): una fracción de los spans exitosos y TODOS los que terminan en error.
/// El sampler no puede saber el resultado al iniciar, así que los no muestreados quedan «solo registrados»
/// y este procesador los marca para exportación si terminan con error. Es por span, no por traza completa [S].
/// </summary>
public sealed class MuestreoConErroresSampler(double fraccion) : Sampler
{
    private readonly Sampler _fraccion = new TraceIdRatioBasedSampler(fraccion);

    public override SamplingResult ShouldSample(in SamplingParameters parametros)
    {
        var resultado = _fraccion.ShouldSample(parametros);
        return resultado.Decision == SamplingDecision.RecordAndSample
            ? resultado
            : new SamplingResult(SamplingDecision.RecordOnly);
    }
}

public sealed class ExportarErroresProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity actividad)
    {
        if (actividad.Status == ActivityStatusCode.Error && !actividad.Recorded)
            actividad.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
    }
}
