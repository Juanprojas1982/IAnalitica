using System.Security.Cryptography;
using IAnalitica.Application.Abstracciones;
using System.Buffers.Text;

namespace IAnalitica.Infrastructure.Seguridad;

public sealed class GeneradorSecretos : IGeneradorSecretos
{
    // Sin caracteres ambiguos (0/O, 1/l/I) porque la clave se dicta por teléfono o WhatsApp (spec 22 §2).
    private const string Mayusculas = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Minusculas = "abcdefghijkmnopqrstuvwxyz";
    private const string Digitos = "23456789";
    private const string Especiales = "#$%&*+-=?@!";
    private const int LongitudClave = 14;

    public string ClaveTemporal()
    {
        var todos = Mayusculas + Minusculas + Digitos + Especiales;
        var caracteres = new List<char>(LongitudClave)
        {
            Elegir(Mayusculas), Elegir(Minusculas), Elegir(Digitos), Elegir(Especiales),
        };
        while (caracteres.Count < LongitudClave) caracteres.Add(Elegir(todos));

        // Fisher-Yates con aleatoriedad criptográfica: las cuatro garantizadas no quedan siempre al principio.
        for (var i = caracteres.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (caracteres[i], caracteres[j]) = (caracteres[j], caracteres[i]);
        }
        return new string(caracteres.ToArray());
    }

    public string SecretoCliente() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static char Elegir(string alfabeto) => alfabeto[RandomNumberGenerator.GetInt32(alfabeto.Length)];
}
