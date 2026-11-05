using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Infrastructure.Identity;

public sealed record SuperadminCreado(string Usuario, string ClaveInicial, DateTime ClaveVenceEnUtc);

/// <summary>
/// Primer superadmin (D38): lo ejecuta el migrador, nunca la aplicación web. Falla si ya existe un superadmin activo.
/// Contraseña aleatoria mostrada una sola vez, vence a las 72 h; doble factor obligatorio en el primer ingreso.
/// </summary>
public static class CrearSuperadmin
{
    public static async Task<Result<SuperadminCreado>> EjecutarAsync(IServiceProvider raiz, string usuario, string correo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(usuario) || string.IsNullOrWhiteSpace(correo))
            return Result<SuperadminCreado>.Falla(Error.Validacion("DATOS_REQUERIDOS", "Indique el usuario y el correo."));

        await using var alcance = raiz.CreateAsyncScope();
        var sp = alcance.ServiceProvider;
        // Solo esta operación corre como superadmin y en modo autenticación (para ver seg.Usuario bajo RLS).
        sp.GetRequiredService<IContextoBdAccesor>().Establecer(new ContextoBd(null, null, true, false, true));

        var db = sp.GetRequiredService<AnaliticaDbContext>();
        if (await db.Users.AsNoTracking().AnyAsync(u => u.EsSuperAdmin && u.Activo, ct))
            return Result<SuperadminCreado>.Falla(Error.Conflicto("SUPERADMIN_EXISTE", "Ya existe un superadmin activo. Reemplácelo desde el portal."));

        var gestor = sp.GetRequiredService<UserManager<UsuarioAplicacion>>();
        var reloj = sp.GetRequiredService<TimeProvider>();
        var clave = sp.GetRequiredService<IGeneradorSecretos>().ClaveTemporal();
        var vence = reloj.GetUtcNow().UtcDateTime.AddHours(72);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var nuevo = new UsuarioAplicacion
        {
            UserName = usuario.Trim(), Email = correo.Trim(), NombreCompleto = "Superadministrador", EsSuperAdmin = true, EmpresaId = null,
            Activo = true, DebeCambiarClave = true, ClaveTemporalVenceEn = vence, CreadoEn = reloj.GetUtcNow().UtcDateTime, LockoutEnabled = true,
        };
        var creado = await gestor.CreateAsync(nuevo, clave);
        if (!creado.Succeeded) return Result<SuperadminCreado>.Falla(GestorUsuarios.Traducir(creado));
        var rol = await gestor.AddToRoleAsync(nuevo, Roles.SuperAdmin);
        if (!rol.Succeeded) return Result<SuperadminCreado>.Falla(GestorUsuarios.Traducir(rol));

        await sp.GetRequiredService<IAuditoriaAdministrativa>().RegistrarAsync(
            new CambioAdministrativo(null, nuevo.Id, "seg.Usuario", nuevo.UserName!, "CREAR", null,
                System.Text.Json.JsonSerializer.Serialize(new { Usuario = nuevo.UserName, Rol = Roles.SuperAdmin, ClaveVenceEnUtc = vence }), "Bootstrap del primer superadmin"), ct);
        await tx.CommitAsync(ct);
        return Result<SuperadminCreado>.Ok(new SuperadminCreado(nuevo.UserName!, clave, vence));
    }
}
