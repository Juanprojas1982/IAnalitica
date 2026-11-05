using System.Diagnostics;
using IAnalitica.Infrastructure.Observabilidad;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace IAnalitica.Integration.Tests;

/// <summary>
/// Procesador de limpieza obligatorio (spec 19 §3, 24 §12.4): estas pruebas FALLAN si en lo exportado aparece un secreto,
/// un documento, un correo, un cuerpo de carga o un texto de IA. Se prueba con el pipeline real y un exportador en memoria.
/// </summary>
public class ObservabilidadTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
    private const string Cedula = "1130456789";
    private const string Correo = "maria.perez@clinica.test";
    private const string Conexion = "Server=tcp:db.test;Database=IAnalitica;User ID=sa;Password=Sup3r#Secreta;";

    private static readonly string[] Prohibidos = { Jwt, Cedula, Correo, "Sup3r#Secreta", "Bearer abc123def456", "secreto-del-conector", "SELECT * FROM fact.Cita", "Juan Camilo Pérez" };

    [Fact]
    public void Los_spans_exportados_no_llevan_secretos_documentos_correos_cuerpos_ni_sql()
    {
        var exportados = new List<Activity>();
        using var fuente = new ActivitySource("IAnalitica.Pruebas.Limpieza");
        using var proveedor = Sdk.CreateTracerProviderBuilder()
            .AddSource("IAnalitica.Pruebas.Limpieza")
            .AddProcessor(new LimpiezaActividadProcessor())
            .AddInMemoryExporter(exportados)
            .Build();

        using (var a = fuente.StartActivity("POST /v1/cargas?token=abc123"))
        {
            a!.SetTag("http.request.header.authorization", "Bearer abc123def456");
            a.SetTag("db.statement", "SELECT * FROM fact.Cita WHERE Paciente = 'Juan Camilo Pérez'");
            a.SetTag("db.query.parameter.cedula", Cedula);
            a.SetTag("http.request.body", "{\"filas\":[{\"identificacion\":\"" + Cedula + "\"}]}");
            a.SetTag("usuario.correo", Correo);
            a.SetTag("detalle", $"Falló con {Jwt} para {Correo} cédula {Cedula}");
            a.SetTag("conexion", Conexion);
            a.SetTag("config", "client_secret=secreto-del-conector;otro=1");
            a.SetTag("ia.pregunta", "¿Cuánto debe Juan Camilo Pérez?");
            a.SetStatus(ActivityStatusCode.Error, $"Error con {Correo}");
        }
        proveedor.ForceFlush();

        var exportado = Assert.Single(exportados);
        var todo = string.Join("|", exportado.TagObjects.Select(t => $"{t.Key}={t.Value}")) + "|" + exportado.StatusDescription + "|" + exportado.DisplayName;
        foreach (var prohibido in Prohibidos) Assert.DoesNotContain(prohibido, todo);
        Assert.Contains(Limpiador.Redactado, todo);
    }

    [Fact]
    public void Los_identificadores_tecnicos_comunes_si_se_conservan()
    {
        var exportados = new List<Activity>();
        using var fuente = new ActivitySource("IAnalitica.Pruebas.Seguros");
        using var proveedor = Sdk.CreateTracerProviderBuilder()
            .AddSource("IAnalitica.Pruebas.Seguros")
            .AddProcessor(new LimpiezaActividadProcessor())
            .AddInMemoryExporter(exportados)
            .Build();

        using (var a = fuente.StartActivity("PROCESAR_CARGA"))
        {
            a!.SetTag("empresa.id", 1234567);
            a.SetTag("carga.id", 98765432L);
            a.SetTag("entidad", "asientos");
            a.SetTag("usuario.id", 42);
            a.SetTag("version.conector", "1.0.3");
            a.SetTag("version.saludips", "2026.10");
        }
        proveedor.ForceFlush();

        var tags = Assert.Single(exportados).TagObjects.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal(1234567, tags["empresa.id"]);
        Assert.Equal(98765432L, tags["carga.id"]);
        Assert.Equal("asientos", tags["entidad"]);
        Assert.Equal("1.0.3", tags["version.conector"]);
    }

    [Fact]
    public void Los_logs_exportados_no_llevan_secretos_documentos_correos_ni_el_mensaje_de_excepciones()
    {
        var exportados = new List<LogRecord>();
        using var fabrica = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.AddProcessor(new LimpiezaLogProcessor());
            o.AddInMemoryExporter(exportados);
        }));
        var logger = fabrica.CreateLogger("Pruebas");

        logger.LogWarning(new InvalidOperationException($"Detalle con {Correo} y {Cedula}"),
            "Carga rechazada para {Correo} con documento {Documento} y conexión {Conexion} token {Token}", Correo, Cedula, Conexion, Jwt);

        var registro = Assert.Single(exportados);
        var todo = registro.FormattedMessage + "|" + registro.Body + "|" +
                   string.Join("|", (registro.Attributes ?? Array.Empty<KeyValuePair<string, object?>>()).Select(a => $"{a.Key}={a.Value}")) + "|" + registro.Exception;
        foreach (var prohibido in Prohibidos) Assert.DoesNotContain(prohibido, todo);
        Assert.Null(registro.Exception);
        Assert.Contains("System.InvalidOperationException", todo);   // el tipo sí se conserva
    }

    [Theory]
    [InlineData("Password=abc123;Server=x", "abc123")]
    [InlineData("{\"password\":\"Qwerty#2026\"}", "Qwerty#2026")]
    [InlineData("https://x.test/cb?code=XYZ789&state=1", "XYZ789")]
    [InlineData("Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA")]
    [InlineData("llamar al 3105551234", "3105551234")]
    [InlineData("NIT 900123456", "900123456")]
    public void El_limpiador_enmascara_patrones_de_secretos_y_documentos(string entrada, string secreto)
        => Assert.DoesNotContain(secreto, Limpiador.LimpiarTexto(entrada));

    [Theory]
    [InlineData("Authorization")]
    [InlineData("db.statement")]
    [InlineData("http.request.body")]
    [InlineData("ia.pregunta")]
    [InlineData("ClientSecret")]
    [InlineData("connectionString")]
    [InlineData("Set-Cookie")]
    public void Las_claves_prohibidas_se_redactan_sea_cual_sea_el_valor(string clave)
        => Assert.Equal(Limpiador.Redactado, Limpiador.Limpiar(clave, "cualquier cosa inocente"));

    [Fact]
    public void Sin_nada_sensible_el_texto_no_cambia() => Assert.Equal("Carga 7 terminó en 120 ms", Limpiador.LimpiarTexto("Carga 7 terminó en 120 ms"));

    [Fact]
    public void El_muestreo_deja_pasar_siempre_los_errores_y_solo_una_fraccion_de_los_exitosos()
    {
        var exportados = new List<Activity>();
        using var fuente = new ActivitySource("IAnalitica.Pruebas.Muestreo");
        using var proveedor = Sdk.CreateTracerProviderBuilder()
            .AddSource("IAnalitica.Pruebas.Muestreo")
            .SetSampler(new MuestreoConErroresSampler(0.1))
            .AddProcessor(new ExportarErroresProcessor())
            .AddInMemoryExporter(exportados)
            .Build();

        for (var i = 0; i < 500; i++) { using var ok = fuente.StartActivity("ok"); }
        for (var i = 0; i < 50; i++) { using var mal = fuente.StartActivity("error"); mal!.SetStatus(ActivityStatusCode.Error); }
        proveedor.ForceFlush();

        Assert.Equal(50, exportados.Count(a => a.DisplayName == "error"));                 // 100 % de los errores
        var exitosos = exportados.Count(a => a.DisplayName == "ok");
        Assert.InRange(exitosos, 20, 90);                                                  // ≈ 10 % de 500
    }

    [Fact]
    public void Con_fraccion_1_todo_se_exporta_como_en_desarrollo_local()
    {
        var exportados = new List<Activity>();
        using var fuente = new ActivitySource("IAnalitica.Pruebas.Dev");
        using var proveedor = Sdk.CreateTracerProviderBuilder()
            .AddSource("IAnalitica.Pruebas.Dev")
            .SetSampler(new MuestreoConErroresSampler(1.0))
            .AddInMemoryExporter(exportados)
            .Build();
        for (var i = 0; i < 100; i++) { using var _ = fuente.StartActivity("x"); }
        proveedor.ForceFlush();
        Assert.Equal(100, exportados.Count);
    }
}
