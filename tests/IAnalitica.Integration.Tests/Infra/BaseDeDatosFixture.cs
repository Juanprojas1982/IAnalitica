using IAnalitica.Application;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Semantica;
using IAnalitica.Modulos.Financiero;
using IAnalitica.Modulos.Ingreso;
using IAnalitica.Database.Migrator;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Identity;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Testcontainers.MsSql;

namespace IAnalitica.Integration.Tests.Infra;

/// <summary>
/// SQL Server 2022 real en contenedor (Testcontainers) con TODOS los scripts de database/scripts aplicados por el migrador de producción.
/// Crea un usuario por rol de BD: las pruebas nunca usan sa/dbo para las comprobaciones de RLS.
/// </summary>
public sealed class BaseDeDatosFixture : IAsyncLifetime
{
    private const string Contrasena = "Prueba#Segura2026";
    private readonly MsSqlContainer _contenedor = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string CadenaPropietario { get; private set; } = "";   // sa: migraciones y preparación
    public string CadenaApp { get; private set; } = "";           // usuario de rol_app (sujeto a RLS)
    public string CadenaEtl { get; private set; } = "";           // usuario de rol_etl (sin RLS)
    public int SuperadminId { get; private set; }

    public Task InitializeAsync() => IniciarAsync();
    public Task DisposeAsync() => _contenedor.DisposeAsync().AsTask();

    private async Task IniciarAsync()
    {
        await _contenedor.StartAsync();
        var master = new SqlConnectionStringBuilder(_contenedor.GetConnectionString());
        var propietario = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = "IAnalitica", TrustServerCertificate = true };
        CadenaPropietario = propietario.ConnectionString;

        var resultado = Migrador.Ejecutar(CadenaPropietario, asegurarBd: true, TextWriter.Null);
        if (!resultado.Exitoso) throw new InvalidOperationException("Falló la migración: " + resultado.Error);

        await EjecutarAsync(CadenaPropietario, $"""
            CREATE LOGIN app_test WITH PASSWORD = '{Contrasena}', CHECK_POLICY = OFF;
            CREATE USER app_test FOR LOGIN app_test;  ALTER ROLE rol_app ADD MEMBER app_test;
            CREATE LOGIN etl_test WITH PASSWORD = '{Contrasena}', CHECK_POLICY = OFF;
            CREATE USER etl_test FOR LOGIN etl_test;  ALTER ROLE rol_etl ADD MEMBER etl_test;
            """);

        CadenaApp = new SqlConnectionStringBuilder(propietario.ConnectionString) { UserID = "app_test", Password = Contrasena }.ConnectionString;
        CadenaEtl = new SqlConnectionStringBuilder(propietario.ConnectionString) { UserID = "etl_test", Password = Contrasena }.ConnectionString;

        // D38: el primer superadmin lo crea el migrador, con su propia conexión.
        await using var servicios = Construir(CadenaPropietario, sesion: null);
        var creado = await CrearSuperadmin.EjecutarAsync(servicios, "superadmin", "super@ianalitica.test", CancellationToken.None);
        if (!creado.EsExitoso) throw new InvalidOperationException("No se creó el superadmin: " + creado.Errores[0].Mensaje);
        ClaveInicialSuperadmin = creado.Valor.ClaveInicial;
        SuperadminId = await EscalarAsync<int>(CadenaPropietario, "SELECT Id FROM seg.Usuario WITH (NOLOCK) WHERE UserName = 'superadmin'", superadmin: true);
    }

    public string ClaveInicialSuperadmin { get; private set; } = "";

    public ServiceProvider Construir(string cadena, ISesionUsuario? sesion, Action<IServiceCollection>? ajustar = null)
    {
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddInfrastructure(cadena);
        s.AddSingleton(Substitute.For<IConfiguracionPortal>());
        if (sesion is not null) s.AddSingleton(sesion);
        s.AddCapaSemantica(new[] { ModuloFinanciero.Modulo }.Concat(ModuloIngreso.Modulos));
        s.AddApplication(typeof(IAnalitica.Application.Empresas.CrearEmpresaCommand).Assembly);
        ajustar?.Invoke(s);
        return s.BuildServiceProvider(validateScopes: true);
    }

    public static async Task EjecutarAsync(string cadena, string sql)
    {
        await using var c = new SqlConnection(cadena);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Consulta con la identidad de superadmin (para preparar o inspeccionar sin que la RLS oculte filas).</summary>
    public static async Task<T> EscalarAsync<T>(string cadena, string sql, bool superadmin)
    {
        await using var c = new SqlConnection(cadena);
        await c.OpenAsync();
        if (superadmin) await SesionSql.AplicarAsync(c, new ContextoBd(null, null, true, false, true), CancellationToken.None);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }
}

[CollectionDefinition(Nombre)]
public sealed class ColeccionBd : ICollectionFixture<BaseDeDatosFixture>
{
    public const string Nombre = "BaseDeDatos";
}

public sealed class SesionPrueba : ISesionUsuario
{
    private readonly HashSet<string> _permisos;
    public SesionPrueba(int? usuarioId, int? empresaId, bool superAdmin, params string[] permisos)
    {
        UsuarioId = usuarioId; EmpresaId = empresaId; EsSuperAdmin = superAdmin; _permisos = permisos.ToHashSet();
    }
    public int? UsuarioId { get; }
    public int? EmpresaId { get; }
    public bool EsSuperAdmin { get; }
    public bool TienePermiso(string permiso) => _permisos.Contains(permiso);
}
