using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>La API real (Program.cs) apuntando a la base de Testcontainers con un usuario de rol_app.</summary>
public sealed class FabricaApi(BaseDeDatosFixture bd) : WebApplicationFactory<ApiApp>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:App", bd.CadenaApp);
        builder.UseSetting("Observabilidad:Exportador", "ninguno");
    }

    public HttpClient Cliente() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    /// <summary>Cliente con el token de client_credentials ya obtenido.</summary>
    public async Task<HttpClient> ClienteAutenticadoAsync(string clientId, string secreto)
    {
        var http = Cliente();
        var token = await ObtenerTokenAsync(http, clientId, secreto);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public static async Task<string> ObtenerTokenAsync(HttpClient http, string clientId, string secreto)
    {
        var r = await http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secreto, ["scope"] = "ingesta",
        }));
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Token {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }
}

/// <summary>Cliente de alto nivel del contrato 05 para las pruebas.</summary>
public static class ApiExtensiones
{
    public static async Task<HttpResponseMessage> PostLoteAsync(this HttpClient http, long cargaId, int numero, IEnumerable<object> filas, bool gzip = true)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { filas });
        var contenido = gzip ? Comprimir(json) : new ByteArrayContent(json);
        contenido.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (gzip) contenido.Headers.ContentEncoding.Add("gzip");
        return await http.PostAsync($"/v1/cargas/{cargaId}/lotes/{numero}", contenido);
    }

    private static ByteArrayContent Comprimir(byte[] datos)
    {
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) gz.Write(datos);
        return new ByteArrayContent(ms.ToArray());
    }

    public static async Task<long> IniciarAsync(this HttpClient http, string entidad, string modo, DateOnly? desde = null, DateOnly? hasta = null)
    {
        var r = await http.PostAsJsonAsync("/v1/cargas", new { entidad, modo, ventanaDesde = desde?.ToString("yyyy-MM-dd"), ventanaHasta = hasta?.ToString("yyyy-MM-dd"), versionAgente = "1.0" });
        Assert.Equal(System.Net.HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cargaId").GetInt64();
    }

    public static Task<HttpResponseMessage> CerrarAsync(this HttpClient http, long cargaId, int lotes, int filas, decimal? suma = null)
        => http.PostAsJsonAsync($"/v1/cargas/{cargaId}/cerrar", new { totalLotes = lotes, totalFilas = filas, sumaControl = suma });

    public static async Task<string?> CodigoErrorAsync(this HttpResponseMessage r)
        => (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("codigo", out var c) ? c.GetString() : null;

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();
}
