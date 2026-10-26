using System.Text.Json;
using FluentValidation;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Mediacion;
using IAnalitica.Domain.Empresas;

namespace IAnalitica.Application.Empresas;

public sealed record ContactoDto(string Tipo, string Nombre, string? Cargo, string Email, string? Telefono, bool EsPrincipal = false, bool RecibeAlertas = false);

/// <summary>Alta de empresa (spec 22 §4). Solo el superadmin. El EmpresaId lo genera el servidor.</summary>
[RequierePermiso("admin.empresas")]
public sealed record CrearEmpresaCommand(
    string Nit, string DigitoVerificacion, string RazonSocial, string? NombreComercial, string TipoEntidad, string? Naturaleza,
    string EmailContacto, string? Ciudad, string? Departamento, string? Telefono, string? LogoUrl,
    string? ColorPrimario, string? ColorSecundario, string? PlanIa, decimal? CupoIaCop,
    IReadOnlyList<string> Modulos, IReadOnlyList<ContactoDto>? Contactos)
    : IRequest<Result<KitEntrega>>, IComandoTransaccional;

/// <summary>Lo que se muestra UNA sola vez al superadmin. No se guarda en claro en ningún lado.</summary>
public sealed record KitEntrega(
    int EmpresaId, string RazonSocial, string UsuarioAdministrador, string ClaveInicial, DateTime ClaveVenceEnUtc,
    string UrlPortal, string UrlApi, string ClientId, string ClientSecret, IReadOnlyList<string> InstruccionesPrimerIngreso);

public sealed class CrearEmpresaCommandValidator : AbstractValidator<CrearEmpresaCommand>
{
    private const string ColorHex = "^#[0-9A-Fa-f]{6}$";

    public CrearEmpresaCommandValidator()
    {
        RuleFor(x => x.Nit).Must(n => Nit.TryCrear(n, out _)).WithErrorCode("NIT_INVALIDO")
            .WithMessage("El NIT debe tener entre 5 y 15 dígitos, sin guion ni dígito de verificación.");
        RuleFor(x => x).Must(x => Nit.TryCrear(x.Nit, out var nit) && nit.EsDigitoValido(x.DigitoVerificacion))
            .When(x => Nit.TryCrear(x.Nit, out _)).WithName("DigitoVerificacion").WithErrorCode("DV_INVALIDO")
            .WithMessage("El dígito de verificación no corresponde al NIT.");
        RuleFor(x => x.RazonSocial).NotEmpty().WithErrorCode("RAZON_SOCIAL_INVALIDA").WithMessage("La razón social es obligatoria.");
        RuleFor(x => x.RazonSocial).MaximumLength(200).WithErrorCode("RAZON_SOCIAL_INVALIDA").WithMessage("La razón social admite máximo 200 caracteres.");
        RuleFor(x => x.NombreComercial).MaximumLength(150).WithErrorCode("NOMBRE_COMERCIAL_LARGO").WithMessage("El nombre comercial admite máximo 150 caracteres.");
        RuleFor(x => x.TipoEntidad).Must(t => TiposEntidad.Todos.Contains(t)).WithErrorCode("TIPO_ENTIDAD_INVALIDO")
            .WithMessage("Tipo de entidad no válido.");
        RuleFor(x => x.Naturaleza).Must(n => n is null || Naturalezas.Todas.Contains(n)).WithErrorCode("NATURALEZA_INVALIDA")
            .WithMessage("Naturaleza no válida.");
        RuleFor(x => x.EmailContacto).NotEmpty().WithErrorCode("EMAIL_INVALIDO").WithMessage("El correo del administrador es obligatorio.");
        RuleFor(x => x.EmailContacto).EmailAddress().WithErrorCode("EMAIL_INVALIDO").WithMessage("El correo del administrador no es válido.");
        RuleFor(x => x.EmailContacto).MaximumLength(256).WithErrorCode("EMAIL_INVALIDO").WithMessage("El correo admite máximo 256 caracteres.");
        RuleFor(x => x.ColorPrimario!).Matches(ColorHex).When(x => x.ColorPrimario is not null).WithErrorCode("COLOR_INVALIDO")
            .WithMessage("El color primario debe tener el formato #RRGGBB.");
        RuleFor(x => x.ColorSecundario!).Matches(ColorHex).When(x => x.ColorSecundario is not null).WithErrorCode("COLOR_INVALIDO")
            .WithMessage("El color secundario debe tener el formato #RRGGBB.");
        RuleFor(x => x.CupoIaCop).GreaterThanOrEqualTo(0).When(x => x.CupoIaCop is not null).WithErrorCode("CUPO_INVALIDO")
            .WithMessage("El cupo de IA no puede ser negativo.");
        RuleFor(x => x.Modulos).NotEmpty().WithErrorCode("MODULOS_REQUERIDOS").WithMessage("Seleccione al menos un módulo.");
        RuleForEach(x => x.Modulos).Must(m => Domain.Empresas.Modulos.Todos.Contains(m)).WithErrorCode("MODULO_INVALIDO")
            .WithMessage("Módulo no válido.");
        RuleForEach(x => x.Contactos).ChildRules(c =>
        {
            c.RuleFor(k => k.Tipo).Must(t => TiposContacto.Todos.Contains(t)).WithErrorCode("CONTACTO_TIPO_INVALIDO").WithMessage("Tipo de contacto no válido.");
            c.RuleFor(k => k.Nombre).NotEmpty().WithErrorCode("CONTACTO_NOMBRE_INVALIDO").WithMessage("El contacto necesita nombre.");
            c.RuleFor(k => k.Nombre).MaximumLength(150).WithErrorCode("CONTACTO_NOMBRE_INVALIDO").WithMessage("El nombre del contacto admite máximo 150 caracteres.");
            c.RuleFor(k => k.Email).NotEmpty().WithErrorCode("CONTACTO_EMAIL_INVALIDO").WithMessage("El contacto necesita correo.");
            c.RuleFor(k => k.Email).EmailAddress().WithErrorCode("CONTACTO_EMAIL_INVALIDO").WithMessage("El correo del contacto no es válido.");
        });
    }
}

public sealed class CrearEmpresaCommandHandler(
    ISesionUsuario sesion,
    IRepositorioEmpresas empresas,
    IGestorUsuarios usuarios,
    IGestorCredencialesApi credencialesApi,
    IAuditoriaAdministrativa auditoria,
    IGeneradorSecretos secretos,
    IConfiguracionPortal portal,
    TimeProvider reloj) : IRequestHandler<CrearEmpresaCommand, Result<KitEntrega>>
{
    public static readonly TimeSpan VigenciaClaveInicial = TimeSpan.FromHours(72);
    private const string PlanPorDefecto = "PILOTO";

    public async Task<Result<KitEntrega>> Handle(CrearEmpresaCommand c, CancellationToken ct)
    {
        // El validador ya garantizó el formato; aquí solo se reconstruye el valor.
        Nit.TryCrear(c.Nit, out var nit);
        var actor = sesion.UsuarioId ?? throw new InvalidOperationException("La autorización exige un usuario.");
        var nombreAdmin = nit.NombreAdministrador;

        if (await empresas.ExisteNitAsync(nit.Valor, ct))
            return Result<KitEntrega>.Falla(Error.Conflicto("NIT_DUPLICADO", "Ya existe una empresa con ese NIT."));
        if (await usuarios.ExisteNombreAsync(nombreAdmin, ct))
            return Result<KitEntrega>.Falla(Error.Conflicto("USUARIO_DUPLICADO", $"El usuario {nombreAdmin} ya existe."));

        var naturaleza = c.Naturaleza ?? (c.TipoEntidad == TiposEntidad.Ese ? Naturalezas.Publica : Naturalezas.Privada);
        var nueva = new EmpresaNueva(
            Codigo: nit.Valor, Nit: nit.Valor, DigitoVerificacion: c.DigitoVerificacion, RazonSocial: c.RazonSocial.Trim(),
            NombreComercial: c.NombreComercial?.Trim(), TipoEntidad: c.TipoEntidad, Naturaleza: naturaleza,
            EmailContacto: c.EmailContacto.Trim(), Ciudad: c.Ciudad, Departamento: c.Departamento, Telefono: c.Telefono, LogoUrl: c.LogoUrl,
            ColorPrimario: c.ColorPrimario ?? "#0B3D91", ColorSecundario: c.ColorSecundario ?? "#5B6B7F",
            PlanIa: c.PlanIa ?? PlanPorDefecto, CupoIaCop: c.CupoIaCop, Modulos: c.Modulos.Distinct().ToArray(),
            Contactos: (c.Contactos ?? Array.Empty<ContactoDto>())
                .Select(k => new ContactoNuevo(k.Tipo, k.Nombre, k.Cargo, k.Email, k.Telefono, k.EsPrincipal, k.RecibeAlertas)).ToArray());

        var empresaId = await empresas.CrearAsync(nueva, ct);

        var clave = secretos.ClaveTemporal();
        var vence = reloj.GetUtcNow().UtcDateTime + VigenciaClaveInicial;
        var admin = await usuarios.CrearAdministradorPrincipalAsync(
            new AdministradorNuevo(empresaId, nombreAdmin, nueva.EmailContacto, $"Administrador {nueva.RazonSocial}", clave, vence, actor), ct);
        if (!admin.EsExitoso) return Result<KitEntrega>.Falla(admin.Errores);

        var clientId = $"conector-{nit.Valor}";
        var secretoCliente = secretos.SecretoCliente();
        await credencialesApi.CrearAsync(empresaId, clientId, secretoCliente, $"Conector de {nueva.RazonSocial}", ct);

        // Auditoría con antes/después, sin secretos ni contraseñas (spec 19 §3).
        await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, actor, "seg.Empresa", empresaId.ToString(), "CREAR", null,
            JsonSerializer.Serialize(new { nueva.Nit, nueva.RazonSocial, nueva.TipoEntidad, nueva.Naturaleza, nueva.Modulos, nueva.PlanIa }), null), ct);
        await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, actor, "seg.Usuario", nombreAdmin, "CREAR", null,
            JsonSerializer.Serialize(new { Usuario = nombreAdmin, Rol = "ADMIN_EMPRESA", ClaveVenceEnUtc = vence }), null), ct);
        await auditoria.RegistrarAsync(new CambioAdministrativo(empresaId, actor, "seg.ApiClienteEmpresa", clientId, "CREAR", null,
            JsonSerializer.Serialize(new { ClientId = clientId }), null), ct);

        return Result<KitEntrega>.Ok(new KitEntrega(
            empresaId, nueva.RazonSocial, nombreAdmin, clave, vence, portal.UrlPortal, portal.UrlApi, clientId, secretoCliente,
            new[]
            {
                "Entregue el usuario y la contraseña por canales distintos (por ejemplo, usuario por correo y contraseña por llamada).",
                $"La contraseña vence el {vence:yyyy-MM-dd HH:mm} UTC (72 horas). Si no se usa, regénerela desde el panel.",
                "En el primer ingreso el administrador debe cambiar la contraseña y configurar el doble factor con una app autenticadora.",
                "El secreto del conector se muestra una sola vez: configúrelo en SaludIPS (Analitica.ClientId / Analitica.Secreto).",
                "No guarde estos datos en correos sin protección.",
            }));
    }
}
