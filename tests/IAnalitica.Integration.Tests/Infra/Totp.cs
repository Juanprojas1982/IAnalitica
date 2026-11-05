using System.Security.Cryptography;

namespace IAnalitica.Integration.Tests.Infra;

/// <summary>TOTP de RFC 6238 (HMAC-SHA1, 30 s, 6 dígitos): lo que calcula una app autenticadora, para probar el 2FA de punta a punta.</summary>
public static class Totp
{
    public static string Codigo(string claveBase32, DateTimeOffset momento)
    {
        var clave = DecodificarBase32(claveBase32);
        var contador = momento.ToUnixTimeSeconds() / 30;
        var bytes = BitConverter.GetBytes(contador);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);

        var hash = HMACSHA1.HashData(clave, bytes);
        var desplazamiento = hash[^1] & 0x0F;
        var binario = ((hash[desplazamiento] & 0x7F) << 24) | (hash[desplazamiento + 1] << 16) | (hash[desplazamiento + 2] << 8) | hash[desplazamiento + 3];
        return (binario % 1_000_000).ToString("D6");
    }

    private static byte[] DecodificarBase32(string texto)
    {
        const string alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var salida = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in texto.Replace(" ", "").TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alfabeto.IndexOf(c);
            bits += 5;
            if (bits >= 8) { bits -= 8; salida.Add((byte)(buffer >> bits)); buffer &= (1 << bits) - 1; }
        }
        return salida.ToArray();
    }
}
