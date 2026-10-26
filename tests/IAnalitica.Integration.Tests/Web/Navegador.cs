using System.Net;
using System.Text.RegularExpressions;

namespace IAnalitica.Integration.Tests.Web;

/// <summary>Un navegador mínimo: conserva cookies y envía los formularios de Blazor SSR tal como los armaría un navegador (token antiforgery incluido).</summary>
public sealed partial class Navegador(HttpClient cliente)
{
    public HttpClient Cliente { get; } = cliente;
    public string UltimaRuta { get; private set; } = "/";

    /// <summary>HTML ya decodificado (el servidor escribe «ñ» como &amp;#xF1;): para comparar textos visibles.</summary>
    public async Task<(HttpStatusCode Estado, string Html, string? Redireccion)> IrAsync(string ruta)
    {
        var (estado, crudo, redir) = await IrCrudoAsync(ruta);
        return (estado, WebUtility.HtmlDecode(crudo), redir);
    }

    private async Task<(HttpStatusCode Estado, string Crudo, string? Redireccion)> IrCrudoAsync(string ruta)
    {
        var respuesta = await Cliente.GetAsync(ruta);
        UltimaRuta = ruta;
        return (respuesta.StatusCode, await respuesta.Content.ReadAsStringAsync(), respuesta.Headers.Location?.OriginalString);
    }

    /// <summary>Sigue redirecciones (máx. 6) como lo haría el navegador.</summary>
    public async Task<(HttpStatusCode Estado, string Html, string Ruta)> SeguirAsync(string ruta)
    {
        for (var i = 0; i < 6; i++)
        {
            var (estado, html, redir) = await IrAsync(ruta);
            if (redir is null) return (estado, html, ruta);
            ruta = redir.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(redir).PathAndQuery : redir;
        }
        throw new InvalidOperationException("Demasiadas redirecciones.");
    }

    /// <summary>
    /// Pide la página, toma el formulario con ese nombre (campo oculto _handler), completa los campos y lo envía.
    /// Devuelve estado, HTML y la redirección (si hubo).
    /// </summary>
    public async Task<(HttpStatusCode Estado, string Html, string? Redireccion)> EnviarFormularioAsync(
        string ruta, string nombreFormulario, IDictionary<string, string> campos, string? accionPost = null)
    {
        var (estado, html, redirInicial) = await IrCrudoAsync(ruta);
        Assert.True(estado == HttpStatusCode.OK, $"GET {ruta} devolvió {estado} (redirección: {redirInicial}).");

        var formulario = Formularios().Matches(html).Select(m => m.Value)
            .FirstOrDefault(f => f.Contains($"name=\"_handler\"", StringComparison.Ordinal) && f.Contains($"value=\"{nombreFormulario}\"", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No se encontró el formulario '{nombreFormulario}' en {ruta}:\n{html}");

        var datos = new Dictionary<string, string>();
        foreach (Match input in Entradas().Matches(formulario))
        {
            var etiqueta = input.Value;
            var nombre = Atributo(etiqueta, "name");
            if (nombre is null) continue;
            var tipo = Atributo(etiqueta, "type") ?? "text";
            if (tipo is "checkbox" or "radio" && !etiqueta.Contains("checked", StringComparison.OrdinalIgnoreCase)) continue;
            datos[nombre] = WebUtility.HtmlDecode(Atributo(etiqueta, "value") ?? "");
        }
        foreach (var (clave, valor) in campos) datos[clave] = valor;

        var destino = accionPost ?? ruta;
        var respuesta = await Cliente.PostAsync(destino, new FormUrlEncodedContent(datos));
        UltimaRuta = destino;
        return (respuesta.StatusCode, WebUtility.HtmlDecode(await respuesta.Content.ReadAsStringAsync()), respuesta.Headers.Location?.OriginalString);
    }

    /// <summary>Envía un POST usando un token antiforgery tomado de cualquier formulario de una página ya cargada (acciones de tabla).</summary>
    public async Task<(HttpStatusCode Estado, string Html, string? Redireccion)> PostConTokenAsync(string rutaConToken, string destino, IDictionary<string, string>? campos = null)
    {
        var (_, html, _) = await IrCrudoAsync(rutaConToken);
        var token = Entradas().Matches(html).Select(m => m.Value)
            .FirstOrDefault(e => e.Contains("__RequestVerificationToken", StringComparison.Ordinal));
        Assert.NotNull(token);
        var datos = new Dictionary<string, string> { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(Atributo(token!, "value") ?? "") };
        foreach (var (clave, valor) in campos ?? new Dictionary<string, string>()) datos[clave] = valor;
        var respuesta = await Cliente.PostAsync(destino, new FormUrlEncodedContent(datos));
        return (respuesta.StatusCode, WebUtility.HtmlDecode(await respuesta.Content.ReadAsStringAsync()), respuesta.Headers.Location?.OriginalString);
    }

    private static string? Atributo(string etiqueta, string nombre)
    {
        var m = Regex.Match(etiqueta, $"""\b{nombre}="([^"]*)"|\b{nombre}='([^']*)'""", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
    }

    [GeneratedRegex(@"<form\b.*?</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex Formularios();
    [GeneratedRegex(@"<input\b[^>]*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex Entradas();
}
