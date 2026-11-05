using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure.Persistencia;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace IAnalitica.Infrastructure.Identity;

internal sealed class GestorCredencialesApi(IOpenIddictApplicationManager aplicaciones, AnaliticaDbContext db) : IGestorCredencialesApi
{
    public const string AlcanceIngesta = "ingesta";

    public async Task CrearAsync(int empresaId, string clientId, string secreto, string descripcion, CancellationToken ct)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = secreto,                  // OpenIddict lo guarda con hash; no se puede volver a leer
            ClientType = ClientTypes.Confidential,
            ApplicationType = ApplicationTypes.Web,
            DisplayName = descripcion,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
                Permissions.Prefixes.Scope + AlcanceIngesta,
            },
        };
        await aplicaciones.CreateAsync(descriptor, ct).ConfigureAwait(false);

        db.ApiClientes.Add(new ApiClienteEmpresaEntidad { ClientId = clientId, EmpresaId = empresaId, Descripcion = descripcion });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
