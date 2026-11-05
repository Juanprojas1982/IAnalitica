using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Facturacion;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>Facturación, pasos 6-7: PDF almacenado, outbox de envíos y reintentos, contra SQL Server real. Cada prueba usa su propia empresa.</summary>
[Collection(ColeccionBd.Nombre)]
public class FacturacionEnviosTests(BaseDeDatosFixture bd)
{
    private static int _secuencia = 920000000;

    private sealed record Contexto(ServiceProvider Sp, string RutaPdf, string RutaCorreo) : IDisposable
    {
        public EnviosFacturacion Envios => Sp.GetRequiredService<EnviosFacturacion>();
        public GeneradorCuentas Generador => Sp.GetRequiredService<GeneradorCuentas>();
        public void Dispose()
        {
            Sp.Dispose();
            foreach (var r in new[] { RutaPdf, RutaCorreo }) if (Directory.Exists(r)) Directory.Delete(r, true);
        }
    }

    private Contexto Nuevo(ICorreoSaliente? correo = null)
    {
        var pdf = Path.Combine(Path.GetTempPath(), "ianalitica-fac-" + Guid.NewGuid().ToString("N"));
        var mail = Path.Combine(Path.GetTempPath(), "ianalitica-eml-" + Guid.NewGuid().ToString("N"));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Correo:Smtp:Modo"] = "Archivo", ["Correo:Smtp:RutaArchivo"] = mail, ["Correo:Smtp:RemitenteEmail"] = "cobros@prestador.test",
            ["Facturacion:UrlPortal"] = "https://app.prueba.test", ["Facturacion:Almacen:Ruta"] = pdf,
        }).Build();
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddProcesamiento(bd.CadenaEtl);
        s.AddProcesamientoFacturacion();
        s.AddCorreoSaliente(cfg, esDesarrollo: true);
        if (correo is not null) s.Replace(ServiceDescriptor.Singleton(correo));
        return new Contexto(s.BuildServiceProvider(), pdf, mail);
    }

    /// <summary>Empresa con canon, contacto de facturación (y contador) y una cuenta ya emitida del periodo 202610.</summary>
    private async Task<(int EmpresaId, int CuentaId)> CuentaEmitidaAsync(Contexto x, bool contador = true)
    {
        var nit = Interlocked.Increment(ref _secuencia).ToString();
        var id = (await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido(nit, "Cliente de envíos " + nit))).EmpresaId;
        await using (var c = await Escenarios.AbrirAsync(bd.CadenaApp, IAnalitica.Infrastructure.Persistencia.ContextoBd.DeSuperadmin(bd.SuperadminId)))
        {
            await Escenarios.Ejecutar(c, $"INSERT seg.EmpresaContacto (EmpresaId, Tipo, Nombre, Email) VALUES ({id}, 'FACTURACION', N'Tesorería', N'tesoreria{id}@cliente.test')");
            if (contador) await Escenarios.Ejecutar(c, $"INSERT seg.EmpresaContacto (EmpresaId, Tipo, Nombre, Email) VALUES ({id}, 'CONTADOR', N'Contador', N'contador{id}@cliente.test')");
        }
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"""
            INSERT fac.CondicionComercial (EmpresaId, VigenteDesde, PlanId, CanonPactadoCop, CreadoPor)
            VALUES ({id}, '2026-01-01', (SELECT TOP (1) PlanId FROM ia.PlanComercial ORDER BY PlanId), 1500000, {bd.SuperadminId})
            """);
        Assert.Equal(ResultadoGeneracion.Emitida, await x.Generador.GenerarAsync(id, 202610, default));
        return (id, await Etl<int>($"SELECT CuentaCobroId FROM fac.CuentaCobro WHERE EmpresaId = {id}"));
    }

    private async Task<T> Etl<T>(string sql) => await BaseDeDatosFixture.EscalarAsync<T>(bd.CadenaEtl, sql, superadmin: false);

    private async Task<Guid> EnvioAsync(int cuentaId) => await Etl<Guid>($"SELECT EnvioId FROM fac.Envio WHERE CuentaCobroId = {cuentaId} AND Tipo = 'CUENTA_COBRO'");

    [Fact]
    public async Task El_pdf_se_guarda_una_vez_con_su_hash_y_crea_un_unico_envio_con_destinatarios()
    {
        using var x = Nuevo();
        var (emp, cuenta) = await CuentaEmitidaAsync(x);

        await x.Envios.GenerarPdfAsync(cuenta, default);
        await x.Envios.GenerarPdfAsync(cuenta, default);   // idempotente

        var ruta = await Etl<string>($"SELECT PdfRuta FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}");
        Assert.StartsWith($"{emp}/CC-", ruta);
        Assert.True(File.Exists(Path.Combine(x.RutaPdf, ruta)));
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(x.RutaPdf, ruta)), 0, 4));
        Assert.NotNull(await Etl<byte[]>($"SELECT PdfSha256 FROM fac.CuentaCobro WHERE CuentaCobroId = {cuenta}"));

        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.Envio WHERE CuentaCobroId = {cuenta} AND Tipo = 'CUENTA_COBRO'"));
        var envio = await EnvioAsync(cuenta);
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.EnvioDestinatario WHERE EnvioId = '{envio}' AND Clase = 'TO'"));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.EnvioDestinatario WHERE EnvioId = '{envio}' AND Clase = 'CC'"));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM etl.Trabajo WHERE Tipo = 'ENVIAR_CORREO' AND ClaveExclusion = 'CORREO:{envio}'"));
        Assert.Equal(1, await Etl<int>($"SELECT CAST(ConAdjunto AS int) FROM fac.Envio WHERE EnvioId = '{envio}'"));
    }

    [Fact]
    public async Task El_envio_escribe_el_correo_con_el_pdf_adjunto_y_no_se_repite()
    {
        using var x = Nuevo();
        var (_, cuenta) = await CuentaEmitidaAsync(x);
        await x.Envios.GenerarPdfAsync(cuenta, default);
        var envio = await EnvioAsync(cuenta);

        await x.Envios.EnviarAsync(envio, esUltimoIntento: false, default);

        Assert.Equal("ENVIADO", await Etl<string>($"SELECT Estado FROM fac.Envio WHERE EnvioId = '{envio}'"));
        var eml = Assert.Single(Directory.GetFiles(x.RutaCorreo, "*.eml"));
        var mensaje = await MimeKit.MimeMessage.LoadAsync(eml);
        Assert.StartsWith("Cuenta de cobro CC-", mensaje.Subject);
        Assert.Contains("documento no tributario", mensaje.TextBody);
        Assert.Contains("https://app.prueba.test/facturacion/", mensaje.TextBody);
        var adjunto = Assert.Single(mensaje.Attachments.OfType<MimeKit.MimePart>());
        Assert.EndsWith(".pdf", adjunto.FileName);
        Assert.Single(mensaje.To);
        Assert.Single(mensaje.Cc);

        await x.Envios.EnviarAsync(envio, esUltimoIntento: false, default);   // ya enviado: no se reenvía
        Assert.Single(Directory.GetFiles(x.RutaCorreo, "*.eml"));
    }

    [Fact]
    public async Task Una_falla_transitoria_reintenta_y_en_el_ultimo_intento_queda_fallida_con_alerta()
    {
        var correo = Substitute.For<ICorreoSaliente>();
        correo.EnviarAsync(Arg.Any<CorreoSaliente>(), Arg.Any<CancellationToken>()).Returns(new ResultadoEnvioCorreo(ResultadoCorreo.FallaTransitoria, "421", "SERVIDOR_OCUPADO"));
        using var x = Nuevo(correo);
        var (emp, cuenta) = await CuentaEmitidaAsync(x);
        await x.Envios.GenerarPdfAsync(cuenta, default);
        var envio = await EnvioAsync(cuenta);

        await Assert.ThrowsAsync<CorreoTransitorioException>(() => x.Envios.EnviarAsync(envio, esUltimoIntento: false, default));
        Assert.Equal("PENDIENTE", await Etl<string>($"SELECT Estado FROM fac.Envio WHERE EnvioId = '{envio}'"));   // la cola lo reintenta

        await x.Envios.EnviarAsync(envio, esUltimoIntento: true, default);
        Assert.Equal("FALLIDO", await Etl<string>($"SELECT Estado FROM fac.Envio WHERE EnvioId = '{envio}'"));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.Alerta WHERE EmpresaId = {emp} AND Codigo = 'ENVIO_FALLIDO' AND AtendidaEn IS NULL"));
        Assert.Equal(2, await Etl<int>($"SELECT CAST(Intentos AS int) FROM fac.Envio WHERE EnvioId = '{envio}'"));
    }

    [Fact]
    public async Task Una_falla_permanente_no_reintenta_y_sin_smtp_la_alerta_es_global()
    {
        var correo = Substitute.For<ICorreoSaliente>();
        correo.EnviarAsync(Arg.Any<CorreoSaliente>(), Arg.Any<CancellationToken>()).Returns(
            new ResultadoEnvioCorreo(ResultadoCorreo.FallaPermanente, "550", "RECHAZO_DESTINATARIO"),
            new ResultadoEnvioCorreo(ResultadoCorreo.NoConfigurado, null, "SMTP_NO_CONFIGURADO"));
        using var x = Nuevo(correo);
        var (emp, cuenta) = await CuentaEmitidaAsync(x);
        await x.Envios.GenerarPdfAsync(cuenta, default);
        var envio = await EnvioAsync(cuenta);

        await x.Envios.EnviarAsync(envio, esUltimoIntento: false, default);   // permanente: no lanza, queda FALLIDO
        Assert.Equal(("FALLIDO", "RECHAZO_DESTINATARIO", "550"), await FilaAsync(envio));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.Alerta WHERE EmpresaId = {emp} AND Codigo = 'ENVIO_FALLIDO' AND AtendidaEn IS NULL"));

        // El superadmin reintenta (vuelve a PENDIENTE) y ahora el SMTP no está configurado.
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"UPDATE fac.Envio SET Estado = 'PENDIENTE' WHERE EnvioId = '{envio}'");
        await x.Envios.EnviarAsync(envio, esUltimoIntento: false, default);
        Assert.Equal("SMTP_NO_CONFIGURADO", (await FilaAsync(envio)).Item2);
        Assert.True(await Etl<int>("SELECT COUNT(*) FROM fac.Alerta WHERE EmpresaId IS NULL AND Codigo = 'SMTP_NO_CONFIGURADO' AND AtendidaEn IS NULL") >= 1);
    }

    private async Task<(string, string?, string?)> FilaAsync(Guid envio)
    {
        await using var c = new SqlConnection(bd.CadenaEtl);
        await c.OpenAsync();
        return await c.QuerySingleAsync<(string, string?, string?)>($"SELECT Estado, CodigoError, CodigoSmtp FROM fac.Envio WHERE EnvioId = '{envio}'");
    }

    [Fact]
    public async Task Sin_contacto_de_facturacion_el_envio_nace_fallido_con_alerta_y_sin_trabajo()
    {
        using var x = Nuevo();
        var (emp, cuenta) = await CuentaEmitidaAsync(x);
        await using (var c = await Escenarios.AbrirAsync(bd.CadenaApp, IAnalitica.Infrastructure.Persistencia.ContextoBd.DeSuperadmin(bd.SuperadminId)))
            await Escenarios.Ejecutar(c, $"DELETE seg.EmpresaContacto WHERE EmpresaId = {emp}");   // el cliente borró sus contactos tras la emisión

        await x.Envios.GenerarPdfAsync(cuenta, default);

        var envio = await EnvioAsync(cuenta);
        Assert.Equal(("FALLIDO", "SIN_DESTINATARIO", null), await FilaAsync(envio));
        Assert.Equal(0, await Etl<int>($"SELECT COUNT(*) FROM etl.Trabajo WHERE ClaveExclusion = 'CORREO:{envio}'"));
        Assert.Equal(1, await Etl<int>($"SELECT COUNT(*) FROM fac.Alerta WHERE EmpresaId = {emp} AND Codigo = 'SIN_DESTINATARIO' AND AtendidaEn IS NULL"));
    }

    [Fact]
    public async Task Los_recordatorios_van_sin_adjunto_y_una_cuenta_anulada_no_se_regenera()
    {
        using var x = Nuevo();
        var (_, cuenta) = await CuentaEmitidaAsync(x);
        await x.Envios.GenerarPdfAsync(cuenta, default);

        var recordatorio = await x.Envios.EncolarEnvioAsync(cuenta, "RECORDATORIO", "PREVIO_3", esReenvio: false, usuarioId: null, default);
        Assert.Equal(0, await Etl<int>($"SELECT CAST(ConAdjunto AS int) FROM fac.Envio WHERE EnvioId = '{recordatorio}'"));
        await x.Envios.EnviarAsync(recordatorio!.Value, esUltimoIntento: false, default);
        var mensajes = Directory.GetFiles(x.RutaCorreo, "*.eml").Select(f => MimeKit.MimeMessage.Load(f)).ToList();
        var recordado = Assert.Single(mensajes, m => m.Subject.StartsWith("Recordatorio"));
        Assert.Empty(recordado.Attachments);   // D42: solo la cuenta de cobro lleva adjunto

        // El mismo hito no se duplica (índice único por cuenta, tipo y hito).
        await Assert.ThrowsAsync<SqlException>(() => x.Envios.EncolarEnvioAsync(cuenta, "RECORDATORIO", "PREVIO_3", esReenvio: false, usuarioId: null, default));
    }

    [Fact]
    public async Task El_resumen_de_alertas_no_lleva_datos_de_las_empresas()
    {
        using var x = Nuevo();
        var (emp, cuenta) = await CuentaEmitidaAsync(x);
        await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"""
            UPDATE fac.Parametro SET Valor = N'operacion@prestador.test' WHERE Clave = 'CORREO_OPERACION';
            IF NOT EXISTS (SELECT 1 FROM fac.Alerta WHERE EmpresaId = {emp} AND Codigo = 'VARIACION_ANOMALA' AND AtendidaEn IS NULL)
                INSERT fac.Alerta (EmpresaId, PeriodoKey, Codigo, Detalle) VALUES ({emp}, 202610, 'VARIACION_ANOMALA', N'detalle interno')
            """);
        try
        {
            Assert.True(await x.Envios.EnviarResumenAlertasAsync(default));
            var mensaje = await MimeKit.MimeMessage.LoadAsync(Assert.Single(Directory.GetFiles(x.RutaCorreo, "*.eml")));
            var texto = mensaje.Subject + "\n" + mensaje.TextBody;
            Assert.Contains("VARIACION_ANOMALA", texto);
            Assert.DoesNotContain("cliente.test", texto);
            Assert.DoesNotContain("Cliente de envíos", texto);
            Assert.DoesNotContain("detalle interno", texto);
        }
        finally { await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, "UPDATE fac.Parametro SET Valor = N'[BUZÓN DEL PRESTADOR]' WHERE Clave = 'CORREO_OPERACION'"); }
        _ = cuenta;
    }
}
