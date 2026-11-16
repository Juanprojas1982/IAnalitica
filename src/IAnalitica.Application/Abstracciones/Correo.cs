namespace IAnalitica.Application.Abstracciones;

// Correo saliente genérico (08 §32.2): lo reutilizan después el resumen del auditor y las alertas (pendiente 6 de 06).

public sealed record DireccionCorreo(string Email, string? Nombre);

public sealed record AdjuntoCorreo(string NombreArchivo, string TipoMime, ReadOnlyMemory<byte> Contenido);

/// <param name="MessageId">Propio y estable: se reutiliza en los reintentos para que el receptor pueda deduplicar.</param>
public sealed record CorreoSaliente(string MessageId, IReadOnlyList<DireccionCorreo> Para, IReadOnlyList<DireccionCorreo> Copia,
    string Asunto, string CuerpoTexto, string? CuerpoHtml, IReadOnlyList<AdjuntoCorreo> Adjuntos);

public enum ResultadoCorreo { Aceptado, FallaTransitoria, FallaPermanente, NoConfigurado }

/// <param name="CodigoSmtp">Código numérico del servidor, si lo hubo.</param>
/// <param name="CodigoError">Código nuestro (AUTENTICACION, RECHAZO_DESTINATARIO, TIMEOUT, SMTP_NO_CONFIGURADO...). Jamás el texto del servidor: trae direcciones.</param>
public sealed record ResultadoEnvioCorreo(ResultadoCorreo Resultado, string? CodigoSmtp, string? CodigoError);

public interface ICorreoSaliente
{
    Task<ResultadoEnvioCorreo> EnviarAsync(CorreoSaliente correo, CancellationToken ct);

    /// <summary>Conecta y autentica sin enviar (botón «probar conexión» del superadmin).</summary>
    Task<ResultadoEnvioCorreo> ProbarConexionAsync(CancellationToken ct);
}
