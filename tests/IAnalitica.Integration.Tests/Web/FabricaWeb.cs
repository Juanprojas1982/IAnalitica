using IAnalitica.Integration.Tests.Infra;
using Microsoft.AspNetCore.Hosting;
using IAnalitica.Application.Asistente;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Integration.Tests.Web;

/// <summary>El portal real (Program.cs) apuntando a la base de datos de Testcontainers, con un usuario de rol_app.</summary>
public sealed class FabricaWeb(BaseDeDatosFixture bd, int limiteIngresoPorMinuto = 100000) : WebApplicationFactory<IAnalitica.Web.Components.App>
{
    /// <summary>Carpeta de los reportes generados: la comparten el portal y el worker de la prueba.</summary>
    public static readonly string RutaReportes = Path.Combine(Path.GetTempPath(), "ianalitica-pruebas-reportes");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:App", bd.CadenaApp);
        builder.UseSetting("Seguridad:IntervaloValidacionSelloSegundos", "0");   // la desactivación se nota en la solicitud siguiente
        builder.UseSetting("Seguridad:LimiteIngresoPorMinuto", limiteIngresoPorMinuto.ToString());   // por omisión sin límite: el de 20/min se prueba aparte
        builder.UseSetting("Observabilidad:Exportador", "ninguno");
        builder.UseSetting("Portal:UrlPortal", "https://portal.prueba.test");
        builder.UseSetting("Reportes:Almacen:Ruta", RutaReportes);
        builder.UseSetting("Portal:UrlApi", "https://api.prueba.test");
        // El asistente no llama a ningún proveedor real en las pruebas: el cliente de chat lo fija cada prueba
        builder.ConfigureServices(s => s.AddSingleton<IAsistenteChatClientFactory, AsistenteDePrueba>());
    }

    public Navegador NuevoNavegador() => new(CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    }));
}

/// <summary>Fábrica de chat de las pruebas del portal: devuelve el cliente que la prueba haya puesto en <see cref="Cliente"/>.</summary>
public sealed class AsistenteDePrueba : IAsistenteChatClientFactory
{
    public static IChatClient? Cliente { get; set; }
    public IChatClient? Crear(string proveedorCodigo, string modeloCodigo) => Cliente;
}
