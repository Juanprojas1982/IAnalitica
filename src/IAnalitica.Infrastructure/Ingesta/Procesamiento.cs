using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure.Observabilidad;
using IAnalitica.Ingesta.Contracts;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace IAnalitica.Infrastructure.Ingesta;

/// <summary>Conexiones del worker con el login de <c>rol_etl</c> (sin RLS, 24 §4.2). Nunca las usan el portal ni la API.</summary>
public interface IFabricaConexionesEtl
{
    Task<SqlConnection> AbrirAsync(CancellationToken ct);
}

internal sealed class FabricaConexionesEtl(string cadena) : IFabricaConexionesEtl
{
    public async Task<SqlConnection> AbrirAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(cadena);
        try { await cn.OpenAsync(ct).ConfigureAwait(false); return cn; }
        catch { await cn.DisposeAsync().ConfigureAwait(false); throw; }
    }
}

public enum ResultadoTrabajo { Completado, Postergado }

public interface IManejadorTrabajo
{
    string Tipo { get; }
    Task<(ResultadoTrabajo Resultado, TimeSpan Espera)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct);
}

/// <summary>
/// Toma un trabajo de la cola, lo ejecuta con la traza del conector como padre y lo completa, lo posterga o lo falla (reintento/VENENO).
/// El worker lo invoca en bucle con un tope de concurrencia (24 §8); las pruebas lo invocan directamente.
/// </summary>
public sealed class DespachadorTrabajos(IColaTrabajos cola, IEnumerable<IManejadorTrabajo> manejadores, ILogger<DespachadorTrabajos> log)
{
    private static readonly ActivitySource Fuente = new(ObservabilidadExtensions.FuenteWorker);
    private readonly Dictionary<string, IManejadorTrabajo> _manejadores = manejadores.ToDictionary(m => m.Tipo);

    public IReadOnlyCollection<string> Tipos => _manejadores.Keys;

    /// <returns>true si había un trabajo (aunque fallara); false si la cola de ese tipo está vacía.</returns>
    public async Task<bool> EjecutarUnoAsync(string tipo, CancellationToken ct)
    {
        var trabajo = await cola.TomarSiguienteAsync(tipo, ct).ConfigureAwait(false);
        if (trabajo is null) return false;

        ActivityContext.TryParse(trabajo.TraceParent, null, out var padre);
        using var actividad = Fuente.StartActivity($"trabajo {trabajo.Tipo}", ActivityKind.Consumer, padre);
        actividad?.SetTag("trabajo.id", trabajo.TrabajoId);
        actividad?.SetTag("empresa.id", trabajo.EmpresaId);
        try
        {
            var (resultado, espera) = await _manejadores[trabajo.Tipo].EjecutarAsync(trabajo, ct).ConfigureAwait(false);
            if (resultado == ResultadoTrabajo.Postergado) await cola.PostergarAsync(trabajo.TrabajoId, espera, ct).ConfigureAwait(false);
            else await cola.CompletarAsync(trabajo.TrabajoId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // apagado ordenado: el lease devuelve el trabajo a la cola
        }
        catch (Exception e)
        {
            actividad?.SetStatus(ActivityStatusCode.Error);
            log.LogError(e, "Falló el trabajo {Tipo} {TrabajoId} (intento {Intento}/{Max})", trabajo.Tipo, trabajo.TrabajoId, trabajo.Intentos, trabajo.MaxIntentos);
            await cola.FallarAsync(trabajo.TrabajoId, Descripcion(e), CancellationToken.None).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Texto seguro para guardar: tipo y número de error, nunca valores de las filas.</summary>
    internal static string Descripcion(Exception e)
        => e is SqlException s ? $"SQL {s.Number} (línea {s.LineNumber}, {s.Procedure ?? "lote"})" : e.GetType().Name;
}

internal sealed class ManejadorProcesarCarga(ProcesadorCargas procesador) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.ProcesarCarga;

    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(trabajo.Payload ?? throw new InvalidOperationException("El trabajo no trae payload."));
        var cargaId = doc.RootElement.GetProperty("cargaId").GetInt64();
        return await procesador.ProcesarAsync(cargaId, trabajo.EsUltimoIntento, ct).ConfigureAwait(false)
            ? (ResultadoTrabajo.Completado, TimeSpan.Zero)
            : (ResultadoTrabajo.Postergado, TimeSpan.FromSeconds(30));
    }
}

internal sealed class ManejadorCalcularCartera(IFabricaConexionesEtl etl) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.CalcularCartera;

    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(trabajo.Payload ?? throw new InvalidOperationException("El trabajo no trae payload."));
        var corte = DateTime.ParseExact(doc.RootElement.GetProperty("corte").GetString()!, "yyyy-MM-dd", null);
        long? cargaId = doc.RootElement.TryGetProperty("cargaId", out var c) ? c.GetInt64() : null;
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition("etl.usp_GenerarCorteCartera",
            new { EmpresaId = trabajo.EmpresaId ?? throw new InvalidOperationException("Falta la empresa."), Corte = corte, CargaId = cargaId },
            commandType: CommandType.StoredProcedure, commandTimeout: 3600, cancellationToken: ct)).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
}

/// <summary>CALCULAR_COSTOS: bases de costo y distribución escalonada de un periodo (spec 24 §5.4). Falla si la distribución no conserva el costo total.</summary>
internal sealed class ManejadorCalcularCostos(IFabricaConexionesEtl etl) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.CalcularCostos;

    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(trabajo.Payload ?? throw new InvalidOperationException("El trabajo no trae payload."));
        var periodo = doc.RootElement.GetProperty("periodo").GetInt32();
        var empresaId = trabajo.EmpresaId ?? throw new InvalidOperationException("Falta la empresa.");
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        foreach (var procedimiento in new[] { "etl.usp_CalcularBasesCosto", "etl.usp_DistribuirCostos" })
            await cn.ExecuteAsync(new CommandDefinition(procedimiento, new { EmpresaId = empresaId, PeriodoKey = periodo }, commandType: CommandType.StoredProcedure, commandTimeout: 3600, cancellationToken: ct)).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
}

/// <summary>
/// PROCESAR_CARGA (24 §8): aplica una carga CERRADA a las dimensiones o hechos. Atómico por carga; idempotente (reintentar una carga
/// a medias repite el mismo upsert/reemplazo de ventana). Una sola carga por empresa y entidad a la vez (applock).
/// </summary>
public sealed class ProcesadorCargas(IFabricaConexionesEtl etl, IColaTrabajos cola, TimeProvider reloj)
{
    /// <returns>false si otra carga de la misma empresa y entidad está en curso (el trabajo se pospone sin gastar intento).</returns>
    public async Task<bool> ProcesarAsync(long cargaId, bool ultimoIntento, CancellationToken ct)
    {
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        var carga = await cn.QuerySingleOrDefaultAsync<FilaProceso>(new CommandDefinition(
            "SELECT EmpresaId, Entidad, Modo, Estado, VentanaDesde AS Desde, VentanaHasta AS Hasta FROM etl.Carga WHERE CargaId = @cargaId", new { cargaId }, cancellationToken: ct)).ConfigureAwait(false);
        if (carga is null) throw new InvalidOperationException($"La carga {cargaId} no existe.");
        var (empresaId, entidad, modo, estado) = (carga.EmpresaId, carga.Entidad, carga.Modo, carga.Estado);
        if (estado is not (EstadosCarga.Cerrada or EstadosCarga.Procesando)) return true;   // ya terminada o rechazada: nada que hacer

        var recurso = $"carga:{empresaId}:{entidad}";
        var bloqueo = await cn.ExecuteScalarAsync<int>(new CommandDefinition(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @recurso, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;",
            new { recurso }, cancellationToken: ct)).ConfigureAwait(false);
        if (bloqueo < 0) return false;
        try
        {
            await cn.ExecuteAsync(new CommandDefinition("UPDATE etl.Carga SET Estado = 'PROCESANDO' WHERE CargaId = @cargaId", new { cargaId }, cancellationToken: ct)).ConfigureAwait(false);
            try
            {
                var (ins, upd, del, rech) = await AplicarAsync(cn, cargaId, empresaId, entidad, modo, ct).ConfigureAwait(false);

                if (entidad == "configuracion")   // la foto de configuración dispara la auditoría y el validador de precios (spec 20 §2)
                {
                    await cola.EncolarAsync(new TrabajoPendiente(TiposTrabajo.AuditarConfig, empresaId, $"EMP:{empresaId}|AUDITOR", $"{{\"cargaId\":{cargaId}}}", Prioridad: 6), ct).ConfigureAwait(false);
                    await cola.EncolarAsync(new TrabajoPendiente(TiposTrabajo.ValidarPrecios, empresaId, $"EMP:{empresaId}|PRECIOS", $"{{\"cargaId\":{cargaId}}}", Prioridad: 6), ct).ConfigureAwait(false);
                }
                if (entidad is "asientos" or "facturas" && carga.Desde is { } desde && carga.Hasta is { } hasta)   // el costo directo y la base INGRESO cambian con la ventana
                    await EncolarCostosAsync(cn, empresaId, desde, hasta, ct).ConfigureAwait(false);
                if (entidad == "asientos")   // la cartera se recalcula después de cargar contabilidad (contrato 05 §3)
                {
                    var corte = reloj.GetUtcNow().UtcDateTime.AddHours(-5).Date;   // Colombia: UTC-5 todo el año
                    await cola.EncolarAsync(new TrabajoPendiente(TiposTrabajo.CalcularCartera, empresaId, $"EMP:{empresaId}|CARTERA",
                        $"{{\"corte\":\"{corte:yyyy-MM-dd}\",\"cargaId\":{cargaId}}}", Prioridad: 7), ct).ConfigureAwait(false);
                }

                await cn.ExecuteAsync(new CommandDefinition("""
                    UPDATE etl.Carga SET Estado = 'OK', FilasInsertadas = @ins, FilasActualizadas = @upd, FilasEliminadas = @del, FilasRechazadas = @rech,
                           FinalizadaEn = SYSUTCDATETIME(), Error = NULL WHERE CargaId = @cargaId;
                    DELETE stg.Payload WHERE CargaId = @cargaId;
                    """, new { cargaId, ins, upd, del, rech }, cancellationToken: ct)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Último intento: la carga queda en ERROR (el staging se conserva para diagnóstico). Antes, vuelve a CERRADA.
                await cn.ExecuteAsync(new CommandDefinition(
                    "UPDATE etl.Carga SET Estado = @estado, Error = @error, FinalizadaEn = CASE WHEN @estado = 'ERROR' THEN SYSUTCDATETIME() END WHERE CargaId = @cargaId",
                    new { cargaId, estado = ultimoIntento ? EstadosCarga.Error : EstadosCarga.Cerrada, error = DespachadorTrabajos.Descripcion(e) }, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            await cn.ExecuteAsync(new CommandDefinition("EXEC sp_releaseapplock @Resource = @recurso, @LockOwner = 'Session'", new { recurso }, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Máximo de periodos que una carga puede disparar (una carga INICIAL puede abarcar años).</summary>
    private const int MaxPeriodosCosto = 36;

    /// <summary>Un cálculo de costos por cada mes de la ventana (los últimos 36 como máximo), solo si la empresa ya definió reglas de distribución.</summary>
    private async Task EncolarCostosAsync(SqlConnection cn, int empresaId, DateTime desde, DateTime hasta, CancellationToken ct)
    {
        if (!await cn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT CASE WHEN EXISTS (SELECT 1 FROM cfg.CostoRegla WHERE EmpresaId = @empresaId) THEN 1 ELSE 0 END", new { empresaId }, cancellationToken: ct)).ConfigureAwait(false)) return;
        var primero = new DateTime(desde.Year, desde.Month, 1);
        var ultimo = new DateTime(hasta.Year, hasta.Month, 1);
        if (((ultimo.Year - primero.Year) * 12 + ultimo.Month - primero.Month) >= MaxPeriodosCosto) primero = ultimo.AddMonths(-(MaxPeriodosCosto - 1));
        for (var mes = primero; mes <= ultimo; mes = mes.AddMonths(1))
            await cola.EncolarAsync(new TrabajoPendiente(TiposTrabajo.CalcularCostos, empresaId, $"EMP:{empresaId}|COSTOS|{mes:yyyyMM}", $"{{\"periodo\":{mes:yyyyMM}}}", Prioridad: 8), ct).ConfigureAwait(false);
    }

    private sealed record FilaProceso(int EmpresaId, string Entidad, string Modo, string Estado, DateTime? Desde, DateTime? Hasta);

    private static async Task<(int Ins, int Upd, int Del, int Rech)> AplicarAsync(SqlConnection cn, long cargaId, int empresaId, string entidad, string modo, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("CargaId", cargaId);
        p.Add("Ins", dbType: DbType.Int32, direction: ParameterDirection.Output);
        p.Add("Upd", dbType: DbType.Int32, direction: ParameterDirection.Output);
        p.Add("Del", dbType: DbType.Int32, direction: ParameterDirection.Output);
        p.Add("Rech", dbType: DbType.Int32, direction: ParameterDirection.Output);

        switch (entidad)
        {
            case "configuracion":
                var (insConf, rechConf) = await ProcesadorConfiguracion.AplicarAsync(cn, cargaId, empresaId, ct).ConfigureAwait(false);
                return (insConf, 0, 0, rechConf);
            case "asientos":
                await cn.ExecuteAsync(new CommandDefinition("etl.usp_ProcesarAsientos", p, commandType: CommandType.StoredProcedure, commandTimeout: 3600, cancellationToken: ct)).ConfigureAwait(false);
                break;
            case "facturas":
                await cn.ExecuteAsync(new CommandDefinition("etl.usp_ProcesarFacturas", p, commandType: CommandType.StoredProcedure, commandTimeout: 3600, cancellationToken: ct)).ConfigureAwait(false);
                break;
            default:
                var def = DefinicionesCatalogo.Buscar(entidad) ?? throw new InvalidOperationException($"Entidad sin procesador: {entidad}");
                p.Add("Emp", empresaId);
                p.Add("Modo", modo);
                await cn.ExecuteAsync(new CommandDefinition(GeneradorCatalogoSql.Generar(def), p, commandTimeout: 3600, cancellationToken: ct)).ConfigureAwait(false);
                break;
        }
        return (p.Get<int>("Ins"), p.Get<int>("Upd"), p.Get<int>("Del"), p.Get<int>("Rech"));
    }
}
