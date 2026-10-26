using Dapper;
using IAnalitica.Application.Abstracciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace IAnalitica.Infrastructure.Persistencia;

internal sealed class ModulosEmpresa(AnaliticaDbContext db) : IModulosEmpresa
{
    public Task<bool> EstaActivoAsync(int empresaId, string modulo, CancellationToken ct)
        => db.EmpresaModulos.AsNoTracking().AnyAsync(m => m.EmpresaId == empresaId && m.Modulo == modulo && m.Activo, ct);
}

/// <summary>La versión de carga de una empresa es la última carga correcta: al terminar otra, el caché de sus consultas deja de aplicar.</summary>
internal sealed class VersionCarga(IFabricaConexiones conexiones) : IVersionCarga
{
    public async Task<long> ObtenerAsync(int empresaId, CancellationToken ct)
    {
        await using var c = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT ISNULL(MAX(CargaId), 0) FROM etl.Carga WHERE EmpresaId = @empresaId AND Estado = 'OK'", new { empresaId }, cancellationToken: ct)).ConfigureAwait(false);
    }
}

/// <summary>Caché en memoria (gratis, §8). Con varias instancias se cambia por HybridCache sin tocar a los consumidores.</summary>
internal sealed class CacheConsultasMemoria(IMemoryCache cache) : ICacheConsultas
{
    public async Task<T> ObtenerOCrearAsync<T>(string clave, TimeSpan duracion, Func<Task<T>> crear, CancellationToken ct)
    {
        if (cache.TryGetValue(clave, out T? existente) && existente is not null) return existente;
        var valor = await crear().ConfigureAwait(false);
        cache.Set(clave, valor, duracion);
        return valor;
    }
}
