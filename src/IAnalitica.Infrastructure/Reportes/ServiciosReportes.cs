using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Reportes;
using IAnalitica.Infrastructure.Persistencia;

namespace IAnalitica.Infrastructure.Reportes;

public sealed class OpcionesAlmacenReportes
{
    /// <summary>Carpeta raíz de los archivos. Con varias instancias hay que compartirla (o pasar a Blob, previa cotización: D32).</summary>
    public string Ruta { get; set; } = Path.Combine(Path.GetTempPath(), "ianalitica-reportes");
}

/// <summary>Archivos en disco local ($0). La ruta guardada es relativa a la raíz: nunca se confía en una ruta que venga de la base fuera de ella.</summary>
internal sealed class AlmacenReportesDisco(Microsoft.Extensions.Options.IOptions<OpcionesAlmacenReportes> opciones) : IAlmacenReportes
{
    private string Raiz => Path.GetFullPath(opciones.Value.Ruta);

    public async Task<string> GuardarAsync(int empresaId, Guid solicitudId, string extension, byte[] contenido, CancellationToken ct)
    {
        var relativa = Path.Combine(empresaId.ToString(System.Globalization.CultureInfo.InvariantCulture), solicitudId.ToString("N") + "." + extension.TrimStart('.'));
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

    public Task EliminarAsync(string ruta, CancellationToken ct)
    {
        var completa = Resolver(ruta);
        if (File.Exists(completa)) File.Delete(completa);
        return Task.CompletedTask;
    }

    private string Resolver(string relativa)
    {
        var raiz = Raiz;
        var completa = Path.GetFullPath(Path.Combine(raiz, relativa));
        if (!completa.StartsWith(raiz + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Ruta de reporte fuera del almacén.");
        return completa;
    }
}

/// <summary>Notificaciones del portal (la campana): <c>seg.Notificacion</c> con el contexto del usuario.</summary>
internal sealed class Notificaciones(IFabricaConexiones conexiones, ISesionUsuario sesion) : INotificaciones
{
    public async Task NotificarAsync(int empresaId, int usuarioId, string tipo, string titulo, string? enlace, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition(
            "INSERT seg.Notificacion (EmpresaId, UsuarioId, Tipo, Titulo, Enlace) VALUES (@empresaId, @usuarioId, @tipo, @titulo, @enlace)",
            new { empresaId, usuarioId, tipo, titulo = titulo[..Math.Min(titulo.Length, 200)], enlace }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<int> ContarNoLeidasAsync(CancellationToken ct)
    {
        if (sesion.UsuarioId is not { } usuarioId || sesion.EmpresaId is not { } empresaId) return 0;
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM seg.Notificacion WHERE EmpresaId = @empresaId AND UsuarioId = @usuarioId AND Leida = 0", new { empresaId, usuarioId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task MarcarLeidasAsync(string tipoPrefijo, CancellationToken ct)
    {
        if (sesion.UsuarioId is not { } usuarioId || sesion.EmpresaId is not { } empresaId) return;
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition(
            "UPDATE seg.Notificacion SET Leida = 1 WHERE EmpresaId = @empresaId AND UsuarioId = @usuarioId AND Leida = 0 AND Tipo LIKE @patron",
            new { empresaId, usuarioId, patron = tipoPrefijo + "%" }, cancellationToken: ct)).ConfigureAwait(false);
    }
}

internal sealed class PermisosUsuario(IFabricaConexiones conexiones) : IPermisosUsuario
{
    public async Task<IReadOnlySet<string>> ObtenerAsync(int usuarioId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        var permisos = await cn.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT rp.Permiso FROM seg.UsuarioRol ur
            JOIN seg.RolPermiso rp ON rp.RolId = ur.RoleId
            WHERE ur.UserId = @usuarioId
            """, new { usuarioId }, cancellationToken: ct)).ConfigureAwait(false);
        return permisos.ToHashSet();
    }
}

/// <summary>Sesión de un trabajo que corre con la identidad de quien lo pidió (reportes, IA): se llena antes de ejecutar.</summary>
public sealed class SesionTrabajo : ISesionUsuario
{
    private IReadOnlySet<string> _permisos = new HashSet<string>();
    public int? UsuarioId { get; private set; }
    public int? EmpresaId { get; private set; }
    public bool EsSuperAdmin => false;
    public bool TienePermiso(string permiso) => _permisos.Contains(permiso);

    public void Establecer(int usuarioId, int empresaId, IReadOnlySet<string> permisos)
    {
        UsuarioId = usuarioId; EmpresaId = empresaId; _permisos = permisos;
    }
}
