using System.Data;
using System.Text.Json;
using Dapper;
using IAnalitica.Domain.Auditoria;
using IAnalitica.Infrastructure.Ingesta;
using Microsoft.Extensions.Logging;

namespace IAnalitica.Infrastructure.Auditoria;

public sealed record ResumenAuditoria(int ReglasEvaluadas, int ReglasFallidas, int Nuevos, int Corregidos, int Abiertos, int IndiceTotal, IReadOnlyList<string> Errores);

/// <summary>
/// Motor del auditor (spec 16 §4). Corre con rol_etl (sin RLS): cada consulta de regla filtra por @EmpresaId y el motor siempre pasa la empresa del trabajo.
/// Ciclo del hallazgo: NUEVO (primera detección) → ABIERTO (sigue en la siguiente evaluación) → CORREGIDO (dejó de detectarse, automático);
/// ACEPTADO y FALSO_POSITIVO los pone una persona y suprimen el hallazgo (no se vuelve a crear). Si una regla falla no se toca lo que ya había de ella.
/// </summary>
public sealed class MotorAuditoria(IFabricaConexionesEtl etl, IEnumerable<IReglaConfiguracion> reglas, TimeProvider reloj, ILogger<MotorAuditoria> log)
{
    private sealed record Meta(string Codigo, string SeveridadBase, string? UmbralesJson, bool Activa);
    private sealed record Ajuste(string Codigo, bool Activa, string? SeveridadAjustada, string? UmbralesJson);
    private sealed record Existente(long HallazgoId, string ClaveEntidad, string Estado);

    public async Task<ResumenAuditoria> EjecutarAsync(int empresaId, CancellationToken ct)
    {
        var ahora = reloj.GetUtcNow();
        var hoy = DateOnly.FromDateTime(ahora.UtcDateTime.AddHours(-5));   // Colombia: UTC-5 todo el año
        await using var cn = await etl.AbrirAsync(ct).ConfigureAwait(false);
        var metas = (await cn.QueryAsync<Meta>(new CommandDefinition("SELECT Codigo, SeveridadBase, UmbralesJson, Activa FROM cfg.ReglaConfiguracion", cancellationToken: ct)).ConfigureAwait(false)).ToDictionary(m => m.Codigo);
        var ajustes = (await cn.QueryAsync<Ajuste>(new CommandDefinition("SELECT Codigo, Activa, SeveridadAjustada, UmbralesJson FROM cfg.ReglaConfiguracionEmpresa WHERE EmpresaId = @empresaId", new { empresaId }, cancellationToken: ct)).ConfigureAwait(false)).ToDictionary(a => a.Codigo);

        int evaluadas = 0, fallidas = 0, nuevos = 0, corregidos = 0;
        var errores = new List<string>();
        foreach (var regla in reglas)
        {
            ct.ThrowIfCancellationRequested();
            if (!metas.TryGetValue(regla.Codigo, out var meta) || !meta.Activa) continue;
            ajustes.TryGetValue(regla.Codigo, out var aj);
            if (aj is { Activa: false }) continue;

            var severidadRegla = Severidades.DeTexto(aj?.SeveridadAjustada) ?? Severidades.DeTexto(meta.SeveridadBase) ?? regla.SeveridadBase;
            var ctx = new ContextoAuditoria(empresaId, hoy, Umbrales(meta.UmbralesJson, aj?.UmbralesJson), new ConsultasEtl(cn, ct));
            IReadOnlyList<HallazgoDetectado> detectados;
            try
            {
                detectados = await regla.EvaluarAsync(ctx, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                fallidas++;
                errores.Add($"{regla.Codigo}: {e.Message}");
                log.LogError(e, "La regla {Regla} falló para la empresa {Empresa}; se conservan sus hallazgos.", regla.Codigo, empresaId);
                continue;
            }
            evaluadas++;
            var (n, c) = await SincronizarAsync(cn, empresaId, regla.Codigo, severidadRegla, aj?.SeveridadAjustada is not null, detectados, ahora.UtcDateTime, ct).ConfigureAwait(false);
            nuevos += n; corregidos += c;
        }
        var (abiertos, indice) = await GuardarIndiceAsync(cn, empresaId, hoy, ct).ConfigureAwait(false);
        return new ResumenAuditoria(evaluadas, fallidas, nuevos, corregidos, abiertos, indice, errores);
    }

    private static Dictionary<string, decimal> Umbrales(string? baseJson, string? empresaJson)
    {
        var r = new Dictionary<string, decimal>();
        foreach (var json in new[] { baseJson, empresaJson })
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number) r[p.Name] = p.Value.GetDecimal();   // lo de la empresa pisa lo de fábrica
        }
        return r;
    }

    private static async Task<(int Nuevos, int Corregidos)> SincronizarAsync(IDbConnection cn, int empresaId, string codigo, Severidad severidadRegla, bool severidadForzada,
        IReadOnlyList<HallazgoDetectado> detectados, DateTime ahora, CancellationToken ct)
    {
        // Una misma clave puede venir repetida: gana la última
        var porClave = detectados.GroupBy(d => Cortar(d.ClaveEntidad, 200)!).ToDictionary(g => g.Key, g => g.Last());
        var existentes = (await cn.QueryAsync<Existente>(new CommandDefinition("""
            SELECT HallazgoId, ClaveEntidad, Estado FROM fact.HallazgoConfiguracion WITH (UPDLOCK)
            WHERE EmpresaId = @empresaId AND Codigo = @codigo
            """, new { empresaId, codigo }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var abiertos = existentes.Where(e => e.Estado is "NUEVO" or "ABIERTO").ToDictionary(e => e.ClaveEntidad);
        var suprimidos = existentes.Where(e => e.Estado is "ACEPTADO" or "FALSO_POSITIVO").Select(e => e.ClaveEntidad).ToHashSet();

        int nuevos = 0, corregidos = 0;
        using var tx = cn.BeginTransaction();
        foreach (var (clave, d) in porClave)
        {
            // La severidad la manda la regla (si la sube por caso) salvo que la empresa haya forzado una
            var sev = Severidades.ATexto(severidadForzada ? severidadRegla : Severidades.DeTexto(d.Severidad) ?? severidadRegla);
            var p = new
            {
                empresaId, codigo, clave, nombre = Cortar(d.NombreEntidad, 300), sev, enc = Cortar(d.ValorEncontrado, 400), esp = Cortar(d.ValorEsperado, 400),
                cant = d.ImpactoCantidad, valor = d.ImpactoValor, detalle = Cortar(d.ImpactoDetalle, 400), ahora,
            };
            if (suprimidos.Contains(clave)) continue;
            if (abiertos.ContainsKey(clave))
            {
                await cn.ExecuteAsync(new CommandDefinition("""
                    UPDATE fact.HallazgoConfiguracion SET Estado = 'ABIERTO', Severidad = @sev, NombreEntidad = @nombre, ValorEncontrado = @enc, ValorEsperado = @esp,
                           ImpactoCantidad = @cant, ImpactoValor = @valor, ImpactoDetalle = @detalle, UltimaDeteccion = @ahora
                    WHERE EmpresaId = @empresaId AND Codigo = @codigo AND ClaveEntidad = @clave AND Estado IN ('NUEVO','ABIERTO')
                    """, p, tx, cancellationToken: ct)).ConfigureAwait(false);
            }
            else
            {
                await cn.ExecuteAsync(new CommandDefinition("""
                    INSERT fact.HallazgoConfiguracion (EmpresaId, Codigo, ClaveEntidad, NombreEntidad, Severidad, ValorEncontrado, ValorEsperado, ImpactoCantidad, ImpactoValor, ImpactoDetalle, PrimeraDeteccion, UltimaDeteccion)
                    VALUES (@empresaId, @codigo, @clave, @nombre, @sev, @enc, @esp, @cant, @valor, @detalle, @ahora, @ahora)
                    """, p, tx, cancellationToken: ct)).ConfigureAwait(false);
                nuevos++;
            }
        }
        foreach (var (clave, e) in abiertos)
        {
            if (porClave.ContainsKey(clave)) continue;
            await cn.ExecuteAsync(new CommandDefinition("UPDATE fact.HallazgoConfiguracion SET Estado = 'CORREGIDO', CerradoEn = @ahora WHERE EmpresaId = @empresaId AND HallazgoId = @id",
                new { empresaId, id = e.HallazgoId, ahora }, tx, cancellationToken: ct)).ConfigureAwait(false);
            corregidos++;
        }
        tx.Commit();
        return (nuevos, corregidos);
    }

    /// <summary>Índice 0–100 por área y total: 100 menos los pesos de los hallazgos abiertos (CRÍTICA 20, ALTA 10, MEDIA 4, BAJA 1) [S], con piso en 0. Un registro por día (se reemplaza si se recalcula).</summary>
    private static async Task<(int Abiertos, int IndiceTotal)> GuardarIndiceAsync(IDbConnection cn, int empresaId, DateOnly hoy, CancellationToken ct)
    {
        var filas = (await cn.QueryAsync<(string Area, string Severidad, int N)>(new CommandDefinition("""
            SELECT r.Area, h.Severidad, COUNT(*) AS N
            FROM fact.HallazgoConfiguracion h JOIN cfg.ReglaConfiguracion r ON r.Codigo = h.Codigo
            WHERE h.EmpresaId = @empresaId AND h.Estado IN ('NUEVO','ABIERTO') GROUP BY r.Area, h.Severidad
            """, new { empresaId }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        var areas = (await cn.QueryAsync<string>(new CommandDefinition("SELECT DISTINCT Area FROM cfg.ReglaConfiguracion WHERE Activa = 1", cancellationToken: ct)).ConfigureAwait(false)).ToList();

        int Indice(IEnumerable<(string Area, string Severidad, int N)> f) => Math.Max(0, 100 - f.Sum(x => Severidades.Peso(x.Severidad) * x.N));
        var fecha = hoy.ToDateTime(TimeOnly.MinValue);
        var registros = areas.Select(a => (Area: a, Indice: Indice(filas.Where(f => f.Area == a)), Abiertos: filas.Where(f => f.Area == a).Sum(f => f.N)))
            .Append((Area: "TOTAL", Indice: Indice(filas), Abiertos: filas.Sum(f => f.N))).ToList();
        foreach (var r in registros)
            await cn.ExecuteAsync(new CommandDefinition("""
                MERGE fact.IndiceSaludConfiguracion AS t USING (SELECT @empresaId AS EmpresaId, @fecha AS Fecha, @area AS Area) s
                  ON t.EmpresaId = s.EmpresaId AND t.Fecha = s.Fecha AND t.Area = s.Area
                WHEN MATCHED THEN UPDATE SET Indice = @indice, Abiertos = @abiertos
                WHEN NOT MATCHED THEN INSERT (EmpresaId, Fecha, Area, Indice, Abiertos) VALUES (@empresaId, @fecha, @area, @indice, @abiertos);
                """, new { empresaId, fecha, area = r.Area, indice = r.Indice, abiertos = r.Abiertos }, cancellationToken: ct)).ConfigureAwait(false);
        var total = registros[^1];
        return (total.Abiertos, total.Indice);
    }

    private static string? Cortar(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private sealed class ConsultasEtl(IDbConnection cn, CancellationToken ct) : IConsultasAuditoria
    {
        public async Task<IReadOnlyList<HallazgoDetectado>> ConsultarAsync(string sql, IReadOnlyDictionary<string, object?> parametros, CancellationToken _)
        {
            var p = new DynamicParameters();
            foreach (var kv in parametros) p.Add(kv.Key, kv.Value);
            var filas = await cn.QueryAsync<HallazgoDetectado>(new CommandDefinition(sql, p, commandTimeout: 300, cancellationToken: ct)).ConfigureAwait(false);
            return filas.ToList();
        }
    }
}

/// <summary>AUDITAR_CONFIG: corre el auditor de una empresa tras cargar su foto de configuración.</summary>
internal sealed class ManejadorAuditarConfig(MotorAuditoria motor) : IManejadorTrabajo
{
    public string Tipo => IAnalitica.Application.Abstracciones.TiposTrabajo.AuditarConfig;

    public async Task<(ResultadoTrabajo, TimeSpan)> EjecutarAsync(IAnalitica.Application.Abstracciones.TrabajoTomado trabajo, CancellationToken ct)
    {
        await motor.EjecutarAsync(trabajo.EmpresaId ?? throw new InvalidOperationException("Falta la empresa."), ct).ConfigureAwait(false);
        return (ResultadoTrabajo.Completado, TimeSpan.Zero);
    }
}
