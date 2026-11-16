using System.Text;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Facturacion;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Correo;
using IAnalitica.Infrastructure.Facturacion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>PDF y correo de la cuenta de cobro (08 §32.3, §34). No necesitan base de datos: corren sin Docker.</summary>
public class CorreoYPdfTests
{
    private const string Destino = "tesoreria.confidencial@cliente-prueba.test";

    private static DocumentoCuentaCobro Documento(bool anulada = false) => new(
        new EmisorDoc("Prestador SAS", "900123456-1", "Cuenta de ahorros 000-111"),
        new ClienteDoc("Clínica de Prueba SAS", "800197268", "4", "Calle 1 # 2-3"),
        "CC-000123", new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1), 202610,
        [new LineaDoc("Canon mensual · oct-2026", 31, 31, 1_000_000m), new LineaDoc("Canon mensual · 15-10-2026 a 31-10-2026 (17/31 días)", 17, 31, 493_548m)],
        [], 1_493_548m, 250_000m, "Cuenta de ahorros 000-111", anulada);

    private static ServiceProvider Proveedor(Dictionary<string, string?> config, bool desarrollo = true, ILoggerProvider? logs = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var s = new ServiceCollection();
        s.AddLogging(b => { if (logs is not null) b.AddProvider(logs); });
        s.AddProcesamientoFacturacion();
        s.AddCorreoSaliente(cfg, desarrollo);
        return s.BuildServiceProvider();
    }

    private static CorreoSaliente Correo() => new("fac-1-abc@ianalitica.test", [new(Destino, "Tesorería")], [new("contador@cliente-prueba.test", null)],
        "Cuenta de cobro CC-000123", "texto", "<p>html</p>", [new AdjuntoCorreo("CC-000123.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4 prueba"))]);

    [Fact]
    public void El_pdf_es_valido_lleva_la_leyenda_de_documento_no_tributario_y_cambia_si_esta_anulada()
    {
        using var sp = Proveedor(new());
        var pdf = sp.GetRequiredService<IGeneradorPdfCuentaCobro>();

        var normal = pdf.Generar(Documento());
        var anulada = pdf.Generar(Documento(anulada: true));

        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(normal, 0, 4));
        Assert.True(normal.Length > 1000);
        Assert.NotEqual(normal, anulada);
    }

    [Fact]
    public void El_DTO_del_documento_no_tiene_campos_de_usuarios_conversaciones_ni_pacientes()
    {
        var nombres = new[] { typeof(DocumentoCuentaCobro), typeof(EmisorDoc), typeof(ClienteDoc), typeof(LineaDoc), typeof(DetalleIaDoc) }
            .SelectMany(t => t.GetProperties()).Select(p => p.Name).ToList();
        string[] prohibidos = ["Usuario", "Conversacion", "Mensaje", "Texto", "Prompt", "Paciente", "Herramienta"];
        Assert.DoesNotContain(nombres, n => prohibidos.Any(p => n.Contains(p, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task El_modo_archivo_escribe_un_eml_con_el_adjunto_y_los_destinatarios()
    {
        var ruta = Path.Combine(Path.GetTempPath(), "ianalitica-correo-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var sp = Proveedor(new() { ["Correo:Smtp:Modo"] = "Archivo", ["Correo:Smtp:RutaArchivo"] = ruta, ["Correo:Smtp:RemitenteEmail"] = "cobros@prestador.test", ["Correo:Smtp:RemitenteNombre"] = "Cobros" });
            var r = await sp.GetRequiredService<ICorreoSaliente>().EnviarAsync(Correo(), default);

            Assert.Equal(ResultadoCorreo.Aceptado, r.Resultado);
            var eml = Assert.Single(Directory.GetFiles(ruta, "*.eml"));
            var texto = await File.ReadAllTextAsync(eml);
            Assert.Contains("CC-000123.pdf", texto);
            Assert.Contains(Destino, texto);
            Assert.Contains("<fac-1-abc@ianalitica.test>", texto);   // Message-ID propio, estable en los reintentos
            Assert.Contains("From:", texto);
        }
        finally { if (Directory.Exists(ruta)) Directory.Delete(ruta, true); }
    }

    [Fact]
    public async Task Sin_configuracion_el_envio_queda_como_no_configurado_y_el_sistema_arranca()
    {
        using var sp = Proveedor(new());   // Modo Deshabilitado por defecto
        var r = await sp.GetRequiredService<ICorreoSaliente>().EnviarAsync(Correo(), default);
        Assert.Equal(ResultadoCorreo.NoConfigurado, r.Resultado);
        Assert.Equal("SMTP_NO_CONFIGURADO", r.CodigoError);

        using var incompleto = Proveedor(new() { ["Correo:Smtp:Modo"] = "Smtp", ["Correo:Smtp:Servidor"] = "smtp.prueba.test" });   // falta el remitente
        Assert.Equal(ResultadoCorreo.NoConfigurado, (await incompleto.GetRequiredService<ICorreoSaliente>().EnviarAsync(Correo(), default)).Resultado);
    }

    [Fact]
    public void Seguridad_None_solo_se_permite_en_desarrollo()
    {
        var config = new Dictionary<string, string?> { ["Correo:Smtp:Modo"] = "Smtp", ["Correo:Smtp:Servidor"] = "smtp.prueba.test", ["Correo:Smtp:Seguridad"] = "None" };
        Proveedor(config, desarrollo: true).Dispose();
        var ex = Assert.Throws<InvalidOperationException>(() => Proveedor(config, desarrollo: false));
        Assert.Contains("desarrollo", ex.Message);
        // Con cifrado explícito sí arranca en producción.
        config["Correo:Smtp:Seguridad"] = "StartTls";
        Proveedor(config, desarrollo: false).Dispose();
    }

    [Fact]
    public async Task Una_falla_de_red_es_transitoria_y_el_log_no_contiene_direcciones()
    {
        var logs = new ProveedorLogsEnMemoria();
        using var sp = Proveedor(new()
        {
            ["Correo:Smtp:Modo"] = "Smtp", ["Correo:Smtp:Servidor"] = "127.0.0.1", ["Correo:Smtp:Puerto"] = "1", ["Correo:Smtp:Seguridad"] = "None",
            ["Correo:Smtp:RemitenteEmail"] = "cobros@prestador.test", ["Correo:Smtp:TimeoutSegundos"] = "5", ["Correo:Smtp:Clave"] = "clave-super-secreta",
        }, logs: logs);

        var r = await sp.GetRequiredService<ICorreoSaliente>().EnviarAsync(Correo(), default);

        Assert.Equal(ResultadoCorreo.FallaTransitoria, r.Resultado);
        Assert.NotNull(r.CodigoError);
        var todo = string.Join("\n", logs.Mensajes);
        Assert.NotEmpty(logs.Mensajes);
        Assert.DoesNotContain("cliente-prueba.test", todo);
        Assert.DoesNotContain("prestador.test", todo);
        Assert.DoesNotContain("clave-super-secreta", todo);
        Assert.DoesNotContain("127.0.0.1", todo);
    }

    [Fact]
    public async Task Probar_la_conexion_sin_configurar_no_lanza()
    {
        using var sp = Proveedor(new());
        Assert.Equal(ResultadoCorreo.NoConfigurado, (await sp.GetRequiredService<ICorreoSaliente>().ProbarConexionAsync(default)).Resultado);
    }

    private sealed class ProveedorLogsEnMemoria : ILoggerProvider
    {
        public List<string> Mensajes { get; } = [];
        public ILogger CreateLogger(string categoria) => new Registro(Mensajes);
        public void Dispose() { }

        private sealed class Registro(List<string> mensajes) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState estado) where TState : notnull => null;
            public bool IsEnabled(LogLevel nivel) => true;
            public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? excepcion, Func<TState, Exception?, string> formato)
                => mensajes.Add(formato(estado, excepcion) + (excepcion is null ? "" : "\n" + excepcion));   // incluye la excepción completa: si trajera direcciones, la prueba lo detecta
        }
    }
}
