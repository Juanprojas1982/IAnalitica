using IAnalitica.Infrastructure.Identity;

namespace IAnalitica.Infrastructure.Persistencia;

// Entidades de persistencia de F0. El resto de tablas llega con su fase (mapeo DB-first, sin migraciones, §3.2).
// Se mantienen internas: Application solo conoce las interfaces de repositorio.

internal sealed class EmpresaEntidad
{
    public int EmpresaId { get; set; }
    public string Codigo { get; set; } = "";
    public string Nit { get; set; } = "";
    public string? DigitoVerificacion { get; set; }
    public string RazonSocial { get; set; } = "";
    public string? NombreComercial { get; set; }
    public string TipoEntidad { get; set; } = "";
    public string Naturaleza { get; set; } = "PRIVADA";
    public string? Ciudad { get; set; }
    public string? Departamento { get; set; }
    public string? Telefono { get; set; }
    public string? EmailContacto { get; set; }
    public string? LogoUrl { get; set; }
    public string ColorPrimario { get; set; } = "#0B3D91";
    public string ColorSecundario { get; set; } = "#5B6B7F";
    public bool Activa { get; set; } = true;
    public List<EmpresaModuloEntidad> Modulos { get; set; } = new();
    public List<EmpresaContactoEntidad> Contactos { get; set; } = new();
}

internal sealed class EmpresaModuloEntidad
{
    public int EmpresaId { get; set; }
    public string Modulo { get; set; } = "";
    public bool Activo { get; set; } = true;
}

internal sealed class EmpresaContactoEntidad
{
    public int ContactoId { get; set; }
    public int EmpresaId { get; set; }
    public string Tipo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? Cargo { get; set; }
    public string Email { get; set; } = "";
    public string? Telefono { get; set; }
    public bool EsPrincipal { get; set; }
    public bool RecibeAlertas { get; set; }
}

internal sealed class ApiClienteEmpresaEntidad
{
    public string ClientId { get; set; } = "";
    public int EmpresaId { get; set; }
    public string? Descripcion { get; set; }
    public int LimitePorMinuto { get; set; } = 60;
    public bool Activo { get; set; } = true;
}

internal sealed class HistorialClaveEntidad
{
    public long HistorialId { get; set; }
    public int UsuarioId { get; set; }
    public UsuarioAplicacion? Usuario { get; set; }
    public string HashClave { get; set; } = "";
    public DateTime CreadoEn { get; set; }
}

internal sealed class CambioAdministrativoEntidad
{
    public long CambioId { get; set; }
    public int? EmpresaId { get; set; }
    public int UsuarioId { get; set; }
    public string Entidad { get; set; } = "";
    public string ClaveEntidad { get; set; } = "";
    public string Accion { get; set; } = "";
    public string? ValorAnteriorJson { get; set; }
    public string? ValorNuevoJson { get; set; }
    public string? Motivo { get; set; }
    public string? Ip { get; set; }
}

internal sealed class SeguridadEventoEntidad
{
    public long EventoId { get; set; }
    public int? EmpresaId { get; set; }
    public int? UsuarioId { get; set; }
    public string? NombreUsuario { get; set; }
    public string Evento { get; set; } = "";
    public string? Detalle { get; set; }
    public string? Ip { get; set; }
}

internal sealed class IaPlanEntidad
{
    public short PlanId { get; set; }
    public string Codigo { get; set; } = "";
}

internal sealed class IaModeloEntidad
{
    public short ModeloId { get; set; }
    public string Codigo { get; set; } = "";
}

internal sealed class IaParametroEntidad
{
    public string Clave { get; set; } = "";
    public string Valor { get; set; } = "";
}

internal sealed class IaEmpresaConfigEntidad
{
    public int EmpresaId { get; set; }
    public bool Habilitado { get; set; }
    public short PlanId { get; set; }
    public decimal? CupoIncluidoCop { get; set; }
    public short ModeloPrincipalId { get; set; }
}

internal sealed class RolPermisoEntidad
{
    public int RolId { get; set; }
    public string Permiso { get; set; } = "";
}
