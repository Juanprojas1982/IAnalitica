namespace IAnalitica.Application.Seguridad;

/// <summary>Qué debe pasar antes de que un usuario con la contraseña correcta vea cualquier dato (24 §4.1, spec 22 §2).</summary>
public enum PasoAcceso
{
    /// <summary>Puede entrar.</summary>
    Permitido,
    /// <summary>Cuenta desactivada.</summary>
    Desactivado,
    /// <summary>La contraseña inicial pasó de las 72 h sin usarse: el superadmin debe regenerarla.</summary>
    ClaveTemporalVencida,
    /// <summary>Debe elegir una contraseña nueva.</summary>
    CambiarClave,
    /// <summary>Debe configurar la app autenticadora.</summary>
    ConfigurarDobleFactor,
    /// <summary>Debe ingresar el código de su app autenticadora.</summary>
    VerificarDobleFactor,
}

public sealed record DatosAcceso(
    bool Activo, bool DebeCambiarClave, DateTime? ClaveTemporalVenceEnUtc,
    bool DobleFactorObligatorio, bool DobleFactorActivo, bool DobleFactorVerificadoEnEstaSesion);

public static class PoliticaAcceso
{
    /// <summary>
    /// El doble factor es obligatorio para el superadmin y el Admin{NIT}; para el resto es opcional y lo configura cada empresa.
    /// </summary>
    public static bool DobleFactorObligatorio(bool esSuperAdmin, bool esAdminPrincipal, bool empresaLoExigeATodos)
        => esSuperAdmin || esAdminPrincipal || empresaLoExigeATodos;

    public static PasoAcceso Evaluar(DatosAcceso d, DateTime ahoraUtc)
    {
        if (!d.Activo) return PasoAcceso.Desactivado;

        if (d.DebeCambiarClave && d.ClaveTemporalVenceEnUtc is { } vence && vence <= ahoraUtc)
            return PasoAcceso.ClaveTemporalVencida;

        // El segundo factor se pide siempre que esté activo, aunque falte cambiar la clave: así nadie cambia clave solo con la inicial.
        if (d.DobleFactorActivo && !d.DobleFactorVerificadoEnEstaSesion) return PasoAcceso.VerificarDobleFactor;

        if (d.DebeCambiarClave) return PasoAcceso.CambiarClave;
        if (d.DobleFactorObligatorio && !d.DobleFactorActivo) return PasoAcceso.ConfigurarDobleFactor;
        return PasoAcceso.Permitido;
    }
}
