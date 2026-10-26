using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Comun;

namespace IAnalitica.Infrastructure.Persistencia;

/// <summary>
/// Transacción por comando. Si el resultado es una falla de negocio (<see cref="IResultado"/>) se hace rollback:
/// las fallas no lanzan excepciones, pero tampoco deben dejar escrituras parciales.
/// </summary>
internal sealed class TransaccionEf(AnaliticaDbContext db) : ITransaccion
{
    public async Task<T> EjecutarAsync<T>(Func<Task<T>> accion, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await accion().ConfigureAwait(false);   // ya hay una externa: no se anida

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var resultado = await accion().ConfigureAwait(false);
            if (resultado is IResultado { EsExitoso: false })
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                db.ChangeTracker.Clear();
            }
            else
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            return resultado;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
