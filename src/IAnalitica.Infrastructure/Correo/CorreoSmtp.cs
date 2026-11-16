using System.Net.Sockets;
using IAnalitica.Application.Abstracciones;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace IAnalitica.Infrastructure.Correo;

public enum ModoCorreo { Deshabilitado, Archivo, Smtp }

/// <summary>El modo de seguridad es siempre explícito: no se expone Auto ni StartTlsWhenAvailable (08 §32.3).</summary>
public enum SeguridadSmtp { None, StartTls, SslOnConnect }

/// <summary>
/// Sección <c>Correo:Smtp</c>. Todo es configuración de despliegue; <see cref="Clave"/> llega por Key Vault / User Secrets / variable de entorno
/// (<c>Correo__Smtp__Clave</c>) y NUNCA por <c>appsettings</c>, repo, BD ni logs (D53).
/// </summary>
public sealed class OpcionesCorreoSmtp
{
    public ModoCorreo Modo { get; set; } = ModoCorreo.Deshabilitado;
    public string? Servidor { get; set; }
    public int Puerto { get; set; } = 465;
    public SeguridadSmtp Seguridad { get; set; } = SeguridadSmtp.SslOnConnect;
    public string? Usuario { get; set; }
    public string? Clave { get; set; }
    public string? RemitenteEmail { get; set; }
    public string? RemitenteNombre { get; set; }
    public string? ResponderA { get; set; }
    public int TimeoutSegundos { get; set; } = 30;
    /// <summary>Carpeta de los .eml en modo <see cref="ModoCorreo.Archivo"/> (desarrollo y pruebas).</summary>
    public string RutaArchivo { get; set; } = Path.Combine(AppContext.BaseDirectory, "correo-salida");

    public bool SmtpCompleto => !string.IsNullOrWhiteSpace(Servidor) && Puerto is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(RemitenteEmail);

    /// <summary>Errores de configuración que impiden arrancar. <c>None</c> (sin cifrado) solo se admite en desarrollo.</summary>
    public IReadOnlyList<string> Validar(bool esDesarrollo)
    {
        var errores = new List<string>();
        if (Modo != ModoCorreo.Smtp) return errores;
        if (Seguridad == SeguridadSmtp.None && !esDesarrollo) errores.Add("Correo:Smtp:Seguridad = None solo se permite en desarrollo.");
        if (TimeoutSegundos is < 5 or > 300) errores.Add("Correo:Smtp:TimeoutSegundos debe estar entre 5 y 300.");
        return errores;
    }
}

internal static class ConstructorMime
{
    public static MimeMessage Crear(OpcionesCorreoSmtp o, CorreoSaliente c)
    {
        var m = new MimeMessage { MessageId = c.MessageId.Trim('<', '>'), Subject = c.Asunto };
        m.From.Add(new MailboxAddress(o.RemitenteNombre ?? "", o.RemitenteEmail ?? "noreply@localhost"));
        if (!string.IsNullOrWhiteSpace(o.ResponderA)) m.ReplyTo.Add(MailboxAddress.Parse(o.ResponderA));
        foreach (var d in c.Para) m.To.Add(new MailboxAddress(d.Nombre ?? "", d.Email));
        foreach (var d in c.Copia) m.Cc.Add(new MailboxAddress(d.Nombre ?? "", d.Email));
        var cuerpo = new BodyBuilder { TextBody = c.CuerpoTexto, HtmlBody = c.CuerpoHtml };
        foreach (var a in c.Adjuntos) cuerpo.Attachments.Add(a.NombreArchivo, a.Contenido.ToArray(), ContentType.Parse(a.TipoMime));
        m.Body = cuerpo.ToMessageBody();
        return m;
    }
}

/// <summary>
/// SMTP genérico con MailKit. Clasificación de fallas (08 §32.3): 4xx y red/timeout = transitoria (la cola reintenta); 5xx y autenticación = permanente.
/// <b>Prohibido</b> usar <c>ProtocolLogger</c> o registrar <c>ex.Message</c>: traen direcciones de correo. Solo se registran el tipo de excepción y el código.
/// </summary>
internal sealed class CorreoSmtp(IOptions<OpcionesCorreoSmtp> opciones, ILogger<CorreoSmtp> log) : ICorreoSaliente
{
    public async Task<ResultadoEnvioCorreo> EnviarAsync(CorreoSaliente correo, CancellationToken ct)
    {
        var o = opciones.Value;
        if (!o.SmtpCompleto) return new(ResultadoCorreo.NoConfigurado, null, "SMTP_NO_CONFIGURADO");
        try
        {
            using var mensaje = ConstructorMime.Crear(o, correo);
            using var cliente = new SmtpClient { Timeout = o.TimeoutSegundos * 1000 };
            await ConectarAsync(cliente, o, ct).ConfigureAwait(false);
            await cliente.SendAsync(mensaje, ct).ConfigureAwait(false);
            await cliente.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
            return new(ResultadoCorreo.Aceptado, null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // apagado ordenado: el lease devuelve el trabajo
        catch (Exception e) { return Clasificar(e, log); }
    }

    public async Task<ResultadoEnvioCorreo> ProbarConexionAsync(CancellationToken ct)
    {
        var o = opciones.Value;
        if (!o.SmtpCompleto) return new(ResultadoCorreo.NoConfigurado, null, "SMTP_NO_CONFIGURADO");
        try
        {
            using var cliente = new SmtpClient { Timeout = o.TimeoutSegundos * 1000 };
            await ConectarAsync(cliente, o, ct).ConfigureAwait(false);
            await cliente.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
            return new(ResultadoCorreo.Aceptado, null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return Clasificar(e, log); }
    }

    private static async Task ConectarAsync(SmtpClient cliente, OpcionesCorreoSmtp o, CancellationToken ct)
    {
        var seguridad = o.Seguridad switch
        {
            SeguridadSmtp.None => SecureSocketOptions.None,
            SeguridadSmtp.StartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.SslOnConnect,
        };
        await cliente.ConnectAsync(o.Servidor!, o.Puerto, seguridad, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(o.Usuario)) await cliente.AuthenticateAsync(o.Usuario, o.Clave ?? "", ct).ConfigureAwait(false);
    }

    internal static ResultadoEnvioCorreo Clasificar(Exception e, ILogger log)
    {
        var resultado = e switch
        {
            AuthenticationException => new ResultadoEnvioCorreo(ResultadoCorreo.FallaPermanente, null, "AUTENTICACION"),
            SmtpCommandException c when (int)c.StatusCode is >= 400 and < 500 => new(ResultadoCorreo.FallaTransitoria, ((int)c.StatusCode).ToString(), "SERVIDOR_OCUPADO"),
            SmtpCommandException c => new(ResultadoCorreo.FallaPermanente, ((int)c.StatusCode).ToString(),
                c.ErrorCode is SmtpErrorCode.RecipientNotAccepted ? "RECHAZO_DESTINATARIO" : c.ErrorCode is SmtpErrorCode.SenderNotAccepted ? "RECHAZO_REMITENTE" : "RECHAZO_SERVIDOR"),
            TimeoutException or OperationCanceledException => new(ResultadoCorreo.FallaTransitoria, null, "TIMEOUT"),
            SocketException or IOException or SmtpProtocolException or ServiceNotConnectedException or ServiceNotAuthenticatedException => new(ResultadoCorreo.FallaTransitoria, null, "RED"),
            _ => new(ResultadoCorreo.FallaTransitoria, null, "DESCONOCIDO"),
        };
        log.LogWarning("Falla de correo ({Tipo}, {Codigo}, smtp {Smtp})", e.GetType().Name, resultado.CodigoError, resultado.CodigoSmtp);   // jamás e.Message
        return resultado;
    }
}

/// <summary>Modo <c>Archivo</c>: escribe cada correo como .eml (con adjuntos) en <see cref="OpcionesCorreoSmtp.RutaArchivo"/>. Para desarrollo y pruebas.</summary>
internal sealed class CorreoArchivo(IOptions<OpcionesCorreoSmtp> opciones) : ICorreoSaliente
{
    public async Task<ResultadoEnvioCorreo> EnviarAsync(CorreoSaliente correo, CancellationToken ct)
    {
        var o = opciones.Value;
        using var mensaje = ConstructorMime.Crear(o, correo);
        Directory.CreateDirectory(o.RutaArchivo);
        var nombre = new string(correo.MessageId.Where(char.IsLetterOrDigit).ToArray());
        await using var fs = File.Create(Path.Combine(o.RutaArchivo, $"{(nombre.Length == 0 ? Guid.NewGuid().ToString("N") : nombre)}.eml"));
        await mensaje.WriteToAsync(fs, ct).ConfigureAwait(false);
        return new(ResultadoCorreo.Aceptado, null, null);
    }

    public Task<ResultadoEnvioCorreo> ProbarConexionAsync(CancellationToken ct) => Task.FromResult(new ResultadoEnvioCorreo(ResultadoCorreo.Aceptado, null, null));
}

/// <summary>Sin SMTP configurado: el worker arranca igual y los envíos quedan FALLIDO / SMTP_NO_CONFIGURADO con alerta global.</summary>
internal sealed class CorreoNoConfigurado : ICorreoSaliente
{
    private static readonly ResultadoEnvioCorreo R = new(ResultadoCorreo.NoConfigurado, null, "SMTP_NO_CONFIGURADO");
    public Task<ResultadoEnvioCorreo> EnviarAsync(CorreoSaliente correo, CancellationToken ct) => Task.FromResult(R);
    public Task<ResultadoEnvioCorreo> ProbarConexionAsync(CancellationToken ct) => Task.FromResult(R);
}
