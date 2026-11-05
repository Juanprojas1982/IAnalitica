using System.Text.Json;
using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Domain.Facturacion;
using IAnalitica.Infrastructure.Ingesta;
using Microsoft.Extensions.Logging;

namespace IAnalitica.Infrastructure.Facturacion;

internal sealed record Parametro(string Clave, string Valor);

/// <summary>
/// Programador de facturación (08 §32.4). Cada llamada revisa la hora de Bogotá (UTC-5) y encola lo que ya venció y no se ha hecho:
/// el día <c>DIA_PREVALIDACION</c> a las 08:00 la prevalidación del mes en curso, y el día 1 a <c>HORA_GENERACION</c> la generación del mes vencido, y cada día a las 07:30 el resumen de alertas.
/// La idempotencia real está en <c>fac.TareaProgramada</c> (no repite) y en <c>UX_FacCC_Periodo</c>; si el worker estuvo caído, se pone al día
/// en la llamada siguiente (mejor tarde que nunca). Se llama cada pocos minutos desde el worker.
/// </summary>
public sealed class PlanificadorFacturacion(IFabricaConexionesEtl etl, IColaTrabajos cola, TimeProvider reloj, ILogger<PlanificadorFacturacion> log)
{
    private static readonly TimeSpan HoraPrevalidacion = TimeSpan.FromHours(8);
    private static readonly TimeSpan HoraResumen = new(7, 30, 0);

    /// <returns>Trabajos encolados en esta llamada.</returns>
    public async Task<int> EjecutarAsync(CancellationToken ct)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime.AddHours(-5);   // America/Bogota = UTC-5 todo el año
        var mes = ahora.Year * 100 + ahora.Month;
        var anterior = Prorrateo.PeriodoAnterior(mes);

        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        var parametros = (await cn.QueryAsync<Parametro>(new CommandDefinition(
            "SELECT Clave, Valor FROM fac.Parametro WHERE Clave IN ('DIA_PREVALIDACION', 'HORA_GENERACION')", cancellationToken: ct)).ConfigureAwait(false)).ToDictionary(x => x.Clave, x => x.Valor);
        var diaPre = int.TryParse(parametros.GetValueOrDefault("DIA_PREVALIDACION"), out var d) ? d : 25;
        var horaGen = TimeSpan.TryParse(parametros.GetValueOrDefault("HORA_GENERACION"), out var h) ? h : TimeSpan.FromHours(6);

        var encolados = 0;
        // Prevalidación del mes en curso, desde el día configurado a las 08:00.
        if (ahora.Day > diaPre || (ahora.Day == diaPre && ahora.TimeOfDay >= HoraPrevalidacion))
            encolados += await UnaVezAsync(cn, $"FAC:PRE|PER:{mes}", TiposTrabajo.PrevalidarFacturacion, mes, ct).ConfigureAwait(false);
        // Resumen diario de alertas abiertas para el prestador (07:30); el manejador no envía nada si no hay alertas.
        if (ahora.TimeOfDay >= HoraResumen)
            encolados += await UnaVezAsync(cn, $"FAC:RES|DIA:{ahora:yyyyMMdd}", TiposTrabajo.ResumenAlertas, mes, ct).ConfigureAwait(false);
        // Generación del mes vencido: el día 1 desde la hora configurada; si se perdió, en cualquier momento posterior del mes.
        if (ahora.Day > 1 || ahora.TimeOfDay >= horaGen)
            encolados += await UnaVezAsync(cn, $"FAC:GEN|PER:{anterior}", TiposTrabajo.GenerarCuentasCobro, anterior, ct).ConfigureAwait(false);
        return encolados;
    }

    private async Task<int> UnaVezAsync(Microsoft.Data.SqlClient.SqlConnection cn, string clave, string tipo, int periodo, CancellationToken ct)
    {
        if (await cn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM fac.TareaProgramada WHERE Clave = @clave", new { clave }, cancellationToken: ct)).ConfigureAwait(false) > 0)
            return 0;
        // Primero se encola y después se marca: si el marcado falla, la próxima llamada reintenta (los trabajos son idempotentes).
        await cola.EncolarAsync(new TrabajoPendiente(tipo, null, clave, JsonSerializer.Serialize(new { periodo }), Prioridad: 3), ct).ConfigureAwait(false);
        await cn.ExecuteAsync(new CommandDefinition(
            "IF NOT EXISTS (SELECT 1 FROM fac.TareaProgramada WHERE Clave = @clave) INSERT fac.TareaProgramada (Clave) VALUES (@clave)", new { clave }, cancellationToken: ct)).ConfigureAwait(false);
        log.LogInformation("Facturación: encolado {Tipo} del periodo {Periodo}", tipo, periodo);
        return 1;
    }
}
