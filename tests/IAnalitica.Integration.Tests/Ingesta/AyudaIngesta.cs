using System.Text.Json;
using Dapper;
using IAnalitica.Application;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Application.Semantica;
using IAnalitica.Infrastructure;
using IAnalitica.Infrastructure.Ingesta;
using IAnalitica.Integration.Tests.Infra;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace IAnalitica.Integration.Tests.Ingesta;

/// <summary>Resultado de procesar una carga de prueba.</summary>
public sealed record ResumenCarga(long CargaId, string Estado, int Insertadas, int Actualizadas, int Eliminadas, int Rechazadas, string? Error, List<(string? Clave, string Motivo)> Rechazos);

/// <summary>
/// Prepara y procesa cargas directamente en la BD con el login de rol_etl (sin pasar por la API): aísla la lógica de procesamiento.
/// Cada prueba usa su propia empresa, así que no hay interferencia entre pruebas.
/// </summary>
public sealed class Procesamiento : IAsyncDisposable
{
    private static int _secuencia = 700000000;
    private readonly BaseDeDatosFixture _bd;
    private readonly ServiceProvider _sp;

    public int EmpresaId { get; private set; }
    public ProcesadorCargas Procesador => _sp.GetRequiredService<ProcesadorCargas>();
    public IColaTrabajos Cola => _sp.GetRequiredService<IColaTrabajos>();
    public IAnalitica.Infrastructure.Auditoria.MotorAuditoria Auditor => _sp.GetRequiredService<IAnalitica.Infrastructure.Auditoria.MotorAuditoria>();
    /// <summary>Motor del auditor con reloj fijo (y, si se pasan, otras reglas en lugar de las del módulo).</summary>
    public IAnalitica.Infrastructure.Auditoria.MotorAuditoria AuditorEn(DateTimeOffset ahora, params IAnalitica.Domain.Auditoria.IReglaConfiguracion[] reglas)
        => new(_sp.GetRequiredService<IFabricaConexionesEtl>(), reglas.Length > 0 ? reglas : IAnalitica.Modulos.Auditor.ModuloAuditor.Reglas(),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(ahora), Microsoft.Extensions.Logging.Abstractions.NullLogger<IAnalitica.Infrastructure.Auditoria.MotorAuditoria>.Instance);
    public IAnalitica.Infrastructure.Auditoria.ValidadorPrecios ValidadorEn(DateTimeOffset ahora)
        => new(_sp.GetRequiredService<IFabricaConexionesEtl>(), new Microsoft.Extensions.Time.Testing.FakeTimeProvider(ahora),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IAnalitica.Infrastructure.Auditoria.ValidadorPrecios>.Instance);
    public DespachadorTrabajos Despachador => _sp.GetRequiredService<DespachadorTrabajos>();

    private Procesamiento(BaseDeDatosFixture bd)
    {
        _bd = bd;
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton(TimeProvider.System);
        s.AddProcesamiento(bd.CadenaEtl);
        s.AddReglasAuditor(IAnalitica.Modulos.Auditor.ModuloAuditor.Reglas());
        _sp = s.BuildServiceProvider();
    }

    public static async Task<Procesamiento> NuevaEmpresaAsync(BaseDeDatosFixture bd, params string[] modulos)
    {
        var p = new Procesamiento(bd);
        var nit = Interlocked.Increment(ref _secuencia).ToString();
        var kit = await Escenarios.CrearComoSuperadminAsync(bd, Escenarios.ComandoValido(nit, "Empresa de prueba " + nit, modulos: modulos.Length == 0 ? null : modulos));
        p.EmpresaId = kit.EmpresaId;
        return p;
    }

    /// <summary>Procesamiento para una empresa que ya existe (la creó otra parte de la prueba).</summary>
    public static Procesamiento ParaEmpresa(BaseDeDatosFixture bd, int empresaId) { var p = new Procesamiento(bd); p.EmpresaId = empresaId; return p; }

    public async Task<SqlConnection> EtlAsync()
    {
        var c = new SqlConnection(_bd.CadenaEtl);
        await c.OpenAsync();
        return c;
    }

    public async Task<T> EscalarAsync<T>(string sql, object? p = null)
    {
        await using var c = await EtlAsync();
        return await c.ExecuteScalarAsync<T>(sql, p) ?? default!;
    }

    /// <summary>Consulta con @e = la empresa de la prueba.</summary>
    public async Task<List<T>> ConsultarAsync<T>(string sql)
    {
        await using var c = await EtlAsync();
        return (await c.QueryAsync<T>(sql, new { e = EmpresaId })).ToList();
    }

    public async Task EjecutarAsync(string sql, object? p = null)
    {
        await using var c = await EtlAsync();
        await c.ExecuteAsync(sql, p);
    }

    /// <summary>Crea una carga CERRADA con las filas repartidas en lotes de <paramref name="porLote"/>.</summary>
    public async Task<long> CrearCargaAsync(string entidad, string modo, IEnumerable<object> filas, DateOnly? desde = null, DateOnly? hasta = null, int porLote = 5000)
    {
        await using var c = await EtlAsync();
        var id = await c.ExecuteScalarAsync<long>("""
            INSERT etl.Carga (EmpresaId, Entidad, Modo, VentanaDesde, VentanaHasta, Corte, Origen, Estado, CerradaEn)
            OUTPUT inserted.CargaId VALUES (@e, @entidad, @modo, @d, @h, @corte, 'prueba', 'CERRADA', SYSUTCDATETIME())
            """, new { e = EmpresaId, entidad, modo, d = desde?.ToDateTime(TimeOnly.MinValue), h = hasta?.ToDateTime(TimeOnly.MinValue), corte = modo == "FOTO" ? DateTime.UtcNow.Date : (DateTime?)null });
        var lista = filas.ToList();
        var n = 1;
        foreach (var lote in lista.Chunk(porLote))
        {
            await c.ExecuteAsync("INSERT stg.Payload (CargaId, EmpresaId, Entidad, NumeroLote, Filas, Json) VALUES (@id, @e, @entidad, @n, @f, @j)",
                new { id, e = EmpresaId, entidad, n = n++, f = lote.Length, j = JsonSerializer.Serialize(lote) });
        }
        return id;
    }

    public async Task<ResumenCarga> ProcesarAsync(string entidad, string modo, IEnumerable<object> filas, DateOnly? desde = null, DateOnly? hasta = null, int porLote = 5000)
    {
        var id = await CrearCargaAsync(entidad, modo, filas, desde, hasta, porLote);
        await Procesador.ProcesarAsync(id, ultimoIntento: true, CancellationToken.None);
        return await ResumenAsync(id);
    }

    public async Task<ResumenCarga> ResumenAsync(long id)
    {
        await using var c = await EtlAsync();
        var r = await c.QuerySingleAsync("""
            SELECT Estado, ISNULL(FilasInsertadas,0) Ins, ISNULL(FilasActualizadas,0) Upd, ISNULL(FilasEliminadas,0) Del, ISNULL(FilasRechazadas,0) Rech, Error
            FROM etl.Carga WHERE CargaId = @id
            """, new { id });
        var rechazos = (await c.QueryAsync<(string? Clave, string Motivo)>("SELECT SourceKey, Motivo FROM etl.Rechazo WHERE CargaId = @id ORDER BY RechazoId", new { id })).ToList();
        return new ResumenCarga(id, r.Estado, r.Ins, r.Upd, r.Del, r.Rech, r.Error, rechazos);
    }

    public Task<int> ContarAsync(string tabla, string? donde = null)
        => EscalarAsync<int>($"SELECT COUNT(*) FROM {tabla} WHERE EmpresaId = @e {(donde is null ? "" : "AND " + donde)}", new { e = EmpresaId });

    public async ValueTask DisposeAsync() => await _sp.DisposeAsync();

    // ---------- Catálogos mínimos para los hechos ----------
    public async Task CargarCatalogosBaseAsync()
    {
        await ProcesarAsync("sedes", "COMPLETA", new object[] { new { codigoOrigen = "1", nombre = "Sede principal", activa = true } });
        await ProcesarAsync("tipos-comprobante", "COMPLETA", new object[] { new { codigoOrigen = "T1", prefijo = "CSI0", nombre = "Comprobante" } });
        await ProcesarAsync("cuentas", "COMPLETA", new object[]
        {
            new { codigo = "130505", nombre = "Clientes", nivel = 6, naturaleza = "D", detalleCartera = 2, aceptaMovimiento = true },
            new { codigo = "413501", nombre = "Ingresos", nivel = 6, naturaleza = "C", aceptaMovimiento = true },
            new { codigo = "111005", nombre = "Bancos", nivel = 6, naturaleza = "D", esBanco = true, aceptaMovimiento = true },
            new { codigo = "233500", nombre = "Proveedores", nivel = 6, naturaleza = "C", detalleCartera = 1, aceptaMovimiento = true },
        });
        await ProcesarAsync("terceros", "INCREMENTAL", new object[]
        {
            new { codigoOrigen = "P1", identificacion = "900100100", esPaciente = false, nombre = "EPS Uno", tipoPersona = "J" },
            new { codigoOrigen = "P2", identificacion = "900200200", esPaciente = false, nombre = "EPS Dos", tipoPersona = "J" },
        });
        await ProcesarAsync("pagadores", "COMPLETA", new object[]
        {
            new { codigoOrigen = "E1", nombre = "EPS Uno", terceroCodigo = "P1", esParticular = false, activo = true },
            new { codigoOrigen = "E2", nombre = "EPS Dos", terceroCodigo = "P2", esParticular = false, activo = true },
        });
    }

    public static object Linea(string key, string mov, string fecha, string cuenta, decimal debito, decimal credito, string? tercero = null,
        string? rel = null, string? numDoc = null, int estado = 1, string tipo = "T1", string? fechaCartera = null)
        => new Dictionary<string, object?>
        {
            ["sourceKey"] = key, ["movimientoSourceKey"] = mov, ["fecha"] = fecha, ["tipoComprobanteCodigo"] = tipo, ["numeroComprobante"] = "N-" + mov,
            ["cuentaCodigo"] = cuenta, ["debito"] = debito, ["credito"] = credito, ["estado"] = estado, ["terceroCodigo"] = tercero,
            ["relacionadoSourceKey"] = rel, ["numDocumento"] = numDoc, ["fechaCartera"] = fechaCartera, ["sedeCodigo"] = "1",
        }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);

    public static object Factura(string key, string doc, string fecha, string pagador, decimal neto, bool anulada = false, string sede = "1",
        int? estadoFe = null, string? fechaRips = null, string? usuario = null)
        => new Dictionary<string, object?>
        {
            ["sourceKey"] = key, ["numeroDocumento"] = doc, ["fechaEmision"] = fecha, ["pagadorCodigo"] = pagador, ["sedeCodigo"] = sede,
            ["cantidad"] = 1, ["valorServicio"] = neto, ["valorNeto"] = neto, ["anulada"] = anulada,
            ["estadoFE"] = estadoFe, ["fechaEnvioRips"] = fechaRips, ["usuarioCodigo"] = usuario,
        }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);
}

/// <summary>El cableado del worker real (cargas, cartera y reportes) para pruebas que necesitan procesar trabajos.</summary>
public static class WorkerDePrueba
{
    public static ServiceProvider Construir(BaseDeDatosFixture bd, string rutaReportes)
    {
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton(NSubstitute.Substitute.For<IAnalitica.Application.Abstracciones.IConfiguracionPortal>());
        s.AddInfrastructure(bd.CadenaApp);
        s.AddApplication(typeof(IAnalitica.Application.Empresas.CrearEmpresaCommand).Assembly);
        s.AddCapaSemantica(new[] { IAnalitica.Modulos.Financiero.ModuloFinanciero.Modulo }.Concat(IAnalitica.Modulos.Ingreso.ModuloIngreso.Modulos));
        s.AddProcesamiento(bd.CadenaEtl);
        s.AddReglasAuditor(IAnalitica.Modulos.Auditor.ModuloAuditor.Reglas());
        s.AddGeneracionReportes();
        s.Configure<IAnalitica.Infrastructure.Reportes.OpcionesAlmacenReportes>(o => o.Ruta = rutaReportes);
        return s.BuildServiceProvider();
    }
}
