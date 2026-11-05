using System.Text.RegularExpressions;

namespace IAnalitica.Infrastructure.Observabilidad;

/// <summary>
/// Reglas del procesador de limpieza obligatorio (spec 19 §3): la telemetría nunca lleva cuerpos de cargas, parámetros SQL,
/// preguntas o respuestas de IA, secretos, tokens, cadenas de conexión ni nombres, documentos o correos de personas.
/// Es conservador: ante la duda enmascara. Lo que deba conservarse va a la auditoría SQL (aud.*), no a la telemetría.
/// </summary>
public static partial class Limpiador
{
    public const string Redactado = "[REDACTADO]";
    public const string Documento = "[DOCUMENTO]";
    public const string Correo = "[CORREO]";

    // Una clave que contenga alguno de estos fragmentos se elimina completa, sea cual sea el valor.
    private static readonly string[] FragmentosProhibidos =
    {
        "authorization", "password", "contrasena", "clave", "secret", "secreto", "token", "apikey", "api-key", "api_key", "cookie",
        "connectionstring", "connection_string", "db.statement", "db.query", "db.parameter", "sql.parameter",
        "body", "payload", "cuerpo", "pregunta", "respuesta", "prompt", "completion", "mensaje.texto",
        "user.name", "user.email", "enduser", "nombre.paciente", "documento", "identificacion", "correo", "email",
    };

    // Identificadores técnicos permitidos (spec 19 §2): no se les aplica el enmascarado por patrón.
    private static readonly HashSet<string> ClavesSeguras = new(StringComparer.OrdinalIgnoreCase)
    {
        "empresa.id", "carga.id", "entidad", "usuario.id", "version.conector", "version.saludips", "resultado", "error.tipo",
        "trabajo.id", "trabajo.tipo", "reporte.id", "filas", "duracion.ms", "http.response.status_code", "http.request.method",
        "http.route", "url.path", "server.port", "db.system", "db.name", "db.namespace", "service.name", "service.version",
    };

    public static bool ClaveProhibida(string clave)
        => !ClavesSeguras.Contains(clave) && FragmentosProhibidos.Any(f => clave.Contains(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>Limpia un valor de texto: tokens, cadenas de conexión, JSON con secretos, correos y números largos (documentos).</summary>
    public static string LimpiarTexto(string texto)
    {
        if (texto.Length == 0) return texto;
        var r = Jwt().Replace(texto, Redactado);
        r = Portador().Replace(r, "$1 " + Redactado);
        r = ParametroUrl().Replace(r, "$1" + Redactado);
        r = ParteConexion().Replace(r, "$1=" + Redactado);
        r = JsonSecreto().Replace(r, "\"$1\":\"" + Redactado + "\"");
        r = CorreoElectronico().Replace(r, Correo);
        r = NumeroLargo().Replace(r, Documento);
        return r;
    }

    /// <summary>Devuelve el valor a exportar: el mismo si es seguro, uno enmascarado o <see cref="Redactado"/> si la clave está prohibida.</summary>
    public static object? Limpiar(string clave, object? valor)
    {
        if (valor is null) return null;
        if (ClaveProhibida(clave)) return Redactado;
        if (ClavesSeguras.Contains(clave)) return valor;
        return valor is string s ? LimpiarTexto(s) : valor;
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex Portador();

    [GeneratedRegex(@"([?&](?:token|access_token|code|password|pwd|key|api_key|sig|signature|secret)=)[^&\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex ParametroUrl();

    [GeneratedRegex(@"\b(password|pwd|user id|uid|secret|accountkey|sharedaccesskey|client_secret)\s*=\s*[^;\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex ParteConexion();

    [GeneratedRegex("\"(password|clave|contrasena|secret|secreto|token|clientsecret)\"\\s*:\\s*\"[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecreto();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex CorreoElectronico();

    // Cédulas, NIT, teléfonos: 6 o más dígitos seguidos. Los identificadores técnicos pasan por ClavesSeguras.
    [GeneratedRegex(@"(?<![\w.])\d{6,}(?![\w])")]
    private static partial Regex NumeroLargo();
}
