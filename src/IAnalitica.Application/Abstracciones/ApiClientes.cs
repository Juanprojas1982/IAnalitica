namespace IAnalitica.Application.Abstracciones;

/// <summary>Cliente de la API de ingesta: el ClientId pertenece a una sola empresa (24 §4.3).</summary>
public sealed record ClienteApi(string ClientId, int EmpresaId, int LimitePorMinuto);

public interface IRepositorioApiClientes
{
    /// <summary>El cliente si está activo y su empresa también; si no, null. Registra el último uso.</summary>
    Task<ClienteApi?> ObtenerActivoAsync(string clientId, CancellationToken ct);
}
