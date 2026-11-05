using System.Text.Json;
using Dapper;
using IAnalitica.Application.Abstracciones;
using IAnalitica.Infrastructure.Ingesta;
using Microsoft.Extensions.Logging;

namespace IAnalitica.Infrastructure.Facturacion;

/// <summary>
/// REVISAR_MORA (08 §32.4, §33.6). Cada día encola recordatorios previos al vencimiento y avisos de mora de las cuentas EMITIDA con saldo, sin adjunto
/// y con enlace al portal, y deja una notificación (campana) a los administradores de la empresa. Es idempotente por hito
/// (<c>UX_FacEnvio_Hito</c>): una cuenta recibe cada hito una sola vez. La mora corre desde el día siguiente al vencimiento; la suspensión es manual.
/// </summary>
public sealed class RevisorMora(IFabricaConexionesEtl etl, EnviosFacturacion envios, TimeProvider reloj, ILogger<RevisorMora> log)
{
    private sealed record FilaPendiente(int CuentaCobroId, int EmpresaId, string Numero, DateTime FechaVencimiento);
    private sealed record FilaParametro(string Clave, string Valor);
    private sealed record FilaHito(int CuentaCobroId, string Hito);

    /// <returns>Envíos encolados.</returns>
    public async Task<int> EjecutarAsync(CancellationToken ct)
    {
        var hoy = DateOnly.FromDateTime(reloj.GetUtcNow().UtcDateTime.AddHours(-5));   // America/Bogota
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        var p = (await cn.QueryAsync<FilaParametro>(new CommandDefinition(
            "SELECT Clave, Valor FROM fac.Parametro WHERE Clave IN ('RECORDATORIO_DIAS_ANTES', 'AVISOS_MORA_DIAS')", cancellationToken: ct)).ConfigureAwait(false)).ToDictionary(x => x.Clave, x => x.Valor);
        var diasAntes = int.TryParse(p.GetValueOrDefault("RECORDATORIO_DIAS_ANTES"), out var a) ? a : 3;
        var avisos = (p.GetValueOrDefault("AVISOS_MORA_DIAS") ?? "1,15,30").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).Distinct().OrderBy(n => n).ToList();

        var cuentas = (await cn.QueryAsync<FilaPendiente>(new CommandDefinition(
            "SELECT CuentaCobroId, EmpresaId, Numero, FechaVencimiento FROM fac.CuentaCobro WHERE Estado = 'EMITIDA' AND TotalCop - PagadoCop > 0", cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var hitos = (await cn.QueryAsync<FilaHito>(new CommandDefinition(
            "SELECT CuentaCobroId, Hito FROM fac.Envio WHERE Hito IS NOT NULL AND Estado <> 'CANCELADO'", cancellationToken: ct)).ConfigureAwait(false))
            .GroupBy(x => x.CuentaCobroId).ToDictionary(g => g.Key, g => g.Select(x => x.Hito).ToHashSet());

        var encolados = 0;
        foreach (var c in cuentas)
        {
            var vence = DateOnly.FromDateTime(c.FechaVencimiento);
            var ya = hitos.GetValueOrDefault(c.CuentaCobroId) ?? [];
            var dias = hoy.DayNumber - vence.DayNumber;   // > 0 = en mora (desde el día siguiente al vencimiento)

            if (dias <= 0 && -dias <= diasAntes && !ya.Contains($"PREVIO_{diasAntes}"))
            {
                await envios.EncolarEnvioAsync(c.CuentaCobroId, "RECORDATORIO", $"PREVIO_{diasAntes}", esReenvio: false, usuarioId: null, ct).ConfigureAwait(false);
                encolados++;
            }
            else if (dias > 0)
            {
                // Solo el aviso más alto alcanzado que aún no se envió (si el worker estuvo caído no se mandan varios de golpe).
                var umbral = avisos.Where(n => dias >= n).OrderByDescending(n => n).FirstOrDefault();
                if (umbral > 0 && !ya.Any(h => h.StartsWith("MORA_", StringComparison.Ordinal) && int.Parse(h.AsSpan(5)) >= umbral))
                {
                    await envios.EncolarEnvioAsync(c.CuentaCobroId, "AVISO_MORA", $"MORA_{umbral}", esReenvio: false, usuarioId: null, ct).ConfigureAwait(false);
                    await cn.ExecuteAsync(new CommandDefinition(
                        "INSERT seg.Notificacion (EmpresaId, UsuarioId, Tipo, Titulo, Enlace) VALUES (@e, NULL, 'FACTURACION_MORA', @titulo, @enlace)",
                        new { e = c.EmpresaId, titulo = $"La cuenta de cobro {c.Numero} está vencida", enlace = $"/facturacion/{c.CuentaCobroId}" }, cancellationToken: ct)).ConfigureAwait(false);
                    encolados++;
                }
            }
        }
        if (encolados > 0) log.LogInformation("Mora: {N} envíos encolados", encolados);
        return encolados;
    }
}

internal sealed class ManejadorRevisarMora(RevisorMora mora) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.RevisarMora;
    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        await mora.EjecutarAsync(ct).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
}

/// <summary>ENCOLAR_ENVIO: la aplicación (rol_app) pide un reenvío o el aviso de anulación; el worker (rol_etl) crea el envío y su correo.</summary>
internal sealed class ManejadorEncolarEnvio(EnviosFacturacion envios) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.EncolarEnvio;
    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(trabajo.Payload ?? throw new InvalidOperationException("El trabajo no trae payload."));
        var r = doc.RootElement;
        await envios.EncolarEnvioAsync(r.GetProperty("cuentaCobroId").GetInt32(), r.GetProperty("tipo").GetString()!, null,
            r.GetProperty("esReenvio").GetBoolean(), trabajo.UsuarioId, ct).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
}

/// <summary>REGENERAR_BORRADOR: recalcula un borrador retenido (p. ej. tras definir el canon o agregar un contacto de facturación).</summary>
internal sealed class ManejadorRegenerarBorrador(GeneradorCuentas generador) : IManejadorTrabajo
{
    public string Tipo => TiposTrabajo.RegenerarBorrador;
    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(TrabajoTomado trabajo, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(trabajo.Payload ?? throw new InvalidOperationException("El trabajo no trae payload."));
        await generador.RegenerarBorradorAsync(doc.RootElement.GetProperty("cuentaCobroId").GetInt32(), ct).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
}
