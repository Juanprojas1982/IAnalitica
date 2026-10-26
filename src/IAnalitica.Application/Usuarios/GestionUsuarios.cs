using System.Text.Json;
using FluentValidation;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Empresas;
using IAnalitica.Application.Mediacion;

namespace IAnalitica.Application.Usuarios;

/// <summary>Roles que el Admin{NIT} puede asignar. ADMIN_EMPRESA y SUPERADMIN no se asignan jamás (D27).</summary>
public static class RolesAsignables
{
    public static readonly IReadOnlyList<string> Lista = new[] { "GERENTE", "FINANCIERO", "CARTERA", "TESORERO", "FARMACIA", "AUDITOR", "CONSULTA" };
}

public sealed record CredencialTemporal(int UsuarioId, string Usuario, string ClaveInicial, DateTime ClaveVenceEnUtc);

file static class Ayudas
{
    public static Result<T> SinEmpresa<T>() => Result<T>.Falla(Error.NoAutorizado("No hay una empresa en el contexto de la solicitud."));
    public static Result SinEmpresa() => Result.Falla(Error.NoAutorizado("No hay una empresa en el contexto de la solicitud."));

    public static string Json(UsuarioResumen u) => JsonSerializer.Serialize(new { u.Usuario, u.Email, u.NombreCompleto, u.Roles, u.Activo });
}

// ---------- Crear ----------
[RequierePermiso("admin.usuarios")]
public sealed record CrearUsuarioCommand(string NombreUsuario, string Email, string NombreCompleto, IReadOnlyList<string> Roles)
    : IRequest<Result<CredencialTemporal>>, IComandoTransaccional;

public sealed class CrearUsuarioCommandValidator : AbstractValidator<CrearUsuarioCommand>
{
    public CrearUsuarioCommandValidator()
    {
        RuleFor(x => x.NombreUsuario).NotEmpty().WithErrorCode("USUARIO_INVALIDO").WithMessage("El usuario es obligatorio.");
        RuleFor(x => x.NombreUsuario).Length(4, 60).WithErrorCode("USUARIO_INVALIDO").WithMessage("El usuario debe tener entre 4 y 60 caracteres.");
        RuleFor(x => x.NombreUsuario).Matches("^[A-Za-z0-9._-]+$").WithErrorCode("USUARIO_INVALIDO").WithMessage("El usuario solo admite letras, números, punto, guion o guion bajo.");
        RuleFor(x => x.NombreUsuario).Must(n => !n.StartsWith("Admin", StringComparison.OrdinalIgnoreCase))
            .WithErrorCode("USUARIO_RESERVADO").WithMessage("Los nombres que empiezan por 'Admin' están reservados para el administrador de la empresa.");
        RuleFor(x => x.Email).NotEmpty().WithErrorCode("EMAIL_INVALIDO").WithMessage("El correo es obligatorio.");
        RuleFor(x => x.Email).EmailAddress().WithErrorCode("EMAIL_INVALIDO").WithMessage("El correo no es válido.");
        RuleFor(x => x.Email).MaximumLength(256).WithErrorCode("EMAIL_INVALIDO").WithMessage("El correo admite máximo 256 caracteres.");
        RuleFor(x => x.NombreCompleto).NotEmpty().WithErrorCode("NOMBRE_INVALIDO").WithMessage("El nombre es obligatorio.");
        RuleFor(x => x.NombreCompleto).MaximumLength(150).WithErrorCode("NOMBRE_INVALIDO").WithMessage("El nombre admite máximo 150 caracteres.");
        RuleFor(x => x.Roles).NotEmpty().WithErrorCode("ROL_REQUERIDO").WithMessage("Asigne al menos un rol.");
        RuleForEach(x => x.Roles).Must(r => RolesAsignables.Lista.Contains(r)).WithErrorCode("ROL_NO_ASIGNABLE")
            .WithMessage("Ese rol no se puede asignar. Solo existe un administrador por empresa (el Admin{NIT}).");
    }
}

public sealed class CrearUsuarioCommandHandler(
    ISesionUsuario sesion, IGestionUsuariosEmpresa usuarios, IGestorUsuarios existencia, IAuditoriaAdministrativa auditoria,
    IGeneradorSecretos secretos, TimeProvider reloj) : IRequestHandler<CrearUsuarioCommand, Result<CredencialTemporal>>
{
    public async Task<Result<CredencialTemporal>> Handle(CrearUsuarioCommand c, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId || sesion.UsuarioId is not { } actor) return Ayudas.SinEmpresa<CredencialTemporal>();
        var nombre = c.NombreUsuario.Trim();
        if (await existencia.ExisteNombreAsync(nombre, ct))
            return Result<CredencialTemporal>.Falla(Error.Conflicto("USUARIO_DUPLICADO", "Ese nombre de usuario ya está en uso."));

        var clave = secretos.ClaveTemporal();
        var vence = reloj.GetUtcNow().UtcDateTime + CrearEmpresaCommandHandler.VigenciaClaveInicial;
        var id = await usuarios.CrearAsync(new UsuarioNuevo(empresaId, nombre, c.Email.Trim(), c.NombreCompleto.Trim(), c.Roles.Distinct().ToArray(), clave, vence, actor), ct);
        if (!id.EsExitoso) return Result<CredencialTemporal>.Falla(id.Errores);

        await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, actor, "seg.Usuario", nombre, "CREAR", null,
            JsonSerializer.Serialize(new { Usuario = nombre, c.Roles, ClaveVenceEnUtc = vence }), null), ct);
        return Result<CredencialTemporal>.Ok(new CredencialTemporal(id.Valor, nombre, clave, vence));
    }
}

// ---------- Editar ----------
[RequierePermiso("admin.usuarios")]
public sealed record EditarUsuarioCommand(int UsuarioId, string Email, string NombreCompleto, IReadOnlyList<string> Roles)
    : IRequest<Result>, IComandoTransaccional;

public sealed class EditarUsuarioCommandValidator : AbstractValidator<EditarUsuarioCommand>
{
    public EditarUsuarioCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithErrorCode("EMAIL_INVALIDO").WithMessage("El correo es obligatorio.");
        RuleFor(x => x.Email).EmailAddress().WithErrorCode("EMAIL_INVALIDO").WithMessage("El correo no es válido.");
        RuleFor(x => x.Email).MaximumLength(256).WithErrorCode("EMAIL_INVALIDO").WithMessage("El correo admite máximo 256 caracteres.");
        RuleFor(x => x.NombreCompleto).NotEmpty().WithErrorCode("NOMBRE_INVALIDO").WithMessage("El nombre es obligatorio.");
        RuleFor(x => x.NombreCompleto).MaximumLength(150).WithErrorCode("NOMBRE_INVALIDO").WithMessage("El nombre admite máximo 150 caracteres.");
        RuleFor(x => x.Roles).NotEmpty().WithErrorCode("ROL_REQUERIDO").WithMessage("Asigne al menos un rol.");
        RuleForEach(x => x.Roles).Must(r => RolesAsignables.Lista.Contains(r)).WithErrorCode("ROL_NO_ASIGNABLE")
            .WithMessage("Ese rol no se puede asignar. Solo existe un administrador por empresa (el Admin{NIT}).");
    }
}

public sealed class EditarUsuarioCommandHandler(ISesionUsuario sesion, IGestionUsuariosEmpresa usuarios, IAuditoriaAdministrativa auditoria)
    : IRequestHandler<EditarUsuarioCommand, Result>
{
    public async Task<Result> Handle(EditarUsuarioCommand c, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId || sesion.UsuarioId is not { } actor) return Ayudas.SinEmpresa();
        var antes = await usuarios.ObtenerAsync(empresaId, c.UsuarioId, ct);
        if (antes is null) return Result.Falla(Error.NoEncontrado("El usuario no existe en su empresa."));
        if (antes.EsAdminPrincipal)
            return Result.Falla(Error.Conflicto("ADMIN_PRINCIPAL_PROTEGIDO", "El administrador de la empresa solo lo modifica el superadmin."));

        var r = await usuarios.EditarAsync(empresaId, c.UsuarioId, c.Email.Trim(), c.NombreCompleto.Trim(), c.Roles.Distinct().ToArray(), ct);
        if (!r.EsExitoso) return r;

        var despues = await usuarios.ObtenerAsync(empresaId, c.UsuarioId, ct);
        await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, actor, "seg.Usuario", antes.Usuario, "MODIFICAR", Ayudas.Json(antes), Ayudas.Json(despues!), null), ct);
        return Result.Ok();
    }
}

// ---------- Desactivar / reactivar (no se borran: auditoría) ----------
[RequierePermiso("admin.usuarios")]
public sealed record CambiarEstadoUsuarioCommand(int UsuarioId, bool Activo) : IRequest<Result>, IComandoTransaccional;

public sealed class CambiarEstadoUsuarioCommandHandler(ISesionUsuario sesion, IGestionUsuariosEmpresa usuarios, IAuditoriaAdministrativa auditoria)
    : IRequestHandler<CambiarEstadoUsuarioCommand, Result>
{
    public async Task<Result> Handle(CambiarEstadoUsuarioCommand c, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId || sesion.UsuarioId is not { } actor) return Ayudas.SinEmpresa();
        var antes = await usuarios.ObtenerAsync(empresaId, c.UsuarioId, ct);
        if (antes is null) return Result.Falla(Error.NoEncontrado("El usuario no existe en su empresa."));
        if (antes.EsAdminPrincipal)
            return Result.Falla(Error.Conflicto("ADMIN_PRINCIPAL_PROTEGIDO", "El administrador de la empresa no se puede desactivar desde la empresa; lo reemplaza el superadmin."));
        if (antes.Activo == c.Activo) return Result.Ok();

        var r = await usuarios.CambiarActivoAsync(empresaId, c.UsuarioId, c.Activo, ct);
        if (!r.EsExitoso) return r;
        await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, actor, "seg.Usuario", antes.Usuario, c.Activo ? "REACTIVAR" : "DESACTIVAR",
            Ayudas.Json(antes), Ayudas.Json(antes with { Activo = c.Activo }), null), ct);
        return Result.Ok();
    }
}

// ---------- Restablecer clave de un usuario (Admin{NIT}) ----------
[RequierePermiso("admin.usuarios")]
public sealed record RestablecerClaveUsuarioCommand(int UsuarioId) : IRequest<Result<CredencialTemporal>>, IComandoTransaccional;

public sealed class RestablecerClaveUsuarioCommandHandler(
    ISesionUsuario sesion, IGestionUsuariosEmpresa usuarios, IAuditoriaAdministrativa auditoria, IGeneradorSecretos secretos, TimeProvider reloj)
    : IRequestHandler<RestablecerClaveUsuarioCommand, Result<CredencialTemporal>>
{
    public async Task<Result<CredencialTemporal>> Handle(RestablecerClaveUsuarioCommand c, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId || sesion.UsuarioId is not { } actor) return Ayudas.SinEmpresa<CredencialTemporal>();
        var usuario = await usuarios.ObtenerAsync(empresaId, c.UsuarioId, ct);
        if (usuario is null) return Result<CredencialTemporal>.Falla(Error.NoEncontrado("El usuario no existe en su empresa."));
        if (usuario.EsAdminPrincipal)
            return Result<CredencialTemporal>.Falla(Error.Conflicto("ADMIN_PRINCIPAL_PROTEGIDO", "La clave del administrador de la empresa la restablece el superadmin."));

        var clave = secretos.ClaveTemporal();
        var vence = reloj.GetUtcNow().UtcDateTime + CrearEmpresaCommandHandler.VigenciaClaveInicial;
        var r = await usuarios.RestablecerClaveAsync(empresaId, c.UsuarioId, clave, vence, quitarDobleFactor: false, ct);
        if (!r.EsExitoso) return Result<CredencialTemporal>.Falla(r.Errores);

        await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, actor, "seg.Usuario", usuario.Usuario, "RESTABLECER_CLAVE", null,
            JsonSerializer.Serialize(new { ClaveVenceEnUtc = vence }), null), ct);
        return Result<CredencialTemporal>.Ok(new CredencialTemporal(c.UsuarioId, usuario.Usuario, clave, vence));
    }
}

// ---------- Listar ----------
[RequierePermiso("admin.usuarios")]
public sealed record ListarUsuariosQuery : IRequest<Result<IReadOnlyList<UsuarioResumen>>>;

public sealed class ListarUsuariosQueryHandler(ISesionUsuario sesion, IGestionUsuariosEmpresa usuarios)
    : IRequestHandler<ListarUsuariosQuery, Result<IReadOnlyList<UsuarioResumen>>>
{
    public async Task<Result<IReadOnlyList<UsuarioResumen>>> Handle(ListarUsuariosQuery q, CancellationToken ct)
        => sesion.EmpresaId is { } empresaId
            ? Result<IReadOnlyList<UsuarioResumen>>.Ok(await usuarios.ListarAsync(empresaId, ct))
            : Ayudas.SinEmpresa<IReadOnlyList<UsuarioResumen>>();
}

// ---------- Reemplazo del Admin{NIT}: solo el superadmin (spec 22 §5) ----------
[RequierePermiso("admin.empresas")]
public sealed record RestablecerAdminEmpresaCommand(int EmpresaId, string Motivo) : IRequest<Result<CredencialTemporal>>, IComandoTransaccional;

public sealed class RestablecerAdminEmpresaCommandValidator : AbstractValidator<RestablecerAdminEmpresaCommand>
{
    public RestablecerAdminEmpresaCommandValidator()
    {
        RuleFor(x => x.Motivo).NotEmpty().WithErrorCode("MOTIVO_REQUERIDO").WithMessage("Indique el motivo: queda en la auditoría.");
        RuleFor(x => x.Motivo).MinimumLength(10).WithErrorCode("MOTIVO_REQUERIDO").WithMessage("El motivo debe tener al menos 10 caracteres.");
        RuleFor(x => x.Motivo).MaximumLength(500).WithErrorCode("MOTIVO_REQUERIDO").WithMessage("El motivo admite máximo 500 caracteres.");
    }
}

/// <summary>Restablece la clave del Admin{NIT} y quita su doble factor para que la nueva persona configure el suyo.</summary>
public sealed class RestablecerAdminEmpresaCommandHandler(
    ISesionUsuario sesion, IGestionUsuariosEmpresa usuarios, IAuditoriaAdministrativa auditoria, IGeneradorSecretos secretos, TimeProvider reloj)
    : IRequestHandler<RestablecerAdminEmpresaCommand, Result<CredencialTemporal>>
{
    public async Task<Result<CredencialTemporal>> Handle(RestablecerAdminEmpresaCommand c, CancellationToken ct)
    {
        var actor = sesion.UsuarioId ?? throw new InvalidOperationException("La autorización exige un usuario.");
        var admin = await usuarios.ObtenerAdminPrincipalAsync(c.EmpresaId, ct);
        if (admin is null) return Result<CredencialTemporal>.Falla(Error.NoEncontrado("La empresa no existe o no tiene administrador."));

        var clave = secretos.ClaveTemporal();
        var vence = reloj.GetUtcNow().UtcDateTime + CrearEmpresaCommandHandler.VigenciaClaveInicial;
        var r = await usuarios.RestablecerClaveAsync(c.EmpresaId, admin.Id, clave, vence, quitarDobleFactor: true, ct);
        if (!r.EsExitoso) return Result<CredencialTemporal>.Falla(r.Errores);

        await auditoria.RegistrarAsync(new CambioAdministrativo(c.EmpresaId, actor, "seg.Usuario", admin.Usuario, "RESTABLECER_CLAVE",
            JsonSerializer.Serialize(new { admin.DobleFactorActivo }), JsonSerializer.Serialize(new { DobleFactorActivo = false, ClaveVenceEnUtc = vence }), c.Motivo.Trim()), ct);
        return Result<CredencialTemporal>.Ok(new CredencialTemporal(admin.Id, admin.Usuario, clave, vence));
    }
}
