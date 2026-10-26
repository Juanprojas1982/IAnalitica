using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.Data.SqlClient;
using static IAnalitica.Integration.Tests.Infra.Escenarios;

namespace IAnalitica.Integration.Tests;

/// <summary>
/// Criterio de aceptación de F0 (24 §13): con dos empresas creadas, el Admin{NIT} de una no puede ver ni modificar
/// nada de la otra. Se prueba contra SQL Server real, con un usuario de rol_app (nunca sa/dbo).
/// </summary>
[Collection(ColeccionBd.Nombre)]
public class AislamientoEntreEmpresasTests(BaseDeDatosFixture bd)
{
    private const int PermisoDenegado = 229;

    private async Task<(DosEmpresas Dos, ContextoBd AdminA, ContextoBd AdminB)> PrepararAsync()
    {
        var dos = await bd.ObtenerDosEmpresasAsync();
        var idA = await EmpresaIdAsync(DosEmpresas.NitA);
        var idB = await EmpresaIdAsync(DosEmpresas.NitB);
        return (dos, ContextoBd.DeEmpresa(idA, dos.AdminAId, esAdminPrincipal: true), ContextoBd.DeEmpresa(idB, dos.AdminBId, esAdminPrincipal: true));
    }

    private Task<int> EmpresaIdAsync(string nit)
        => BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, $"SELECT EmpresaId FROM seg.Empresa WHERE Nit = '{nit}'", superadmin: true);

    [Fact]
    public async Task Se_crearon_dos_empresas_cada_una_con_su_Admin_NIT()
    {
        var (dos, _, _) = await PrepararAsync();
        Assert.Equal("Admin800197268", dos.A.UsuarioAdministrador);
        Assert.Equal("Admin860034313", dos.B.UsuarioAdministrador);
        Assert.NotEqual(dos.A.EmpresaId, dos.B.EmpresaId);
        Assert.Equal(2, await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, "SELECT COUNT(*) FROM seg.Empresa", superadmin: true));
    }

    [Fact]
    public async Task El_Admin_de_A_no_ve_ni_una_fila_de_B_en_ninguna_tabla_con_EmpresaId()
    {
        var (_, adminA, adminB) = await PrepararAsync();
        await using var c = await AbrirAsync(bd.CadenaApp, adminA);
        var tablas = await Tablas(c, EsquemasConRls);
        Assert.NotEmpty(tablas);

        foreach (var tabla in tablas)
        {
            var deOtras = await Escalar<int>(c, $"SELECT COUNT(*) FROM {tabla} WHERE EmpresaId IS NOT NULL AND EmpresaId <> {adminA.EmpresaId}");
            Assert.True(deOtras == 0, $"{tabla}: el Admin de A ve {deOtras} fila(s) de otra empresa.");
        }

        // Sanidad: el aislamiento no es porque las tablas estén vacías.
        await using var comoB = await AbrirAsync(bd.CadenaApp, adminB);
        Assert.Equal(1, await Escalar<int>(comoB, "SELECT COUNT(*) FROM seg.Empresa"));
        Assert.True(await Escalar<int>(comoB, "SELECT COUNT(*) FROM seg.EmpresaModulo") > 0);
    }

    [Fact]
    public async Task Los_datos_propios_si_se_ven_y_los_de_B_no()
    {
        var (dos, adminA, _) = await PrepararAsync();
        await using var c = await AbrirAsync(bd.CadenaApp, adminA);

        Assert.Equal(1, await Escalar<int>(c, "SELECT COUNT(*) FROM seg.Empresa"));
        Assert.Equal(dos.A.EmpresaId, await Escalar<int>(c, "SELECT EmpresaId FROM seg.Empresa"));
        Assert.Equal(1, await Escalar<int>(c, "SELECT COUNT(*) FROM seg.Usuario"));   // solo su Admin{NIT}
        Assert.Equal(2, await Escalar<int>(c, "SELECT COUNT(*) FROM seg.EmpresaModulo"));
        Assert.Equal(1, await Escalar<int>(c, "SELECT COUNT(*) FROM seg.ApiClienteEmpresa"));
        Assert.Equal(0, await Escalar<int>(c, $"SELECT COUNT(*) FROM seg.Usuario WHERE UserName = '{dos.B.UsuarioAdministrador}'"));
        Assert.Equal(0, await Escalar<int>(c, $"SELECT COUNT(*) FROM seg.ApiClienteEmpresa WHERE ClientId = '{dos.B.ClientId}'"));
        Assert.Equal(1, await Escalar<int>(c, "SELECT COUNT(*) FROM ia.EmpresaConfig"));
    }

    [Fact]
    public async Task El_Admin_de_A_no_puede_modificar_ni_borrar_filas_de_B_en_ninguna_tabla()
    {
        var (_, adminA, adminB) = await PrepararAsync();
        var idB = adminB.EmpresaId!.Value;
        await using var c = await AbrirAsync(bd.CadenaApp, adminA);
        var comprobadas = 0;

        foreach (var tabla in await Tablas(c, EsquemasConRls))
        {
            // rol_app no tiene UPDATE/DELETE en todas las tablas; un permiso denegado también impide la modificación.
            var columna = await ColumnaActualizable(c, tabla);
            foreach (var sql in new[] { $"UPDATE {tabla} SET [{columna}] = [{columna}] WHERE EmpresaId = {idB}", $"DELETE {tabla} WHERE EmpresaId = {idB}" })
            {
                try
                {
                    var filas = await Ejecutar(c, sql);
                    Assert.True(filas == 0, $"{sql} afectó {filas} fila(s) de B.");
                    comprobadas++;
                }
                catch (SqlException ex) when (ex.Number == PermisoDenegado) { }
            }
        }
        Assert.True(comprobadas > 20, $"Se comprobaron muy pocas sentencias ({comprobadas}).");

        // B conserva todo.
        await using var comoB = await AbrirAsync(bd.CadenaApp, adminB);
        Assert.Equal(1, await Escalar<int>(comoB, "SELECT COUNT(*) FROM seg.Empresa"));
        Assert.Equal(1, await Escalar<int>(comoB, "SELECT COUNT(*) FROM seg.Usuario"));
        Assert.Equal(1, await Escalar<int>(comoB, "SELECT COUNT(*) FROM seg.ApiClienteEmpresa"));
    }

    [Fact]
    public async Task El_Admin_de_A_no_puede_escribir_en_nombre_de_B_ni_mover_sus_filas_a_B()
    {
        var (_, adminA, adminB) = await PrepararAsync();
        var idA = adminA.EmpresaId!.Value;
        var idB = adminB.EmpresaId!.Value;
        await using var c = await AbrirAsync(bd.CadenaApp, adminA);

        await Ejecutar(c, $"INSERT cfg.EmpresaParametro (EmpresaId, Clave, Valor) VALUES ({idA}, 'PRUEBA.PROPIA', 'x')");
        Assert.Equal(1, await Escalar<int>(c, "SELECT COUNT(*) FROM cfg.EmpresaParametro WHERE Clave = 'PRUEBA.PROPIA'"));

        var insertar = await Assert.ThrowsAsync<SqlException>(() => Ejecutar(c, $"INSERT cfg.EmpresaParametro (EmpresaId, Clave, Valor) VALUES ({idB}, 'PRUEBA.AJENA', 'x')"));
        Assert.Equal(33504, insertar.Number);   // BLOCK PREDICATE AFTER INSERT

        var mover = await Assert.ThrowsAsync<SqlException>(() => Ejecutar(c, $"UPDATE cfg.EmpresaParametro SET EmpresaId = {idB} WHERE Clave = 'PRUEBA.PROPIA'"));
        Assert.Equal(33504, mover.Number);      // BLOCK PREDICATE AFTER UPDATE

        await using var comoB = await AbrirAsync(bd.CadenaApp, adminB);
        Assert.Equal(0, await Escalar<int>(comoB, "SELECT COUNT(*) FROM cfg.EmpresaParametro"));
    }

    [Fact]
    public async Task Un_usuario_de_A_no_puede_cambiarse_de_empresa_dentro_de_su_conexion()
    {
        var (_, adminA, adminB) = await PrepararAsync();
        await using var c = await AbrirAsync(bd.CadenaApp, adminA);

        // El contexto es de solo lectura (@read_only = 1): no se puede reescribir para suplantar a B.
        var ex = await Assert.ThrowsAsync<SqlException>(() => Ejecutar(c, $"EXEC sp_set_session_context N'EmpresaId', {adminB.EmpresaId}"));
        Assert.Contains("read_only", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await Escalar<int>(c, $"SELECT COUNT(*) FROM seg.Empresa WHERE EmpresaId = {adminB.EmpresaId}"));
    }

    [Fact]
    public async Task Sin_contexto_la_aplicacion_no_ve_nada()
    {
        await PrepararAsync();
        await using var c = await AbrirAsync(bd.CadenaApp, ContextoBd.Vacio);
        foreach (var tabla in await Tablas(c, EsquemasConRls.Replace(",'aud'", "")))
            Assert.Equal(0, await Escalar<int>(c, $"SELECT COUNT(*) FROM {tabla} WHERE EmpresaId IS NOT NULL"));
        Assert.Equal(0, await Escalar<int>(c, "SELECT COUNT(*) FROM seg.Usuario"));
    }

    [Fact]
    public async Task El_superadmin_ve_todas_las_empresas()
    {
        await PrepararAsync();
        await using var c = await AbrirAsync(bd.CadenaApp, ContextoBd.DeSuperadmin(bd.SuperadminId));
        Assert.True(await Escalar<int>(c, "SELECT COUNT(*) FROM seg.Empresa") >= 2);
        Assert.True(await Escalar<int>(c, "SELECT COUNT(*) FROM seg.Usuario") >= 3);
    }

    [Fact]
    public async Task Durante_la_autenticacion_se_ven_usuarios_pero_ningun_dato_de_negocio()
    {
        await PrepararAsync();
        await using var c = await AbrirAsync(bd.CadenaApp, ContextoBd.ParaAutenticar);
        Assert.True(await Escalar<int>(c, "SELECT COUNT(*) FROM seg.Usuario") >= 3);
        Assert.Equal(0, await Escalar<int>(c, "SELECT COUNT(*) FROM seg.Empresa"));
        Assert.Equal(0, await Escalar<int>(c, "SELECT COUNT(*) FROM seg.EmpresaModulo"));
        Assert.Equal(0, await Escalar<int>(c, "SELECT COUNT(*) FROM aud.CambioAdministrativo"));
    }

    [Fact]
    public async Task El_rol_de_procesamiento_ve_todas_las_empresas()
    {
        await PrepararAsync();
        await using var c = new SqlConnection(bd.CadenaEtl);
        await c.OpenAsync();
        Assert.True(await Escalar<int>(c, "SELECT COUNT(*) FROM seg.Empresa") >= 2);
    }

    [Fact]
    public async Task La_auditoria_de_una_empresa_solo_la_ve_su_Admin_NIT_y_la_global_solo_el_superadmin()
    {
        var (dos, adminA, _) = await PrepararAsync();
        await using var comoAdmin = await AbrirAsync(bd.CadenaApp, adminA);
        Assert.True(await Escalar<int>(comoAdmin, "SELECT COUNT(*) FROM aud.CambioAdministrativo") >= 3);
        Assert.Equal(0, await Escalar<int>(comoAdmin, "SELECT COUNT(*) FROM aud.CambioAdministrativo WHERE EmpresaId IS NULL OR EmpresaId <> " + adminA.EmpresaId));

        await using var comoGerente = await AbrirAsync(bd.CadenaApp, ContextoBd.DeEmpresa(adminA.EmpresaId!.Value, 999999));
        Assert.Equal(0, await Escalar<int>(comoGerente, "SELECT COUNT(*) FROM aud.CambioAdministrativo"));

        await using var comoSuper = await AbrirAsync(bd.CadenaApp, ContextoBd.DeSuperadmin(bd.SuperadminId));
        Assert.True(await Escalar<int>(comoSuper, "SELECT COUNT(*) FROM aud.CambioAdministrativo WHERE EmpresaId IS NULL") >= 1);   // alta del superadmin
        _ = dos;
    }

    [Fact]
    public async Task La_auditoria_es_de_solo_insercion_incluso_para_el_superadmin()
    {
        await PrepararAsync();
        await using var c = await AbrirAsync(bd.CadenaApp, ContextoBd.DeSuperadmin(bd.SuperadminId));
        foreach (var tabla in new[] { "aud.CambioAdministrativo", "aud.Seguridad", "aud.Acceso", "aud.AccesoSuperadmin" })
        {
            var columna = await ColumnaActualizable(c, tabla);
            var u = await Assert.ThrowsAsync<SqlException>(() => Ejecutar(c, $"UPDATE {tabla} SET [{columna}] = [{columna}]"));
            Assert.Equal(PermisoDenegado, u.Number);
            var d = await Assert.ThrowsAsync<SqlException>(() => Ejecutar(c, $"DELETE {tabla}"));
            Assert.Equal(PermisoDenegado, d.Number);
        }
    }

    [Fact]
    public async Task Toda_tabla_con_EmpresaId_esta_cubierta_por_la_politica()
    {
        await PrepararAsync();
        await using var c = new SqlConnection(bd.CadenaPropietario);
        await c.OpenAsync();
        var sinPolitica = new List<string>();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.name + '.' + o.name FROM sys.tables o JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.columns col ON col.object_id = o.object_id AND col.name = 'EmpresaId'
            WHERE s.name <> 'crm' AND NOT EXISTS (SELECT 1 FROM sys.security_predicates p WHERE p.target_object_id = o.object_id)
            """;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) sinPolitica.Add(r.GetString(0));
        Assert.Empty(sinPolitica);
    }

    [Fact]
    public async Task El_pool_de_conexiones_no_arrastra_el_contexto_de_otro_usuario()
    {
        var (_, adminA, adminB) = await PrepararAsync();
        // Un solo hilo del pool: la segunda apertura reutiliza físicamente la conexión de la primera.
        var cadena = new SqlConnectionStringBuilder(bd.CadenaApp) { MaxPoolSize = 1, Pooling = true }.ConnectionString;

        await using (var primera = await AbrirAsync(cadena, adminA))
            Assert.Equal(adminA.EmpresaId, await Escalar<int>(primera, "SELECT CAST(SESSION_CONTEXT(N'EmpresaId') AS int)"));

        await using var segunda = new SqlConnection(cadena);
        await segunda.OpenAsync();   // sin fijar contexto: debe venir limpia por sp_reset_connection
        Assert.Equal(0, await Escalar<int>(segunda, "SELECT COUNT(*) FROM seg.Empresa"));
        Assert.Null(await Escalar<int?>(segunda, "SELECT CAST(SESSION_CONTEXT(N'EmpresaId') AS int)"));

        await SesionSql.AplicarAsync(segunda, adminB, CancellationToken.None);
        Assert.Equal(adminB.EmpresaId, await Escalar<int>(segunda, "SELECT EmpresaId FROM seg.Empresa"));
    }
}
