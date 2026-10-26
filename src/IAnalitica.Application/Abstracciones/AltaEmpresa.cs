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
