using IAnalitica.Application.Seguridad;

namespace IAnalitica.Application.Tests.Seguridad;

public class PoliticaAccesoTests
{
    private static readonly DateTime Ahora = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private static DatosAcceso Datos(bool activo = true, bool cambiar = false, DateTime? vence = null, bool obligatorio = false, bool activo2fa = false, bool verificado = false)
        => new(activo, cambiar, vence, obligatorio, activo2fa, verificado);

    [Fact]
    public void Usuario_desactivado_no_entra() => Assert.Equal(PasoAcceso.Desactivado, PoliticaAcceso.Evaluar(Datos(activo: false), Ahora));

    [Fact]
    public void Clave_inicial_vencida_exige_que_el_superadmin_la_regenere()
        => Assert.Equal(PasoAcceso.ClaveTemporalVencida, PoliticaAcceso.Evaluar(Datos(cambiar: true, vence: Ahora.AddMinutes(-1)), Ahora));

    [Fact]
    public void Clave_inicial_vigente_exige_cambiarla()
        => Assert.Equal(PasoAcceso.CambiarClave, PoliticaAcceso.Evaluar(Datos(cambiar: true, vence: Ahora.AddHours(1)), Ahora));

    [Fact]
    public void Admin_sin_doble_factor_debe_configurarlo_despues_de_cambiar_la_clave()
    {
        Assert.Equal(PasoAcceso.CambiarClave, PoliticaAcceso.Evaluar(Datos(cambiar: true, vence: Ahora.AddHours(1), obligatorio: true), Ahora));
        Assert.Equal(PasoAcceso.ConfigurarDobleFactor, PoliticaAcceso.Evaluar(Datos(obligatorio: true), Ahora));
    }

    [Fact]
    public void Con_doble_factor_activo_siempre_se_verifica_antes_que_nada_mas()
    {
        Assert.Equal(PasoAcceso.VerificarDobleFactor, PoliticaAcceso.Evaluar(Datos(cambiar: true, vence: Ahora.AddHours(1), activo2fa: true), Ahora));
        Assert.Equal(PasoAcceso.VerificarDobleFactor, PoliticaAcceso.Evaluar(Datos(obligatorio: true, activo2fa: true), Ahora));
        Assert.Equal(PasoAcceso.Permitido, PoliticaAcceso.Evaluar(Datos(obligatorio: true, activo2fa: true, verificado: true), Ahora));
    }

    [Fact]
    public void Usuario_normal_sin_pendientes_entra() => Assert.Equal(PasoAcceso.Permitido, PoliticaAcceso.Evaluar(Datos(), Ahora));

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void El_doble_factor_es_obligatorio_para_superadmin_y_admin_principal_o_si_la_empresa_lo_exige(bool sa, bool admin, bool empresa, bool esperado)
        => Assert.Equal(esperado, PoliticaAcceso.DobleFactorObligatorio(sa, admin, empresa));
}
