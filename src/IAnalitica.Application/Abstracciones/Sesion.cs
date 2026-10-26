namespace IAnalitica.Application.Abstracciones;

/// <summary>
/// Usuario y empresa de la solicitud en curso. El EmpresaId sale de la sesión (portal) o del token (API); nunca del cliente (§3.4).
/// </summary>
public interface ISesionUsuario
{
    int? UsuarioId { get; }
    int? EmpresaId { get; }
    bool EsSuperAdmin { get; }
    bool TienePermiso(string permiso);
}

/// <summary>Módulos activos de la empresa de la sesión (seg.EmpresaModulo).</summary>
public interface IModulosEmpresa
{
    Task<bool> EstaActivoAsync(int empresaId, string modulo, CancellationToken ct);
}

/// <summary>Versión de carga de la empresa: invalida el caché cuando termina una carga.</summary>
public interface IVersionCarga
{
    Task<long> ObtenerAsync(int empresaId, CancellationToken ct);
}

public interface ICacheConsultas
{
    Task<T> ObtenerOCrearAsync<T>(string clave, TimeSpan duracion, Func<Task<T>> crear, CancellationToken ct);
}

/// <summary>Transacción de EF Core para comandos marcados como <see cref="Mediacion.IComandoTransaccional"/>.</summary>
public interface ITransaccion
{
    Task<T> EjecutarAsync<T>(Func<Task<T>> accion, CancellationToken ct);
}
