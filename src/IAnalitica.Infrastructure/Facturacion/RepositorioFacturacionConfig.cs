using System.Data;
using Dapper;
using IAnalitica.Application.Comun;
using IAnalitica.Application.Facturacion;
using IAnalitica.Infrastructure.Persistencia;
using Microsoft.Data.SqlClient;

namespace IAnalitica.Infrastructure.Facturacion;

/// <summary>
/// Configuración comercial con rol_app y el contexto del superadmin. Escribe solo por los procedimientos fac.usp_* (validan el actor y auditan).
/// Los THROW 501xx de esos procedimientos son errores de negocio con texto propio: se devuelven como <see cref="Error"/>.
/// </summary>
internal sealed class RepositorioFacturacionConfig(IFabricaConexiones conexiones) : IRepositorioFacturacionConfig
{
    public async Task<IReadOnlyList<PlanFacturacionVista>> ListarPlanesAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<PlanFacturacionVista>(new CommandDefinition("""
            SELECT p.PlanId, p.Codigo, p.Nombre, c.CanonMensualCop AS CanonVigenteCop, c.VigenteDesde AS CanonDesde
            FROM ia.PlanComercial p
            OUTER APPLY (SELECT TOP (1) CanonMensualCop, VigenteDesde FROM fac.CanonPlan x
                         WHERE x.PlanId = p.PlanId AND x.VigenteDesde <= CAST(DATEADD(HOUR, -5, SYSUTCDATETIME()) AS date)
                         ORDER BY x.VigenteDesde DESC) c
            WHERE p.Activo = 1 ORDER BY p.PlanId
            """, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<IReadOnlyList<CanonPlanVista>> ListarCanonesAsync(short planId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<CanonPlanVista>(new CommandDefinition(
            "SELECT PlanId, VigenteDesde, CanonMensualCop FROM fac.CanonPlan WHERE PlanId = @planId ORDER BY VigenteDesde DESC", new { planId }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public Task<Result> GuardarCanonPlanAsync(short planId, DateOnly vigenteDesde, decimal canonMensualCop, int usuarioId, CancellationToken ct)
        => EjecutarAsync("fac.usp_GuardarCanonPlan", new { PlanId = planId, VigenteDesde = vigenteDesde.ToDateTime(TimeOnly.MinValue), CanonMensualCop = canonMensualCop, UsuarioId = usuarioId }, ct);

    public async Task<IReadOnlyList<CondicionVista>> ListarCondicionesAsync(int empresaId, CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<CondicionVista>(new CommandDefinition("""
            SELECT c.CondicionId, c.EmpresaId, c.VigenteDesde, c.VigenteHasta, c.PlanId, p.Nombre AS PlanNombre, c.CanonPactadoCop, c.DiasPago,
                   c.RequiereAprobacion, c.Facturable, c.ReferenciaContrato
            FROM fac.CondicionComercial c JOIN ia.PlanComercial p ON p.PlanId = c.PlanId
            WHERE c.EmpresaId = @empresaId ORDER BY c.VigenteDesde DESC
            """, new { empresaId }, cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<Result<int>> GuardarCondicionAsync(GuardarCondicionComercialCommand c, int usuarioId, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("EmpresaId", c.EmpresaId); p.Add("VigenteDesde", c.VigenteDesde.ToDateTime(TimeOnly.MinValue)); p.Add("PlanId", c.PlanId);
        p.Add("CanonPactadoCop", c.CanonPactadoCop); p.Add("DiasPago", c.DiasPago); p.Add("RequiereAprobacion", c.RequiereAprobacion);
        p.Add("Facturable", c.Facturable); p.Add("ReferenciaContrato", c.ReferenciaContrato); p.Add("UsuarioId", usuarioId);
        p.Add("CondicionId", dbType: DbType.Int32, direction: ParameterDirection.Output);
        try
        {
            await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
            await cn.ExecuteAsync(new CommandDefinition("fac.usp_GuardarCondicion", p, commandType: CommandType.StoredProcedure, cancellationToken: ct)).ConfigureAwait(false);
            return p.Get<int>("CondicionId");
        }
        catch (SqlException e) when (EsNegocio(e)) { return Result<int>.Falla(Traducir(e)); }
    }

    public async Task<IReadOnlyList<ParametroVista>> ListarParametrosAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<ParametroVista>(new CommandDefinition("SELECT Clave, Valor, Descripcion FROM fac.Parametro ORDER BY Clave", cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<IReadOnlyList<EmpresaFacturacionVista>> ListarEmpresasAsync(CancellationToken ct)
    {
        await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
        return (await cn.QueryAsync<EmpresaFacturacionVista>(new CommandDefinition("SELECT EmpresaId, Nit, RazonSocial, Activa FROM seg.Empresa ORDER BY RazonSocial", cancellationToken: ct)).ConfigureAwait(false)).ToList();
    }

    public Task<Result> GuardarParametroAsync(string clave, string valor, int usuarioId, CancellationToken ct)
        => EjecutarAsync("fac.usp_GuardarParametro", new { Clave = clave, Valor = valor, UsuarioId = usuarioId }, ct);

    private async Task<Result> EjecutarAsync(string procedimiento, object parametros, CancellationToken ct)
    {
        try
        {
            await using var cn = await conexiones.AbrirAsync(ct).ConfigureAwait(false);
            await cn.ExecuteAsync(new CommandDefinition(procedimiento, parametros, commandType: CommandType.StoredProcedure, cancellationToken: ct)).ConfigureAwait(false);
            return Result.Ok();
        }
        catch (SqlException e) when (EsNegocio(e)) { return Result.Falla(Traducir(e)); }
    }

    /// <summary>THROW 50100-50199 de fac.usp_* (negocio) y violaciones de CHECK/UNIQUE de fac.* (547, 2601, 2627).</summary>
    internal static bool EsNegocio(SqlException e) => e.Number is >= 50100 and <= 50199 or 547 or 2601 or 2627;

    internal static Error Traducir(SqlException e) => e.Number switch
    {
        50100 => Error.NoAutorizado(),
        50138 => Error.NoEncontrado("La empresa no existe."),
        50150 => Error.NoEncontrado("El parámetro no existe."),
        50140 => Error.Conflicto(CodigosFacturacion.Canon, e.Message),
        50160 or 50161 => Error.Conflicto(CodigosFacturacion.Vigencia, e.Message),
        50111 or 50114 => Error.Conflicto(CodigosFacturacion.Estado, e.Message),
        50120 => Error.Validacion(CodigosFacturacion.Motivo, e.Message),
        >= 50130 and <= 50139 => Error.Conflicto(CodigosFacturacion.Pago, e.Message),
        >= 50151 and <= 50154 => Error.Validacion(CodigosFacturacion.Parametro, e.Message),
        547 => Error.Validacion("DATOS_INVALIDOS", "Los datos no cumplen las reglas de facturación."),   // jamás el texto del motor: trae nombres de objetos
        2601 or 2627 => Error.Conflicto("DUPLICADO", "Ya existe un registro equivalente."),
        _ => Error.Conflicto("FACTURACION", e.Message),
    };
}
