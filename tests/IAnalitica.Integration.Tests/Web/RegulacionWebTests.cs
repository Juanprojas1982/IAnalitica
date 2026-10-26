using System.Net;
using System.Text.RegularExpressions;
using IAnalitica.Integration.Tests.Infra;
using static IAnalitica.Integration.Tests.Web.Flujos;

namespace IAnalitica.Integration.Tests.Web;

/// <summary>Catálogo regulatorio por HTTP: solo el superadmin (admin.catalogos), importación multipart y factor validado.</summary>
[Collection(ColeccionBd.Nombre)]
public partial class RegulacionWebTests(BaseDeDatosFixture bd)
{
    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")] private static partial Regex Token();

    [Fact]
    public async Task Un_anonimo_y_un_administrador_de_empresa_no_entran()
    {
        var anonimo = Fabrica(bd).NuevoNavegador();
        var (estado, _, redir) = await anonimo.IrAsync("/admin/regulacion");
        Assert.Equal(HttpStatusCode.Redirect, estado);
        Assert.Contains("/ingresar", redir);

        var kit = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido("817" + Random.Shared.Next(100000, 999999), "Empresa cualquiera", modulos: new[] { "FINANCIERO" }));
        var (clave, totp) = await PrimerIngresoAsync(Fabrica(bd).NuevoNavegador(), kit.UsuarioAdministrador, kit.ClaveInicial, NuevoUsuarioNavegadorClave, requiereDobleFactor: true);
        var n = Fabrica(bd).NuevoNavegador();
        Assert.True(await IngresarAsync(n, new Credenciales(kit.UsuarioAdministrador, clave, totp)));
        var (estado2, _, redir2) = await n.IrAsync("/admin/regulacion");
        Assert.True(estado2 is HttpStatusCode.Forbidden || (estado2 == HttpStatusCode.Redirect && redir2!.Contains("acceso-denegado")), $"{estado2} {redir2}");
    }

    [Fact]
    public async Task El_superadmin_importa_una_lista_por_multipart_y_valida_un_factor()
    {
        var n = Fabrica(bd).NuevoNavegador();
        Assert.True(await IngresarAsync(n, await SuperadminListoAsync(bd)));
        var pagina = await n.Cliente.GetStringAsync("/admin/regulacion");
        Assert.Contains("Importar lista de precios máximos", System.Net.WebUtility.HtmlDecode(pagina));
        var token = Token().Match(pagina).Groups[1].Value;
        Assert.NotEmpty(token);

        // Sin el mapeo configurado el sistema no adivina los campos del archivo
        using var cuerpo = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" }, { new StringContent("CNPMDM"), "entidad" }, { new StringContent("W817"), "numero" },
            { new StringContent("2026"), "anio" }, { new StringContent("PRECIO_MAXIMO"), "tema" }, { new StringContent("2026-08-01"), "expedicion" },
            { new StringContent("2026-08-19"), "vigenteDesde" }, { new StringContent("https://ejemplo.invalido"), "url" },
            { new ByteArrayContent("[]"u8.ToArray()), "archivo", "precios.json" },
        };
        var r = await n.Cliente.PostAsync("/admin/regulacion/precios-maximos", cuerpo);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("error=", r.Headers.Location!.OriginalString);
        Assert.Contains("mapeo", Uri.UnescapeDataString(r.Headers.Location.OriginalString), StringComparison.OrdinalIgnoreCase);

        var cum = $"{Random.Shared.Next(800000, 899999)}-1";
        var (_, _, ok) = await n.PostConTokenAsync("/admin/regulacion", "/admin/regulacion/factores", new Dictionary<string, string> { ["cum"] = cum, ["unidades"] = "30", ["validadoPor"] = "QF Prueba" });
        Assert.Contains("ok=", ok);
        Assert.Equal("MANUAL", await BaseDeDatosFixture.EscalarAsync<string>(bd.CadenaPropietario, $"SELECT Fuente FROM reg.FactorPresentacion WHERE Cum = '{cum}'", superadmin: true));
    }
}
