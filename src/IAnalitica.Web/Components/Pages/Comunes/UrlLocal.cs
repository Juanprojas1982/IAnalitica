namespace IAnalitica.Web.Components.Pages.Comunes;

public static class UrlLocal
{
    /// <summary>Solo se redirige a rutas del propio sitio (evita redirecciones abiertas tras el ingreso).</summary>
    public static string Segura(string? destino)
        => destino is { Length: > 0 } && destino[0] == '/' && !destino.StartsWith("//", StringComparison.Ordinal) && !destino.StartsWith("/\\", StringComparison.Ordinal)
            ? destino
            : "/";
}
