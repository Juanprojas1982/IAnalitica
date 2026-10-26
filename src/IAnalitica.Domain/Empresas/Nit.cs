namespace IAnalitica.Domain.Empresas;

/// <summary>NIT colombiano: solo dígitos, sin puntos, guiones ni dígito de verificación (spec 22 §1.2).</summary>
public readonly record struct Nit
{
    private static readonly int[] Pesos = { 3, 7, 13, 17, 19, 23, 29, 37, 41, 43, 47, 53, 59, 67, 71 };

    private Nit(string valor) => Valor = valor;

    public string Valor { get; }

    /// <summary>Nombre del administrador principal de la empresa: <c>Admin{NIT}</c> (D25).</summary>
    public string NombreAdministrador => "Admin" + Valor;

    public override string ToString() => Valor;

    /// <summary>Acepta el NIT con o sin separadores de miles ("900.987.452"); rechaza guion y dígito de verificación.</summary>
    public static bool TryCrear(string? texto, out Nit nit)
    {
        nit = default;
        if (string.IsNullOrWhiteSpace(texto)) return false;

        var limpio = texto.Trim().Replace(".", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (limpio.Length is < 5 or > 15 || !limpio.All(char.IsAsciiDigit) || limpio.StartsWith('0')) return false;

        nit = new Nit(limpio);
        return true;
    }

    /// <summary>Dígito de verificación por el módulo 11 de la DIAN.</summary>
    public char CalcularDigitoVerificacion()
    {
        var suma = 0;
        for (var i = 0; i < Valor.Length; i++)
            suma += (Valor[Valor.Length - 1 - i] - '0') * Pesos[i];

        var resto = suma % 11;
        var dv = resto >= 2 ? 11 - resto : resto;
        return (char)('0' + dv);
    }

    public bool EsDigitoValido(string? digito)
        => digito is { Length: 1 } && digito[0] == CalcularDigitoVerificacion();
}
