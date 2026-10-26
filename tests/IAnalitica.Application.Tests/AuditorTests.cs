using IAnalitica.Application.Auditoria;
using IAnalitica.Domain.Auditoria;

namespace IAnalitica.Application.Tests;

public class AuditorTests
{
    private static readonly CambiarEstadoHallazgoValidator Validador = new();

    [Theory]
    [InlineData("ACEPTAR", null, false)]
    [InlineData("ACEPTAR", "corta", false)]
    [InlineData("ACEPTAR", "          ", false)]
    [InlineData("ACEPTAR", "Riesgo asumido por la gerencia", true)]
    [InlineData("FALSO_POSITIVO", "Es contratista externo", true)]
    [InlineData("FALSO_POSITIVO", null, false)]
    [InlineData("REABRIR", null, true)]
    [InlineData("BORRAR", "Justificación suficiente", false)]
    public void Valida_la_accion_y_la_justificacion(string accion, string? justificacion, bool valido)
        => Assert.Equal(valido, Validador.Validate(new CambiarEstadoHallazgoCommand(1, accion, justificacion)).IsValid);

    [Fact]
    public void La_justificacion_no_pasa_de_500_caracteres()
        => Assert.False(Validador.Validate(new CambiarEstadoHallazgoCommand(1, "ACEPTAR", new string('x', 501))).IsValid);

    [Theory]
    [InlineData("CRITICA", 20)]
    [InlineData("ALTA", 10)]
    [InlineData("MEDIA", 4)]
    [InlineData("BAJA", 1)]
    public void Cada_severidad_resta_su_peso_al_indice(string severidad, int peso)
        => Assert.Equal(peso, Severidades.Peso(severidad));

    [Fact]
    public void Las_severidades_se_convierten_de_ida_y_vuelta()
    {
        foreach (var s in Enum.GetValues<Severidad>()) Assert.Equal(s, Severidades.DeTexto(Severidades.ATexto(s)));
        Assert.Null(Severidades.DeTexto("otra"));
    }
}
