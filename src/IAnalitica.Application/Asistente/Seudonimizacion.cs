using System.Text.RegularExpressions;

namespace IAnalitica.Application.Asistente;

/// <summary>
/// Seudonimización de personas (spec 07 §8.2). Los nombres de las dimensiones que pueden ser personas naturales se envían al proveedor como
/// «Usuario #12» / «Tercero #34» (la llave interna, que no identifica por sí sola) y se re-hidratan en la respuesta que ve la empresa.
/// </summary>
public sealed class Seudonimizador
{
    private static readonly IReadOnlyDictionary<string, string> Prefijos = new Dictionary<string, string> { ["usuario"] = "Usuario", ["tercero"] = "Tercero" };

    private readonly Dictionary<string, string> _alias = new();   // alias → nombre real

    public static bool EsPersona(string dimension) => Prefijos.ContainsKey(dimension);

    /// <summary>Alias estable para (dimensión, llave): el mismo en todos los turnos de la conversación.</summary>
    public string Alias(string dimension, string clave, string etiqueta)
    {
        var alias = $"{Prefijos[dimension]} #{clave}";
        _alias[alias] = etiqueta;
        return alias;
    }

    /// <summary>Devuelve los nombres reales en el texto. Solo reemplaza alias conocidos de este turno.</summary>
    public string Rehidratar(string texto)
    {
        foreach (var (alias, real) in _alias.OrderByDescending(kv => kv.Key.Length))   // «Tercero #12» antes que «Tercero #1»
            texto = Regex.Replace(texto, Regex.Escape(alias) + @"(?!\d)", _ => real);
        return texto;
    }
}

/// <summary>
/// La salida del modelo se muestra como texto (Blazor la escapa). Aun así se quitan las etiquetas HTML y los enlaces o imágenes externos,
/// porque el contenido puede copiarse a otros sitios (spec 07 §8.5).
/// </summary>
public static partial class SanitizadorSalida
{
    [GeneratedRegex(@"<[^>]*>")] private static partial Regex Etiquetas();
    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")] private static partial Regex Imagenes();
    [GeneratedRegex(@"\[([^\]]*)\]\((?:https?:)?//[^)]*\)", RegexOptions.IgnoreCase)] private static partial Regex Enlaces();
    [GeneratedRegex(@"\b(?:https?://|www\.)\S+", RegexOptions.IgnoreCase)] private static partial Regex Urls();

    public static string Limpiar(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return "";
        texto = Imagenes().Replace(texto, "");
        texto = Enlaces().Replace(texto, "$1");
        texto = Etiquetas().Replace(texto, "");
        texto = Urls().Replace(texto, "[enlace omitido]");
        return texto.Trim();
    }
}
