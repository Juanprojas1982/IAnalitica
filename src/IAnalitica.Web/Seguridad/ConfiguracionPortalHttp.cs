using IAnalitica.Application.Abstracciones;

namespace IAnalitica.Web.Seguridad;

/// <summary>URL del portal y de la API para el kit de entrega: de la configuración; si falta, la del host actual.</summary>
public sealed class ConfiguracionPortalHttp(IConfiguration configuracion, IHttpContextAccessor http) : IConfiguracionPortal
{
    public string UrlPortal => configuracion["Portal:UrlPortal"] ?? BaseActual();
    public string UrlApi => configuracion["Portal:UrlApi"] ?? BaseActual();

    private string BaseActual() => http.HttpContext is { } c ? $"{c.Request.Scheme}://{c.Request.Host}" : "";
}
