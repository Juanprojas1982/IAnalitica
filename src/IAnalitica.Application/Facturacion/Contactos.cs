using FluentValidation;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;

namespace IAnalitica.Application.Facturacion;

public sealed record ContactoFacturacionVista(int ContactoId, string Tipo, string Nombre, string Email);

/// <summary>Contactos a los que se envía la cuenta de cobro (08 S3): <c>FACTURACION</c> (para) y <c>CONTADOR</c> (copia), de la empresa de la sesión (RLS).</summary>
public interface IRepositorioContactosFacturacion
{
    Task<IReadOnlyList<ContactoFacturacionVista>> ListarAsync(CancellationToken ct);
    Task<int> GuardarAsync(int empresaId, string tipo, string nombre, string email, CancellationToken ct);
    Task<bool> QuitarAsync(int contactoId, CancellationToken ct);
}

public static class ContactosFacturacion
{
    public const int MaxFacturacion = 3;
    public const int MaxContador = 2;   // máximo 5 destinatarios por correo
}

[RequierePermiso("admin.usuarios")]
public sealed record ListarContactosFacturacionQuery : IRequest<Result<IReadOnlyList<ContactoFacturacionVista>>>;

public sealed class ListarContactosFacturacionHandler(IRepositorioContactosFacturacion repo) : IRequestHandler<ListarContactosFacturacionQuery, Result<IReadOnlyList<ContactoFacturacionVista>>>
{
    public async Task<Result<IReadOnlyList<ContactoFacturacionVista>>> Handle(ListarContactosFacturacionQuery r, CancellationToken ct)
        => Result<IReadOnlyList<ContactoFacturacionVista>>.Ok(await repo.ListarAsync(ct).ConfigureAwait(false));
}

[RequierePermiso("admin.usuarios")]
public sealed record GuardarContactoFacturacionCommand(string Tipo, string Nombre, string Email) : IRequest<Result<int>>;

public sealed class GuardarContactoFacturacionValidator : AbstractValidator<GuardarContactoFacturacionCommand>
{
    public GuardarContactoFacturacionValidator()
    {
        RuleFor(x => x.Tipo).Must(t => t is "FACTURACION" or "CONTADOR").WithErrorCode("TIPO_INVALIDO").WithMessage("El tipo debe ser de facturación o contador.");
        RuleFor(x => x.Nombre).NotEmpty().MaximumLength(150).WithErrorCode("NOMBRE_INVALIDO").WithMessage("Indique el nombre (máximo 150 caracteres).");
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256).EmailAddress().WithErrorCode("EMAIL_INVALIDO").WithMessage("Indique un correo válido.");
    }
}

public sealed class GuardarContactoFacturacionHandler(ISesionUsuario sesion, IRepositorioContactosFacturacion repo, IAuditoriaAdministrativa auditoria)
    : IRequestHandler<GuardarContactoFacturacionCommand, Result<int>>
{
    public async Task<Result<int>> Handle(GuardarContactoFacturacionCommand r, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId || sesion.UsuarioId is not { } usuarioId) return Result<int>.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        var actuales = await repo.ListarAsync(ct).ConfigureAwait(false);
        var tope = r.Tipo == "FACTURACION" ? ContactosFacturacion.MaxFacturacion : ContactosFacturacion.MaxContador;
        if (actuales.Count(c => c.Tipo == r.Tipo) >= tope) return Result<int>.Falla(Error.Conflicto("CONTACTOS_MAXIMO", $"Solo se admiten {tope} contactos de este tipo."));
        if (actuales.Any(c => c.Tipo == r.Tipo && string.Equals(c.Email, r.Email.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Result<int>.Falla(Error.Conflicto("CONTACTO_DUPLICADO", "Ese correo ya está registrado."));

        var id = await repo.GuardarAsync(empresaId, r.Tipo, r.Nombre.Trim(), r.Email.Trim(), ct).ConfigureAwait(false);
        // La auditoría no guarda el correo ni el nombre (datos personales): solo el tipo y el identificador.
        await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, usuarioId, "seg.EmpresaContacto", id.ToString(), "CREAR", null, $"{{\"tipo\":\"{r.Tipo}\"}}", null), ct).ConfigureAwait(false);
        return id;
    }
}

[RequierePermiso("admin.usuarios")]
public sealed record QuitarContactoFacturacionCommand(int ContactoId) : IRequest<Result>;

public sealed class QuitarContactoFacturacionHandler(ISesionUsuario sesion, IRepositorioContactosFacturacion repo, IAuditoriaAdministrativa auditoria)
    : IRequestHandler<QuitarContactoFacturacionCommand, Result>
{
    public async Task<Result> Handle(QuitarContactoFacturacionCommand r, CancellationToken ct)
    {
        if (sesion.EmpresaId is not { } empresaId || sesion.UsuarioId is not { } usuarioId) return Result.Falla(Error.NoAutorizado("Debe iniciar sesión."));
        if (!await repo.QuitarAsync(r.ContactoId, ct).ConfigureAwait(false)) return Result.Falla(Error.NoEncontrado("El contacto no existe."));
        await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, usuarioId, "seg.EmpresaContacto", r.ContactoId.ToString(), "DESACTIVAR", null, null, null), ct).ConfigureAwait(false);
        return Result.Ok();
    }
}
