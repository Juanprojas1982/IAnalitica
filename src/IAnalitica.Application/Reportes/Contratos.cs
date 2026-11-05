using IAnalitica.Domain.Modulos;

namespace IAnalitica.Application.Reportes;

public sealed record SolicitudReporte(
    Guid SolicitudId, int EmpresaId, int UsuarioId, string TipoReporte, string Formato, string ParametrosJson, string Estado,
    DateTime SolicitadoEn, DateTime? TerminadoEn, DateTime? ExpiraEn, string? BlobRuta, long? TamanoBytes, long? VersionCarga, string? Error);

public sealed record NuevaSolicitud(string TipoReporte, string Formato, string ParametrosJson, string HashParametros);

/// <summary>Resultado de pedir un reporte: la solicitud y si se reutilizó uno idéntico reciente (spec 21 §4).</summary>
public sealed record SolicitudCreada(Guid SolicitudId, bool Reutilizado, int PosicionEnCola);

/// <summary>
/// Solicitudes de reporte (<c>rpt.SolicitudReporte</c>) con el contexto del usuario (RLS por empresa). Crear una solicitud y encolar
/// su trabajo es una sola transacción (outbox).
/// </summary>
public interface IRepositorioReportes
{
    Task<int> ContarActivasAsync(int usuarioId, CancellationToken ct);
    Task<SolicitudReporte?> BuscarReutilizableAsync(int usuarioId, string hash, long versionCarga, int horas, CancellationToken ct);
    Task<Guid> CrearAsync(int empresaId, int usuarioId, NuevaSolicitud solicitud, long versionCarga, CancellationToken ct);
    Task<int> PosicionEnColaAsync(Guid solicitudId, CancellationToken ct);
    Task<IReadOnlyList<SolicitudReporte>> ListarAsync(int usuarioId, bool todasLasDeLaEmpresa, int maximo, CancellationToken ct);
    Task<SolicitudReporte?> ObtenerAsync(Guid solicitudId, CancellationToken ct);
    Task<bool> CancelarAsync(Guid solicitudId, int usuarioId, CancellationToken ct);
    Task RegistrarDescargaAsync(int usuarioId, int empresaId, Guid solicitudId, string? ip, CancellationToken ct);
}

/// <summary>Dónde viven los archivos generados. Pilotos: disco local. Con Azure Blob solo previa cotización y aprobación (D32).</summary>
public interface IAlmacenReportes
{
    Task<string> GuardarAsync(int empresaId, Guid solicitudId, string extension, byte[] contenido, CancellationToken ct);
    Task<Stream?> AbrirAsync(string ruta, CancellationToken ct);
    Task EliminarAsync(string ruta, CancellationToken ct);
}

public interface INotificaciones
{
    Task NotificarAsync(int empresaId, int usuarioId, string tipo, string titulo, string? enlace, CancellationToken ct);
    Task<int> ContarNoLeidasAsync(CancellationToken ct);
    Task MarcarLeidasAsync(string tipoPrefijo, CancellationToken ct);
}

/// <summary>Permisos de un usuario según sus roles (<c>seg.RolPermiso</c>), para los trabajos que corren con la identidad de quien los pidió.</summary>
public interface IPermisosUsuario
{
    Task<IReadOnlySet<string>> ObtenerAsync(int usuarioId, CancellationToken ct);
}

/// <summary>Exporta un reporte a un formato. Los tres se registran y se eligen por <see cref="Formato"/>.</summary>
public interface IExportadorReporte
{
    string Formato { get; }
    string TipoContenido { get; }
    string Extension { get; }
    byte[] Exportar(ReporteDashboard reporte);
}
