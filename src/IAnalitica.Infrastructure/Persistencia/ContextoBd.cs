using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace IAnalitica.Infrastructure.Persistencia;

/// <summary>
/// Claves de SESSION_CONTEXT que lee la RLS (24 §4.2, siempre/9000_rls.sql). Sin contexto, la aplicación no ve nada.
/// <see cref="Autenticacion"/> solo se activa en el inicio de sesión, el 2FA, el cambio de clave y el endpoint de token.
/// </summary>
public sealed record ContextoBd(int? EmpresaId, int? UsuarioId, bool EsSuperAdmin, bool EsAdminPrincipal, bool Autenticacion)
{
    public static readonly ContextoBd Vacio = new(null, null, false, false, false);
    public static ContextoBd DeEmpresa(int empresaId, int usuarioId, bool esAdminPrincipal = false) => new(empresaId, usuarioId, false, esAdminPrincipal, false);
    public static ContextoBd DeSuperadmin(int usuarioId) => new(null, usuarioId, true, false, false);
    public static ContextoBd DeApi(int empresaId) => new(empresaId, null, false, false, false);
    public static ContextoBd ParaAutenticar => Vacio with { Autenticacion = true };
}

public interface IContextoBdAccesor
{
    ContextoBd Actual { get; }
    void Establecer(ContextoBd contexto);

    /// <summary>Activa <see cref="ContextoBd.Autenticacion"/> mientras dure el bloque (las conexiones nuevas lo toman).</summary>
    IDisposable ParaAutenticacion();
}

public sealed class ContextoBdAccesor : IContextoBdAccesor
{
    public ContextoBd Actual { get; private set; } = ContextoBd.Vacio;

    public void Establecer(ContextoBd contexto) => Actual = contexto;

    public IDisposable ParaAutenticacion()
    {
        var anterior = Actual;
        Actual = anterior with { Autenticacion = true };
        return new Restaurador(this, anterior);
    }

    private sealed class Restaurador(ContextoBdAccesor dueno, ContextoBd anterior) : IDisposable
    {
        public void Dispose() => dueno.Actual = anterior;
    }
}

public static class SesionSql
{
    /// <summary>
    /// Fija el contexto con <c>@read_only = 1</c> en la conexión recién abierta. Se llama en CADA apertura:
    /// el pool ejecuta sp_reset_connection y borra el contexto de la sesión anterior.
    /// </summary>
    public static string Comando(ContextoBd c)
    {
        var partes = new List<string>();
        if (c.EmpresaId is { } e) partes.Add($"EXEC sp_set_session_context N'EmpresaId', {e}, @read_only = 1;");
        if (c.UsuarioId is { } u) partes.Add($"EXEC sp_set_session_context N'UsuarioId', {u}, @read_only = 1;");
        partes.Add($"EXEC sp_set_session_context N'EsSuperAdmin', {(c.EsSuperAdmin ? 1 : 0)}, @read_only = 1;");
        partes.Add($"EXEC sp_set_session_context N'EsAdminPrincipal', {(c.EsAdminPrincipal ? 1 : 0)}, @read_only = 1;");
        partes.Add($"EXEC sp_set_session_context N'Autenticacion', {(c.Autenticacion ? 1 : 0)}, @read_only = 1;");
        return string.Join(' ', partes);   // solo enteros del propio sistema: no hay texto del cliente en el comando
    }

    public static void Aplicar(DbConnection conexion, ContextoBd contexto)
    {
        using var cmd = conexion.CreateCommand();
        cmd.CommandText = Comando(contexto);
        cmd.ExecuteNonQuery();
    }

    public static async Task AplicarAsync(DbConnection conexion, ContextoBd contexto, CancellationToken ct)
    {
        await using var cmd = conexion.CreateCommand();
        cmd.CommandText = Comando(contexto);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Interceptor de EF Core: fija el SESSION_CONTEXT en cada apertura de conexión (24 §4.2).</summary>
public sealed class SessionContextInterceptor(IContextoBdAccesor accesor) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => SesionSql.Aplicar(connection, accesor.Actual);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => SesionSql.AplicarAsync(connection, accesor.Actual, cancellationToken);
}

/// <summary>Conexiones para Dapper (consultas analíticas) con el mismo contexto de sesión.</summary>
public interface IFabricaConexiones
{
    Task<SqlConnection> AbrirAsync(CancellationToken ct);
}

public sealed class FabricaConexiones(string cadena, IContextoBdAccesor accesor) : IFabricaConexiones
{
    public async Task<SqlConnection> AbrirAsync(CancellationToken ct)
    {
        var conexion = new SqlConnection(cadena);
        try
        {
            await conexion.OpenAsync(ct).ConfigureAwait(false);
            await SesionSql.AplicarAsync(conexion, accesor.Actual, ct).ConfigureAwait(false);
            return conexion;
        }
        catch
        {
            await conexion.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
