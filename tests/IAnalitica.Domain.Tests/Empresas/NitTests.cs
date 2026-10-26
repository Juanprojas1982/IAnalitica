using IAnalitica.Domain.Empresas;

namespace IAnalitica.Domain.Tests.Empresas;

public class NitTests
{
    [Theory]
    [InlineData("800197268", '4')]   // NIT de la DIAN
    [InlineData("900.373.915", '9')]   // verificado a mano: suma 761, 761 mod 11 = 2, DV = 9
    [InlineData("860034313", '7')]
    public void Calcula_el_digito_de_verificacion_modulo_11(string texto, char esperado)
    {
        Assert.True(Nit.TryCrear(texto, out var nit));
        Assert.Equal(esperado, nit.CalcularDigitoVerificacion());
        Assert.True(nit.EsDigitoValido(esperado.ToString()));
    }

    [Fact]
    public void Un_digito_incorrecto_no_es_valido()
    {
        Assert.True(Nit.TryCrear("800197268", out var nit));
        Assert.False(nit.EsDigitoValido("5"));
        Assert.False(nit.EsDigitoValido(null));
        Assert.False(nit.EsDigitoValido("44"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("800197268-4")]    // el guion y el DV no hacen parte del NIT
    [InlineData("ABC12345")]
    [InlineData("0123456")]
    [InlineData("1234")]
    [InlineData("1234567890123456")]
    public void Rechaza_nits_mal_formados(string? texto) => Assert.False(Nit.TryCrear(texto, out _));

    [Fact]
    public void El_administrador_se_llama_Admin_mas_el_nit_sin_separadores()
    {
        Assert.True(Nit.TryCrear("900.987.452", out var nit));
        Assert.Equal("900987452", nit.Valor);
        Assert.Equal("Admin900987452", nit.NombreAdministrador);
    }
}
