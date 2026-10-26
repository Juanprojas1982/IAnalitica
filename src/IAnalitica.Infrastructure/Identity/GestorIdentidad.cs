using IAnalitica.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IAnalitica.Infrastructure.Identity;

/// <summary>No permite repetir ninguna de las últimas 5 contraseñas (24 §4.1).</summary>
public sealed class HistorialClaveValidator(AnaliticaDbContext db) : IPasswordValidator<UsuarioAplicacion>
{
    public const int Recordadas = 5;

    public async Task<IdentityResult> ValidateAsync(UserManager<UsuarioAplicacion> manager, UsuarioAplicacion user, string? password)
    {
        if (user.Id == 0 || string.IsNullOrEmpty(password)) return IdentityResult.Success;

        var hashes = await db.HistorialClaves.AsNoTracking()
            .Where(h => h.UsuarioId == user.Id)
            .OrderByDescending(h => h.HistorialId)
            .Take(Recordadas)
            .Select(h => h.HashClave)
            .ToListAsync().ConfigureAwait(false);

        foreach (var hash in hashes)
        {
            var r = manager.PasswordHasher.VerifyHashedPassword(user, hash, password);
            if (r is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded)
                return IdentityResult.Failed(new IdentityError
                {
                    Code = "CLAVE_REPETIDA",
                    Description = $"No puede repetir ninguna de sus últimas {Recordadas} contraseñas.",
                });
        }
        return IdentityResult.Success;
    }
}

/// <summary>Descripciones de error de Identity en español (Colombia).</summary>
public sealed class DescriptorErroresEs : IdentityErrorDescriber
{
    public override IdentityError PasswordTooShort(int length) => new() { Code = nameof(PasswordTooShort), Description = $"La contraseña debe tener al menos {length} caracteres." };
    public override IdentityError PasswordRequiresDigit() => new() { Code = nameof(PasswordRequiresDigit), Description = "La contraseña debe incluir al menos un número." };
    public override IdentityError PasswordRequiresLower() => new() { Code = nameof(PasswordRequiresLower), Description = "La contraseña debe incluir al menos una minúscula." };
    public override IdentityError PasswordRequiresUpper() => new() { Code = nameof(PasswordRequiresUpper), Description = "La contraseña debe incluir al menos una mayúscula." };
    public override IdentityError PasswordRequiresNonAlphanumeric() => new() { Code = nameof(PasswordRequiresNonAlphanumeric), Description = "La contraseña debe incluir al menos un carácter especial." };
    public override IdentityError PasswordMismatch() => new() { Code = nameof(PasswordMismatch), Description = "La contraseña actual no es correcta." };
    public override IdentityError DuplicateUserName(string userName) => new() { Code = nameof(DuplicateUserName), Description = $"El usuario {userName} ya existe." };
    public override IdentityError DuplicateEmail(string email) => new() { Code = nameof(DuplicateEmail), Description = $"El correo {email} ya está registrado." };
    public override IdentityError InvalidEmail(string? email) => new() { Code = nameof(InvalidEmail), Description = "El correo no es válido." };
    public override IdentityError InvalidUserName(string? userName) => new() { Code = nameof(InvalidUserName), Description = "El nombre de usuario no es válido." };
    public override IdentityError InvalidToken() => new() { Code = nameof(InvalidToken), Description = "El código no es válido." };
}
