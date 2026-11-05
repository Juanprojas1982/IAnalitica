using IAnalitica.Infrastructure.Persistencia;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.Data.SqlClient;
using static IAnalitica.Integration.Tests.Infra.Escenarios;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>
/// Facturación del SaaS, paso 1 (08 §30, §35): esquema <c>fac</c>, RLS y restricciones, contra SQL Server real.
/// Los datos se preparan con rol_etl (sin RLS); las comprobaciones corren con rol_app y el contexto de cada actor.
/// </summary>
[Collection(ColeccionBd.Nombre)]
public class FacturacionEsquemaTests(BaseDeDatosFixture bd)
{
    private const int PermisoDenegado = 229;
    private const int BloqueoRls = 33504;        // BLOCK PREDICATE
    private const int ViolacionRestriccion = 547; // CHECK / FK
    private const int IndiceUnico = 2601;       // índice único
    private const int RestriccionUnica = 2627;   // UNIQUE constraint

    private sealed record Datos(int EmpresaA, int EmpresaB, int CuentaA, int CuentaB, int PagoA, ContextoBd AdminA, ContextoBd AdminB, ContextoBd Super);

    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static Datos? _datos;

    private async Task<Datos> PrepararAsync()
    {
        await Candado.WaitAsync();
        try
        {
            if (_datos is not null) return _datos;
            var dos = await bd.ObtenerDosEmpresasAsync();
            var idA = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, $"SELECT EmpresaId FROM seg.Empresa WHERE Nit = '{DosEmpresas.NitA}'", superadmin: true);
            var idB = await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaPropietario, $"SELECT EmpresaId FROM seg.Empresa WHERE Nit = '{DosEmpresas.NitB}'", superadmin: true);
            var su = bd.SuperadminId;

            await BaseDeDatosFixture.EjecutarAsync(bd.CadenaEtl, $"""
                DECLARE @plan smallint = (SELECT TOP (1) PlanId FROM ia.PlanComercial ORDER BY PlanId);
                INSERT fac.CondicionComercial (EmpresaId, VigenteDesde, PlanId, CanonPactadoCop, CreadoPor) VALUES
                    ({idA}, '2026-01-01', @plan, 1000000, {su}), ({idB}, '2026-01-01', @plan, 2000000, {su});
                INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, Numero, Estado, TotalCop, DiasPago, FechaEmision, FechaVencimiento, ClienteNit)
                SELECT EmpresaId, 202601, CondicionId, 'CC-9' + RIGHT('00000' + CAST(EmpresaId AS varchar(10)), 5), 'EMITIDA', CanonPactadoCop, 30, '2026-02-01', '2026-03-03', '{DosEmpresas.NitA}'
                FROM fac.CondicionComercial WHERE EmpresaId IN ({idA}, {idB});
                INSERT fac.Pago (EmpresaId, Clase, Medio, FechaPago, ValorCop, RegistradoPor) VALUES ({idA}, 'RECAUDO', 'TRANSFERENCIA', '2026-02-10', 500000, {su});
                INSERT fac.Alerta (EmpresaId, PeriodoKey, Codigo, Detalle) VALUES ({idA}, 202601, 'SIN_DESTINATARIO', N'prueba'), (NULL, NULL, 'SMTP_NO_CONFIGURADO', N'global');
                """);

            _datos = new Datos(idA, idB,
                await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaEtl, $"SELECT CuentaCobroId FROM fac.CuentaCobro WHERE EmpresaId = {idA}", superadmin: false),
                await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaEtl, $"SELECT CuentaCobroId FROM fac.CuentaCobro WHERE EmpresaId = {idB}", superadmin: false),
                await BaseDeDatosFixture.EscalarAsync<int>(bd.CadenaEtl, $"SELECT PagoId FROM fac.Pago WHERE EmpresaId = {idA}", superadmin: false),
                ContextoBd.DeEmpresa(idA, dos.AdminAId, esAdminPrincipal: true),
                ContextoBd.DeEmpresa(idB, dos.AdminBId, esAdminPrincipal: true),
                ContextoBd.DeSuperadmin(su));
            return _datos;
        }
        finally { Candado.Release(); }
    }

    private static async Task<int> CodigoErrorAsync(SqlConnection c, string sql)
        => (await Assert.ThrowsAsync<SqlException>(() => Ejecutar(c, sql))).Number;

    [Fact]
    public async Task El_Admin_de_A_solo_ve_la_facturacion_de_su_empresa()
    {
        var d = await PrepararAsync();
        await using var c = await AbrirAsync(bd.CadenaApp, d.AdminA);

        Assert.Equal(1, await Escalar<int>(c, "SELECT COUNT(*) FROM fac.CuentaCobro"));
        Assert.Equal(0, await Escalar<int>(c, $"SELECT COUNT(*) FROM fac.CuentaCobro WHERE EmpresaId = {d.EmpresaB}"));
        Assert.Equal(1, await Escalar<int>(c, "SELECT COUNT(*) FROM fac.CondicionComercial"));
        Assert.Equal(1, await Escalar<int>(c, "SELECT COUNT(*) FROM fac.Pago"));

        await using var comoB = await AbrirAsync(bd.CadenaApp, d.AdminB);
        Assert.Equal(1, await Escalar<int>(comoB, "SELECT COUNT(*) FROM fac.CuentaCobro"));
        Assert.Equal(0, await Escalar<int>(comoB, "SELECT COUNT(*) FROM fac.Pago"));
    }

    [Fact]
    public async Task El_Admin_de_empresa_no_escribe_en_fac_ni_siquiera_en_lo_suyo()
    {
        var d = await PrepararAsync();
        await using var c = await AbrirAsync(bd.CadenaApp, d.AdminA);

        Assert.Equal(BloqueoRls, await CodigoErrorAsync(c,
            $"INSERT fac.Pago (EmpresaId, Clase, Medio, FechaPago, ValorCop, RegistradoPor) VALUES ({d.EmpresaA}, 'RECAUDO', 'OTRO', '2026-02-11', 1, 1)"));
        Assert.Equal(BloqueoRls, await CodigoErrorAsync(c, $"INSERT fac.Pago (EmpresaId, Clase, Medio, FechaPago, ValorCop, RegistradoPor) VALUES ({d.EmpresaB}, 'RECAUDO', 'OTRO', '2026-02-11', 1, 1)"));
        Assert.Equal(BloqueoRls, await CodigoErrorAsync(c, $"UPDATE fac.CuentaCobro SET Observaciones = N'x' WHERE CuentaCobroId = {d.CuentaA}"));
        Assert.Equal(PermisoDenegado, await CodigoErrorAsync(c, $"DELETE fac.CuentaCobro WHERE CuentaCobroId = {d.CuentaA}"));
        Assert.Equal(PermisoDenegado, await CodigoErrorAsync(c, "UPDATE fac.Parametro SET Valor = N'1' WHERE Clave = 'REQUIERE_APROBACION'"));
        Assert.Equal(PermisoDenegado, await CodigoErrorAsync(c, "UPDATE fac.Consecutivo SET Ultimo = 0"));
        Assert.Equal(PermisoDenegado, await CodigoErrorAsync(c, "INSERT fac.CanonPlan (PlanId, VigenteDesde, CanonMensualCop, CreadoPor) VALUES (1, '2026-01-01', 1, 1)"));
    }

    [Fact]
    public async Task Las_alertas_las_ve_solo_el_superadmin()
    {
        var d = await PrepararAsync();
        await using var admin = await AbrirAsync(bd.CadenaApp, d.AdminA);
        Assert.Equal(0, await Escalar<int>(admin, "SELECT COUNT(*) FROM fac.Alerta"));
        // El filtro las oculta: marcar una alerta como atendida no afecta ninguna fila.
        Assert.Equal(0, await Ejecutar(admin, $"UPDATE fac.Alerta SET AtendidaPor = 1, AtendidaEn = SYSUTCDATETIME() WHERE EmpresaId = {d.EmpresaA}"));

        await using var su = await AbrirAsync(bd.CadenaApp, d.Super);
        Assert.True(await Escalar<int>(su, "SELECT COUNT(*) FROM fac.Alerta") >= 2);   // la de A y la global (EmpresaId NULL)
    }

    [Fact]
    public async Task Sin_contexto_no_se_ve_nada_en_fac()
    {
        await PrepararAsync();
        await using var c = await AbrirAsync(bd.CadenaApp, ContextoBd.Vacio);
        foreach (var tabla in await Tablas(c, "'fac'"))
            Assert.Equal(0, await Escalar<int>(c, $"SELECT COUNT(*) FROM {tabla}"));
    }

    [Fact]
    public async Task El_superadmin_ve_todo_y_si_puede_escribir_con_rol_app()
    {
        var d = await PrepararAsync();
        await using var c = await AbrirAsync(bd.CadenaApp, d.Super);
        Assert.True(await Escalar<int>(c, "SELECT COUNT(*) FROM fac.CuentaCobro") >= 2);

        await Ejecutar(c, $"UPDATE fac.CuentaCobro SET Observaciones = N'ok' WHERE CuentaCobroId = {d.CuentaA}");
        Assert.Equal("ok", await Escalar<string>(c, $"SELECT Observaciones FROM fac.CuentaCobro WHERE CuentaCobroId = {d.CuentaA}"));
        // Y el Admin de A ve esa observación (visible al cliente, 08 §30.3).
        await using var admin = await AbrirAsync(bd.CadenaApp, d.AdminA);
        Assert.Equal("ok", await Escalar<string>(admin, $"SELECT Observaciones FROM fac.CuentaCobro WHERE CuentaCobroId = {d.CuentaA}"));
    }

    [Fact]
    public async Task Toda_tabla_fac_con_EmpresaId_tiene_politica_y_las_alertas_solo_de_superadmin()
    {
        await PrepararAsync();
        await using var c = new SqlConnection(bd.CadenaPropietario);
        await c.OpenAsync();
        Assert.Equal(0, await Escalar<int>(c, """
            SELECT COUNT(*) FROM sys.tables o JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.columns col ON col.object_id = o.object_id AND col.name = 'EmpresaId'
            WHERE s.name = 'fac' AND NOT EXISTS (SELECT 1 FROM sys.security_predicates p WHERE p.target_object_id = o.object_id)
            """));
        // fac.Alerta: filtro y bloqueos con fn_SoloSuperadmin (ninguno con fn_FiltroEmpresa).
        Assert.Equal(0, await Escalar<int>(c, """
            SELECT COUNT(*) FROM sys.security_predicates p
            WHERE p.target_object_id = OBJECT_ID('fac.Alerta') AND p.predicate_definition NOT LIKE '%fn_SoloSuperadmin%'
            """));
        // Resto de fac con EmpresaId: lectura por empresa y escritura solo del superadmin (4 bloqueos).
        Assert.Equal(4, await Escalar<int>(c, """
            SELECT COUNT(*) FROM sys.security_predicates p
            WHERE p.target_object_id = OBJECT_ID('fac.CuentaCobro') AND p.predicate_type_desc = 'BLOCK'
            """));
    }

    [Fact]
    public async Task Una_cuenta_en_borrador_no_tiene_numero_y_una_emitida_si()
    {
        var d = await PrepararAsync();
        await using var c = new SqlConnection(bd.CadenaEtl);
        await c.OpenAsync();
        var cond = await Escalar<int>(c, $"SELECT CondicionId FROM fac.CondicionComercial WHERE EmpresaId = {d.EmpresaA}");

        Assert.Equal(ViolacionRestriccion, await CodigoErrorAsync(c,
            $"INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, Numero, Estado, DiasPago) VALUES ({d.EmpresaA}, 202602, {cond}, 'CC-000001', 'BORRADOR', 30)"));
        Assert.Equal(ViolacionRestriccion, await CodigoErrorAsync(c,
            $"INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, Estado, DiasPago) VALUES ({d.EmpresaA}, 202602, {cond}, 'EMITIDA', 30)"));
        Assert.Equal(ViolacionRestriccion, await CodigoErrorAsync(c,
            $"INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, Estado, DiasPago) VALUES ({d.EmpresaA}, 202602, {cond}, 'PAGADA', 30)"));
        // Idempotencia: un solo borrador/emitida/pagada por empresa y periodo.
        Assert.Equal(IndiceUnico, await CodigoErrorAsync(c,
            $"INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, Estado, DiasPago) VALUES ({d.EmpresaA}, 202601, {cond}, 'BORRADOR', 30)"));
        // Un número de cuenta no se repite.
        Assert.Equal(IndiceUnico, await CodigoErrorAsync(c,
            $"""
            INSERT fac.CuentaCobro (EmpresaId, PeriodoKey, CondicionId, Numero, Estado, DiasPago, FechaEmision, FechaVencimiento, ClienteNit)
            SELECT {d.EmpresaA}, 202603, {cond}, Numero, 'EMITIDA', 30, '2026-04-01', '2026-05-01', '1' FROM fac.CuentaCobro WHERE CuentaCobroId = {d.CuentaA}
            """));
    }

    [Fact]
    public async Task El_prorrateo_exige_dias_coherentes_con_el_tramo()
    {
        var d = await PrepararAsync();
        await using var c = new SqlConnection(bd.CadenaEtl);
        await c.OpenAsync();
        var cond = await Escalar<int>(c, $"SELECT CondicionId FROM fac.CondicionComercial WHERE EmpresaId = {d.EmpresaA}");
        string Linea(string desde, string hasta, int dias, int diasPeriodo, decimal valor)
            => $"""
               INSERT fac.CuentaCobroLinea (CuentaCobroId, EmpresaId, Orden, Tipo, Concepto, PeriodoServicioKey, CondicionId, FechaDesde, FechaHasta, DiasFacturados, DiasPeriodo, CanonMensualCop, ValorCop)
               VALUES ({d.CuentaA}, {d.EmpresaA}, 1, 'CANON', N'Canon', 202601, {cond}, '{desde}', '{hasta}', {dias}, {diasPeriodo}, 1000000, {valor})
               """;

        // 14 días de 31 → 451.613 (ROUND(1.000.000 × 14 / 31, 0)); fechas y días coherentes.
        Assert.Equal(1, await Ejecutar(c, Linea("2026-01-18", "2026-01-31", 14, 31, 451613)));
        // Días que no corresponden al tramo, o más días que el mes: rechazado.
        Assert.Equal(ViolacionRestriccion, await CodigoErrorAsync(c, Linea("2026-01-01", "2026-01-10", 14, 31, 1)));
        Assert.Equal(ViolacionRestriccion, await CodigoErrorAsync(c, Linea("2026-01-01", "2026-02-05", 36, 31, 1)));
        // Mismo tramo dos veces: rechazado (UQ por cuenta, tipo y fecha de inicio).
        Assert.Equal(RestriccionUnica, await CodigoErrorAsync(c, Linea("2026-01-18", "2026-01-31", 14, 31, 451613)));
    }

    [Fact]
    public async Task Las_condiciones_pueden_empezar_a_mitad_de_mes_pero_solo_una_queda_abierta()
    {
        var d = await PrepararAsync();
        await using var c = new SqlConnection(bd.CadenaEtl);
        await c.OpenAsync();
        var plan = await Escalar<int>(c, "SELECT TOP (1) PlanId FROM ia.PlanComercial");
        var su = bd.SuperadminId;

        // Cerrar la abierta a mitad de mes y abrir otra al día siguiente (alta/cambio con prorrateo).
        await Ejecutar(c, $"UPDATE fac.CondicionComercial SET VigenteHasta = '2026-03-14' WHERE EmpresaId = {d.EmpresaB} AND VigenteHasta IS NULL");
        await Ejecutar(c, $"INSERT fac.CondicionComercial (EmpresaId, VigenteDesde, PlanId, CanonPactadoCop, CreadoPor) VALUES ({d.EmpresaB}, '2026-03-15', {plan}, 2500000, {su})");
        // Una segunda abierta para la misma empresa: rechazado.
        Assert.Equal(IndiceUnico, await CodigoErrorAsync(c,
            $"INSERT fac.CondicionComercial (EmpresaId, VigenteDesde, PlanId, CreadoPor) VALUES ({d.EmpresaB}, '2026-06-01', {plan}, {su})"));
        // Hasta anterior a desde: rechazado.
        Assert.Equal(ViolacionRestriccion, await CodigoErrorAsync(c,
            $"INSERT fac.CondicionComercial (EmpresaId, VigenteDesde, VigenteHasta, PlanId, CreadoPor) VALUES ({d.EmpresaB}, '2025-05-10', '2025-05-09', {plan}, {su})"));
        // El canon de plan sí se mueve por mes completo.
        Assert.Equal(ViolacionRestriccion, await CodigoErrorAsync(c,
            $"INSERT fac.CanonPlan (PlanId, VigenteDesde, CanonMensualCop, CreadoPor) VALUES ({plan}, '2026-03-15', 1, {su})"));
    }

    [Fact]
    public async Task Un_pago_de_una_empresa_no_se_puede_aplicar_a_la_cuenta_de_otra()
    {
        var d = await PrepararAsync();
        await using var c = new SqlConnection(bd.CadenaEtl);
        await c.OpenAsync();

        // Pago de A (EmpresaId A) contra la cuenta de B: la FK compuesta lo impide.
        Assert.Equal(ViolacionRestriccion, await CodigoErrorAsync(c,
            $"INSERT fac.AplicacionPago (EmpresaId, PagoId, CuentaCobroId, ValorCop) VALUES ({d.EmpresaA}, {d.PagoA}, {d.CuentaB}, 100)"));
        Assert.Equal(ViolacionRestriccion, await CodigoErrorAsync(c,
            $"INSERT fac.AplicacionPago (EmpresaId, PagoId, CuentaCobroId, ValorCop) VALUES ({d.EmpresaB}, {d.PagoA}, {d.CuentaB}, 100)"));
        Assert.Equal(1, await Ejecutar(c, $"INSERT fac.AplicacionPago (EmpresaId, PagoId, CuentaCobroId, ValorCop) VALUES ({d.EmpresaA}, {d.PagoA}, {d.CuentaA}, 100)"));
    }

    [Fact]
    public async Task Semillas_parametros_serie_unica_y_permisos()
    {
        await PrepararAsync();
        await using var c = new SqlConnection(bd.CadenaPropietario);
        await c.OpenAsync();
        await SesionSql.AplicarAsync(c, new ContextoBd(null, null, true, false, true), CancellationToken.None);

        Assert.Equal("0", await Escalar<string>(c, "SELECT Valor FROM fac.Parametro WHERE Clave = 'REQUIERE_APROBACION'"));
        Assert.Equal("0", await Escalar<string>(c, "SELECT Valor FROM fac.Parametro WHERE Clave = 'INCLUIR_EXCEDENTE_IA'"));
        Assert.Equal("DIAS_MES", await Escalar<string>(c, "SELECT Valor FROM fac.Parametro WHERE Clave = 'PRORRATEO'"));
        Assert.Equal(1, await Escalar<int>(c, "SELECT COUNT(*) FROM fac.Consecutivo"));
        Assert.Equal(ViolacionRestriccion, await CodigoErrorAsync(c, "INSERT fac.Consecutivo (Serie) VALUES ('NC')"));   // una sola serie: CC

        var permisos = await Escalar<int>(c, """
            SELECT COUNT(*) FROM seg.RolPermiso rp JOIN seg.Rol r ON r.Id = rp.RolId
            WHERE (r.NormalizedName = 'SUPERADMIN' AND rp.Permiso IN ('fac.administrar','fac.consultar'))
               OR (r.NormalizedName = 'ADMIN_EMPRESA' AND rp.Permiso = 'fac.consultar')
            """);
        Assert.Equal(3, permisos);
        Assert.Equal(0, await Escalar<int>(c, "SELECT COUNT(*) FROM seg.RolPermiso WHERE Permiso = 'fac.administrar' AND RolId <> (SELECT Id FROM seg.Rol WHERE NormalizedName = 'SUPERADMIN')"));
    }
}
