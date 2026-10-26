using IAnalitica.Application.Empresas;
using IAnalitica.Domain.Empresas;
using IAnalitica.Application.Mediacion;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Integration.Tests.Infra;

/// <summary>Dos empresas creadas con el caso de uso real (como superadmin), para las pruebas de aislamiento.</summary>
public sealed class DosEmpresas
{
    public required KitEntrega A { get; init; }
    public required KitEntrega B { get; init; }
    public required int AdminAId { get; init; }
    public required int AdminBId { get; init; }

    public const string NitA = "800197268";   // DV 4
    public const string NitB = "860034313";   // DV 7
}

public static class Escenarios
{
    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static DosEmpresas? _dos;

    public static CrearEmpresaCommand Comando(string nit, string dv, string razon, string tipo = "IPS") => new(
        nit, dv, razon, null, tipo, null, $"admin{nit}@prueba.test", "Cali", "Valle", null, null, null, null, null, null,
        new[] { "FINANCIERO", "CARTERA" }, null);

    /// <summary>Comando válido: el dígito de verificación se calcula con el mismo módulo 11 de la DIAN.</summary>
    public static CrearEmpresaCommand ComandoValido(string nit, string razon, string tipo = "IPS")
    {
        Nit.TryCrear(nit, out var n);
        return Comando(nit, n.CalcularDigitoVerificacion().ToString(), razon, tipo);
    }

    public static async Task<DosEmpresas> ObtenerDosEmpresasAsync(this BaseDeDatosFixture bd)
    {
        await Candado.WaitAsync();
        try
        {
            if (_dos is not null) return _dos;
            var a = await CrearComoSuperadminAsync(bd, Comando(DosEmpresas.NitA, "4", "Clínica A SAS"));
            var b = await CrearComoSuperadminAsync(bd, Comando(DosEmpresas.NitB, "7", "Hospital B ESE", "ESE"));
            _dos = new DosEmpresas
            {
                A = a, B = b,
                AdminAId = await IdUsuarioAsync(bd, a.UsuarioAdministrador),
                AdminBId = await IdUsuarioAsync(bd, b.UsuarioAdministrador),
            };
            return _dos;
        }
        finally { Candado.Release(); }
    }

    public static async Task<KitEntrega> CrearComoSuperadminAsync(BaseDeDatosFixture bd, CrearEmpresaCommand comando)
    {
        var resultado = await EnviarComoSuperadminAsync(bd, comando);
        return resultado.EsExitoso ? resultado.Valor : throw new InvalidOperationException(string.Join("; ", resultado.Errores.Select(e => e.Codigo + ": " + e.Mensaje)));
    }

    public static async Task<IAnalitica.Application.Comun.Result<KitEntrega>> EnviarComoSuperadminAsync(BaseDeDatosFixture bd, CrearEmpresaCommand comando)
    {
        var sesion = new SesionPrueba(bd.SuperadminId, null, true, "admin.empresas");
        await using var sp = bd.Construir(bd.CadenaApp, sesion);
        await using var alcance = sp.CreateAsyncScope();
        alcance.ServiceProvider.GetRequiredService<IContextoBdAccesor>().Establecer(ContextoBd.DeSuperadmin(bd.SuperadminId));
        return await alcance.ServiceProvider.GetRequiredService<ISender>().Send(comando);
    }

    private static Task<int> IdUsuarioAsync(BaseDeDatosFixture bd, string nombre)
        => BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, $"SELECT Id FROM seg.Usuario WHERE UserName = '{nombre}'", superadmin: true);

    /// <summary>Abre una conexión de rol_app con el contexto indicado, igual que lo hace el interceptor.</summary>
    public static async Task<SqlConnection> AbrirAsync(string cadena, ContextoBd contexto)
    {
        var c = new SqlConnection(cadena);
        await c.OpenAsync();
        await SesionSql.AplicarAsync(c, contexto, CancellationToken.None);
        return c;
    }

    public static async Task<T> Escalar<T>(SqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? default! : (T)Convert.ChangeType(r, typeof(T));
    }

    public static async Task<int> Ejecutar(SqlConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<List<string>> Tablas(SqlConnection c, string filtroEsquemas)
    {
        var lista = new List<string>();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT s.name + '.' + o.name FROM sys.tables o JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.columns col ON col.object_id = o.object_id AND col.name = 'EmpresaId'
            WHERE s.name IN ({filtroEsquemas}) ORDER BY 1
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) lista.Add(r.GetString(0));
        return lista;
    }

    /// <summary>Una columna que se puede reasignar a sí misma (ni identity, ni calculada, ni rowversion), para un UPDATE sin cambios.</summary>
    public static async Task<string> ColumnaActualizable(SqlConnection c, string tabla)
        => await Escalar<string>(c, $"SELECT TOP (1) name FROM sys.columns WHERE object_id = OBJECT_ID('{tabla}') AND is_identity = 0 AND is_computed = 0 AND system_type_id <> 189 ORDER BY column_id");

    public const string EsquemasConRls = "'seg','cfg','etl','stg','dim','fact','ia','rpt','reg','saludips','aud'";
}
