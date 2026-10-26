using System.Reflection;
using DbUp;
using DbUp.Engine;
using DbUp.Support;
using Microsoft.Data.SqlClient;

namespace IAnalitica.Database.Migrator;

/// <summary>
/// Aplica los scripts de <c>database/scripts</c> (embebidos): los numerados una sola vez (registro de DbUp) y los de
/// <c>siempre/</c> (RLS) en cada ejecución, después de los numerados. Nunca se edita un script ya aplicado (§6.2).
/// </summary>
public static class Migrador
{
    public const string Colacion = "Modern_Spanish_CI_AS";
    private const string PrefijoSiempre = "IAnalitica.Database.Migrator.scripts.siempre.";
    private const string PrefijoScripts = "IAnalitica.Database.Migrator.scripts.";

    public static ResultadoMigracion Ejecutar(string cadenaConexion, bool asegurarBd, TextWriter salida)
    {
        if (asegurarBd) AsegurarBaseDeDatos(cadenaConexion);

        var ensamblado = Assembly.GetExecutingAssembly();
        var numerados = ConstruirMotor(cadenaConexion, ensamblado, salida,
            n => n.StartsWith(PrefijoScripts, StringComparison.Ordinal) && !n.StartsWith(PrefijoSiempre, StringComparison.Ordinal),
            new SqlScriptOptions { ScriptType = ScriptType.RunOnce, RunGroupOrder = 1 });
        var resultado = numerados.PerformUpgrade();
        if (!resultado.Successful) return new ResultadoMigracion(false, resultado.Error?.Message, resultado.Scripts.Count());

        var siempre = ConstruirMotor(cadenaConexion, ensamblado, salida,
            n => n.StartsWith(PrefijoSiempre, StringComparison.Ordinal),
            new SqlScriptOptions { ScriptType = ScriptType.RunAlways, RunGroupOrder = 2 });
        var resultadoSiempre = siempre.PerformUpgrade();
        return new ResultadoMigracion(resultadoSiempre.Successful, resultadoSiempre.Error?.Message,
            resultado.Scripts.Count() + resultadoSiempre.Scripts.Count());
    }

    private static UpgradeEngine ConstruirMotor(string cadena, Assembly ensamblado, TextWriter salida, Func<string, bool> filtro, SqlScriptOptions opciones)
        => DeployChanges.To
            .SqlDatabase(cadena)
            .WithScriptsEmbeddedInAssembly(ensamblado, filtro, opciones)
            .JournalToSqlTable("dbo", "SchemaVersions")
            .WithExecutionTimeout(TimeSpan.FromMinutes(5))
            .LogTo(new LogDeSalida(salida))
            .Build();

    /// <summary>Crea la BD con la colación del diseño si no existe (local y pruebas). En Azure la BD se aprovisiona aparte.</summary>
    private static void AsegurarBaseDeDatos(string cadena)
    {
        var builder = new SqlConnectionStringBuilder(cadena);
        var nombre = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(nombre)) throw new InvalidOperationException("La cadena de conexión debe incluir la base de datos (Initial Catalog).");
        builder.InitialCatalog = "master";

        using var conexion = new SqlConnection(builder.ConnectionString);
        conexion.Open();
        using var cmd = conexion.CreateCommand();
        cmd.CommandText = $"DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@n) + N' COLLATE {Colacion}'; IF DB_ID(@n) IS NULL EXEC (@sql);";
        cmd.Parameters.AddWithValue("@n", nombre);
        cmd.ExecuteNonQuery();
    }

    private sealed class LogDeSalida(TextWriter salida) : DbUp.Engine.Output.IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) { }
        public void LogDebug(string format, params object[] args) { }
        public void LogInformation(string format, params object[] args) => salida.WriteLine(string.Format(format, args));
        public void LogWarning(string format, params object[] args) => salida.WriteLine("ADVERTENCIA: " + string.Format(format, args));
        public void LogError(string format, params object[] args) => salida.WriteLine("ERROR: " + string.Format(format, args));
        public void LogError(Exception ex, string format, params object[] args) => salida.WriteLine("ERROR: " + string.Format(format, args) + " " + ex.Message);
    }
}

public sealed record ResultadoMigracion(bool Exitoso, string? Error, int ScriptsEjecutados);
