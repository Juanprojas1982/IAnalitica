using IAnalitica.Domain.Auditoria;

namespace IAnalitica.Modulos.Auditor;

/// <summary>
/// Regla cuyo núcleo es una consulta SQL sobre <c>saludips.*</c>. El SQL es una constante del código (nunca texto de usuario) y debe filtrar por
/// <c>@EmpresaId</c>: el motor corre con rol_etl, que no tiene RLS, así que el filtro por empresa lo pone cada consulta.
/// Parámetros disponibles: @EmpresaId, @Anio, @Hoy, @Desde30 (clave yyyymmdd de hace 30 días), @Ahora (UTC).
/// </summary>
public abstract class ReglaSql(string codigo, string area, Severidad severidadBase, string sql) : IReglaConfiguracion
{
    public string Codigo { get; } = codigo;
    public string Area { get; } = area;
    public Severidad SeveridadBase { get; } = severidadBase;
    protected string Sql { get; } = sql;

    /// <summary>La regla puede no aplicar hoy (p. ej. CON-02 solo desde noviembre).</summary>
    protected virtual bool Aplica(ContextoAuditoria ctx) => true;

    protected virtual IReadOnlyDictionary<string, object?> Parametros(ContextoAuditoria ctx) => new Dictionary<string, object?>();

    public Task<IReadOnlyList<HallazgoDetectado>> EvaluarAsync(ContextoAuditoria ctx, CancellationToken ct)
    {
        if (!Aplica(ctx)) return Task.FromResult<IReadOnlyList<HallazgoDetectado>>(Array.Empty<HallazgoDetectado>());
        var p = new Dictionary<string, object?>
        {
            ["EmpresaId"] = ctx.EmpresaId,
            ["Anio"] = ctx.Anio,
            ["Hoy"] = ctx.Hoy.ToDateTime(TimeOnly.MinValue),
            ["Desde30"] = int.Parse(ctx.Hoy.AddDays(-30).ToString("yyyyMMdd")),
            ["Ahora"] = DateTime.UtcNow,
        };
        foreach (var kv in Parametros(ctx)) p[kv.Key] = kv.Value;
        return ctx.Datos.ConsultarAsync(Sql, p, ct);
    }

    /// <summary>Fragmento: facturación de los últimos 30 días del pagador <paramref name="codigoPagador"/> (expresión SQL con el IdEmpresa), como evidencia de impacto.</summary>
    protected static string ImpactoFacturacion(string codigoPagador) => $"""
        OUTER APPLY (
            SELECT COUNT(DISTINCT fl.NumeroDocumento) AS Cant, SUM(fl.ValorNeto) AS Valor
            FROM fact.FacturaLinea fl JOIN dim.Pagador dp ON dp.PagadorId = fl.PagadorId AND dp.EmpresaId = fl.EmpresaId
            WHERE fl.EmpresaId = @EmpresaId AND dp.CodigoOrigen = CAST({codigoPagador} AS varchar(30))
              AND fl.FechaEmisionKey >= @Desde30 AND fl.Anulada = 0 AND fl.TipoDocumento = 'FACTURA') imp
        """;
}
