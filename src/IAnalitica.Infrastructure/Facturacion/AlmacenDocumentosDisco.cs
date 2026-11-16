using IAnalitica.Application.Facturacion;
using Microsoft.Extensions.Options;

namespace IAnalitica.Infrastructure.Facturacion;

public sealed class OpcionesAlmacenFacturacion
{
    /// <summary>Raíz de los PDF. En Container Apps el disco no es durable: producción exige Blob (08 §34, P16).</summary>
    public string Ruta { get; set; } = Path.Combine(AppContext.BaseDirectory, "facturacion");
}

/// <summary>Almacén en disco de las cuentas de cobro: raíz propia, sin expiración, con control de ruta como <c>AlmacenReportesDisco</c>.</summary>
internal sealed class AlmacenDocumentosDisco(IOptions<OpcionesAlmacenFacturacion> opciones) : IAlmacenDocumentosFacturacion
{
    private string Raiz => Path.GetFullPath(opciones.Value.Ruta);

    public async Task<string> GuardarAsync(int empresaId, string nombre, byte[] contenido, CancellationToken ct)
    {
        if (nombre.AsSpan().IndexOfAny(['/', '\\']) >= 0 || nombre.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("Nombre de documento inválido.", nameof(nombre));
        var relativa = Path.Combine(empresaId.ToString(System.Globalization.CultureInfo.InvariantCulture), nombre);
        var completa = Resolver(relativa);
        Directory.CreateDirectory(Path.GetDirectoryName(completa)!);
        await File.WriteAllBytesAsync(completa, contenido, ct).ConfigureAwait(false);
        return relativa.Replace('\\', '/');
    }

    public Task<Stream?> AbrirAsync(string ruta, CancellationToken ct)
    {
        var completa = Resolver(ruta);
        return Task.FromResult<Stream?>(File.Exists(completa) ? new FileStream(completa, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null);
    }

    private string Resolver(string relativa)
    {
        var raiz = Raiz;
        var completa = Path.GetFullPath(Path.Combine(raiz, relativa));
        if (!completa.StartsWith(raiz + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Ruta de documento fuera del almacén.");
        return completa;
    }
}
