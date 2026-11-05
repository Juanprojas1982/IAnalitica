namespace IAnalitica.Application.Mediacion;

/// <summary>El request exige este permiso del usuario de la sesión (seg.RolPermiso).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequierePermisoAttribute(string permiso) : Attribute
{
    public string Permiso { get; } = permiso;
}

/// <summary>El request exige que el módulo esté activo para la empresa de la sesión.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequiereModuloAttribute(string modulo) : Attribute
{
    public string Modulo { get; } = modulo;
}

/// <summary>Consulta cuyo resultado se guarda por empresa + clave + versión de carga.</summary>
public interface IConsultaCacheable
{
    string ClaveCache { get; }
    TimeSpan Duracion { get; }
}

/// <summary>Comando que se ejecuta dentro de una transacción.</summary>
public interface IComandoTransaccional { }
