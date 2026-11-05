using System.Net;
using System.Text.RegularExpressions;
using IAnalitica.Integration.Tests.Infra;

namespace IAnalitica.Integration.Tests.Web;

public sealed record Credenciales(string Usuario, string Clave, string? ClaveTotp);

/// <summary>Recorridos de usuario completos contra el portal real (HTTP + SQL Server).</summary>
public static partial class Flujos
{
    private static FabricaWeb? _fabrica;
    private static Task<Credenciales>? _superadmin;
    private static readonly object Candado = new();

    public static FabricaWeb Fabrica(BaseDeDatosFixture bd)
    {
        lock (Candado) return _fabrica ??= new FabricaWeb(bd);
    }

    /// <summary>El superadmin hace su primer ingreso UNA vez (cambio de clave + doble factor) y queda listo para el resto de pruebas.</summary>
    public static Task<Credenciales> SuperadminListoAsync(BaseDeDatosFixture bd)
    {
        lock (Candado)
            return _superadmin ??= PrepararSuperadminAsync(bd);
    }

    private static async Task<Credenciales> PrepararSuperadminAsync(BaseDeDatosFixture bd)
    {
        var n = Fabrica(bd).NuevoNavegador();
        var (clave, totp) = await PrimerIngresoAsync(n, "superadmin", bd.ClaveInicialSuperadmin, "Super-Clave-Segura-1!", requiereDobleFactor: true);
        return new Credenciales("superadmin", clave, totp);
    }

    public static string NuevoUsuarioNavegadorClave => "Clave-Nueva-Segura-9!";

    /// <summary>Ingreso completo: usuario y clave y, si tiene doble factor, el código. Devuelve true si quedó con sesión.</summary>
    public static async Task<bool> IngresarAsync(Navegador n, Credenciales c, string? returnUrl = null)
    {
        var ruta = "/ingresar" + (returnUrl is null ? "" : "?returnUrl=" + Uri.EscapeDataString(returnUrl));
        var (_, _, redir) = await n.EnviarFormularioAsync(ruta, "ingresar",
            new Dictionary<string, string> { ["Entrada.Usuario"] = c.Usuario, ["Entrada.Clave"] = c.Clave });
        if (redir is null) return false;
        if (redir.Contains("/ingresar/2fa", StringComparison.Ordinal))
        {
            if (c.ClaveTotp is null) return false;
            var (_, _, final) = await n.EnviarFormularioAsync(redir.StartsWith("http") ? new Uri(redir).PathAndQuery : redir, "codigo",
                new Dictionary<string, string> { ["Codigo.Valor"] = Totp.Codigo(c.ClaveTotp, DateTimeOffset.UtcNow) });
            return final is not null && !final.Contains("/ingresar", StringComparison.Ordinal);
        }
        return !redir.Contains("/ingresar", StringComparison.Ordinal);
    }

    /// <summary>Primer ingreso con contraseña inicial: cambio obligatorio y (si aplica) doble factor obligatorio.</summary>
    public static async Task<(string Clave, string? ClaveTotp)> PrimerIngresoAsync(Navegador n, string usuario, string claveInicial, string claveNueva, bool requiereDobleFactor)
    {
        var (estado0, _, redirLogin) = await n.EnviarFormularioAsync("/ingresar", "ingresar",
            new Dictionary<string, string> { ["Entrada.Usuario"] = usuario, ["Entrada.Clave"] = claveInicial });
        Assert.True(redirLogin is not null, $"El ingreso inicial de {usuario} no redirigió (estado {estado0}).");

        // Antes de ver cualquier dato lo mandan a cambiar la clave.
        var (_, _, redirHome) = await n.IrAsync("/");
        Assert.Contains("/cuenta/cambiar-clave", redirHome);

        var (_, _, tras) = await n.EnviarFormularioAsync("/cuenta/cambiar-clave", "cambiar-clave",
            new Dictionary<string, string> { ["Entrada.Actual"] = claveInicial, ["Entrada.Nueva"] = claveNueva, ["Entrada.Confirmar"] = claveNueva });
        Assert.NotNull(tras);

        string? totp = null;
        if (requiereDobleFactor)
        {
            var (_, _, redirDf) = await n.IrAsync("/");
            Assert.Contains("/cuenta/doble-factor", redirDf);

            var (_, paginaDf, _) = await n.IrAsync("/cuenta/doble-factor");
            totp = ClaveDoble().Match(paginaDf).Groups[1].Value.Replace(" ", "");
            Assert.False(string.IsNullOrEmpty(totp), "La página de doble factor no mostró la clave.");

            var (estadoAct, htmlAct, _) = await n.EnviarFormularioAsync("/cuenta/doble-factor", "activar-2fa",
                new Dictionary<string, string> { ["Entrada.Codigo"] = Totp.Codigo(totp, DateTimeOffset.UtcNow) });
            Assert.Equal(HttpStatusCode.OK, estadoAct);
            Assert.Contains("códigos de recuperación", htmlAct);
        }

        var (estadoFinal, _, redirFinal) = await n.IrAsync("/");
        Assert.True(estadoFinal == HttpStatusCode.OK && redirFinal is null, $"Tras el primer ingreso debería ver el inicio (estado {estadoFinal}, redirección {redirFinal}).");
        return (claveNueva, totp);
    }

    public static string Texto(string html, string id)
    {
        var m = Regex.Match(html, $"""id="{Regex.Escape(id)}"[^>]*>(.*?)</""", RegexOptions.Singleline);
        Assert.True(m.Success, $"No se encontró el elemento '{id}'.");
        return WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
    }

    [GeneratedRegex("""id="clave-2fa"[^>]*>(.*?)</code>""", RegexOptions.Singleline)]
    private static partial Regex ClaveDoble();
}
