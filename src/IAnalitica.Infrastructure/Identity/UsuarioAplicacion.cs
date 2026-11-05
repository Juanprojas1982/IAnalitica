using Microsoft.AspNetCore.Identity;

namespace IAnalitica.Infrastructure.Identity;

/// <summary>Usuario del portal (seg.Usuario). Reemplaza al diseño v2 (spec 22 §3).</summary>
public sealed class UsuarioAplicacion : IdentityUser<int>
{
    public int? EmpresaId { get; set; }                 // null solo para el superadmin
    public bool EsSuperAdmin { get; set; }
    public string NombreCompleto { get; set; } = "";
    public bool DebeCambiarClave { get; set; }          // true al crear o restablecer
    public DateTime? ClaveTemporalVenceEn { get; set; }
    public bool EsAdminPrincipal { get; set; }          // el Admin{NIT}: no se puede eliminar ni degradar
    public bool Activo { get; set; } = true;
    public DateTime CreadoEn { get; set; }
    public int? CreadoPor { get; set; }
}

public sealed class RolAplicacion : IdentityRole<int>
{
    public RolAplicacion() { }
    public RolAplicacion(string nombre) : base(nombre) { }
}

public static class Roles
{
    public const string SuperAdmin = "SUPERADMIN";
    public const string AdminEmpresa = "ADMIN_EMPRESA";
    public const string Gerente = "GERENTE";
    public const string Financiero = "FINANCIERO";
    public const string Cartera = "CARTERA";
    public const string Tesorero = "TESORERO";
    public const string Farmacia = "FARMACIA";
    public const string Auditor = "AUDITOR";
    public const string Consulta = "CONSULTA";

    /// <summary>Roles que el Admin{NIT} puede asignar (D27: ADMIN_EMPRESA y SUPERADMIN no se asignan).</summary>
    public static readonly string[] Asignables = { Gerente, Financiero, Cartera, Tesorero, Farmacia, Auditor, Consulta };
}
