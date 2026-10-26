using IAnalitica.Application.Comun;

namespace IAnalitica.Application.Abstracciones;

public sealed record ContactoNuevo(string Tipo, string Nombre, string? Cargo, string Email, string? Telefono, bool EsPrincipal, bool RecibeAlertas);

public sealed record EmpresaNueva(
    string Codigo, string Nit, string DigitoVerificacion, string RazonSocial, string? NombreComercial, string TipoEntidad, string Naturaleza,
    string EmailContacto, string? Ciudad, string? Departamento, string? Telefono, string? LogoUrl, string ColorPrimario, string ColorSecundario,
    string PlanIa, decimal? CupoIaCop, IReadOnlyList<string> Modulos, IReadOnlyList<ContactoNuevo> Contactos);

public interface IRepositorioEmpresas
{
    Task<bool> ExisteNitAsync(string nit, CancellationToken ct);

    /// <summary>
    /// Inserta seg.Empresa, seg.EmpresaModulo, seg.EmpresaContacto e ia.EmpresaConfig (deshabilitada hasta el consentimiento).
    /// Devuelve el EmpresaId. Lanza si el plan de IA no existe.
    /// </summary>
    Task<int> CrearAsync(EmpresaNueva empresa, CancellationToken ct);
}

public sealed record AdministradorNuevo(int EmpresaId, string NombreUsuario, string Email, string NombreCompleto, string ClaveTemporal, DateTime ClaveVenceEnUtc, int? CreadoPor);

public interface IGestorUsuarios
{
    Task<bool> ExisteNombreAsync(string nombreUsuario, CancellationToken ct);

    /// <summary>Crea el Admin{NIT}: rol ADMIN_EMPRESA, EsAdminPrincipal, DebeCambiarClave y clave temporal con vencimiento.</summary>
    Task<Result<int>> CrearAdministradorPrincipalAsync(AdministradorNuevo datos, CancellationToken ct);
}

public interface IGestorCredencialesApi
{
    /// <summary>Crea el cliente OAuth (client credentials) y su fila en seg.ApiClienteEmpresa. El secreto se guarda solo con hash.</summary>
    Task CrearAsync(int empresaId, string clientId, string secreto, string descripcion, CancellationToken ct);
}

public sealed record CambioAdministrativo(
    int? EmpresaId, int UsuarioId, string Entidad, string ClaveEntidad, string Accion, string? ValorAnteriorJson, string? ValorNuevoJson, string? Motivo);

/// <summary>aud.CambioAdministrativo: solo inserción. Nunca recibe contraseñas, secretos ni tokens.</summary>
public interface IAuditoriaAdministrativa
{
    Task RegistrarAsync(CambioAdministrativo cambio, CancellationToken ct);
}

/// <summary>Valores que solo conoce la configuración del despliegue.</summary>
public interface IConfiguracionPortal
{
    string UrlPortal { get; }
    string UrlApi { get; }
}

public interface IGeneradorSecretos
{
    /// <summary>Contraseña aleatoria de 14 caracteres que cumple la política (D26).</summary>
    string ClaveTemporal();

    /// <summary>Secreto del cliente del conector: 256 bits aleatorios.</summary>
    string SecretoCliente();
}

public sealed record UsuarioResumen(
    int Id, string Usuario, string Email, string NombreCompleto, IReadOnlyList<string> Roles, bool Activo,
    bool DebeCambiarClave, bool DobleFactorActivo, bool EsAdminPrincipal);

public sealed record UsuarioNuevo(
    int EmpresaId, string NombreUsuario, string Email, string NombreCompleto, IReadOnlyList<string> Roles,
    string ClaveTemporal, DateTime ClaveVenceEnUtc, int CreadoPor);

/// <summary>Operaciones sobre usuarios de UNA empresa. Todas reciben el EmpresaId de la sesión, nunca del cliente (spec 22 §5).</summary>
public interface IGestionUsuariosEmpresa
{
    Task<UsuarioResumen?> ObtenerAsync(int empresaId, int usuarioId, CancellationToken ct);
    Task<UsuarioResumen?> ObtenerAdminPrincipalAsync(int empresaId, CancellationToken ct);
    Task<IReadOnlyList<UsuarioResumen>> ListarAsync(int empresaId, CancellationToken ct);
    Task<Result<int>> CrearAsync(UsuarioNuevo datos, CancellationToken ct);
    Task<Result> EditarAsync(int empresaId, int usuarioId, string email, string nombreCompleto, IReadOnlyList<string> roles, CancellationToken ct);
    Task<Result> CambiarActivoAsync(int empresaId, int usuarioId, bool activo, CancellationToken ct);

    /// <summary>Nueva clave temporal, cambio obligatorio y (opcional) elimina el doble factor anterior para que la nueva persona lo configure.</summary>
    Task<Result> RestablecerClaveAsync(int empresaId, int usuarioId, string claveTemporal, DateTime venceEnUtc, bool quitarDobleFactor, CancellationToken ct);
}

/// <summary>aud.Seguridad (spec 19 §5): inicios de sesión fallidos, bloqueos, activación o baja del doble factor. Solo inserción.</summary>
public interface IAuditoriaSeguridad
{
    Task RegistrarAsync(string evento, int? empresaId, int? usuarioId, string? nombreUsuario, string? detalle, string? ip, CancellationToken ct);
}
